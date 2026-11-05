/**
 * Lifecycle and authenticated loopback transport for the WPF pet process.
 *
 * The pet is a separate OS process because DSH client plugins are browser-side
 * sandboxed UI and cannot create transparent native windows.  The host plugin
 * owns the process and speaks newline-delimited JSON over 127.0.0.1; the first
 * line must carry a random per-launch token.
 */
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { randomBytes, timingSafeEqual } from 'node:crypto'
import { createInterface } from 'node:readline'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { writePetConfig } from './config.js'

const HELLO_TIMEOUT_MS = 15_000
const PONG_TIMEOUT_MS = 35_000

export class PetProcess {
  constructor(config, logger, hooks = {}) {
    this.config = config
    this.logger = logger
    this.hooks = hooks
    this.server = null
    this.socket = null
    this.reader = null
    this.child = null
    this.port = 0
    this.token = ''
    this.started = false
    this.ready = false
    this.stopping = false
    this.restartTimer = null
    this.attempts = 0
    this.lastPong = 0
    this.lastPing = 0
    this.lastSocketAt = 0
    this.lastExitReason = ''
    // Every start/stop advances a generation.  Late socket-close or child-exit
    // events from the previous process must not schedule another restart.
    this.generation = 0
  }

  async start() {
    if (this.started) return
    this.generation += 1
    this.started = true
    this.stopping = false
    this.attempts = 0
    this.token = randomBytes(24).toString('hex')
    writePetConfig(this.config)
    await this.listen()
    this.spawnPet()
    this.logger?.info?.(`desktop-pet: pet process starting on 127.0.0.1:${this.port}`)
  }

  listen() {
    return new Promise((resolve, reject) => {
      const server = createServer((socket) => this.accept(socket))
      server.once('error', (error) => {
        this.logger?.error?.(`desktop-pet: bridge listen failed: ${error.message}`)
        reject(error)
      })
      server.listen(0, '127.0.0.1', () => {
        this.server = server
        this.port = server.address().port
        resolve()
      })
    })
  }

  spawnPet() {
    if (this.stopping) return
    if (!existsSync(this.config.petScript)) {
      this.logger?.error?.(`desktop-pet: missing pet script ${this.config.petScript}`)
      this.scheduleRestart('missing script')
      return
    }
    const shell = powershellPath()
    const args = [
      '-NoProfile',
      '-STA',
      '-ExecutionPolicy',
      'Bypass',
      '-File',
      this.config.petScript,
      '-ConfigPath',
      this.config.petConfigPath,
      '-Port',
      String(this.port),
      '-Token',
      this.token,
      '-DataDir',
      this.config.dataDir,
    ]
    this.logger?.info?.(`desktop-pet: launching ${shell}`)
    let child
    try {
      child = spawn(shell, args, {
        windowsHide: true,
        // The pet writes its own launcher/pet logs.  Ignoring stdio also keeps
        // the host compatible with confined launchers that cannot create
        // anonymous pipe handles for a child process.
        stdio: 'ignore',
        env: {
          ...process.env,
          DSH_PET_DATA_DIR: this.config.dataDir,
        },
      })
    } catch (error) {
      this.logger?.error?.(`desktop-pet: spawn failed: ${error.message}`)
      this.scheduleRestart('spawn failed')
      return
    }
    this.child = child
    this.ready = false
    this.lastPong = Date.now()
    const generation = this.generation
    child.on('error', (error) => {
      this.logger?.error?.(`desktop-pet: child error: ${error.message}`)
    })
    child.on('exit', (code, signal) => {
      this.lastExitReason = signal ? `signal ${signal}` : `exit ${code}`
      this.logger?.warn?.(`desktop-pet: pet exited (${this.lastExitReason})`)
      if (generation !== this.generation) return
      this.child = null
      this.ready = false
      this.closeSocket()
      if (!this.stopping) this.scheduleRestart(this.lastExitReason)
    })
  }

  logChild(stream, chunk) {
    const text = String(chunk).trim()
    if (!text) return
    for (const line of text.split(/\r?\n/)) {
      if (line.trim() === '') continue
      if (this.config.debug) this.logger?.info?.(`desktop-pet[${stream}]: ${line}`)
      else this.logger?.debug?.(`desktop-pet[${stream}]: ${line}`)
    }
  }

