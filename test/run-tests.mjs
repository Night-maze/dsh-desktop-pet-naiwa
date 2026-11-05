/**
 * Zero-dependency unit tests for the host half.
 *
 * Run: node test/run-tests.mjs
 * The test data directory lives under the plugin's _research folder and is
 * never a user document.
 */
import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { resolveConfig } from '../lib/config.js'
import {
  dateKey,
  dayRange,
  formatMoney,
  formatTokens,
  isPeak,
  startOfZonedDay,
  summarizeEntries,
  zonedParts,
} from '../lib/pricing.js'
import { UsageLedger, lastStreamUsage, normalizeUsage } from '../lib/usage.js'

const root = process.cwd()
const tempRoot = join(root, '_research')
mkdirSync(tempRoot, { recursive: true })
const dataDir = mkdtempSync(join(tempRoot, 'unit-'))

function test(name, fn) {
  try {
    fn()
    console.log(`ok - ${name}`)
  } catch (error) {
    console.error(`FAIL - ${name}`)
    console.error(error)
    process.exitCode = 1
  }
}

function fakeLogger() {
  return { info() {}, warn() {}, error() {}, debug() {} }
}

const config = resolveConfig({
  dataDir,
  timezone: 'Asia/Shanghai',
  pricing: {
    currency: 'CNY',
    peakHours: [['09:00', '12:00'], ['14:00', '18:00']],
    holidays: [],
  },
})

test('config clamps gesture and refresh values', () => {
  const clamped = resolveConfig({
    dataDir,
    longPressMs: 1,
    dragStartPx: 500,
    edgeVisibleRatio: 99,
    balanceRefreshMs: 1,
    statsRefreshMs: 1,
  })
  assert.equal(clamped.longPressMs, 120)
  assert.equal(clamped.dragStartPx, 40)
  assert.equal(clamped.edgeVisibleRatio, 0.85)
  assert.equal(clamped.balanceRefreshMs, 30_000)
  assert.equal(clamped.statsRefreshMs, 5_000)
})

test('normalizeUsage rejects malformed counts', () => {
  assert.equal(normalizeUsage(null), null)
  assert.equal(normalizeUsage({ inputTokens: -1, outputTokens: 1 }), null)
  assert.deepEqual(
    normalizeUsage({ inputTokens: 1, outputTokens: 2, cacheReadTokens: 3, cacheWriteTokens: 4 }),
    { input: 1, output: 2, cacheRead: 3, cacheWrite: 4 },
  )
})

test('lastStreamUsage finds the newest usage chunk', () => {
  const usage = { inputTokens: 1, outputTokens: 2 }
  const stream = [{ type: 'text-delta' }, { chunk: { type: 'usage', usage } }]
  assert.equal(lastStreamUsage(stream), usage)
})

test('timezone day and peak windows are correct', () => {
  const noon = new Date('2026-11-03T04:00:00Z').getTime() // 12:00 Beijing
  const parts = zonedParts(noon, 'Asia/Shanghai')
  assert.equal(parts.hour, 12)
  assert.equal(parts.weekday, 2)
  assert.equal(dateKey(noon, 'Asia/Shanghai'), '2026-11-03')
  assert.equal(isPeak(new Date('2026-11-03T03:00:00Z').getTime(), config.pricing, 'Asia/Shanghai'), true) // 11:00
  assert.equal(isPeak(new Date('2026-11-03T05:00:00Z').getTime(), config.pricing, 'Asia/Shanghai'), false) // 13:00
  assert.equal(isPeak(new Date('2026-11-03T04:00:00Z').getTime(), config.pricing, 'Asia/Shanghai'), false) // 12:00 exclusive
  assert.equal(isPeak(new Date('2026-11-07T03:00:00Z').getTime(), config.pricing, 'Asia/Shanghai'), false) // Saturday
  const start = startOfZonedDay(noon, 'Asia/Shanghai')
  assert.equal(new Date(start).toISOString(), '2026-11-02T16:00:00.000Z')
  const range = dayRange(noon, 'Asia/Shanghai')
  assert.equal(range.end - range.start, 24 * 60 * 60 * 1000)
})

