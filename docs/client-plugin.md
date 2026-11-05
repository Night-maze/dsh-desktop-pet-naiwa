# 桌宠的客户端半身与设置页

本文说明左侧栏“桌宠”入口、设置页是怎样加到 DSH Web UI 里的，以及几个容易混淆的概念。

## 1. Host 插件与 Client 插件

DSH 插件可以有两半：

| 半身 | 运行位置 | 能做什么 | 本插件文件 |
|---|---|---|---|
| Host（服务端） | DSH Host / Cordis 进程 | 读会话事件、账本、余额；启动 WPF 子进程；注册 HTTP 路由 | `lib/index.js` |
| Client（浏览器端） | DSH Web UI 沙箱 | 注册侧栏入口、主面板、React 表单；调用 Host 接口 | `lib/client.js` |

桌宠窗口是 **WPF 子进程**，不是浏览器组件。Browser 插件没有创建透明置顶原生窗口的 API，所以窗口始终由 Host 插件启动；Client 只负责“入口 + 设置表单”。

## 2. `dsh.client` 声明

`package.json` 里增加：

```json
"exports": {
  "./client": "./lib/client.js"
},
"dsh": {
  "client": {
    "platform": "web",
    "inject": [
      "@deepseek-ai/dsh-client-connection",
      "@deepseek-ai/dsh-client-ui-layout",
      "@deepseek-ai/dsh-client-ui-renderer",
      "@deepseek-ai/dsh-client-ui-sidebar"
    ]
  }
}
```

- `platform: "web"`：这是浏览器半身。
- `exports["./client"]`：Host 的 `dsh-client-modules` 用这个路径找到并分发 bundle。
- `inject`：客户端模块图的前置依赖，保证 slots、layout、sidebar 先就绪。

`lib/client.js` 采用 DSH 客户端 bundle 的标准形状：

```js
window.__ModuleLoader__.load({
  id: 'dsh-desktop-pet-naiwa',
  factory: (require) => {
    const React = require('react')
    // ...
    exports.apply = apply
    exports.inject = ['slots']
    return module.exports
  },
})
```

它不是普通 ESM 入口，而是由 DSH 的模块加载器在浏览器里执行的 lazy-CJS bundle。因此不需要额外的前端构建链；修改 `lib/client.js` 后，Host 的 client graph 需要重新读取 bundle（见第 6 节）。

## 3. 侧边栏 slot

侧边栏 shell 暴露 `sidebar.panellist` 列表槽：

```js
ctx.slots.inject('sidebar.panellist', () =>
  ctx.slots.register(
    {
      name: 'sidebar.panellist',
      id: 'desktop-pet',
      order: 20,
      label: () => '桌宠',
    },
    PetIcon, // React 组件，收到 { size }
  ),
)
```

- `id`：面板 id，也是点击后要打开的 `main` key。
- `order`：在同级入口中的排序。
- `label`：可见文字、无障碍名称、折叠后的 tooltip。
- 第二个参数是图标组件；sidebar 只传 `size`，组件自己画 SVG/emoji。
- 点击由 sidebar shell 调用 `ctx.layout.selectPanel(id)`，不是我们手写导航。

## 4. `main` keyed slot

主内容区用 keyed `main` 槽，key 必须与 side bar 的 `id` 相同：

```js
const registerMain = () =>
  ctx.slots.register(
    {
      name: 'main',
      key: 'desktop-pet',
    },
    PetPanel, // React 组件
  )

try {
  registerMain()
} catch {
  // main 声明如果晚到，用 declaration-aware injector 等待。
  ctx.slots.inject('main', registerMain)
}
```

`ctx.slots.inject` 会在槽声明存在时立即执行回调；声明尚未出现时，等声明提交后再执行。桌宠的 `main` 注册优先直接执行（`ui-layout` 已在 inject 列表里），失败再回退到 injector，避免不同组合顺序下整个页面注册失败。

## 5. 设置表单如何改桌宠

浏览器不能直接读写 profile 的 `cordis.patch.yml`，所以 Host 插件注册一个仅回环可访问的 HTTP 路由：

```text
GET  /desktop-pet-api/config
POST /desktop-pet-api/config
GET  /desktop-pet-api/status
POST /desktop-pet-api/action
```

- `GET config` 返回当前 `scale`、`volume`、`statsDisplay` 等，以及今日统计和余额。
- `POST config` 校验字段，写入数据目录的 `ui-config.json`，重建配置，写 `pet-config.json`，重启 WPF 子进程。
- `POST action` 支持 `refresh`、`restart`、`show`、`hide`、`open_readme`。
- 路由只接受 `127.0.0.1`、`::1`、`::ffff:127.0.0.1` 来源，响应 `cache-control: no-store`。

桌面版页面地址是 `dsh-app://app`。此时 client 使用**相对路径** `/desktop-pet-api/...`，Electron 的 `dsh-app://app` 协议会把非静态资源转发到 Host，并注入 Host cookie。不要直接用 `__DSH_TRANSPORT__.streamBaseUrl` 拼绝对 URL：那会变成跨源请求，浏览器报 `Failed to fetch`。Web HTTP 页面则使用 `location.origin + /desktop-pet-api`。

配置分层：

```text
默认值
  < profile 的 - id: desktop-pet config
  < 数据目录的 ui-config.json
```

`ui-config.json` 是设置页最后一次保存的覆盖层；删除它并重启插件即可回到 profile 配置。注意 profile patch 对 row 的 `config` 是整体替换，数组（如 `interactions`）不会逐项合并。

## 6. 新增 `dsh.client` 后为什么要刷新/重启

DSH 的 client graph 在 Host 启动时扫描 Loader entries 的 `dsh.client` 元数据。  
如果插件已经以 Host 行运行，之后才增加 `dsh.client`，Host 可能缓存了“该包没有 client”的结果。

`lib/index.js` 里有一个尽力而为的 `nudgeClientModules()`：

- 清掉 `clientModules.pkgMeta` 中“没有 client”的缓存；
- 把当前 Loader entries 标脏并 `flush()`；
- 对 `dsh-desktop-pet-naiwa` 调用 `rebuilt()`，让浏览器拿到新 bundle revision。

这是开发期便利，不是公开 API。正常首次安装/重启 DSH 时不需要它；如果页面仍看不到入口，刷新页面或重启 DSH 一次即可。

## 7. 文件清单

| 文件 | 作用 |
|---|---|
| `lib/index.js` | Host 插件、设置 API、client graph nudge |
| `lib/client.js` | sidebar + main + React 设置表单 |
| `package.json` | `dsh.bundle` 与 `dsh.client` 声明 |
| `cordis.patch.yml` | 插入 Host 行 `desktop-pet` |
| `ui-config.json` | 设置页保存的覆盖层（运行时生成） |
