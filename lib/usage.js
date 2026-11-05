/**
 * Durable, replay-aware daily token ledger.
 *
 * A provider reports usage on `assistant/message` (successful settlement) and
 * `assistant/attempt` (a failed/retried attempt).  Both are billed attempts, so
 * both are counted.  Within one step, a later attempt replaces an earlier
 * sample; `llm/retry-started` explicitly closes the replacement slot, matching
 * DSH's own token-usage projection semantics.
 *
 * The ledger is persisted so a DSH restart does not erase today's total.  On
 * startup the plugin replays today's stored sessions once; keys are
 * `sessionId:seq`, so live events and replay cannot double count.
 */
import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dateKey, dayRange, formatTokens, resolveCurrency, summarizeEntries } from './pricing.js'

const MAX_DAYS = 14
const FLUSH_DELAY_MS = 1500

export function normalizeUsage(usage) {
  if (!usage || typeof usage !== 'object') return null
  const input = Number(usage.inputTokens)
  const output = Number(usage.outputTokens)
  const cacheRead = usage.cacheReadTokens === undefined ? 0 : Number(usage.cacheReadTokens)
  const cacheWrite = usage.cacheWriteTokens === undefined ? 0 : Number(usage.cacheWriteTokens)
  if (!Number.isSafeInteger(input) || input < 0) return null
  if (!Number.isSafeInteger(output) || output < 0) return null
  if (!Number.isSafeInteger(cacheRead) || cacheRead < 0) return null
  if (!Number.isSafeInteger(cacheWrite) || cacheWrite < 0) return null
  return { input, output, cacheRead, cacheWrite }
}

export function lastStreamUsage(stream) {
  if (!Array.isArray(stream)) return null
  for (let index = stream.length - 1; index >= 0; index -= 1) {
    const item = stream[index]
    const chunk = item && typeof item === 'object' && item.chunk ? item.chunk : item
    if (chunk && typeof chunk === 'object' && chunk.type === 'usage' && chunk.usage) return chunk.usage
  }
  return null
}

export class UsageLedger {
  constructor(config, logger) {
    this.config = config
    this.logger = logger
    this.days = {}
    this.attempts = new Map()
    this.routes = new Map()
    this.partial = false
    this.flushTimer = null
    this.closed = false
    this.backfilled = false
  }

  load() {
    try {
      if (!existsSync(this.config.usagePath)) return
      const parsed = JSON.parse(readFileSync(this.config.usagePath, 'utf8'))
      if (!parsed || parsed.version !== 1 || typeof parsed.days !== 'object') return
      for (const [day, value] of Object.entries(parsed.days)) {
        if (!value || typeof value.entries !== 'object') continue
        const entries = {}
        for (const [key, entry] of Object.entries(value.entries)) {
          const normalized = normalizeEntry(entry)
          if (normalized) entries[key] = normalized
        }
        this.days[day] = { entries }
      }
      this.prune()
    } catch (error) {
      this.logger?.warn?.(`desktop-pet: usage ledger load failed: ${error.message}`)
    }
  }

  ensureDay(day) {
    if (!this.days[day]) this.days[day] = { entries: {} }
    return this.days[day]
  }

  today(time = Date.now()) {
    return dateKey(time, this.config.timezone)
  }

  entriesFor(day) {
    return this.days[day]?.entries ?? {}
  }

