# 验收自测清单

环境：Windows，2560×1440 @125%，Windows PowerShell 5.1，DSH `0.2.1-rc.5`，Python Pillow/numpy（仅生成资源时使用）。

状态说明：`通过` = 已有自动化/实机证据；`待人工` = 代码完成但需要人眼/手感/多屏确认；`部分` = 已覆盖主路径，边界未完全覆盖。

## A. Agent 进度与完成音效

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| A1 | 订阅任务开始 | 通过 | `turn/start` → `agent start`；Mock 集成与实机进度条 |
| A2 | 进度更新（步骤/工具） | 通过 | 实机显示“第 4 步 · 运行命令”；`step/tool` 投影 |
| A3 | 进度条/状态文字 | 通过 | WPF 不确定进度条 + 文本，不伪造百分比 |
| A4 | 任务完成提醒 | 通过 | `turn/end completed` → 心形表情 + 气泡；Mock 截图 |
| A5 | 任务失败提醒 | 通过（Mock） | `turn/end error/max-tokens` 与 `agent/error` 兜底 |
| A6 | 完成音效 | 通过（无失败日志） | `complete.wav` 经 MediaPlayer 播放；不阻塞 UI |
| A7 | 失败/取消音效 | 通过（代码） | `error.wav`；取消只显示文本，不误播完成音 |
| A8 | 多任务会话不串台 | 部分 | 按 session 维护 task；只展示最近 active，未覆盖所有并发组合 |
| A9 | 插件中途挂载可接管当前 turn | 通过 | `progressTask` 首次 step/tool 自动 start |

## B. Token 花费与余额

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| B1 | 今日累计 Token | 通过 | `usage.json`；启动回填 6 个 Session |
| B2 | 今日累计金额 | 通过（估算） | 真实 Token × 公开单价；UI `≈`；非账单 |
| B3 | 当前余额 | 通过 | 实机 `余额 ¥1.00`，来自 `deepseekAccount.getBalance` |
| B4 | 货币与精度 | 通过 | `currency`/`costPrecision`；CNY/USD 表 |
| B5 | 峰谷价 | 通过 | 单元测试 11:00/13:00/周末 |
| B6 | 定时/事件刷新 | 通过 | 30s stats、60s balance、事件后即时 `sendStats` |
| B7 | 失败降级 | 通过（代码） | 超时/失败保留旧值并 `stale`，显示 `—` |
| B8 | 不伪造数据 | 通过 | 无默认假余额；未定价模型只显示 Token |
| B9 | 刷新退避/无死循环 | 通过（代码） | 余额最小 30s，失败 2^n 到 5min；定时器固定 |

## C. 单击随机互动

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| C1 | 单击触发随机互动 | 通过 | 自动化鼠标日志 `click interaction requested` |
| C2 | 至少 3–5 种内置 | 通过 | blink/jump/shake/heart/line/dance 共 6 种 |
| C3 | 可配置/可扩展 | 通过 | `interactions` 权重/动画/精灵/台词 |
| C4 | 区分单击/长按/拖拽 | 通过（长按待人工） | `longPressMs`/`dragStartPx` 状态机；合成鼠标易先触发拖拽 |
| C5 | 文件拖入独立手势 | 通过 | DragEnter/Drop 分支，不触发 click/press |
| C6 | 透明区域不挡桌面 | 通过（代码+实机） | `WM_NCHITTEST` 按 alpha 返回 `HTTRANSPARENT` |

## D. 长按提起、拖拽晃动、贴边半隐藏

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| D1 | 长按达到阈值进入提起 | 待人工 | 代码 `OnLongPress`；合成输入测试先触发拖拽 |
| D2 | 拖拽钟摆/弹簧晃动 | 通过（实机） | 日志 `drag begin/end`；弹簧角度/位移 |
| D3 | 松开自然回弹 | 通过（实机） | `EndDrag` 目标归零 + 阻尼弹簧 |
| D4 | 屏幕边缘自动半隐藏 | 通过（实机） | 日志 `edge snap right/bottom`、`hide to edge` |
| D5 | 悬停滑出、离开再隐藏 | 通过（实机） | 日志 `show from edge` / `hide to edge` |
| D6 | 多显示器/边界 | 部分 | 按 `Screen.WorkingArea` 物理像素；仅单屏 125% 实测 |
| D7 | 窗口置顶/不遮挡关键区域 | 通过（代码） | `Topmost` + `SHQueryUserNotificationState` 全屏隐藏 |
| D8 | 位置持久化 | 通过 | `window.json`，启动 clamp 到虚拟屏幕内 |

## E. 拖入文件“吃掉”并移动到回收站

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| E1 | 拖入高亮/张嘴 | 通过（代码） | DragEnter 切 open 精灵 + 气泡 |
| E2 | 吃掉/咀嚼/吞咽动画 | 通过（代码） | 交替 open/chew + 缩放 + 音效 |
| E3 | 实际移动到回收站 | 通过 | 普通进程测试：moved=2，文件/目录均出现在回收站 |
| E4 | 禁止永久删除 | 通过 | 唯一路径 `IFileOperation` + `FOFX_RECYCLEONDELETE`；无 File.Delete 回退 |
| E5 | 失败保留原文件 | 通过 | 无权限/保护路径/不存在等返回 failure，原文件存在 |
| E6 | 目录 | 通过 | 测试目录 moved，回收站可见 |
| E7 | 多个文件 | 通过 | 测试文件+目录同时 moved=2 |
| E8 | 无权限/占用/网络/超大 | 部分 | 代码捕获并提示；未逐一构造所有系统错误 |
| E9 | 结果提示 | 通过 | 成功气泡；失败详细窗口列出每个失败原因 |
| E10 | 受限进程防误删 | 通过 | 沙箱 `RestrictedReason=低完整性级别`，Recycle 拒绝且文件保留 |

