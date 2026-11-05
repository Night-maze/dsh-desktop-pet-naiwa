/**
 * DSH host plugin: desktop companion.
 *
 * What this plugin owns:
 *  - one WPF pet process (transparent, frameless, topmost) and its authenticated
 *    loopback bridge;
 *  - DSH session/agent event projection into friendly progress and completion;
 *  - a durable, replay-aware daily token ledger;
 *  - real account balance reads through `ctx.deepseekAccount`.
 *
 * What it deliberately does not do:
 *  - invent token usage, cost, balance, or progress percentages;
 *  - permanently delete user files.  Drops are handled inside the pet with the
 *    Windows Shell recycle API only.
 */
import { appendFileSync, existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { basename, join } from 'node:path'
import { spawn } from 'node:child_process'
import { deepMerge, resolveConfig, writePetConfig } from './config.js'
import { UsageLedger, formatUsageDetail } from './usage.js'
import { BalanceService } from './balance.js'
import { PetProcess } from './pet-process.js'
import { formatMoney, formatTokens, resolveCurrency, zonedParts } from './pricing.js'

export const name = 'desktop-pet'
// webServer is the desktop/Web host route carrier used by the browser settings
// page. It is composed by dsh-web-app for every browser-facing profile.
export const inject = ['sessions', 'sessionPersistence', 'deepseekAccount', 'webServer']

function uiConfigPath(dataDir) {
  return join(dataDir, 'ui-config.json')
}

function loadUiOverrides(dataDir) {
  const file = uiConfigPath(dataDir)
  if (!existsSync(file)) return {}
  try {
    const value = JSON.parse(readFileSync(file, 'utf8'))
    return value && typeof value === 'object' && !Array.isArray(value) ? value : {}
  } catch {
    return {}
  }
}

function writeUiOverrides(dataDir, value) {
  const file = uiConfigPath(dataDir)
  const temp = `${file}.${process.pid}.tmp`
  writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 })
  try {
    renameSync(temp, file)
  } catch {
    // The profile directory can briefly hold the target on Windows.  The
    // settings page is the only writer, so a direct write is acceptable here.
    writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 })
  }
}