  /**
   * Fold one durable session event into the ledger.
   * @returns {boolean} whether a new/replaced entry was written
   */
  ingest(sessionId, event, options = {}) {
    if (!event || typeof event !== 'object') return false
    const type = event.type
    if (type === 'request/context' && event.data) {
      const provider = typeof event.data.provider === 'string' ? event.data.provider : ''
      const model = typeof event.data.model === 'string' ? event.data.model : ''
      if (provider || model) this.routes.set(sessionId, { provider, model })
      return false
    }
    if (type === 'llm/retry-started') {
      const attemptKey = this.attemptKey(sessionId, event.data)
      if (attemptKey) this.attempts.delete(attemptKey)
      return false
    }
    if (type === 'turn/end') {
      const turn = event.data && Number.isSafeInteger(event.data.turn) ? event.data.turn : null
      if (turn !== null) {
        const prefix = `${sessionId}:${turn}:`
        for (const key of [...this.attempts.keys()]) {
          if (key.startsWith(prefix)) this.attempts.delete(key)
        }
      }
      return false
    }
    if (type !== 'assistant/message' && type !== 'assistant/attempt') return false

    const usage = normalizeUsage(
      type === 'assistant/message' && event.data?.usage ? event.data.usage : lastStreamUsage(event.data?.stream) ?? event.data?.usage,
    )
    if (!usage) return false
    if (!Number.isFinite(event.time)) return false

    const day = dateKey(event.time, this.config.timezone)
    const entries = this.ensureDay(day).entries
    const key = `${sessionId}:${event.seq}`
    if (entries[key]) return false

    const route = this.routeFor(sessionId, event)
    const attemptKey = this.attemptKey(sessionId, event.data)
    if (attemptKey) {
      const previous = this.attempts.get(attemptKey)
      if (previous && previous !== key && entries[previous]) delete entries[previous]
    }

    entries[key] = {
      t: event.time,
      m: route.model,
      p: route.provider,
      i: usage.input,
      o: usage.output,
      cr: usage.cacheRead,
      cw: usage.cacheWrite,
    }
    if (attemptKey) this.attempts.set(attemptKey, key)
    if (!options.quiet) this.scheduleFlush()
    return true
  }

  routeFor(sessionId, event) {
    if (event.type === 'assistant/message') {
      const source = event.data?.message?.source
      if (source && (source.provider || source.model)) {
        const route = { provider: source.provider ?? '', model: source.model ?? '' }
        this.routes.set(sessionId, route)
        return route
      }
    }
    return this.routes.get(sessionId) ?? { provider: '', model: '' }
  }

  attemptKey(sessionId, data) {
    if (!data || !Number.isSafeInteger(data.turn) || !Number.isSafeInteger(data.step)) return ''
    return `${sessionId}:${data.turn}:${data.step}`
  }