test('cost uses peak/off-peak list prices and never invents a price', () => {
  const peakTime = new Date('2026-11-03T03:00:00Z').getTime() // 11:00 Beijing
  const offPeakTime = new Date('2026-11-03T05:00:00Z').getTime() // 13:00 Beijing
  const entry = { t: peakTime, m: 'deepseek-flash', i: 1_000_000, o: 0, cr: 0, cw: 0 }
  const peak = summarizeEntries({ a: entry }, 'CNY', config.pricing, 'Asia/Shanghai')
  assert.equal(Math.round(peak.cost * 100) / 100, 2)
  const offPeak = summarizeEntries({ a: { ...entry, t: offPeakTime } }, 'CNY', config.pricing, 'Asia/Shanghai')
  assert.equal(Math.round(offPeak.cost * 100) / 100, 1)
  const unknown = summarizeEntries({ a: { ...entry, m: '' } }, 'CNY', config.pricing, 'Asia/Shanghai')
  assert.equal(unknown.priced, false)
})

test('money and token formatting is stable', () => {
  assert.match(formatMoney(0.1234, 'CNY', 4), /0\.1234/)
  assert.equal(formatTokens(999), '999')
  assert.equal(formatTokens(1_500), '1.5K')
  assert.equal(formatTokens(1_500_000), '1.5M')
})

test('ledger dedupes live events and replaces an attempt in one step', () => {
  const ledger = new UsageLedger(config, fakeLogger())
  const now = new Date('2026-11-03T05:00:00Z').getTime() // 13:00 Beijing
  const sid = 's1'
  ledger.ingest(sid, { type: 'request/context', seq: 0, time: now, data: { provider: 'p', model: 'deepseek-flash' } })
  ledger.ingest(sid, { type: 'assistant/attempt', seq: 1, time: now, data: { turn: 1, step: 1, stream: [] , usage: { inputTokens: 10, outputTokens: 5 } } })
  ledger.ingest(sid, { type: 'assistant/message', seq: 2, time: now, data: { turn: 1, step: 1, usage: { inputTokens: 20, outputTokens: 15 }, message: { source: { provider: 'p', model: 'deepseek-flash' } } } })
  const summary = ledger.summary('CNY', now)
  assert.equal(summary.entryCount, 1)
  assert.equal(summary.input, 20)
  assert.equal(summary.output, 15)
  ledger.dispose()
})

test('ledger keeps retried attempts only after retry-started', () => {
  const ledger = new UsageLedger(config, fakeLogger())
  const now = new Date('2026-11-03T05:00:00Z').getTime()
  const sid = 's2'
  ledger.ingest(sid, { type: 'request/context', seq: 0, time: now, data: { provider: 'p', model: 'deepseek-flash' } })
  ledger.ingest(sid, { type: 'assistant/attempt', seq: 1, time: now, data: { turn: 1, step: 1, usage: { inputTokens: 10, outputTokens: 5 } } })
  ledger.ingest(sid, { type: 'llm/retry-started', seq: 2, time: now, data: { turn: 1, step: 1 } })
  ledger.ingest(sid, { type: 'assistant/message', seq: 3, time: now, data: { turn: 1, step: 1, usage: { inputTokens: 20, outputTokens: 15 }, message: { source: { provider: 'p', model: 'deepseek-flash' } } } })
  const summary = ledger.summary('CNY', now)
  assert.equal(summary.entryCount, 2)
  assert.equal(summary.input, 30)
  assert.equal(summary.output, 20)
  ledger.dispose()
})

// Best-effort cleanup of this test's own artifact.  It is not a user document.
try {
  rmSync(dataDir, { recursive: true, force: true, maxRetries: 3 })
} catch {
  // A locked Windows file must not turn a passing test red.
}

if (process.exitCode) {
  console.error('unit tests failed')
  process.exit(process.exitCode)
}
console.log('all unit tests passed')
