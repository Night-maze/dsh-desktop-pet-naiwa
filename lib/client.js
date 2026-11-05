/**
 * Browser half of the desktop-pet bundle.
 *
 * This file is served by dsh-client-modules as the package's `./client` entry.
 * It is deliberately a hand-written lazy-CJS module (the same
 * window.__ModuleLoader__.load/factory shape the official client packages
 * compile to), so the plugin can be developed without a separate client build.
 *
 * It adds:
 *  - a "桌宠" row in the sidebar's `sidebar.panellist`;
 *  - a root `main` panel keyed `desktop-pet`;
 *  - a small settings form that talks to the Host plugin through the
 *    loopback-only `/desktop-pet-api` route.
 */
window.__ModuleLoader__.load({
  id: 'dsh-desktop-pet-naiwa',
  factory: (require) => {
    var module = { exports: {} };
    var exports = module.exports;

    const React = require('react')
    const h = React.createElement

    const PANEL_ID = 'desktop-pet'
    const API = '/desktop-pet-api'

    const palette = {
      text: 'var(--dsw-alias-label-primary, #f2f3f5)',
      secondary: 'var(--dsw-alias-label-secondary, #a8acb8)',
      tertiary: 'var(--dsw-alias-label-tertiary, #7d818c)',
      border: 'var(--dsw-alias-border-l2, rgba(255,255,255,.1))',
      card: 'var(--dsw-alias-bg-secondary, #17181f)',
      cardStroke: 'var(--dsw-alias-settings-card-stroke, rgba(255,255,255,.12))',
      accent: 'var(--dsw-alias-state-business-primary, #6b8cff)',
      danger: 'var(--dsw-alias-state-error-primary, #ff6b6b)',
      success: 'var(--dsw-alias-state-success-primary, #43c58a)',
    }

    function apiBase() {
      // Desktop pages are served from dsh-app://app.  Electron forwards
      // non-asset paths from that custom protocol to the Host HTTP server and
      // injects the Host cookie, so a relative request is the authenticated
      // path.  Fetching streamBaseUrl directly would be cross-origin and fails
      // CORS with "Failed to fetch".
      if (typeof location !== 'undefined' && location.protocol === 'dsh-app:') return API
      if (typeof location !== 'undefined' && (location.protocol === 'http:' || location.protocol === 'https:')) {
        return `${location.origin}${API}`
      }
      const transport = typeof window === 'undefined' ? undefined : window.__DSH_TRANSPORT__
      if (transport && typeof transport.streamBaseUrl === 'string' && transport.streamBaseUrl.length > 0) {
        return `${transport.streamBaseUrl.replace(/\/$/, '')}${API}`
      }
      return API
    }

    function reportClient(value) {
      try {
        void fetch(`${apiBase()}/client-log`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(value),
        })
      } catch {
        // Diagnostics must never break the panel.
      }
    }

    if (typeof window !== 'undefined') {
      window.addEventListener('error', (event) => {
        reportClient({ kind: 'error', message: event.message, stack: event.error?.stack })
      })
      window.addEventListener('unhandledrejection', (event) => {
        reportClient({ kind: 'rejection', message: String(event.reason?.message ?? event.reason), stack: event.reason?.stack })
      })
    }

    async function api(path, body) {
      const response = await fetch(`${apiBase()}${path}`, {
        method: body === undefined ? 'GET' : 'POST',
        credentials: 'same-origin',
        headers: body === undefined ? {} : { 'content-type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      })
      let payload
      try {
        payload = await response.json()
      } catch {
        throw new Error(`HTTP ${response.status}（接口返回的不是 JSON）`)
      }
      if (!response.ok || payload?.ok === false) {
        throw new Error(payload?.error || `HTTP ${response.status}`)
      }
      return payload
    }

    function Field(props) {
      return h(
        'label',
        {
          style: {
            display: 'grid',
            gridTemplateColumns: '140px minmax(0, 1fr) 56px',
            alignItems: 'center',
            gap: 10,
            padding: '10px 0',
            borderBottom: `0.5px solid ${palette.border}`,
          },
        },
        h('span', { style: { color: palette.text, fontSize: 13 } }, props.label),
        props.children,
        h('span', { style: { color: palette.tertiary, fontSize: 12, textAlign: 'right' } }, props.value),
      )
    }

    function Button(props) {
      return h(
        'button',
        {
          type: 'button',
          disabled: props.disabled,
          onClick: props.onClick,
          style: {
            height: 32,
            padding: '0 14px',
            borderRadius: 8,
            border: `0.5px solid ${props.primary ? palette.accent : palette.border}`,
            background: props.primary ? palette.accent : 'transparent',
            color: props.primary ? '#fff' : palette.text,
            cursor: props.disabled ? 'default' : 'pointer',
            opacity: props.disabled ? 0.55 : 1,
            fontSize: 13,
          },
        },
        props.label,
      )
    }

    function PetPanel() {
      const [data, setData] = React.useState(null)
      const [error, setError] = React.useState('')
      const [loading, setLoading] = React.useState(true)
      const [busy, setBusy] = React.useState('')
      const [message, setMessage] = React.useState('')
      const [draft, setDraft] = React.useState({
        scale: 0.8,
        volume: 0.7,
        statsDisplay: 'always',
        longPressMs: 450,
        dragStartPx: 10,
      })

      const load = React.useCallback(async () => {
        setLoading(true)
        setError('')
        try {
          const next = await api('/config')
          setData(next)
          if (next?.config) {
            setDraft({
              scale: next.config.scale,
              volume: next.config.volume,
              statsDisplay: next.config.statsDisplay,
              longPressMs: next.config.longPressMs,
              dragStartPx: next.config.dragStartPx,
            })
          }
        } catch (reason) {
          setError(reason.message)
        } finally {
          setLoading(false)
        }
      }, [])

      React.useEffect(() => {
        void load()
      }, [load])

      const save = async () => {
        setBusy('save')
        setMessage('')
        try {
          const next = await api('/config', draft)
          setData(next)
          setMessage('已应用；桌宠已按新配置重新启动。')
        } catch (reason) {
          setMessage(`保存失败：${reason.message}`)
        } finally {
          setBusy('')
        }
      }

      const run = async (action) => {
        setBusy(action)
        setMessage('')
        try {
          const next = await api('/action', { action })
          setData(next)
          if (action === 'open_readme') setMessage('已用记事本打开完整配置说明。')
          else if (action === 'refresh') setMessage('余额与统计已刷新。')
          else if (action === 'restart') setMessage('桌宠已重启。')
          else setMessage('操作已执行。')
        } catch (reason) {
          setMessage(`操作失败：${reason.message}`)
        } finally {
          setBusy('')
        }
      }

      const stats = data?.stats
      const pet = data?.pet

      return h(
        'div',
        {
          style: {
            boxSizing: 'border-box',
            padding: '28px 32px 40px',
            maxWidth: 780,
            margin: '0 auto',
            color: palette.text,
            fontFamily: 'var(--dsw-font-family, "Microsoft YaHei UI", system-ui, sans-serif)',
          },
        },
        h('h1', { style: { margin: '0 0 6px', fontSize: 22, fontWeight: 600 } }, '桌面宠物'),
        h(
          'p',
          { style: { margin: '0 0 20px', color: palette.secondary, fontSize: 13, lineHeight: 1.6 } },
          '这是桌宠的 Host 插件设置页。保存后会重启 WPF 桌宠子进程；更多高级配置（互动台词、价格表、保护路径等）仍在 profile 的 cordis.patch.yml 中。',
        ),
        h(
          'div',
          {
            style: {
              display: 'flex',
              gap: 8,
              flexWrap: 'wrap',
              marginBottom: 18,
            },
          },
          h(Button, { label: busy === 'refresh' ? '刷新中…' : '刷新统计', onClick: () => run('refresh'), disabled: Boolean(busy) }),
          h(Button, { label: busy === 'restart' ? '重启中…' : '重启桌宠', onClick: () => run('restart'), disabled: Boolean(busy) }),
          h(Button, { label: '显示', onClick: () => run('show'), disabled: Boolean(busy) }),
          h(Button, { label: '隐藏', onClick: () => run('hide'), disabled: Boolean(busy) }),
          h(Button, { label: '打开配置说明', onClick: () => run('open_readme'), disabled: Boolean(busy) }),
        ),
        error
          ? h(
              'div',
              {
                style: {
                  padding: '10px 12px',
                  marginBottom: 16,
                  borderRadius: 8,
                  color: palette.danger,
                  background: 'rgba(255,80,80,.08)',
                  border: `0.5px solid rgba(255,80,80,.3)`,
                  fontSize: 13,
                  whiteSpace: 'pre-wrap',
                },
              },
              `${error}\n请求地址：${apiBase()}/config\n页面：${typeof location === 'undefined' ? 'unknown' : location.href}\n如果仍失败，请刷新页面；桌面版会通过 dsh-app://app 的协议转发到 Host。`,
            )
          : null,
        h(
          'div',
          {
            style: {
              padding: '14px 18px',
              marginBottom: 18,
              borderRadius: 12,
              background: palette.card,
              border: `0.5px solid ${palette.cardStroke}`,
            },
          },
          h(
            'div',
            { style: { display: 'flex', justifyContent: 'space-between', gap: 12, alignItems: 'baseline' } },
            h('strong', { style: { fontSize: 14 } }, '当前状态'),
            h(
              'span',
              { style: { color: pet?.ready ? palette.success : palette.tertiary, fontSize: 12 } },
              pet?.ready ? '桌宠已连接' : '桌宠未连接',
            ),
          ),
          h(
            'div',
            { style: { marginTop: 8, fontSize: 13, lineHeight: 1.7 } },
            h('div', null, stats?.line ?? '今日 —'),
            h('div', { style: { color: palette.secondary } }, stats?.balance ?? '余额 —'),
            h(
              'div',
              { style: { color: palette.tertiary, fontSize: 12, marginTop: 4, whiteSpace: 'pre-wrap' } },
              stats?.detail ?? '统计数据尚未返回',
            ),
          ),
        ),
        h(
          'div',
          {
            style: {
              padding: '4px 18px 16px',
              borderRadius: 12,
              background: palette.card,
              border: `0.5px solid ${palette.cardStroke}`,
            },
          },
          h('h2', { style: { fontSize: 15, margin: '12px 0 0' } }, '外观与交互'),
          h(
            Field,
            { label: '整体缩放', value: `${Number(draft.scale).toFixed(2)}×` },
            h('input', {
              type: 'range',
              min: 0.5,
              max: 2,
              step: 0.05,
              value: draft.scale,
              onChange: (event) => setDraft({ ...draft, scale: Number(event.target.value) }),
              style: { width: '100%', accentColor: palette.accent },
            }),
          ),
          h(
            Field,
            { label: '音效音量', value: `${Math.round(Number(draft.volume) * 100)}%` },
            h('input', {
              type: 'range',
              min: 0,
              max: 1,
              step: 0.05,
              value: draft.volume,
              onChange: (event) => setDraft({ ...draft, volume: Number(event.target.value) }),
              style: { width: '100%', accentColor: palette.accent },
            }),
          ),
          h(
            Field,
            { label: '统计条显示', value: '' },
            h(
              'select',
              {
                value: draft.statsDisplay,
                onChange: (event) => setDraft({ ...draft, statsDisplay: event.target.value }),
                style: {
                  height: 32,
                  width: '100%',
                  borderRadius: 8,
                  border: `0.5px solid ${palette.border}`,
                  background: 'transparent',
                  color: palette.text,
                  padding: '0 8px',
                },
              },
              h('option', { value: 'always' }, '始终显示'),
              h('option', { value: 'hover' }, '悬停显示'),
              h('option', { value: 'off' }, '隐藏'),
            ),
          ),
          h(
            Field,
            { label: '长按提起', value: `${draft.longPressMs} ms` },
            h('input', {
              type: 'range',
              min: 120,
              max: 1200,
              step: 10,
              value: draft.longPressMs,
              onChange: (event) => setDraft({ ...draft, longPressMs: Number(event.target.value) }),
              style: { width: '100%', accentColor: palette.accent },
            }),
          ),
          h(
            Field,
            { label: '拖拽阈值', value: `${draft.dragStartPx} px` },
            h('input', {
              type: 'range',
              min: 2,
              max: 40,
              step: 1,
              value: draft.dragStartPx,
              onChange: (event) => setDraft({ ...draft, dragStartPx: Number(event.target.value) }),
              style: { width: '100%', accentColor: palette.accent },
            }),
          ),
          h(
            'div',
            { style: { display: 'flex', gap: 10, alignItems: 'center', marginTop: 16, flexWrap: 'wrap' } },
            h(Button, { primary: true, label: busy === 'save' ? '应用中…' : '保存并应用', onClick: save, disabled: Boolean(busy) }),
            h(Button, { label: '重新读取', onClick: load, disabled: Boolean(busy) }),
            message
              ? h('span', { style: { color: message.includes('失败') ? palette.danger : palette.secondary, fontSize: 12 } }, message)
              : null,
          ),
        ),
        h(
          'p',
          { style: { margin: '18px 0 0', color: palette.tertiary, fontSize: 12, lineHeight: 1.6 } },
          '设置写入数据目录的 ui-config.json，并覆盖 profile 中的同名字段。想恢复 profile 配置，删除该文件后重启插件即可。',
        ),
        loading ? h('p', { style: { color: palette.tertiary, fontSize: 12 } }, '正在读取桌宠状态…') : null,
      )
    }

    function PetIcon({ size }) {
      const edge = size || 18
      return h(
        'svg',
        {
          width: edge,
          height: edge,
          viewBox: '0 0 24 24',
          fill: 'currentColor',
          'aria-hidden': true,
          style: { display: 'block' },
        },
        h('ellipse', { cx: 12, cy: 15.5, rx: 5.2, ry: 4.4 }),
        h('circle', { cx: 6.4, cy: 9.2, r: 2.2 }),
        h('circle', { cx: 10.5, cy: 6.8, r: 2.3 }),
        h('circle', { cx: 15.5, cy: 6.8, r: 2.3 }),
        h('circle', { cx: 18.8, cy: 9.6, r: 2.0 }),
      )
    }

    function apply(ctx) {
      ctx.slots.inject('sidebar.panellist', () =>
        ctx.slots.register(
          {
            name: 'sidebar.panellist',
            id: PANEL_ID,
            order: 20,
            label: () => '桌宠',
          },
          PetIcon,
        ),
      )
      // Prefer a direct registration (ui-layout is in this bundle's inject
      // list, so the keyed `main` declaration normally exists).  If a host
      // composition declares it later, fall back to the declaration-aware
      // injector rather than failing the whole panel.
      const registerMain = () =>
        ctx.slots.register(
          {
            name: 'main',
            key: PANEL_ID,
          },
          PetPanel,
        )
      try {
        registerMain()
      } catch (error) {
        reportClient({ kind: 'register-main', message: String(error?.message ?? error), stack: error?.stack })
        ctx.slots.inject('main', registerMain)
      }
    }

    exports.apply = apply
    exports.inject = ['slots']
    return module.exports
  },
})