## F. 资源、配置与异常

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| F1 | 图片可配置 | 通过 | `imagePath`/`spriteDir`；内置 12 精灵 |
| F2 | 图片加载失败 fallback | 通过（代码） | 内置矢量小怪兽 + 日志 + 一次性提示 |
| F3 | 音效可配置 | 通过 | `soundDir`，WAV 缺失 fallback 系统音 |
| F4 | 音效播放失败不崩 | 通过 | MediaPlayer 失败 → SoundPlayer 兜底 → 系统音 |
| F5 | 台词/刷新/阈值配置化 | 通过 | 全部在 `config.example.yml` |
| F6 | 接口超时 | 通过（代码） | 余额 15s 超时；回填 15s/Session |
| F7 | 状态机防误触 | 通过（代码） | `pressed/dragging/pickedUp/fileDrag` 互斥 |
| F8 | 轻量动画 | 通过（代码） | 16ms DispatcherTimer，仅变换属性，无大位图重绘 |
| F9 | 日志 | 通过 | `host.log`/`pet.log`/`launcher.log`，可轮转 |

## G. DSH 插件机制与生命周期

| # | 验收项 | 状态 | 证据 / 方法 |
|---|---|---|---|
| G1 | 通过 Cordis 加载 | 通过 | profile 行加载，`pet window ready` |
| G2 | 使用真实服务 | 通过 | `sessions`/`sessionPersistence`/`deepseekAccount`/`tokenMeter` 注入 |
| G3 | 回填今日历史 | 通过 | `backfilled 6 session(s) for 2026-11-03` |
| G4 | 热重载卸载/重启 | 通过 | 修改配置后 `plugin disposed` → 新 `pet window ready` |
| G5 | 不阻塞 Host | 通过 | 子进程 + 异步回填 + 限流 |
| G6 | 正式 bundle 安装 | 通过（link:） | profile 用 `link:` 安装 `dsh-desktop-pet-naiwa` bundle；绝对路径开发行已移除 |
| G7 | 左侧栏 client 入口 | 通过 | 左侧栏出现“🐾 桌宠”；`dsh.client` + `lib/client.js` 已注册 |
| G8 | 设置 API | 通过 | `GET/POST /desktop-pet-api/config` 返回并持久化 `scale/volume/statsDisplay`；`ui-config.json` 生成 |
| G9 | 设置保存不重复重启 | 通过 | `restart requested` → 一次 `pet window ready`；代际令牌阻止旧 socket close 再触发重启 |

## 已执行的命令与结果

1. `Add-Type -Path pet/DesktopPet.cs ...` → `COMPILED_OK`（多次）
2. `node test/run-tests.mjs` → 8/8 通过
3. `node test/mock-host.mjs` → 真实 WPF + mock Cordis，start/progress/complete 正常
4. `powershell -STA -File scripts/test-recycle.ps1`（普通进程）→ `ok=true`，moved=2，回收站可见
5. 沙箱内 `RestrictedReason()` → `低完整性级别`；Recycle 返回 0 moved、1 failure，文件 `Test-Path=True`
6. 实机 DSH：
   - `host.log`: `backfilled 6 session(s)`、`pet window ready`
   - 窗口显示真实余额 `¥1.00`，今日估算 `≈¥4.7689`
   - 当前 turn 显示“第 4 步 · 运行命令”
7. HMR：修改 config 后 `plugin disposed` → 新进程 `pet window ready`

## 最终复查（2026-11-04 00:05）

- DSH 重启后插件重新加载，`backfilled 6 session(s) for 2026-11-04`。
- 屏幕上只有 1 个桌宠实例，显示真实余额 `¥0.83`、今日估算 `≈¥0.5033`、进度“第 35 步 · 运行命令”。
- 沙箱保护实测：`RestrictedReason()` 返回“低完整性级别”；对测试文件调用 `Recycle` 返回 0 moved、1 failure，文件 `Test-Path=True`。
- 普通进程实测：`RestrictedReason()` 返回空（`NORMAL_PROCESS`），回收站测试此前 moved=2 且回收站可见。
- 静态扫描：插件源码中没有 `File.Delete`/`Directory.Delete`/`SHFileOperation` 实际调用（仅注释中出现禁用说明）。

### 半尺寸复查（2026-11-04 00:57）

- 当前 profile 已设置 `scale: 0.5`，窗口从 450×538 物理像素变为 **225×269 物理像素**（180×215 DIP，@125%），长宽正好一半。
- `pet.log`：`actual=180x215.2`；截图见 `docs/screenshot.png`。
- 统计条/气泡/进度条随窗口一起缩放；统计文字仍可读，超长时省略。
- 点击形象现在由 `interactions[].sprite` 控制；`animation` 的默认状态图只作为 fallback。

## 待人工确认

1. 长按提起的手感（合成鼠标容易先触发拖拽）。
2. 多显示器、不同 DPI 组合下的贴边/位置恢复。
3. 全屏游戏/视频/演示模式下的隐藏与恢复。
4. 完成音效的主观听感与音量。
5. 网络盘/回收站禁用/超大文件的真实失败提示。
6. 正式 bundle 安装后，删除开发行，确认只加载一个实例。
