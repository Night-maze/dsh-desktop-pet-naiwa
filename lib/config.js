/**
 * Configuration normalization for the desktop-pet host plugin.
 *
 * The plugin is loaded through Cordis, so `apply(ctx, config)` receives the
 * raw YAML config object.  We deliberately do not depend on
 * `@deepseek-ai/schemastery`: an external plugin should remain installable
 * with only Node built-ins.  Defaults, clamping, and path resolution live here.
 */
import { existsSync, mkdirSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { basename, dirname, isAbsolute, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export const PLUGIN_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
export const PLUGIN_VERSION = '1.0.0'

export const DEFAULT_INTERACTIONS = [
  {
    id: 'blink',
    weight: 1.2,
    animation: 'blink',
    sprite: 'sleepy',
    texts: ['眨眼~', '看不到我看不到我'],
  },
  {
    id: 'jump',
    weight: 1.0,
    animation: 'jump',
    sprite: 'cheer',
    texts: ['跳一下！', '嘿咻！'],
  },
  {
    id: 'shake',
    weight: 1.0,
    animation: 'shake',
    sprite: 'wave',
    texts: ['晃晃脑袋～', '晕乎乎…'],
  },
  {
    id: 'heart',
    weight: 0.8,
    animation: 'heart',
    sprite: 'heart',
    texts: ['给你一颗小心心', '最喜欢你啦'],
  },
  {
    id: 'line',
    weight: 1.4,
    animation: 'line',
    sprite: 'peace',
    texts: ['今天也要加油！', '要摸摸头吗？', '再戳就咬你哦（骗你的）', '记得喝水呀～'],
  },
    {
    id: 'line',
    weight: 1.4,
    animation: 'line',
    sprite: 'think',
    texts: ['今天也要加油！', '要摸摸头吗？', '再戳就咬你哦（骗你的）', '记得喝水呀～'],
  },
  {
    id: 'dance',
    weight: 0.6,
    animation: 'dance',
    sprite: 'cheer',
    texts: ['跳舞给你看！', '啦啦啦～'],
  },
]

export const DEFAULT_LINES = [
  '戳我干嘛呀～',
  '今天也要加油！',
  '要摸摸头吗？',
  '我可是很厉害的！',
  '再戳就咬你哦（骗你的）',
  '记得喝水呀～',
]

export const DEFAULT_TOOL_LABELS = {
  pwsh: '运行命令',
  bash: '运行命令',
  read: '看文件',
  write: '写文件',
  edit: '改代码',
  fs: '整理文件',
  web_search: '上网查资料',
  web_fetch: '打开网页',
  todo_write: '列计划',
  subagent: '叫小伙伴帮忙',
  workflow: '编排任务',
  present: '生成成果',
}

/** Official public list prices, CNY per million tokens (2026-11). */
export const DEFAULT_PRICES_CNY = {
  'deepseek-flash': {
    peak: { cacheHit: 0.04, input: 2.0, output: 8.0 },
    offPeak: { cacheHit: 0.02, input: 1.0, output: 4.0 },
  },
  'deepseek-v4-pro': {
    peak: { cacheHit: 0.3, input: 9.0, output: 27.0 },
    offPeak: { cacheHit: 0.15, input: 4.5, output: 13.5 },
  },
}

/** Official public list prices, USD per million tokens (2026-11). */
export const DEFAULT_PRICES_USD = {
  'deepseek-flash': {
    peak: { cacheHit: 0.006, input: 0.3, output: 1.2 },
    offPeak: { cacheHit: 0.003, input: 0.15, output: 0.6 },
  },
  'deepseek-v4-pro': {
    peak: { cacheHit: 0.044, input: 1.32, output: 3.96 },
    offPeak: { cacheHit: 0.022, input: 0.66, output: 1.98 },
  },
}

const DEFAULTS = {
  enabled: true,
  autoStart: true,
  imagePath: '',
  spriteDir: '',
  soundDir: '',
  scale: 0.5,
  volume: 0.7,
  topmost: true,
  shadow: true,
  avoidFullscreen: true,
  statsDisplay: 'always',
  longPressMs: 450,
  dragStartPx: 10,
  edgeThreshold: 24,
  edgeVisibleRatio: 0.38,
  edgeHideDelayMs: 750,
  edgeSlideMs: 180,
  bubbleMs: 3800,
  debug: false,
  timezone: 'Asia/Shanghai',
  locale: 'zh-CN',
  currency: 'auto',
  costPrecision: 4,
  balanceRefreshMs: 60_000,
  statsRefreshMs: 30_000,
  balanceTimeoutMs: 15_000,
  restart: {
    maxAttempts: 5,
    baseDelayMs: 1000,
    maxDelayMs: 30_000,
  },
  backfill: {
    enabled: true,
    maxSessions: 120,
    maxBytes: 8 * 1024 * 1024,
    maxTotalBytes: 64 * 1024 * 1024,
    timeoutMs: 15_000,
    concurrency: 2,
  },
  pricing: {
    currency: 'auto',
    defaultModel: 'deepseek-flash',
    peakHours: [
      ['09:00', '12:00'],
      ['14:00', '18:00'],
    ],
    holidays: [],
    models: {
      CNY: DEFAULT_PRICES_CNY,
      USD: DEFAULT_PRICES_USD,
    },
  },
  interactions: DEFAULT_INTERACTIONS,
  lines: DEFAULT_LINES,
  toolLabels: DEFAULT_TOOL_LABELS,
  protectPaths: [],
  skipSubagents: true,
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function deepMerge(base, extra) {
  if (!isPlainObject(extra)) return clone(base)
  const out = clone(base)
  for (const [key, value] of Object.entries(extra)) {
    if (isPlainObject(value) && isPlainObject(out[key])) out[key] = deepMerge(out[key], value)
    else out[key] = clone(value)
  }
  return out
}

function clone(value) {
  if (Array.isArray(value)) return value.map(clone)
  if (isPlainObject(value)) {
    const out = {}
    for (const [key, item] of Object.entries(value)) out[key] = clone(item)
    return out
  }
  return value
}

export function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value))
}

