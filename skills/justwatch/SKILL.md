---
name: justwatch
description: >-
  Manage local dev services supervised by JustWatch, the Windows tray app with a
  control API at http://localhost:6767. Use this skill whenever the user wants to
  check whether a local server/service is running, see its PID or port, start,
  stop or restart it, read or tail its logs, or add, edit or remove a service
  JustWatch should keep running ("is the api up?", "restart the web server",
  "show me the backend logs", "add my vite app to JustWatch", "stop everything").
  Also use it when a local app behaves as if its server is down or serving stale
  code. Covers the HTTP API, PowerShell/curl usage and what to do when JustWatch
  itself is not running.
---

# JustWatch: managing local dev services

JustWatch is a Windows tray app that starts, watches and auto-restarts local
dev servers ("services"). Everything you can do in its UI is exposed through a
small HTTP API on this computer. Act through the API, then confirm the new state
from the API response. Never assume an action worked.

- Base URL: `http://localhost:6767` (port is `web.port` in the config; 6767 by default)
- No auth. Requests must come from this machine.
- Live agent docs: `GET /llms.txt`. Endpoint index: `GET /api`.

## Calling the API

`GET` works for every read and every start/stop/restart action. Prefer it: a
body-less `POST` is rejected by HTTP.sys with `411 Length Required` unless you
send `Content-Length: 0`.

In PowerShell, `curl` is an alias for `Invoke-WebRequest`. Use `curl.exe` or:

```powershell
Invoke-RestMethod http://localhost:6767/api/services
Invoke-RestMethod http://localhost:6767/api/services/api/restart
```

## Status

| Method | Path | Returns |
| --- | --- | --- |
| GET | `/api/services` | Active or failed services only (compact) |
| GET | `/api/services?scope=all` | Every configured service, including stopped ones |
| GET | `/api/services?project=<name>` | Filter by project label (combine with `scope=all`) |
| GET | `/api/services?scope=all&detail=full` | Adds display metadata (description, URL, tray settings) |
| GET | `/api/services/{id}` | One service |

Each service snapshot looks like:

```json
{ "id": "api", "name": "API", "project": "My app", "status": "running",
  "running": true, "pid": 59364, "port": 5000 }
```

`status` is one of `stopped | starting | running | stopping | crashed | recoveringwithagent`.
`running` is the quick check. `pid` is null when stopped. Stopped services are
omitted from the default list, not deleted. Use `scope=all` to find them.

## Start, stop, restart

| Path | Does |
| --- | --- |
| `/api/services/{id}/start` | Start (reclaims the port first if something else holds it) |
| `/api/services/{id}/stop` | Stop the whole process tree |
| `/api/services/{id}/restart` | Stop, then start |

Each call returns the service's new snapshot. After a restart, a new `pid`
proves it came back. There is no bulk endpoint: loop over ids, then re-check
`/api/services`.

## Logs

Each service keeps a buffer of recent output (ANSI colour codes included; strip
`\x1b[...m` before quoting).

```bash
curl -s "http://localhost:6767/api/services/api/logs?lines=100"   # last 100 lines
curl -sN --max-time 30 http://localhost:6767/api/services/api/logs/stream   # live SSE
```

The stream sends `event: backlog` with history first. Each later `data:` event is
a JSON-encoded string chunk. A `: ping` arrives every 15 s. Always use a timeout.
For debugging, a `?lines=N` dump is usually enough.

## Add, edit or remove services

The service list is read and written as a whole, guarded by a revision hash:

1. `GET /api/services/configuration` returns `{ projectName, projectPath, revision, services: [...] }`.
2. Change the `services` array: append, edit or remove entries.
3. `PUT /api/services/configuration` with `Content-Type: application/json` and
   body `{ "revision": "<revision from step 1>", "services": [...] }`.

