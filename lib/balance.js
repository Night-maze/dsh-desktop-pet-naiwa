/**
 * Real account balance reads.
 *
 * `ctx.deepseekAccount.getBalance` returns the Platform wallet projection; it
 * is never replaced by a zero or a guess.  A failure keeps the previous value
 * but marks it stale, and a signed-out account shows `—`.
 */
import { formatMoney } from './pricing.js'

export class BalanceService {
  constructor(ctx, config, logger) {
    this.ctx = ctx
    this.config = config
    this.logger = logger
    this.inFlight = null
    this.snapshot = {
      available: false,
      status: 'unknown',
      text: '—',
      detail: '余额尚未读取',
      wallets: [],
      currency: null,
      updatedAt: 0,
      stale: false,
    }
    this.failures = 0
    this.nextAt = 0
  }

  shouldRefresh(now = Date.now()) {
    return now >= this.nextAt && !this.inFlight
  }

  async refresh(force = false) {
    const now = Date.now()
    if (this.inFlight) return this.inFlight
    if (!force && now < this.nextAt) return this.snapshot
    const account = this.ctx.deepseekAccount
    if (!account || typeof account.getBalance !== 'function') {
      this.fail('账户服务不可用')
      return this.snapshot
    }
    this.inFlight = this.read(account).finally(() => {
      this.inFlight = null
    })
    return this.inFlight
  }

  async read(account) {
    const client = {
      version: process.env.DSH_CLIENT_VERSION || '1.0.0',
      locale: this.config.locale === 'zh-CN' ? 'zh-CN' : 'en-US',
      timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
    }
    try {
      const result = await withTimeout(account.getBalance(client), this.config.balanceTimeoutMs)
      if (result === null || result === undefined) {
        this.snapshot = {
          available: false,
          status: 'signed-out',
          text: '—',
          detail: '未登录 DeepSeek 账户',
          wallets: [],
          currency: null,
          updatedAt: Date.now(),
          stale: false,
        }
        this.failures = 0
        this.nextAt = Date.now() + this.config.balanceRefreshMs
        return this.snapshot
      }
      if (result.status !== 'ready') {
        this.fail('平台余额读取失败')
        return this.snapshot
      }
      this.apply(result)
      this.failures = 0
      this.nextAt = Date.now() + this.config.balanceRefreshMs
      return this.snapshot
    } catch (error) {
      this.fail(error.message || '平台余额读取超时')
      return this.snapshot
    }
  }

  apply(result) {
    const normal = Array.isArray(result.value) ? result.value : []
    const bonus = Array.isArray(result.bonusWallets) ? result.bonusWallets : []
    const byCurrency = new Map()
    for (const wallet of normal) {
      if (!wallet || typeof wallet.currency !== 'string') continue
      const amount = Number(wallet.balance)
      if (!Number.isFinite(amount)) continue
      const current = byCurrency.get(wallet.currency) ?? { currency: wallet.currency, normal: 0, bonus: 0 }
      current.normal += amount
      byCurrency.set(wallet.currency, current)
    }
    for (const wallet of bonus) {
      if (!wallet || typeof wallet.currency !== 'string') continue
      const amount = Number(wallet.balance)
      if (!Number.isFinite(amount)) continue
      const current = byCurrency.get(wallet.currency) ?? { currency: wallet.currency, normal: 0, bonus: 0 }
      current.bonus += amount
      byCurrency.set(wallet.currency, current)
    }
    const wallets = [...byCurrency.values()].map((wallet) => ({
      ...wallet,
      total: wallet.normal + wallet.bonus,
      text: formatMoney(wallet.normal + wallet.bonus, wallet.currency, 2),
    }))
    let primary = wallets.find((wallet) => wallet.currency === this.config.currency)
    if (!primary && this.config.currency === 'auto') primary = wallets[0]
    if (!primary && wallets.length > 0) primary = wallets[0]
    if (!primary) {
      this.snapshot = {
        available: false,
        status: 'empty',
        text: '—',
        detail: '账户没有可用钱包',
        wallets,
        currency: null,
        updatedAt: Date.now(),
        stale: false,
      }
      return
    }
    const detailParts = []
    if (primary.normal !== 0 || primary.bonus !== 0) {
      detailParts.push(`充值 ${formatMoney(primary.normal, primary.currency, 2)}`)
      if (primary.bonus !== 0) detailParts.push(`赠送 ${formatMoney(primary.bonus, primary.currency, 2)}`)
    }
    for (const wallet of wallets) {
      if (wallet.currency !== primary.currency) detailParts.push(wallet.text)
    }
    this.snapshot = {
      available: true,
      status: 'ready',
      text: primary.text,
      detail: detailParts.join(' · '),
      wallets,
      currency: primary.currency,
      updatedAt: Date.now(),
      stale: false,
    }
  }

  fail(message) {
    this.failures += 1
    const delay = Math.min(this.config.balanceRefreshMs * 2 ** Math.min(this.failures - 1, 3), 5 * 60_000)
    this.nextAt = Date.now() + delay
    this.snapshot = {
      ...this.snapshot,
      stale: this.snapshot.available,
      detail: message,
      updatedAt: this.snapshot.available ? this.snapshot.updatedAt : 0,
    }
    this.logger?.warn?.(`desktop-pet: balance unavailable: ${message}`)
  }

  getSnapshot() {
    return this.snapshot
  }
}

function withTimeout(promise, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`账户余额请求超时（${timeoutMs}ms）`)), timeoutMs)
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