  accept(socket) {
    if (this.stopping) {
      socket.destroy()
      return
    }
    if (this.socket) {
      // Only one pet window may attach.  A stale process reconnecting after a
      // crash is replaced by the restart path, not by a second socket.
      socket.destroy()
      return
    }
    this.socket = socket
    this.lastSocketAt = Date.now()
    const generation = this.generation
    socket.setNoDelay(true)
    socket.setTimeout(HELLO_TIMEOUT_MS, () => {
      this.logger?.warn?.('desktop-pet: handshake timed out')
      socket.destroy()
    })
    const reader = createInterface({ input: socket, crlfDelay: Infinity })
    let helloTimer = setTimeout(() => {
      this.logger?.warn?.('desktop-pet: no hello received')
      socket.destroy()
    }, HELLO_TIMEOUT_MS)
    helloTimer.unref?.()

    const onFirstLine = (line) => {
      clearTimeout(helloTimer)
      helloTimer = null
      let hello
      try {
        hello = JSON.parse(line)
      } catch {
        socket.destroy()
        return
      }
      if (!hello || hello.type !== 'hello' || !validToken(hello.token, this.token)) {
        this.logger?.warn?.('desktop-pet: rejected an unauthenticated connection')
        socket.destroy()
        return
      }
      this.reader = reader
      socket.setTimeout(0)
      reader.on('line', (next) => this.onLine(next))
      socket.on('error', (error) => {
        this.logger?.warn?.(`desktop-pet: bridge socket error: ${error.message}`)
      })
      socket.on('close', () => {
        if (this.socket === socket) {
          this.socket = null
          this.reader = null
          this.ready = false
        }
        if (generation !== this.generation) return
        if (!this.stopping) this.scheduleRestart('bridge closed')
      })
      this.lastPong = Date.now()
      this.send({
        type: 'init',
        protocolVersion: 1,
        pluginVersion: '1.0.0',
        config: {
          statsDisplay: this.config.statsDisplay,
          debug: this.config.debug,
        },
      })
    }
    reader.once('line', onFirstLine)
  }

  onLine(line) {
    if (!line) return
    let message
    try {
      message = JSON.parse(line)
    } catch {
      this.logger?.warn?.('desktop-pet: invalid JSON from pet')
      return
    }
    if (!message || typeof message !== 'object') return
    const type = message.type
    if (type === 'ready') {
      this.ready = true
      this.attempts = 0
      this.lastPong = Date.now()
      this.logger?.info?.('desktop-pet: pet window ready')
      this.hooks.onReady?.()
      return
    }
    if (type === 'pong') {
      this.lastPong = Date.now()
      return
    }
    if (type === 'request') {
      this.hooks.onRequest?.(typeof message.action === 'string' ? message.action : '')
      return
    }
    if (type === 'log') {
      const level = typeof message.level === 'string' ? message.level : 'info'
      const text = typeof message.message === 'string' ? message.message : ''
      if (level === 'error') this.logger?.error?.(`desktop-pet: ${text}`)
      else if (level === 'warn') this.logger?.warn?.(`desktop-pet: ${text}`)
      else this.logger?.info?.(`desktop-pet: ${text}`)
      return
    }
    if (type === 'file_result') {
      const moved = Number(message.moved) || 0
      const failures = Array.isArray(message.failures) ? message.failures : []
      this.logger?.info?.(`desktop-pet: recycled ${moved} item(s), ${failures.length} failure(s)`)
      if (failures.length > 0) this.logger?.warn?.(`desktop-pet: recycle failures: ${failures.join(' | ')}`)
      return
    }
    if (type === 'bye') {
      this.logger?.info?.('desktop-pet: pet said goodbye')
    }
  }

  send(message) {
    if (!this.socket || this.socket.destroyed || !this.socket.writable) return false
    try {
      this.socket.write(`${JSON.stringify(message)}\n`)
      return true
    } catch (error) {
      this.logger?.warn?.(`desktop-pet: send failed: ${error.message}`)
      return false
    }
  }