JustWatch saves the config file, keeps comments and unrelated settings, and
applies the change live. Only services whose command changed restart. A
removed service is stopped. A new service starts only if `autoStart` is true;
otherwise call `/start`. A `409` means the config changed meanwhile: GET again and redo.

Service fields (only `id`, `name`, `command` are required):

| Field | Meaning |
| --- | --- |
| `id` | Lowercase letters, digits, hyphens; starts with a letter. Used in URLs. |
| `name` | Display name |
| `command`, `args` | Executable and argument array, e.g. `"bun"`, `["run","dev"]` |
| `workingDirectory` | Absolute path. Empty = config `projectPath` |
| `port` | Port it listens on. Unique. Enables port reclaim + the Open link |
| `project`, `group`, `description` | Labels used to group the UI |
| `autoStart` | Start when JustWatch launches (default true) |
| `autoRestart` | Restart on crash (default true). `crashThreshold`/`crashWindowSeconds` cap retries |
| `restartOutputPatterns` | Restart when output contains one of these strings |
| `environment` | Extra environment variables, e.g. `{ "NODE_ENV": "development" }` |
| `url` | Link to open, if not `http://localhost:<port>` |
| `showInTray`, `showTrayDot`, `trayColor` | Keep the service in the tray menu while stopped; draw a coloured status dot on the tray icon |
| `reclaimPort` | Default true: kill whatever already listens on `port` before starting |

PowerShell example that adds a service and starts it:

```powershell
$cfg = Invoke-RestMethod http://localhost:6767/api/services/configuration
$svc = [ordered]@{ id = 'docs'; name = 'Docs'; project = 'My app'; command = 'npm'
                   args = @('run','dev'); workingDirectory = 'C:\code\my-app\docs'; port = 4000; autoStart = $false }
$body = @{ revision = $cfg.revision; services = @($cfg.services) + $svc } | ConvertTo-Json -Depth 8
Invoke-RestMethod http://localhost:6767/api/services/configuration -Method Put -ContentType 'application/json' -Body $body
Invoke-RestMethod http://localhost:6767/api/services/docs/start
```

Ask the user before removing a service or changing one you didn't create.

## Other endpoints

| Path | Does |
| --- | --- |
| `GET /api/notifications[?since=<id>]` | Recent status-change events (crashes, recoveries, starts) |
| `GET /api/projects` | Project Library: projects found in the configured watch folders |
| `POST /api/telegram/messages` | Send a Telegram status message, if Telegram is configured (`{"text":"...","group":"..."}`) |
| `GET /api/telegram/groups` | Configured Telegram destinations |

## When `localhost:6767` doesn't answer

The API exists only while the tray app runs. If you get connection refused or a timeout:

1. Start it: `Start-Process "$env:LOCALAPPDATA\Programs\JustWatch\app\JustWatch.exe" -ArgumentList '--minimized'`
   (installed copy), or the Start Menu shortcut **JustWatch**. Then wait about 3 s and retry.
2. Not installed? It installs per-user with no admin rights. Ask the user first, then run:
   `irm https://raw.githubusercontent.com/JustSuperHuman/JustWatch/main/install.ps1 | iex`
3. Port changed? Read `web.port` from the config file.

Distinguish the two failures for the user:
- **Can't reach :6767**: JustWatch is down. Start it.
- **:6767 answers but the service is `stopped`/`crashed`**: JustWatch is fine. Start or restart
  the service, then read its logs if it crashes again.

## Files

| What | Where |
| --- | --- |
| Config (installed copy) | `%APPDATA%\JustWatch\justwatch-config.json` (JSON with comments) |
| App | `%LOCALAPPDATA%\Programs\JustWatch\app\` |
| Agent docs next to the app | `AGENTS.md`, `llms.txt`, `skills\justwatch\SKILL.md` |
| Source, issues, releases | https://github.com/JustSuperHuman/JustWatch |

Prefer the API over editing the config file: the API validates and applies
changes live. If you edit the file by hand, the user must restart JustWatch
(tray menu > Exit, then launch again).
