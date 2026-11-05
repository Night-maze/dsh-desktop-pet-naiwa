# 桌宠-奶娃小时候 (DSH Desktop Pet)

A Windows desktop companion built as a DeepSeek Harness plugin. It renders a transparent, frameless, always-on-top WPF window and reports real Agent progress, today's estimated Token cost, and the real Platform wallet balance. It supports click interactions, long-press drag, edge hiding, and drag-and-drop files **to the Windows Recycle Bin**.

中文完整说明：[README.zh.md](README.zh.md)。

## Why a Host plugin plus a WPF child process

DSH client plugins are browser-side React UI running in a sandbox; they cannot create native OS windows. The plugin therefore consists of:

- a Cordis **Host plugin** (`lib/index.js`) that observes `session/event`, maintains a replay-aware daily token ledger, reads the real account balance, and owns a child process;
- a **WPF subprocess** (`pet/DesktopPet.ps1` + `DesktopPet.cs`) that renders the pet and performs gestures/file drops;
- an authenticated loopback newline-JSON bridge between them.

## Features

- Agent `turn/start`, `step/start`, `tool/call`, `turn/end`, `agent/error` projection.
- Progress bar plus step/tool text; no invented percentages.
- Completion sound (original cute-monster WAVs), failure/cancel cues.
- Today's token totals and estimated cost from public list prices (peak/off-peak aware); UI labels the result with `≈`.
- Real balance via `ctx.deepseekAccount.getBalance`; failures keep the last value as stale, never zero.
- Six built-in click interactions (blink, jump, shake, heart, random line, dance), configurable weights/texts.
- Long-press pickup with pendulum/spring drag, natural release, edge half-hide with hover slide-out.
- Transparent hit-testing: transparent pixels pass clicks through.
- Drag-and-drop files/directories to the Recycle Bin via `IFileOperation` + `FOFX_RECYCLEONDELETE`; no permanent-delete fallback. AppContainer/low-integrity processes are refused.
- Configurable image/audio assets, thresholds, refresh intervals, currency, precision, pricing, protected paths.
- A client half registers a **🐾 桌宠** sidebar entry and a settings panel for scale, volume, stats display, drag thresholds, refresh/restart/show/hide. See [docs/client-plugin.md](docs/client-plugin.md).

## Quick start

Install from npm:

```powershell
dsh plugin --profile desktop add dsh-desktop-pet-naiwa
```

Or install a local checkout:

```powershell
dsh plugin --profile desktop add link:F:\path\to\dsh-desktop-pet-naiwa
```

Then enable the `dsh-desktop-pet-naiwa` bundle in **Plugins** (or add it to `dsh.profile.bundles`), remove any absolute-path development row, and restart/reload. After the first client-graph scan, the sidebar shows **🐾 桌宠**; refresh the page if it is not there yet.

See [README.zh.md](README.zh.md) for installation, configuration, security guarantees, tests, acceptance, and known limitations. Full config sample: [config.example.yml](config.example.yml). Acceptance checklist: [docs/acceptance.md](docs/acceptance.md).

## Tests

```powershell
node test/run-tests.mjs          # host unit tests
node test/mock-host.mjs          # real WPF + mock Cordis integration
powershell -STA -File scripts/demo.ps1
```

The recycle test must run in a normal user process, not the DSH workspace sandbox:

```powershell
powershell -STA -File scripts/test-recycle.ps1
```

## Safety boundary

The only file-removal path is the Windows Shell recycle API with `FOFX_RECYCLEONDELETE`. If the Shell cannot recycle, the operation fails and the original stays. The pet refuses drive roots, system directories, reparse points, and restricted (AppContainer/low-integrity) processes. Cost is an estimate, balance is real, and progress is not fabricated.
