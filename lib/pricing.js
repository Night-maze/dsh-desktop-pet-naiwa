/**
 * Cost estimation and money formatting.
 *
 * Important boundary: DSH reports provider token usage, not a billed currency
 * amount.  This module multiplies real `assistant/message` /
 * `assistant/attempt` usage by the public per-million-token list price supplied
 * in configuration.  The UI labels the result as an estimate (≈).  No value is
 * invented when a model has no configured price.
 */
import { formatClock, parseClock } from './config.js'

const TOKENS_PER_MILLION = 1_000_000

const WEEKDAY_INDEX = {
  Sun: 0,
  Mon: 1,
  Tue: 2,
  Wed: 3,
  Thu: 4,
  Fri: 5,
  Sat: 6,
}

export function dateKey(time, timeZone) {
  const parts = zonedParts(time, timeZone)
  return `${parts.year}-${parts.month}-${parts.day}`
}

export function zonedParts(time, timeZone) {
  const formatter = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    weekday: 'short',
    hour12: false,
  })
  const parts = Object.fromEntries(formatter.formatToParts(new Date(time)).map((part) => [part.type, part.value]))
  let hour = Number(parts.hour)
  if (hour === 24) hour = 0
  return {
    year: parts.year,
    month: parts.month,
    day: parts.day,
    hour,
    minute: Number(parts.minute),
    second: Number(parts.second),
    weekday: WEEKDAY_INDEX[parts.weekday] ?? 0,
    date: `${parts.year}-${parts.month}-${parts.day}`,
    minutes: hour * 60 + Number(parts.minute),
  }
}

/** Local midnight at `time` in `timeZone`, as epoch milliseconds. */
export function startOfZonedDay(time, timeZone) {
  const parts = zonedParts(time, timeZone)
  const offset = timeZoneOffsetMinutes(time, timeZone)
  return Date.UTC(Number(parts.year), Number(parts.month) - 1, Number(parts.day)) - offset * 60_000
}

/** Offset east of UTC in minutes at `time`. */
export function timeZoneOffsetMinutes(time, timeZone) {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone,
    timeZoneName: 'longOffset',
  })
  const name = formatter.formatToParts(new Date(time)).find((part) => part.type === 'timeZoneName')?.value ?? 'GMT+00:00'
  const match = /^GMT([+-])(\d{1,2})(?::(\d{2}))?$/.exec(name)
  if (!match) return 0
  const sign = match[1] === '-' ? -1 : 1
  const hours = Number(match[2])
  const minutes = Number(match[3] ?? '0')
  return sign * (hours * 60 + minutes)
}

export function dayRange(time, timeZone) {
  const start = startOfZonedDay(time, timeZone)
  // Asia/Shanghai has no DST; for other zones recompute the next local
  // midnight so 23/25-hour days remain correct.
  const end = startOfZonedDay(start + 36 * 60 * 60 * 1000, timeZone)
  return { start, end }
}

export function isPeak(time, pricing, timeZone) {
  const parts = zonedParts(time, timeZone)
  if (parts.weekday === 0 || parts.weekday === 6) return false
  if (pricing.holidays.includes(parts.date)) return false
  for (const [startText, endText] of pricing.peakHours) {
    const start = parseClock(startText)
    const end = parseClock(endText)
    if (start === null || end === null) continue
    if (parts.minutes >= start && parts.minutes < end) return true
  }
  return false
}

export function resolveCurrency(pricing, balanceCurrency) {
  if (pricing.currency === 'CNY' || pricing.currency === 'USD') return pricing.currency
  if (balanceCurrency === 'CNY' || balanceCurrency === 'USD') return balanceCurrency
  return 'CNY'
}

export function normalizeModel(model) {
  if (typeof model !== 'string') return ''
  return model.trim().toLowerCase()
}