export function clampInt(value, min, max, fallback) {
  const number = Number(value)
  if (!Number.isFinite(number)) return fallback
  return Math.round(clamp(number, min, max))
}

export function dshHome() {
  const fromEnv = process.env.DSH_HOME
  if (fromEnv && fromEnv.trim() !== '') return fromEnv
  return join(homedir(), '.dsh')
}

export function resolvePath(value, base) {
  if (typeof value !== 'string' || value.trim() === '') return ''
  return isAbsolute(value) ? value : resolve(base, value)
}

export function resolveConfig(raw, options = {}) {
  const user = isPlainObject(raw) ? raw : {}
  const merged = deepMerge(DEFAULTS, user)

  const home = dshHome()
  const pluginRoot = options.pluginRoot ?? PLUGIN_ROOT
  const dataDir = resolvePath(user.dataDir, home) || join(home, 'storages', 'desktop-pet')
  mkdirSync(dataDir, { recursive: true })

  merged.enabled = merged.enabled !== false
  merged.autoStart = merged.autoStart !== false
  merged.debug = merged.debug === true
  merged.topmost = merged.topmost !== false
  merged.shadow = merged.shadow !== false
  merged.avoidFullscreen = merged.avoidFullscreen !== false
  merged.skipSubagents = merged.skipSubagents !== false
  merged.scale = clamp(Number(merged.scale) || 1, 0.5, 2)
  merged.volume = clamp(Number(merged.volume), 0, 1)
  if (!Number.isFinite(merged.volume)) merged.volume = 0.7
  merged.longPressMs = clampInt(merged.longPressMs, 120, 3000, 450)
  merged.dragStartPx = clampInt(merged.dragStartPx, 2, 40, 10)
  merged.edgeThreshold = clampInt(merged.edgeThreshold, 4, 160, 24)
  merged.edgeVisibleRatio = clamp(Number(merged.edgeVisibleRatio) || 0.38, 0.12, 0.85)
  merged.edgeHideDelayMs = clampInt(merged.edgeHideDelayMs, 100, 10_000, 750)
  merged.edgeSlideMs = clampInt(merged.edgeSlideMs, 40, 1000, 180)
  merged.bubbleMs = clampInt(merged.bubbleMs, 800, 20_000, 3800)
  merged.statsDisplay = ['always', 'hover', 'off'].includes(merged.statsDisplay)
    ? merged.statsDisplay
    : 'always'
  merged.costPrecision = clampInt(merged.costPrecision, 0, 8, 4)
  merged.balanceRefreshMs = clampInt(merged.balanceRefreshMs, 30_000, 600_000, 60_000)
  merged.statsRefreshMs = clampInt(merged.statsRefreshMs, 5_000, 300_000, 30_000)
  merged.balanceTimeoutMs = clampInt(merged.balanceTimeoutMs, 1000, 60_000, 15_000)
  merged.restart.maxAttempts = clampInt(merged.restart.maxAttempts, 1, 20, 5)
  merged.restart.baseDelayMs = clampInt(merged.restart.baseDelayMs, 250, 30_000, 1000)
  merged.restart.maxDelayMs = clampInt(merged.restart.maxDelayMs, 1000, 300_000, 30_000)
  merged.backfill.maxSessions = clampInt(merged.backfill.maxSessions, 0, 2000, 120)
  merged.backfill.maxBytes = clampInt(merged.backfill.maxBytes, 0, 256 * 1024 * 1024, 8 * 1024 * 1024)
  merged.backfill.maxTotalBytes = clampInt(merged.backfill.maxTotalBytes, 0, 1024 * 1024 * 1024, 64 * 1024 * 1024)
  merged.backfill.timeoutMs = clampInt(merged.backfill.timeoutMs, 1000, 120_000, 15_000)
  merged.backfill.concurrency = clampInt(merged.backfill.concurrency, 1, 8, 2)

  const spriteDir = resolvePath(user.spriteDir, pluginRoot) || join(pluginRoot, 'pet', 'assets', 'sprites')
  const soundDir = resolvePath(user.soundDir, pluginRoot) || join(pluginRoot, 'pet', 'assets', 'sounds')
  const imagePath = resolvePath(user.imagePath, pluginRoot)

  merged.pluginRoot = pluginRoot
  merged.dataDir = dataDir
  merged.spriteDir = spriteDir
  merged.soundDir = soundDir
  merged.imagePath = imagePath
  merged.petScript = join(pluginRoot, 'pet', 'DesktopPet.ps1')
  merged.petSource = join(pluginRoot, 'pet', 'DesktopPet.cs')
  merged.petConfigPath = join(dataDir, 'pet-config.json')
  merged.usagePath = join(dataDir, 'usage.json')
  merged.logPath = join(dataDir, 'host.log')
  merged.configPath = join(dataDir, 'pet-config.json')

  merged.interactions = normalizeInteractions(merged.interactions)
  merged.lines = normalizeStrings(merged.lines, DEFAULT_LINES)
  merged.toolLabels = { ...DEFAULT_TOOL_LABELS, ...(isPlainObject(merged.toolLabels) ? merged.toolLabels : {}) }
  merged.protectPaths = normalizeStrings(merged.protectPaths, [])
  if (merged.protectPaths.length === 0) {
    const windows = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows'
    merged.protectPaths = [
      windows,
      process.env.ProgramFiles || 'C:\\Program Files',
      process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)',
    ]
  }
  merged.currency = normalizeCurrency(merged.currency)
  merged.pricing = normalizePricing(merged.pricing, merged.currency)
  merged.currency = merged.pricing.currency
  return merged
}

