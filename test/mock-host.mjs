/**
 * Standalone integration harness for the host half.
 *
 * It mounts the real plugin against a tiny fake Cordis context, spawns the real
 * WPF pet, emits synthetic Session events, and then disposes.  The fake balance
 * is used only here and is labelled as test data; production always calls
 * ctx.deepseekAccount.getBalance.
 */
import { mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { apply } from '../lib/index.js'

const root = process.cwd()
const dataDir = join(root, '_research', 'pet-runtime')
mkdirSync(dataDir, { recursive: true })

const listeners = new Map()
const cleanups = []
const ctx = {
  logger: {
    info: (message) => console.log('[info]', message),
    warn: (message) => console.warn('[warn]', message),
    error: (message) => console.error('[error]', message),
    debug: (message) => console.log('[debug]', message),
  },
  sessions: {
    list: () => [],
    get: () => undefined,
  },
  sessionPersistence: {
    list: async () => [],
    open: async () => {
      throw new Error('no stored sessions in harness')
    },
  },
  deepseekAccount: {
    getBalance: async () => ({
      status: 'ready',
      value: [{ currency: 'CNY', balance: '12.34' }],
      bonusWallets: [{ currency: 'CNY', balance: '2.00' }],
    }),
  },
  on(event, handler) {
    if (!listeners.has(event)) listeners.set(event, new Set())
    listeners.get(event).add(handler)
    return () => listeners.get(event)?.delete(handler)
  },
  effect(callback) {
    const dispose = callback()
    cleanups.push(dispose)
    return dispose
  },
  emit(event, ...args) {
    for (const handler of listeners.get(event) ?? []) handler(...args)
  },
}

const config = {
  dataDir,
  debug: true,
  statsDisplay: 'always',
  volume: 0,
  timezone: 'Asia/Shanghai',
}

apply(ctx, config)

const session = {
  header: {
    id: 'session-harness',
    cwd: join(root, 'mock-workspace'),
    origin: 'main',
  },
}

function emit(type, data, time = Date.now()) {
  ctx.emit('session/event', session, { type, seq: Math.floor(Math.random() * 1e9), time, data })
}

setTimeout(() => {
  emit('turn/start', { turn: 1 })
  emit('request/context', { provider: 'deepseek-account', model: 'deepseek-flash' })
  emit('step/start', { turn: 1, step: 1 })
  emit('tool/call', { turn: 1, step: 1, callId: 'c1', name: 'pwsh', arguments: '{}' })
}, 2500)

setTimeout(() => {
  emit('assistant/message', {
    turn: 1,
    step: 1,
    message: { source: { provider: 'deepseek-account', model: 'deepseek-flash' } },
    usage: { inputTokens: 1200, outputTokens: 300, cacheReadTokens: 800, cacheWriteTokens: 0 },
  })
  emit('step/end', { turn: 1, step: 1 })
  emit('step/start', { turn: 1, step: 2 })
  emit('tool/call', { turn: 1, step: 2, callId: 'c2', name: 'todo_write', arguments: '{}' })
}, 5000)

setTimeout(() => {
  emit('tool/result', { turn: 1, step: 2, callId: 'c2', message: {}, error: undefined })
  emit('step/end', { turn: 1, step: 2 })
  emit('turn/end', { turn: 1, reason: { kind: 'completed' } })
}, 8000)

setTimeout(async () => {
  for (const dispose of cleanups.reverse()) {
    try {
      await dispose?.()
    } catch (error) {
      console.error('cleanup failed', error)
    }
  }
  process.exit(0)
}, 14_000)
