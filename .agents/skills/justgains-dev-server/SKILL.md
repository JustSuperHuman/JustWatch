---
name: justgains-dev-server
description: >-
  Manage the JustGains dev server processes (api, web, mobile, funnel) through the
  JustWatch control API at http://localhost:6767. Use this skill whenever the
  user wants to check whether the dev servers are running or inspect their
  status/PIDs/ports, or to start, stop, or restart any of them — e.g. "is the api up?",
  "restart mobile", "stop the web server", "are my dev servers running?", "bounce the
  funnel", "kill the api and bring it back", "restart everything". Also reach for it when
  the JustGains app is misbehaving as if a server is down or serving stale code and you
  need to inspect or recycle a process. Covers the HTTP control API plus the bun-script
  fallback for when the JustWatch tray app itself isn't running.
---

# JustGains Dev Server Management

The JustGains dev environment runs four long-lived processes, supervised by the
**JustWatch** tray app. The JustWatch exposes a small, unauthenticated HTTP
control API so you can read status and start/stop/restart each process without
clicking through the tray menu.

This skill is for operating that API. The whole point is to act on the servers
quickly and report back what actually happened — so always confirm the new state
from the API response rather than assuming an action succeeded.

## The services

| id       | name   | what it is                          | port  |
| -------- | ------ | ----------------------------------- | ----- |
| `api`    | API    | `bun run api` — backend API         | 5000  |
| `web`    | Web    | `bun run web` — web frontend        | 3000  |
| `mobile` | Mobile | `bun run mobile` — Expo/Metro       | 8081  |
| `funnel` | Funnel | `bun run funnel` — marketing funnel | (n/a) |

Use the **id** (lowercase) in API paths. "the api server", "backend", "API" → `api`;
"web", "frontend", "website" → `web`; "mobile", "the app", "metro", "expo" → `mobile`;
"funnel" → `funnel`.

## The control API

Base URL: `http://localhost:6767`. No auth.

| Method     | Path                          | Does                                  |
| ---------- | ----------------------------- | ------------------------------------- |
| GET        | `/api/services`               | Status of all four services          |
| GET        | `/api/services/{id}`          | Status of one service                |
| POST / GET | `/api/services/{id}/start`    | Start the service                    |
| POST / GET | `/api/services/{id}/stop`     | Stop the service                     |
| POST / GET | `/api/services/{id}/restart`  | Stop, then start the service         |

Every status/action response is a JSON snapshot of the affected service:

```json
{ "id": "api", "name": "API", "status": "running", "running": true,
  "pid": 59364, "port": 5000, "startedAt": "2026-06-03T14:15:46Z" }
```

`status` is one of `stopped | starting | running | stopping | crashed | recoveringwithagent`.
The boolean `running` is the quick check; `pid` and `startedAt` are null when not running.
`GET /api/services` wraps the four snapshots in `{ "services": [ ... ] }`.

### Calling it

`GET` and `POST` both work for the action endpoints, so the simplest reliable call
is a plain GET — it sidesteps an HTTP.sys quirk where a **body-less POST** is rejected
with `411 Length Required`. Prefer GET unless you have a reason to POST (and if you do
POST with no body, send `Content-Length: 0`).

```bash
# Status of everything
curl -s http://localhost:6767/api/services

# Status of one
curl -s http://localhost:6767/api/services/mobile

# Start / stop / restart (GET form — no body, no 411)
curl -s http://localhost:6767/api/services/api/restart
```

On Windows PowerShell, `curl` is an alias for `Invoke-WebRequest`. Use `curl.exe` for the
syntax above, or the native form:

```powershell
Invoke-RestMethod http://localhost:6767/api/services
Invoke-RestMethod http://localhost:6767/api/services/api/restart
```

## Workflows

**Check status** — GET `/api/services`, then summarize each service as running/stopped
with its pid and port. Lead with whatever the user asked about.

**Restart / stop / start one** — call the action endpoint for that id, then report the
returned snapshot so the user sees the resulting state (e.g. a fresh `pid` after a
restart confirms it actually came back).

**Restart everything** — there's no bulk endpoint; call `restart` for each id. Doing them
in sequence is fine. After, GET `/api/services` and confirm all four are `running`.

**Mobile note** — restarting `mobile` automatically reloads the JustGains app on a running
Android emulator (the JustWatch waits for Metro to come back, then relaunches the app so it
reconnects to the fresh bundler). If an emulator is attached, mention that the app was
reloaded; if not, it's simply skipped.

## When the API isn't responding

If a request to `http://localhost:6767` fails to connect (connection refused / timeout),
the JustWatch tray app almost certainly isn't running — the API only exists while it's up.
The fix lives in the **justwatch** repo, not the dev app repo:

```bash
# From F:\JustWatch
bun run start     # just launch the tray app (no rebuild)
bun run update    # stop, rebuild, relaunch — use after JustWatch code changed
```

`bun run start` is the lighter option to simply bring the JustWatch (and the API) back up.
Use `bun run update` if the JustWatch itself was modified and needs rebuilding. After either,
re-check `http://localhost:6767/api/services`.

Distinguish the two failure modes for the user:
- **Can't reach :6767** → the JustWatch/tray app is down → run a bun script above.
- **:6767 answers but a service shows `stopped`/`crashed`** → the JustWatch is fine; just
  start/restart that service via the API.