function normalizeStrings(value, fallback) {
  if (!Array.isArray(value)) return [...fallback]
  const out = value.filter((item) => typeof item === 'string' && item.trim() !== '').map((item) => item.trim())
  return out.length > 0 ? out : [...fallback]
}

function normalizeInteractions(value) {
  if (!Array.isArray(value)) return clone(DEFAULT_INTERACTIONS)
  const out = []
  for (const item of value) {
    if (!isPlainObject(item) || typeof item.id !== 'string' || item.id.trim() === '') continue
    out.push({
      id: item.id.trim(),
      weight: clamp(Number(item.weight) || 1, 0, 100),
      animation: typeof item.animation === 'string' && item.animation.trim() !== '' ? item.animation.trim() : item.id.trim(),
      sprite: typeof item.sprite === 'string' ? item.sprite.trim() : '',
      texts: normalizeStrings(item.texts, []),
    })
  }
  return out.length > 0 ? out : clone(DEFAULT_INTERACTIONS)
}

function normalizeCurrency(value) {
  if (value === 'CNY' || value === 'USD' || value === 'auto') return value
  return 'auto'
}

function normalizePricing(value, currency) {
  const input = isPlainObject(value) ? value : {}
  const requested = normalizeCurrency(input.currency ?? currency)
  const models = isPlainObject(input.models) ? input.models : {}
  return {
    currency: requested,
    defaultModel: typeof input.defaultModel === 'string' ? input.defaultModel : 'deepseek-flash',
    peakHours: normalizePeakHours(input.peakHours),
    holidays: normalizeStrings(input.holidays, []),
    models: {
      CNY: deepMerge(DEFAULT_PRICES_CNY, isPlainObject(models.CNY) ? models.CNY : {}),
      USD: deepMerge(DEFAULT_PRICES_USD, isPlainObject(models.USD) ? models.USD : {}),
    },
  }
}