export function findPrice(model, currency, pricing) {
  const normalized = normalizeModel(model)
  const table = pricing.models[currency] ?? pricing.models.CNY ?? {}
  if (table[normalized]) return { key: normalized, entry: table[normalized] }
  // Some routes carry provider suffixes, e.g. `deepseek-flash-1017`.
  for (const key of Object.keys(table)) {
    if (normalized.startsWith(key)) return { key, entry: table[key] }
  }
  const fallback = normalizeModel(pricing.defaultModel)
  if (fallback && table[fallback]) return { key: fallback, entry: table[fallback] }
  return null
}

function entryView(entry) {
  return {
    time: Number(entry.t ?? entry.time),
    model: entry.m ?? entry.model ?? '',
    input: Number(entry.i ?? entry.input ?? 0),
    output: Number(entry.o ?? entry.output ?? 0),
    cacheRead: Number(entry.cr ?? entry.cacheRead ?? 0),
    cacheWrite: Number(entry.cw ?? entry.cacheWrite ?? 0),
  }
}

export function entryCost(entry, currency, pricing, timeZone) {
  const view = entryView(entry)
  const model = normalizeModel(view.model)
  if (model === '') return { priced: false, cost: 0, model }
  const found = findPrice(model, currency, pricing)
  if (!found) return { priced: false, cost: 0, model }
  if (!Number.isFinite(view.time)) return { priced: false, cost: 0, model }
  const peak = isPeak(view.time, pricing, timeZone)
  const rates = peak ? found.entry.peak : found.entry.offPeak
  const cacheWriteRate = Number.isFinite(rates.cacheWrite) ? rates.cacheWrite : rates.input
  const cost =
    (view.input / TOKENS_PER_MILLION) * rates.input +
    (view.cacheRead / TOKENS_PER_MILLION) * rates.cacheHit +
    (view.cacheWrite / TOKENS_PER_MILLION) * cacheWriteRate +
    (view.output / TOKENS_PER_MILLION) * rates.output
  return {
    priced: true,
    cost,
    model: found.key,
    peak,
    rates,
  }
}

export function summarizeEntries(entries, currency, pricing, timeZone) {
  let cost = 0
  let pricedTokens = 0
  let unpricedTokens = 0
  let input = 0
  let output = 0
  let cacheRead = 0
  let cacheWrite = 0
  let priced = true
  const models = new Set()
  for (const entry of Object.values(entries)) {
    const view = entryView(entry)
    input += view.input
    output += view.output
    cacheRead += view.cacheRead
    cacheWrite += view.cacheWrite
    const total = view.input + view.output + view.cacheRead + view.cacheWrite
    const result = entryCost(view, currency, pricing, timeZone)
    if (result.priced) {
      cost += result.cost
      pricedTokens += total
      models.add(result.model)
    } else {
      priced = false
      unpricedTokens += total
      if (result.model) models.add(result.model)
    }
  }
  return {
    cost,
    currency,
    priced: priced && pricedTokens > 0,
    pricedTokens,
    unpricedTokens,
    tokens: input + output + cacheRead + cacheWrite,
    input,
    output,
    cacheRead,
    cacheWrite,
    models: [...models],
  }
}

export function formatMoney(amount, currency, precision = 2) {
  try {
    return new Intl.NumberFormat('zh-CN', {
      style: 'currency',
      currency,
      minimumFractionDigits: precision,
      maximumFractionDigits: precision,
    }).format(amount)
  } catch {
    const symbol = currency === 'USD' ? '$' : '¥'
    return `${symbol}${Number(amount).toFixed(precision)}`
  }
}

export function formatTokens(tokens) {
  if (!Number.isFinite(tokens) || tokens < 0) return '—'
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(tokens >= 10_000_000 ? 0 : 1)}M`
  if (tokens >= 1_000) return `${(tokens / 1_000).toFixed(tokens >= 100_000 ? 0 : 1)}K`
  return String(Math.round(tokens))
}

export function formatClockFromMinutes(minutes) {
  return formatClock(minutes)
}