  scheduleFlush() {
    if (this.closed || this.flushTimer) return
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null
      this.flush()
    }, FLUSH_DELAY_MS)
    this.flushTimer.unref?.()
  }

  flush() {
    if (this.closed) return
    try {
      this.prune()
      const payload = {
        version: 1,
        updatedAt: Date.now(),
        days: this.days,
      }
      const temp = `${this.config.usagePath}.${process.pid}.tmp`
      writeFileSync(temp, `${JSON.stringify(payload)}\n`, { encoding: 'utf8', mode: 0o600 })
      try {
        renameSync(temp, this.config.usagePath)
      } catch {
        // A reader may briefly hold the destination on Windows.  Keep the
        // same atomic intent but fall back to a direct write rather than
        // losing the user's daily total.
        writeFileSync(this.config.usagePath, `${JSON.stringify(payload)}\n`, { encoding: 'utf8', mode: 0o600 })
      }
    } catch (error) {
      this.logger?.warn?.(`desktop-pet: usage ledger flush failed: ${error.message}`)
    }
  }

  prune() {
    const days = Object.keys(this.days).sort()
    while (days.length > MAX_DAYS) {
      const day = days.shift()
      delete this.days[day]
    }
  }

  summary(currency, now = Date.now()) {
    const day = this.today(now)
    const entries = this.entriesFor(day)
    const resolved = resolveCurrency(this.config.pricing, currency)
    const summary = summarizeEntries(entries, resolved, this.config.pricing, this.config.timezone)
    const range = dayRange(now, this.config.timezone)
    return {
      ...summary,
      day,
      start: range.start,
      end: range.end,
      partial: this.partial,
      entryCount: Object.keys(entries).length,
    }
  }

  tokensSince(sessionId, since, now = Date.now()) {
    const day = this.today(now)
    let total = 0
    const prefix = `${sessionId}:`
    for (const [key, entry] of Object.entries(this.entriesFor(day))) {
      if (!key.startsWith(prefix)) continue
      if (!Number.isFinite(entry.t) || entry.t < since) continue
      total += entry.i + entry.o + entry.cr + entry.cw
    }
    return total
  }

  /**
   * Replay today's stored sessions.  This runs once, in the background, and is
   * bounded by session count and bytes so a long history cannot stall the host.
   */
  async backfill(ctx) {
    if (this.backfilled || !this.config.backfill.enabled || this.config.backfill.maxSessions <= 0) return
    this.backfilled = true
    const persistence = ctx.sessionPersistence
    if (!persistence || typeof persistence.list !== 'function' || typeof persistence.open !== 'function') {
      this.partial = true
      this.logger?.warn?.('desktop-pet: sessionPersistence unavailable; today total starts from live events')
      return
    }

    let snapshots
    try {
      const listed = await withTimeout(persistence.list(), this.config.backfill.timeoutMs, 'session list')
      snapshots = Array.isArray(listed) ? listed : []
    } catch (error) {
      this.partial = true
      this.logger?.warn?.(`desktop-pet: session list failed: ${error.message}`)
      return
    }

    const today = this.today()
    const candidates = snapshots
      .filter((snapshot) => snapshot && snapshot.header && snapshot.header.id)
      .filter((snapshot) => !(this.config.skipSubagents && (snapshot.header.origin === 'subagent' || snapshot.header.parentSession)))
      .filter((snapshot) => !Number.isFinite(snapshot.sizeBytes) || snapshot.sizeBytes <= this.config.backfill.maxBytes)
      .sort((left, right) => (left.sizeBytes ?? 0) - (right.sizeBytes ?? 0))
      .slice(0, this.config.backfill.maxSessions)

    let totalBytes = 0
    let index = 0
    const worker = async () => {
      while (index < candidates.length) {
        const current = candidates[index++]
        const size = Number(current.sizeBytes) || 0
        if (totalBytes + size > this.config.backfill.maxTotalBytes) {
          this.partial = true
          continue
        }
        totalBytes += size
        const controller = new AbortController()
        const timer = setTimeout(() => controller.abort(), this.config.backfill.timeoutMs)
        timer.unref?.()
        let handle
        try {
          handle = await persistence.open(current.header.id, 'read', { signal: controller.signal })
          const result = await handle.read(0, Number.MAX_SAFE_INTEGER, { signal: controller.signal })
          const events = Array.isArray(result?.events) ? result.events : []
          for (const event of events) {
            this.ingest(current.header.id, event, { quiet: true })
          }
        } catch (error) {
          this.partial = true
          this.logger?.warn?.(`desktop-pet: backfill ${current.header.id} failed: ${error.message}`)
        } finally {
          clearTimeout(timer)
          try {
            await handle?.close?.()
          } catch {
            // Closing a read handle is best-effort; the backend also reaps it.
          }
        }
      }
    }
    const concurrency = this.config.backfill.concurrency
    await Promise.all(Array.from({ length: Math.min(concurrency, candidates.length) }, () => worker()))
    this.flush()
    this.logger?.info?.(`desktop-pet: backfilled ${candidates.length} session(s) for ${today}`)
  }

  dispose() {
    this.closed = true
    if (this.flushTimer) {
      clearTimeout(this.flushTimer)
      this.flushTimer = null
    }
    this.flush()
  }
}

function normalizeEntry(entry) {
  if (!entry || typeof entry !== 'object') return null
  const t = Number(entry.t)
  const i = Number(entry.i)
  const o = Number(entry.o)
  if (!Number.isFinite(t) || !Number.isSafeInteger(i) || i < 0 || !Number.isSafeInteger(o) || o < 0) return null
  const cr = Number(entry.cr)
  const cw = Number(entry.cw)
  return {
    t,
    m: typeof entry.m === 'string' ? entry.m : '',
    p: typeof entry.p === 'string' ? entry.p : '',
    i,
    o,
    cr: Number.isSafeInteger(cr) && cr >= 0 ? cr : 0,
    cw: Number.isSafeInteger(cw) && cw >= 0 ? cw : 0,
  }
}

export function formatUsageDetail(summary) {
  return `Token ${formatTokens(summary.tokens)}（输入 ${formatTokens(summary.input)} · 输出 ${formatTokens(summary.output)} · 缓存 ${formatTokens(summary.cacheRead + summary.cacheWrite)}）`
}

function withTimeout(promise, timeoutMs, label) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`${label} timed out after ${timeoutMs}ms`)), timeoutMs)
    timer.unref?.()
    Promise.resolve(promise).then(
      (value) => {
        clearTimeout(timer)
        resolve(value)
      },
      (error) => {
        clearTimeout(timer)
        reject(error)
      },
    )
  })
}
