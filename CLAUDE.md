# JustWatch

Android development uses MuMu Player. See [MuMu integration](docs/mumu.md) for
configuration, stable instance IDs, lifecycle controls, and regression checks.

.NET 10 WPF tray application that orchestrates the JustGains development environment — process management, AI agent task system, screen recording, builds, and Telegram integration.

## Project Structure

```
src/
  JustWatch.Core/       # Shared library — models, services, task manager
  JustWatch.TrayApp/    # WPF tray app with WebView2 terminal UI
  JustWatch.Test/       # Test console app
config/
  justwatch-config.json           # All configuration (processes, chat, telegram, etc.)
scripts/
  update.ts                     # Full dev loop: stop → build → deploy to app/ → launch
  start.ts / stop.ts            # Quick start/stop
  uninstall-startup.ps1         # Remove the login auto-start entry
```

## Dev Loop

```bash
bun run update    # Stop running app, rebuild, deploy, relaunch — the standard dev cycle
bun run stop      # Just stop the app
bun run start     # Just launch (no rebuild)
```

Always use `bun run update` after making code changes. It handles killing the old process, waiting for file locks, building, deploying, and relaunching.

The project lives at `F:\JustWatch`. `bun run update` deploys the build to `app/` and registers the `JustWatch` HKCU Run entry pointing to `F:\JustWatch\app\JustWatch.exe --minimized`. The canonical configuration is `config/justwatch-config.json`; the runtime uses `app/config-source.txt` to load and save that file. `bun run uninstall-startup` removes the startup entry.

## Dev Services (status, control, logs)

Use the **justgains-dev-server** skill whenever you need to check, start/stop/restart, or
read/stream the logs of the dev services (api, web, mobile, funnel). It covers the JustWatch's
control API at `http://localhost:6767` — including `GET /api/services/{id}/logs?lines=N` for
log dumps and `GET /api/services/{id}/logs/stream` (SSE) for live output. Don't hand-roll
curl calls or hunt for log files; invoke the skill.

## Telegram Status Updates

`POST http://localhost:6767/api/telegram/messages` posts to any group configured
under `telegram.groups` in `justwatch-config.json`. Easiest path:

```bash
bun run tg "Deploy finished"                  # default group
bun run tg --group events "Build 104 is live" # a named group ("all" broadcasts)
bun run tg --list                             # what's configured
bun run tg --file shot.png "Login screen"     # photo/video/document (POST /api/telegram/files)
```

Adding a destination is a config entry, not a code change — see
[Telegram status updates](docs/telegram.md) for fields, per-group bot tokens,
forum topics and how to find a chat id.

## Build

```bash
dotnet build JustWatch.slnx          # Build all projects
dotnet build JustWatch.slnx -c Debug # Same (Debug is default)
```

## Architecture

- **ProcessManager** manages dev processes (bun run api/web/mobile/funnel) with auto-restart and crash detection
- **ChatSession** is the orchestrator — decomposes user requests via Sonnet (OpenRouter), creates task chains
- **TaskManager** executes tasks with dependency resolution and concurrent agent support (SemaphoreSlim(2) for CLI slots)
- **TelegramService** polls for messages and sends convo replies + video recordings
- **ScreenRecorder** captures desktop via FFmpeg (bundled with ShareX)

### Agent Model

- **Opus** (Claude CLI `--model opus`): Complex coding tasks, build error fixes
- **Sonnet** (Claude CLI `--model sonnet`): Test execution with MCP tools
- **Sonnet** (OpenRouter API): Orchestration decisions — intent classification, test planning, window picking

### Task Types

`Chat` | `Build` | `Test` | `Plan` | `Record` | `Agent` — each with its own process, CancellationToken, and output buffer.

## Config

All in `config/justwatch-config.json`. Key sections: `processes`, `chat` (OpenRouter + Claude CLI), `telegram`, `agent`, `watcher`, `notifications`.

## Testing Changes

After modifying code, always run `bun run update` to rebuild and relaunch. To test the chat/agent pipeline programmatically, use the test project:

```bash
dotnet run --project src/JustWatch.Test
```

## Distribution

- Version lives in `Directory.Build.props`. Push a `vX.Y.Z` tag and `.github/workflows/release.yml`
  publishes `JustWatch-win-x64.zip` (self-contained) to GitHub Releases.
- `install.ps1` / `uninstall.ps1` are the `irm | iex` installers; installed copies live in
  `%LOCALAPPDATA%\Programs\JustWatch\app` and read config from `%APPDATA%\JustWatch`.
- `UpdateService` checks releases; the About window (tray menu, flyout info button, sidebar) installs them.
- Agent-facing docs: `llms.txt` and `skills/justwatch/SKILL.md`. They are embedded and served at
  `/llms.txt`, `/skill` and `/api`, and copied next to `JustWatch.exe`. Keep them in sync with API changes.
- `config/justwatch-config.json` holds secrets and is git-ignored; `config/justwatch-config.example.json` is the template.