  healthCheck() {
    if (this.stopping) return
    if (!this.child || this.child.exitCode !== null || this.child.signalCode !== null) {
      this.child = null
      this.closeSocket()
      this.scheduleRestart('child gone')
      return
    }
    if (!this.socket || !this.ready) {
      // A killed WPF process normally emits `exit`; this is the fallback for
      // a lost bridge or a launcher that could not attach within one window.
      if (!this.lastSocketAt || Date.now() - this.lastSocketAt > 20_000) {
        this.killChild()
        this.scheduleRestart('bridge absent')
      }
      return
    }
    const now = Date.now()
    if (now - this.lastPing > 10_000) {
      this.lastPing = now
      this.send({ type: 'ping', t: String(now) })
    }
    if (now - this.lastPong > PONG_TIMEOUT_MS) {
      this.logger?.warn?.('desktop-pet: heartbeat lost; restarting pet')
      this.scheduleRestart('heartbeat lost', true)
    }
  }

  scheduleRestart(reason, force = false) {
    if (this.stopping || this.restartTimer) return
    this.attempts += 1
    if (!force && this.attempts > this.config.restart.maxAttempts) {
      this.logger?.error?.(`desktop-pet: giving up after ${this.config.restart.maxAttempts} restart attempts (${reason})`)
      return
    }
    const delay = Math.min(
      this.config.restart.baseDelayMs * 2 ** Math.min(this.attempts - 1, 5),
      this.config.restart.maxDelayMs,
    )
    this.logger?.warn?.(`desktop-pet: restarting companion in ${delay}ms (${reason})`)
    const generation = this.generation
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null
      if (this.stopping || generation !== this.generation) return
      this.closeSocket()
      this.killChild()
      this.spawnPet()
    }, delay)
    this.restartTimer.unref?.()
  }

  closeSocket() {
    try {
      this.reader?.close()
    } catch {
      // readline close is idempotent enough; a closed stream is fine.
    }
    this.reader = null
    try {
      this.socket?.destroy()
    } catch {
      // ignore
    }
    this.socket = null
    this.ready = false
  }

  killChild() {
    const child = this.child
    this.child = null
    if (!child || child.killed) return
    try {
      child.kill()
    } catch {
      // TerminateProcess failures are retried by the taskkill fallback.
    }
    const pid = child.pid
    if (!pid) return
    setTimeout(() => {
      try {
        if (child.exitCode === null && child.signalCode === null) {
          const killer = spawn('taskkill.exe', ['/pid', String(pid), '/t', '/f'], {
            windowsHide: true,
            stdio: 'ignore',
          })
          killer.unref?.()
        }
      } catch {
        // The process may already be gone.
      }
    }, 1200).unref?.()
  }

  async stop() {
    if (!this.started) return
    this.generation += 1
    this.stopping = true
    this.started = false
    if (this.restartTimer) {
      clearTimeout(this.restartTimer)
      this.restartTimer = null
    }
    this.send({ type: 'shutdown' })
    await delay(500)
    this.closeSocket()
    this.killChild()
    if (this.server) {
      await new Promise((resolve) => {
        try {
          this.server.close(() => resolve())
        } catch {
          resolve()
        }
      })
      this.server = null
    }
    this.port = 0
    this.ready = false
    this.logger?.info?.('desktop-pet: pet process stopped')
  }

  async restart() {
    this.logger?.info?.('desktop-pet: restart requested')
    await this.stop()
    this.stopping = false
    this.attempts = 0
    await this.start()
  }

  async dispose() {
    await this.stop()
  }
}

export function powershellPath() {
  const root = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows'
  const candidate = join(root, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
  return existsSync(candidate) ? candidate : 'powershell.exe'
}

function validToken(received, expected) {
  if (typeof received !== 'string' || typeof expected !== 'string') return false
  const left = Buffer.from(received)
  const right = Buffer.from(expected)
  if (left.length !== right.length) return false
  try {
    return timingSafeEqual(left, right)
  } catch {
    return false
  }
}

function delay(ms) {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms)
    timer.unref?.()
  })
}
