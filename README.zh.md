# 桌宠-奶娃小时候

[![npm](https://img.shields.io/npm/v/dsh-desktop-pet-naiwa.svg)](https://www.npmjs.com/package/dsh-desktop-pet-naiwa)
[![GitHub](https://img.shields.io/badge/GitHub-dsh--desktop--pet--naiwa-181717?logo=github)](https://github.com/Night-maze/dsh-desktop-pet-naiwa)

一个基于 DeepSeek Harness 插件机制的 Windows 桌宠：透明、无边框、置顶，能汇报 Agent 进度、显示今日 Token 估算花费与真实余额、响应单击/长按/拖拽、贴边半隐藏，并把拖入的文件**移动到系统回收站**。

> 当前安装方式是 profile 里用 `link:` 指向本目录的 bundle；左侧栏有“🐾 桌宠”设置页。绝对路径开发行仅用于临时调试。
>
> 金额说明：DSH 只提供 provider-reported Token 用量，不提供已结算的货币金额。因此“今日花费”是**真实 Token × 公开单价**的估算值，UI 用 `≈` 标注；“余额”来自 `ctx.deepseekAccount.getBalance`，是真实平台钱包余额，不估算、不伪造。

## 1. 实现方案

### 为什么是“Host 插件 + WPF 子进程”

DSH 的 client plugin 是浏览器 sandbox 内的 React UI，只能渲染在 Web GUI 里；它没有创建 Electron `BrowserWindow` 或任意 OS 窗口的权限。要实现“桌面级透明、无边框、置顶、可拖拽”的窗口，必须有一个独立的原生窗口进程。

因此本插件采用两层结构：

```text
┌───────────────────────── DSH Host (Node/Cordis) ─────────────────────────┐
│  dsh-desktop-pet-naiwa (host plugin)                                     │
│  • ctx.on('session/event') → turn/step/tool/assistant 事件投影            │
│  • UsageLedger → 今日 Token 账本（回填 + 持久化）                         │
│  • Pricing → 公开单价 × 真实 Token = 估算金额                            │
│  • BalanceService → ctx.deepseekAccount.getBalance() 真实余额            │
│  • PetProcess → 启动/守护 WPF 子进程，127.0.0.1 + 随机 token 鉴权         │
└───────────────┬──────────────────────────────────────────────────────────┘
                │ newline-delimited JSON（仅 loopback）
┌───────────────▼──────────────── Windows WPF 子进程 ──────────────────────┐
│  DesktopPet.ps1 → Add-Type DesktopPet.cs                                 │
│  • 透明/无边框/置顶窗口，逐像素 alpha 命中测试（透明处点击穿透）          │
│  • 单击随机互动、长按提起、拖拽钟摆、贴边半隐藏                          │
│  • 拖入文件 → IFileOperation + FOFX_RECYCLEONDELETE → 回收站             │
│  • 气泡、进度条、统计条、表情精灵、完成音效                              │
└──────────────────────────────────────────────────────────────────────────┘
```

宿主插件不把任何 Token、余额、进度写死；WPF 子进程不访问网络、不读取 DSH 内部状态，只消费宿主发来的 JSON。

### 目录结构

```text
plugins/dsh-desktop-pet-naiwa/
├─ package.json                 # dsh.bundle.patch 声明
├─ cordis.patch.yml             # 作为 bundle 安装时插入 desktop-pet 行
├─ config.example.yml           # 完整配置样例（带注释）
├─ README.md                    # English quick reference
├─ README.zh.md                 # 本文件
├─ lib/
│  ├─ index.js                  # Host 插件入口：事件、任务状态机、统计
│  ├─ config.js                 # 默认值/夹取/路径/配置生成
│  ├─ pricing.js                # 峰谷价、货币/精度、金额格式化
│  ├─ usage.js                  # Token 日账本、回填、持久化
│  ├─ balance.js                # 真实余额读取、超时、退避
│  └─ pet-process.js            # WPF 子进程 + loopback bridge + 重启
├─ pet/
│  ├─ DesktopPet.ps1            # Windows PowerShell 5.1 启动器（STA）
│  ├─ DesktopPet.cs             # WPF 窗口、手势、动画、回收站
│  └─ assets/
│     ├─ sprites/               # 由 naiwa.jpg 生成的 12 张透明精灵
│     └─ sounds/                # 原创合成 WAV
├─ scripts/
│  ├─ build-assets.py           # 从参考图生成精灵（可复现）
│  ├─ build-sounds.py           # 合成音效（可复现）
│  ├─ demo.ps1                  # 无 DSH 的 UI 演示
│  └─ test-recycle.ps1          # 回收站安全功能测试
└─ test/
   ├─ run-tests.mjs             # Host 单元测试
   └─ mock-host.mjs             # 真实 WPF + mock Cordis 集成测试
```

### 关键设计点

- **事件投影**：`turn/start` → 任务开始；`step/start`、`tool/call` → 进度；`assistant/message` / `assistant/attempt` → Token；`turn/end` → 完成/失败/取消；`agent/error` → 失败兜底。
- **进度不伪造百分比**：默认使用不确定进度条 + “第 N 步 · 工具名”，不使用编造的 0–100% 数字。
- **Token 账本**：按 `sessionId:seq` 去重；同一 `turn/step` 的成功消息替换失败 attempt；`llm/retry-started` 后允许重试 attempt 再次计入。启动时从 `ctx.sessionPersistence` 回填今天，受 Session 数/字节/超时限制。
- **价格**：默认内置 `deepseek-flash`、`deepseek-v4-pro` 的 CNY/USD 公开列表价；峰时为北京时间工作日 9–12、14–18，节假日可在配置里补。未配置单价的模型不计金额，只显示 Token。
- **真实余额**：调用 `ctx.deepseekAccount.getBalance`，失败保留上次值并标记 stale，定时刷新有指数退避，绝不返回 0 伪造余额。
- **手势状态机**：`Idle → Pressed → (PickedUp | Dragging) → Idle`；“按下未移动且未超阈值”才是单击；超过 `longPressMs` 进入“被提起”；移动超过 `dragStartPx` 进入拖拽；文件拖入是独立分支。
- **点击穿透**：`WM_NCHITTEST` 按当前精灵 alpha 判断，透明像素返回 `HTTRANSPARENT`，不会挡住桌面其他操作。
- **贴边**：拖拽释放后按当前显示器 `WorkingArea` 和 `edgeThreshold` 选择左/右/上/下，滑到只露 `edgeVisibleRatio`；悬停滑出，离开延迟滑回。
- **回收站**：使用 `IFileOperation` + `FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE`，在 Shell 层强制“回收或失败”，**没有** `File.Delete`/`Directory.Delete` 回退。系统保护目录、盘符根、符号链接/目录联接、无权限、占用、跨盘/网络回收失败都会保留原文件并给出明确错误。

## 2. 安装

### 前置条件

- Windows 10/11。
- Windows PowerShell 5.1（系统自带）+ .NET Framework 4.x（WPF 系统组件）。
- 不需要额外 npm 依赖；Host 侧只用 Node 内置模块。
- DSH 版本：本实现基于 `0.2.1-rc.5` 的 Cordis 插件/服务 API。

### 方式 A：开发行（仅临时调试，不推荐日常使用）

在 `%USERPROFILE%\.dsh\profiles\<profile>\cordis.patch.yml` 末尾追加：

```yaml
- insert:
    - id: desktop-pet
      name: 'F:/path/to/dsh-desktop-pet-naiwa/lib/index.js'
      config:
        enabled: true
        volume: 0.7
        scale: 0.8
```

绝对路径行没有 bundle 身份，Plugins 页面没有开关，也没有 client 设置页。日常使用请用方式 B，并删除这条开发行。

### 方式 B：作为 bundle 安装（推荐）

发布到 npm 后，直接用包名安装：

```powershell
dsh plugin --profile desktop add dsh-desktop-pet-naiwa
```

也可以使用本地源码目录：

```powershell
dsh plugin --profile desktop add link:F:\path\to\dsh-desktop-pet-naiwa
```

`package.json` 已声明：

```json
"dsh": {
  "bundle": { "patch": "./cordis.patch.yml" },
  "client": { "platform": "web", "inject": ["..."] }
}
```

安装步骤：

1. 在 DSH Web GUI 打开 **Plugins → Add plugin**，输入 `dsh-desktop-pet-naiwa`；或选择 **本地目录** 指向 `plugins/dsh-desktop-pet-naiwa`。
2. 在 Plugins 页面把 **桌宠-奶娃小时候** bundle 打开；或把 `"dsh-desktop-pet-naiwa"` 加入 profile `package.json` 的 `dsh.profile.bundles` 列表。
3. 删除绝对路径开发行，避免同一插件加载两次。
4. 刷新 DSH 页面；应出现左侧栏 **🐾 桌宠**。若没有，重启 DSH 一次让 client graph 重新扫描 `dsh.client`。

设置页可调缩放、音量、统计条、长按/拖拽阈值，并刷新统计、重启/显示/隐藏桌宠。设置写入数据目录 `ui-config.json`，覆盖 profile 同名字段；删除该文件并重启插件可恢复 profile 配置。概念和实现细节见 [docs/client-plugin.md](docs/client-plugin.md)。

## 3. 运行

- 插件随 Host 启动，自动拉起 WPF 子进程。
- 右键桌宠：刷新余额/花费、显示/隐藏统计条、回到右下角、打开配置说明、重启桌宠、退出本次会话。
- 统计条点击：请求宿主刷新余额与花费。
- 拖入文件：桌宠张嘴 → 放下 → 咀嚼动画 → 结果气泡；失败会弹出详细原因窗口，原文件保留。
- 将桌宠拖到屏幕边缘：释放后自动半隐藏；鼠标移入滑出，移出延迟滑回。

### 数据文件

默认目录 `$DSH_HOME/storages/desktop-pet/`：

| 文件 | 说明 |
|---|---|
| `pet-config.json` | Host 写给 WPF 的合并配置 |
| `usage.json` | 今日 + 最近 14 天 Token 账本（含模型/时间/四个桶） |
| `host.log` | Host 插件诊断 |
| `pet.log` | WPF/启动器诊断 |
| `launcher.log` | 启动器编译/启动诊断 |
| `window.json` | 上次窗口位置（多显示器恢复用） |

插件自身日志可能轮转为 `pet.log.old.<timestamp>`；不会删除用户文件。

## 4. 配置

完整字段和注释见 [config.example.yml](config.example.yml)。常用项：

| 字段 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 插件总开关 |
| `imagePath` | `''` | 单张自定义形象；留空用内置 12 精灵 |
| `spriteDir` / `soundDir` | 内置 assets | 相对插件目录或绝对路径 |
| `scale` | `1.0` | 整体缩放 0.5–2：窗口、UI、形象一起缩放；`0.5` = 180×215 DIP |
| `volume` | `0.7` | 音效音量 0–1（MediaPlayer） |
| `topmost` | `true` | 置顶 |
| `avoidFullscreen` | `true` | 全屏应用时隐藏（SHQueryUserNotificationState） |
| `statsDisplay` | `always` | `always`/`hover`/`off` |
| `longPressMs` | `450` | 长按提起阈值 |
| `dragStartPx` | `10` | 拖拽启动阈值 |
| `edgeThreshold` | `24` | 贴边触发距离（物理像素） |
| `edgeVisibleRatio` | `0.38` | 半隐藏后可见比例 |
| `edgeHideDelayMs` / `edgeSlideMs` | `750` / `180` | 隐藏延迟/滑动时长 |
| `currency` | `auto` | `CNY`/`USD`/`auto`（跟钱包） |
| `costPrecision` | `4` | 花费小数位 |
| `balanceRefreshMs` | `60000` | 余额刷新，最小 30s；失败指数退避到 5min |
| `statsRefreshMs` | `30000` | 统计推送间隔 |
| `pricing` | 公开价表 | 峰时、节假日、模型单价（每百万 Token） |
| `interactions` | 6 种 | 单击随机互动权重/动画/台词 |
| `protectPaths` | 系统目录 | 拒绝回收的路径前缀 |
| `backfill` | 开启 | 启动回填的数量/字节/超时上限 |

`pricing.holidays` 默认空数组：中国法定节假日每年变化，插件不内置易过期日历；需要精确峰谷账单时请填入 `YYYY-MM-DD`。

### 如何取消/添加“点击随机切换的形象”

单击时并不是直接随机抽 `sprites` 目录里的图片，而是按权重抽 **`interactions` 列表中的一条互动**；每条互动用 `sprite` 指定要显示哪张图（文件名去掉 `.png`）。当前 profile 的开发行在：

`%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`

在 `- id: desktop-pet` 的 `config:` 下加 `interactions:`。**数组是整体替换，不是逐项合并**：想保留哪些就全部列出来，未列出的互动/形象就不会再出现。

例如只保留“比心”和“普通台词”，并新增一个 `wave` 挥手：

```yaml
      config:
        enabled: true
        volume: 0.7
        interactions:
          - id: heart
            weight: 1.0
            animation: heart
            sprite: heart
            texts: ['给你一颗小心心', '最喜欢你啦']
          - id: line
            weight: 1.4
            animation: line
            sprite: shy
            texts: ['今天也要加油！', '要摸摸头吗？']
          - id: wave
            weight: 1.0
            animation: line
            sprite: wave
            texts: ['挥挥手～', '你好呀！']
```

- 想**取消**某个形象：删掉使用它的互动条目（如 `heart`/`dance`），或把整条 `interactions` 换成你想要的列表。
- 想**添加**形象：把 PNG 放进 `pet/assets/sprites/`（或你配置的 `spriteDir`），在互动里写 `sprite: 文件名`；`animation` 可用 `line`（无额外特效）、`blink`、`jump`、`shake`、`heart`、`dance`。
- 内置可用文件名：`idle`、`cheer`、`open`、`peace`、`hold`、`heart`、`wave`、`chew`、`think`、`sleepy`、`shy`、`walk`。
- `blink`/`jump`/`shake`/`heart`/`dance` 的 `sprite` 现在都**优先使用条目里的值**；只有留空时才退回 `spriteShy`/`spriteCheer` 等表情状态。
- 若想控制“工作/完成/失败/拖拽/吃文件”等非点击状态，改 `spriteWork`、`spriteComplete`、`spriteError`、`spriteDrag`、`spriteEat`、`spriteChew` 等字段，而不是 `interactions`。

改完保存会触发热重载；若想完全不随机切换形象，只保留一条 `animation: line`、`sprite: idle` 的互动即可。

### 左侧栏“桌宠”设置页

插件现在包含一个 **client 半身**：

- 左侧栏 `sidebar.panellist` 中出现“🐾 桌宠”入口；
- 点击后打开 keyed `main` 面板 `desktop-pet`；
- 面板可调 `scale`、`volume`、`statsDisplay`、`longPressMs`、`dragStartPx`，并可刷新统计、重启/显示/隐藏桌宠、打开配置说明；
- 设置保存到数据目录的 `ui-config.json`，覆盖 profile 中的同名字段；删除该文件并重启插件可回到 profile 配置。

概念、slot 注册、HTTP 桥和热更新说明见 **[docs/client-plugin.md](docs/client-plugin.md)**。

如果新增 client 半身后左侧栏暂时没出现入口，先刷新 DSH 页面；仍没有则重启 DSH 一次。开发期 `lib/index.js` 会尝试增量重扫 client graph。

## 5. 安全与边界

- **只回收，不永久删除**：`pet/DesktopPet.cs` 的 `RecycleService` 只有一条删除路径：`IFileOperation.DeleteItem` + `FOFX_RECYCLEONDELETE`。代码中没有 `File.Delete`、`Directory.Delete` 或 `SHFileOperation` 的永久删除回退。
- **受限进程保护**：若进程是 AppContainer 或低完整性级别（如 DSH 工作区沙箱），`RestrictedReason()` 会直接拒绝回收并保留文件。这样即使沙箱把 Shell 操作降级为永久删除，也不会执行。
- **保护路径/链接**：盘符根、`C:\Windows`、`Program Files` 等默认拒绝；符号链接/目录联接拒绝，避免误删目标内容。
- **失败保留**：无权限、被占用、回收站禁用、网络共享、文件过大、Shell 拒绝等都会返回失败；原文件不动。
- **不伪造数据**：Token 来自 provider usage；余额来自账号 API；进度不显示编造百分比。接口失败时显示 `—`/stale，不影响基础互动。
- **刷新上限/退避**：余额最小 30s，失败退避；统计 30s；账本写盘 1.5s debounce；无无限循环请求。
- **loopback 鉴权**：bridge 只绑定 `127.0.0.1`，每次启动随机 24 字节 token，首行 hello 校验；只接受一个 pet 连接。

## 6. 测试

### 单元测试（无需 DSH）

```powershell
node test/run-tests.mjs
```

覆盖配置夹取、时区/峰谷、金额/Token 格式化、账本去重/替换/重试。

### WPF + Host 集成（mock Cordis）

```powershell
node test/mock-host.mjs
```

会启动真实 WPF 子进程，模拟 start/progress/complete 和 Token 事件，约 14s 后自动退出。适合看气泡、进度条、完成动画和日志。

### 无 DSH 的 UI 演示

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts/demo.ps1
```

统计条标注“演示”，不会写入真实账单。

### 回收站安全测试

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts/test-recycle.ps1
```

必须在**普通用户进程/普通终端**中运行，不要在 DSH 工作区沙箱内运行。测试会创建唯一的文件与目录，移动到回收站，并验证：

- 原路径消失；
- 回收站可枚举到两个名称；
- 系统保护目录被拒绝；
- 不调用永久删除。

在 DSH 沙箱中运行 `RecycleService.RestrictedReason()` 应返回“低完整性级别/AppContainer”，回收会被拒绝且文件保留。

### DSH 实机验证

查看 `$DSH_HOME\storages\desktop-pet\host.log` / `pet.log`，应看到：

```text
desktop-pet: pet process starting on 127.0.0.1:...
desktop-pet: backfilled N session(s) for YYYY-MM-DD
desktop-pet: pet window ready
```

## 7. 验收

逐项清单、测试证据和“待人工”项见 [docs/acceptance.md](docs/acceptance.md)。

## 8. 降级与假设

1. **DSH client plugin 不能创建原生窗口**：这是当前实现拆成 Host + WPF 的根本原因。若未来 DSH 暴露 desktop window API，可替换 `pet-process.js`/WPF 层，Host 事件与账本逻辑不变。
2. **DSH 不提供货币账单**：金额只能是“Token × 配置单价”的估算；UI 已用 `≈` 和“不是账单”文案标注。真实余额仍来自平台钱包。
3. **节假日**：不内置年度节假日表；配置 `pricing.holidays` 后可精确。
4. **回收站能力受系统策略限制**：Recycle Bin 禁用、网络盘、超大文件、权限不足时无法回收；此时失败并保留，不会降级为永久删除。
5. **多显示器/混合 DPI**：已按物理像素 + `Screen.WorkingArea` 实现，并在 2560×1440 @125% 上验证；不同 DPI 的多屏组合仍需人工验收。
6. **长按与拖拽的边界**：阈值可配；真实鼠标按住不动会进入提起，移动超过 `dragStartPx` 会先进入拖拽。合成鼠标测试容易先触发拖拽，建议人工验证长按手感。
7. **音效音量**：使用 WPF `MediaPlayer.Volume`；系统“应用音量”仍是最终音量。
8. **当前 profile 的开发行**：绝对路径只适合本机开发；分发时请用 bundle 安装。

## 9. 故障排查

| 现象 | 排查 |
|---|---|
| 桌宠不出现 | 看 `host.log` 是否有 `spawn failed`；看 `launcher.log` 是否有 C# 编译错误；确认 Windows PowerShell 5.1 可用 |
| 图片不显示 | 看 `pet.log` 的 `sprite directory=`；缺图会使用内置矢量 fallback |
| 没有声音 | 看 `pet.log` 是否有 `audio failed`；确认 `soundDir` 下 WAV 存在；检查系统音量 |
| 余额一直 — | 账号未登录或 `getBalance` 失败；看 `host.log` 的 `balance unavailable` |
| 拖入文件失败 | 看错误窗口；网络盘/回收站禁用/权限不足都会失败并保留；不要在 DSH 沙箱内操作 |
| 多个桌宠 | 不要同时保留绝对路径开发行和 bundle；删除重复行后热重载/重启 |
| DSH 重启后桌宠没了 | 插件会随 Host 重新加载；若没有，检查 profile 行是否还在，触发一次配置变更热重载 |