function normalizePeakHours(value) {
  if (!Array.isArray(value)) return [['09:00', '12:00'], ['14:00', '18:00']]
  const out = []
  for (const pair of value) {
    if (!Array.isArray(pair) || pair.length !== 2) continue
    const start = parseClock(pair[0])
    const end = parseClock(pair[1])
    if (start === null || end === null || end <= start) continue
    out.push([formatClock(start), formatClock(end)])
  }
  return out.length > 0 ? out : [['09:00', '12:00'], ['14:00', '18:00']]
}

export function parseClock(value) {
  if (typeof value !== 'string') return null
  const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim())
  if (!match) return null
  const hour = Number(match[1])
  const minute = Number(match[2])
  if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return null
  return hour * 60 + minute
}

export function formatClock(minutes) {
  const hour = Math.floor(minutes / 60)
  const minute = minutes % 60
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`
}

/** The JSON file consumed by DesktopPet.cs. */
export function buildPetConfig(config) {
  return {
    spriteDir: config.spriteDir,
    soundDir: config.soundDir,
    imagePath: config.imagePath,
    scale: config.scale,
    volume: config.volume,
    topmost: config.topmost,
    shadow: config.shadow,
    avoidFullscreen: config.avoidFullscreen,
    statsDisplay: config.statsDisplay,
    longPressMs: config.longPressMs,
    dragStartPx: config.dragStartPx,
    edgeThreshold: config.edgeThreshold,
    edgeVisibleRatio: config.edgeVisibleRatio,
    edgeHideDelayMs: config.edgeHideDelayMs,
    edgeSlideMs: config.edgeSlideMs,
    bubbleMs: config.bubbleMs,
    debug: config.debug,
    protectPaths: config.protectPaths,
    interactions: config.interactions,
    lines: config.lines,
    toolLabels: config.toolLabels,
  }
}

export function writePetConfig(config) {
  const payload = `${JSON.stringify(buildPetConfig(config), null, 2)}\n`
  const temp = `${config.petConfigPath}.${process.pid}.tmp`
  writeFileSync(temp, payload, { encoding: 'utf8', mode: 0o600 })
  try {
    renameSync(temp, config.petConfigPath)
  } catch (error) {
    // Windows can briefly hold a target open; the config is small and the
    // pet reloads on restart, so a direct write is an acceptable fallback.
    writeFileSync(config.petConfigPath, payload, { encoding: 'utf8', mode: 0o600 })
  }
}

export function fileExists(path) {
  try {
    return path !== '' && existsSync(path)
  } catch {
    return false
  }
}

export function basenameOf(path) {
  try {
    return basename(path)
  } catch {
    return ''
  }
}