export function apply(ctx, rawConfig) {
  const baseConfig = resolveConfig(rawConfig)
  const logger = makeLogger(ctx, baseConfig)
  if (!baseConfig.enabled) {
    logger.info('desktop-pet: disabled by configuration')
    return
  }

  let uiOverrides = loadUiOverrides(baseConfig.dataDir)
  let config = Object.keys(uiOverrides).length > 0
    ? resolveConfig(deepMerge(rawConfig ?? {}, uiOverrides))
    : baseConfig

  const ledger = new UsageLedger(config, logger)
  const balance = new BalanceService(ctx, config, logger)
  const pet = new PetProcess(config, logger, {
    onReady: () => {
      ready = true
      flushPending()
      sendStats(true)
      void kickoff()
    },
    onRequest: (action) => {
      void handleRequest(action)
    },
  })

  let disposed = false
  let ready = false
  let kicked = false
  let activeSessionId = null
  let lastStatsAt = 0
  let lastDay = ledger.today()
  let statsTimer = null
  let balanceTimer = null
  let healthTimer = null
  let dayTimer = null
  const tasks = new Map()
  const pending = []

  function makeLogger(scope, cfg) {
    const write = (level, message) => {
      try {
        appendFileSync(cfg.logPath, `${new Date().toISOString()} [${level}] ${message}\n`, 'utf8')
      } catch {
        // Host logging is diagnostic only.
      }
    }
    return {
      info: (message) => {
        scope.logger?.info?.(message)
        write('INFO', message)
      },
      warn: (message) => {
        scope.logger?.warn?.(message)
        write('WARN', message)
      },
      error: (message) => {
        scope.logger?.error?.(message)
        write('ERROR', message)
      },
      debug: (message) => {
        scope.logger?.debug?.(message)
        if (cfg.debug) write('DEBUG', message)
      },
    }
  }

  function sendToPet(message) {
    if (disposed) return false
    const sent = pet.send(message)
    if (!sent) {
      if (message.type === 'stats') {
        for (let index = pending.length - 1; index >= 0; index -= 1) {
          if (pending[index].type === 'stats') pending.splice(index, 1)
        }
      }
      pending.push(message)
      if (pending.length > 120) pending.shift()
    }
    return sent
  }

  function flushPending() {
    if (!ready) return
    while (pending.length > 0) {
      const next = pending[0]
      if (!pet.send(next)) break
      pending.shift()
    }
  }

  function buildStats() {
    const balanceSnapshot = balance.getSnapshot()
    const summary = ledger.summary(balanceSnapshot.currency)
    const currency = resolveCurrency(config.pricing, balanceSnapshot.currency)
    const costText = summary.priced
      ? formatMoney(summary.cost, currency, config.costPrecision)
      : ''
    const line =
      summary.tokens === 0
        ? '今日 暂无花费'
        : summary.priced
          ? `今日 ≈${costText}`
          : `今日 ${formatTokens(summary.tokens)} tok`
    const balanceText = balanceSnapshot.available ? `余额 ${balanceSnapshot.text}` : '余额 —'
    const detail = []
    detail.push(formatUsageDetail(summary))
    detail.push(summary.priced ? '按 DeepSeek 公开价估算，不是账单' : '未配置单价，仅显示真实 Token')
    if (summary.unpricedTokens > 0 && summary.pricedTokens > 0) detail.push(`另有 ${formatTokens(summary.unpricedTokens)} tok 未定价`)
    if (summary.partial) detail.push('历史回填不完整')
    if (balanceSnapshot.detail) detail.push(balanceSnapshot.detail)
    if (summary.entryCount > 0) {
      const time = zonedParts(Date.now(), config.timezone)
      detail.push(`更新 ${String(time.hour).padStart(2, '0')}:${String(time.minute).padStart(2, '0')}`)
    }
    return {
      type: 'stats',
      line,
      balance: balanceText,
      detail: detail.join(' · '),
      stale: balanceSnapshot.stale,
      currency,
      tokens: summary.tokens,
      cost: summary.cost,
      priced: summary.priced,
      partial: summary.partial,
      day: summary.day,
      source: 'usage=provider-reported tokens; cost=configured public list price; balance=platform wallet',
    }
  }

  function sendStats(force = false) {
    const now = Date.now()
    if (!force && now - lastStatsAt < 400) return
    lastStatsAt = now
    let payload
    try {
      payload = buildStats()
    } catch (error) {
      logger.error(`desktop-pet: stats build failed: ${error.stack || error.message}`)
      payload = {
        type: 'stats',
        line: '今日 统计暂不可用',
        balance: '余额 —',
        detail: '统计模块出错，基础互动仍可使用',
        stale: true,
      }
    }
    sendToPet(payload)
  }

  async function refreshBalance(force = false) {
    if (disposed) return
    try {
      await balance.refresh(force)
      sendStats(true)
    } catch (error) {
      logger.warn(`desktop-pet: balance refresh threw: ${error.message}`)
    }
  }

  async function kickoff() {
    if (kicked || disposed) return
    kicked = true
    void refreshBalance(true)
    try {
      await ledger.backfill(ctx)
      sendStats(true)
    } catch (error) {
      logger.warn(`desktop-pet: backfill failed: ${error.message}`)
    }
  }

  function skipSession(session) {
    if (!config.skipSubagents) return false
    const header = session?.header
    return Boolean(header && (header.origin === 'subagent' || header.parentSession))
  }

  function taskTitle(session) {
    const cwd = session?.header?.cwd
    const name = cwd ? basename(cwd) : ''
    return name || '当前任务'
  }

  function latestTask() {
    let latest = null
    for (const task of tasks.values()) {
      if (!latest || task.startedAt > latest.startedAt) latest = task
    }
    return latest
  }

  function sendAgent(event, text, extra = {}) {
    sendToPet({
      type: 'agent',
      event,
      text,
      tool: extra.tool ?? '',
      step: extra.step ?? 0,
      durationMs: extra.durationMs ?? 0,
      tokens: extra.tokens ?? 0,
    })
  }

  function startTask(session, event) {
    const sessionId = session.header.id
    const turn = Number(event.data?.turn)
    if (!Number.isSafeInteger(turn)) return
    const previous = tasks.get(sessionId)
    if (previous?.errorTimer) clearTimeout(previous.errorTimer)
    const task = {
      sessionId,
      turn,
      startedAt: Number(event.time) || Date.now(),
      steps: 0,
      tool: '',
      title: taskTitle(session),
      lastSend: 0,
      error: '',
      errorTimer: null,
      errorSent: false,
    }
    tasks.set(sessionId, task)
    activeSessionId = sessionId
    sendAgent('start', `开始：${task.title}`, { step: 0 })
  }

  function progressTask(session, event) {
    let task = tasks.get(session.header.id)
    if (!task && Number.isSafeInteger(Number(event.data?.turn))) {
      // The plugin may have been mounted mid-turn.  Adopt the running turn on
      // the first step/tool event instead of waiting for the next user prompt.
      startTask(session, { data: { turn: Number(event.data.turn) }, time: event.time })
      task = tasks.get(session.header.id)
    }
    if (!task || task.turn !== Number(event.data?.turn)) return
    const step = Number(event.data?.step)
    if (Number.isSafeInteger(step)) task.steps = Math.max(task.steps, step)
    if (event.type === 'tool/call' && typeof event.data?.name === 'string') task.tool = event.data.name
    if (event.type === 'tool/result' && event.data?.error) {
      task.error = String(event.data.error.message || event.data.error.code || '工具失败')
    }
    const now = Date.now()
    if (now - task.lastSend < 180) return
    task.lastSend = now
    if (activeSessionId === task.sessionId) {
      sendAgent('progress', `第 ${task.steps || 1} 步`, { tool: task.tool, step: task.steps })
    }
  }

  function clearTaskErrorTimer(task) {
    if (task?.errorTimer) {
      clearTimeout(task.errorTimer)
      task.errorTimer = null
    }
  }

  function finishTask(session, event) {
    const task = tasks.get(session.header.id)
    if (!task || task.turn !== Number(event.data?.turn)) return
    clearTaskErrorTimer(task)
    const wasActive = activeSessionId === task.sessionId || activeSessionId === null
    tasks.delete(task.sessionId)
    if (activeSessionId === task.sessionId) {
      const latest = latestTask()
      activeSessionId = latest?.sessionId ?? null
    }
    const isActive = wasActive
    const durationMs = Math.max(0, (Number(event.time) || Date.now()) - task.startedAt)
    const tokens = ledger.tokensSince(task.sessionId, task.startedAt, Number(event.time) || Date.now())
    const reason = event.data?.reason
    const kind = typeof reason === 'string' ? reason : reason?.kind ?? 'completed'
    const errorText =
      (typeof reason === 'object' && reason && (reason.error?.message || reason.message)) ||
      task.error ||
      ''
    if (kind === 'aborted' || kind === 'cancelled' || kind === 'canceled') {
      if (isActive) sendAgent('cancel', '任务已取消', { durationMs, tokens })
    } else if (kind === 'error' || kind === 'max-tokens') {
      if (isActive) {
        const text = kind === 'max-tokens' ? '任务达到输出上限，未完整结束' : `任务失败：${errorText || '未知错误'}`
        sendAgent('fail', text, { durationMs, tokens })
      }
    } else {
      if (isActive) {
        const seconds = Math.max(0, Math.round(durationMs / 1000))
        const suffix = tokens > 0 ? ` · ${formatTokens(tokens)} tok` : ''
        sendAgent('complete', `任务完成 · ${task.steps || 0} 步 · ${seconds}s${suffix}`, { durationMs, tokens })
      }
    }
    sendStats(true)
  }

  function handleSessionEvent(session, event) {
    if (disposed || !session || skipSession(session)) return
    try {
      ledger.ingest(session.header.id, event)
    } catch (error) {
      logger.warn(`desktop-pet: usage ingest failed: ${error.message}`)
    }
    try {
      if (event.type === 'turn/start') startTask(session, event)
      else if (event.type === 'step/start' || event.type === 'tool/call' || event.type === 'tool/result') progressTask(session, event)
      else if (event.type === 'turn/end') finishTask(session, event)
    } catch (error) {
      logger.warn(`desktop-pet: task projection failed: ${error.message}`)
    }
  }

  function handleAgentError(event) {
    if (disposed) return
    const agent = event?.data?.agent
    const session = agent?.session
    if (!session || skipSession(session)) return
    const sessionId = session.header.id
    const error = event.data?.error
    const message = String(error?.message || error?.code || error?.name || '未知错误')
    const task = tasks.get(sessionId)
    if (task) {
      task.error = message
      if (!task.errorTimer && !task.errorSent) {
        task.errorTimer = setTimeout(() => {
          task.errorTimer = null
          if (tasks.get(sessionId) !== task) return
          task.errorSent = true
          sendAgent('fail', `任务失败：${message}`)
          tasks.delete(sessionId)
          if (activeSessionId === sessionId) activeSessionId = latestTask()?.sessionId ?? null
          sendStats(true)
        }, 1200)
        task.errorTimer.unref?.()
      }
      return
    }
    if (activeSessionId === sessionId) sendAgent('fail', `任务失败：${message}`)
  }

  async function handleRequest(action) {
    if (disposed) return
    if (action === 'refresh_stats' || action === 'refresh') {
      await refreshBalance(true)
      sendStats(true)
    } else if (action === 'open_config' || action === 'open_readme') {
      openReadme()
    } else if (action === 'restart') {
      await pet.restart()
      sendStats(true)
    } else if (action === 'quit') {
      await pet.stop()
    } else if (action === 'show') {
      if (!pet.started) await pet.start()
      else sendToPet({ type: 'control', action: 'show' })
    } else if (action === 'hide') {
      sendToPet({ type: 'control', action: 'hide' })
    }
  }

  function openReadme() {
    const path = join(config.pluginRoot, 'README.zh.md')
    try {
      const child = spawn('notepad.exe', [path], { detached: true, stdio: 'ignore', windowsHide: true })
      child.unref?.()
    } catch (error) {
      logger.warn(`desktop-pet: cannot open ${path}: ${error.message}`)
    }
  }

  function publicConfig() {
    return {
      scale: config.scale,
      volume: config.volume,
      statsDisplay: config.statsDisplay,
      longPressMs: config.longPressMs,
      dragStartPx: config.dragStartPx,
    }
  }

  function statusPayload() {
    return {
      ok: true,
      config: publicConfig(),
      stats: buildStats(),
      pet: {
        ready,
        started: pet.started,
      },
    }
  }

  async function applyUiConfig(patch) {
    const next = patch?.reset === true ? {} : { ...uiOverrides }
    if (patch?.reset !== true) {
      if (patch?.scale !== undefined) {
        const value = Number(patch.scale)
        if (!Number.isFinite(value)) throw new Error('scale must be a number')
        next.scale = value
      }
      if (patch?.volume !== undefined) {
        const value = Number(patch.volume)
        if (!Number.isFinite(value)) throw new Error('volume must be a number')
        next.volume = value
      }
      if (patch?.statsDisplay !== undefined) {
        if (!['always', 'hover', 'off'].includes(patch.statsDisplay)) {
          throw new Error('statsDisplay must be always, hover, or off')
        }
        next.statsDisplay = patch.statsDisplay
      }
      if (patch?.longPressMs !== undefined) {
        const value = Number(patch.longPressMs)
        if (!Number.isFinite(value)) throw new Error('longPressMs must be a number')
        next.longPressMs = value
      }
      if (patch?.dragStartPx !== undefined) {
        const value = Number(patch.dragStartPx)
        if (!Number.isFinite(value)) throw new Error('dragStartPx must be a number')
        next.dragStartPx = value
      }
    }
    uiOverrides = next
    writeUiOverrides(config.dataDir, uiOverrides)
    const nextConfig = resolveConfig(deepMerge(rawConfig ?? {}, uiOverrides))
    Object.assign(config, nextConfig)
    writePetConfig(config)
    await pet.restart()
    sendStats(true)
    return statusPayload()
  }

  function readJsonBody(req) {
    return new Promise((resolve, reject) => {
      const chunks = []
      let size = 0
      req.on('data', (chunk) => {
        size += chunk.length
        if (size > 64 * 1024) {
          reject(new Error('request body too large'))
          req.destroy()
          return
        }
        chunks.push(chunk)
      })
      req.on('end', () => {
        if (chunks.length === 0) {
          resolve({})
          return
        }
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')))
        } catch (error) {
          reject(new Error(`invalid JSON: ${error.message}`))
        }
      })
      req.on('error', reject)
    })
  }

  function sendJson(res, statusCode, value) {
    if (res.headersSent || res.writableEnded) return
    const body = `${JSON.stringify(value)}\n`
    res.writeHead(statusCode, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store',
      'content-length': Buffer.byteLength(body),
    })
    res.end(body)
  }

  async function handleDesktopPetApi(req, res) {
    const remote = req.socket?.remoteAddress ?? ''
    if (!['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(remote)) {
      sendJson(res, 403, { ok: false, error: 'loopback only' })
      return
    }
    let pathname
    try {
      pathname = new URL(req.url ?? '/', 'http://127.0.0.1').pathname
    } catch {
      sendJson(res, 400, { ok: false, error: 'invalid URL' })
      return
    }
    try {
      if (pathname === '/desktop-pet-api/config' && req.method === 'GET') {
        sendJson(res, 200, statusPayload())
        return
      }
      if (pathname === '/desktop-pet-api/status' && req.method === 'GET') {
        sendJson(res, 200, statusPayload())
        return
      }
      if (pathname === '/desktop-pet-api/config' && req.method === 'POST') {
        const patch = await readJsonBody(req)
        const result = await applyUiConfig(patch)
        sendJson(res, 200, result)
        return
      }
      if (pathname === '/desktop-pet-api/action' && req.method === 'POST') {
        const body = await readJsonBody(req)
        await handleRequest(String(body.action ?? ''))
        sendJson(res, 200, statusPayload())
        return
      }
      if (pathname === '/desktop-pet-api/client-log' && req.method === 'POST') {
        const body = await readJsonBody(req)
        logger.warn(`desktop-pet: client log ${JSON.stringify(body).slice(0, 4000)}`)
        sendJson(res, 200, { ok: true })
        return
      }
      sendJson(res, 404, { ok: false, error: 'not found' })
    } catch (error) {
      logger.warn(`desktop-pet: settings API failed: ${error.message}`)
      sendJson(res, 400, { ok: false, error: error.message })
    }
  }

  function nudgeClientModules() {
    // A linked package may gain its `dsh.client` half after the Host already
    // built the client graph.  Nudge that incremental registry once so a
    // developer does not need a full DSH restart just to see the new panel.
    // This is guarded and best-effort; a normal boot performs the scan itself.
    setTimeout(() => {
      try {
        const registry = ctx.get('clientModules')
        if (!registry || typeof registry.flush !== 'function' || !registry.dirty) return
        // resolveMeta caches a package's `dsh.client` absence by source key.
        // Drop that cache so the one-time rescan can see metadata added after
        // the Host's activation scan.
        if (registry.pkgMeta && typeof registry.pkgMeta.clear === 'function') registry.pkgMeta.clear()
        // Scan every live entry: the Loader name may be a package alias, a
        // file URL, or the bundle name depending on the install shape.
        const loader = ctx.loader ?? ctx.get?.('loader')
        const entries = [...(loader?.entries?.() ?? [])]
        for (const entry of entries) {
          if (entry?.options?.name) registry.dirty.add(entry.options.name)
        }
        registry.dirty.add('dsh-desktop-pet-naiwa')
        registry.flush((error) => logger.warn(`desktop-pet: client module rescan failed: ${error?.message ?? error}`))
        // `flush` preserves a table row when only the bundle bytes changed
        // (same source key).  Publish the new artifact revision explicitly so
        // the open page can hot-reload the edited client module.
        if (typeof registry.rebuilt === 'function') {
          const rev = registry.rebuilt('dsh-desktop-pet-naiwa')
          logger.info(`desktop-pet: client bundle revision ${rev ?? 'unchanged'}`)
        }
        logger.info(`desktop-pet: client module rescan requested (${entries.length} entries)`)
        logger.info('desktop-pet: requested client module rescan')
      } catch (error) {
        logger.warn(`desktop-pet: client module rescan unavailable: ${error.message}`)
      }
    }, 1200).unref?.()
  }

  function startTimers() {
    statsTimer = setInterval(() => sendStats(), config.statsRefreshMs)
    statsTimer.unref?.()
    balanceTimer = setInterval(() => {
      if (balance.shouldRefresh()) void refreshBalance()
    }, 5000)
    balanceTimer.unref?.()
    healthTimer = setInterval(() => pet.healthCheck(), 10_000)
    healthTimer.unref?.()
    dayTimer = setInterval(() => {
      const day = ledger.today()
      if (day !== lastDay) {
        lastDay = day
        sendStats(true)
      }
    }, 30_000)
    dayTimer.unref?.()
  }

  function stopTimers() {
    for (const timer of [statsTimer, balanceTimer, healthTimer, dayTimer]) {
      if (timer) clearInterval(timer)
    }
    statsTimer = null
    balanceTimer = null
    healthTimer = null
    dayTimer = null
  }

  ctx.effect(() => {
    ledger.load()
    const offSession = ctx.on('session/event', handleSessionEvent)
    const offAgentError = ctx.on('agent/error', handleAgentError)
    let disposeApi = null
    if (ctx.webServer?.register) {
      disposeApi = ctx.webServer.register({
        kind: 'prefix',
        path: '/desktop-pet-api',
        handler: handleDesktopPetApi,
      })
    } else {
      logger.warn('desktop-pet: webServer unavailable; settings page API disabled')
    }
    let cancelled = false

    const boot = async () => {
      try {
        await pet.start()
        if (!cancelled) {
          sendStats(true)
          await kickoff()
        }
      } catch (error) {
        logger.error(`desktop-pet: startup failed: ${error.message}`)
      }
    }
    void boot()
    nudgeClientModules()
    startTimers()

    return async () => {
      cancelled = true
      disposed = true
      stopTimers()
      offSession?.()
      offAgentError?.()
      try { disposeApi?.() } catch { }
      for (const task of tasks.values()) clearTaskErrorTimer(task)
      tasks.clear()
      try {
        await pet.dispose()
      } catch (error) {
        logger.warn(`desktop-pet: shutdown failed: ${error.message}`)
      }
      ledger.dispose()
      logger.info('desktop-pet: plugin disposed')
    }
  }, 'desktop-pet: process, events, ledger')
}
