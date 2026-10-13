# Configurable services

Open **Settings** in the desktop window, **Services** in the tray dashboard or tray context menu, or
the browser at `http://localhost:6767/services`.

Add, edit, remove and reorder services there, then **Save changes**.
The desktop sidebar groups services by project. Each service uses one row with
a status dot and a right-aligned port. Long names fade before the port; hover
for the full name and status. Drag the sidebar's right edge to resize it; long
lists scroll vertically. The terminal toolbar shows the project and selected
service beside the service controls. **Settings** opens JustWatch settings in
your browser. The lightning button opens Commands beside Start all / Stop all
at the bottom of the sidebar.

Live output highlights URLs, timestamps, log levels, HTTP methods, source paths,
durations, sizes, and JSON keys. Existing terminal colors take precedence, and
copied text stays unchanged. Highlighting follows wrapped lines and updates as
progress output changes; interactive alternate-screen apps keep their own colors.

Running and pinned services remain in the sidebar. Stopping a pinned service
keeps its terminal and logs selected. When an unpinned service stops, selection
moves to another visible service or Agent. Hidden services stay out of the
sidebar and tray. The web catalog and tray menu use the same project groups.

Started services always appear in the tray menu, even when `showInTray` is off.
The setting now means **Keep in tray when stopped**; it is independent of
`showTrayDot`, which controls the icon indicators. Every service remains
accessible in the desktop/web service catalog. Project and visibility edits do
not restart running processes. The four daily JustGains services are pinned by
default; other entries join the tray when started and leave when stopped.

Service names and **Open** links in the catalog, the tray's name/link button,
and the desktop's **Open** action launch the configured URL (or localhost plus
the service port). Links do not depend on running status or lifecycle controls.
For services without an HTTP endpoint, **Set link** lets you choose the URL.

Each service
can show a tray dot with its own hex color. Up to nine dots are supported; their
order follows the service list. Stopped services are gray. With no selected dots,
JustWatch uses its regular app icon. With selected services, the tray shows only
the status dots. The JustGains preset retains the original API green, Web blue
and Mobile yellow three-dot layout.

Names, projects, descriptions, groups, URLs, order, auto-start and tray settings apply without
restarting a service. Changing execution settings restarts only affected running
services. Removing a service stops it. New services start immediately only if
auto-start is selected; changing auto-start on an existing service affects the
next JustWatch launch. Existing Start All behavior still starts the auto-start set.

## Configuration

`config/justwatch-config.json` remains the source of truth. UI saves replace only
its `processes` array, preserving unrelated settings and comments. Saves check
the configuration revision and use atomic file replacement. If you edit the file
externally, restart JustWatch to load it before saving from the UI.

`bun run update` deploys to `F:\JustWatch\app` and points the runtime at
`F:\JustWatch\config\justwatch-config.json`, so UI edits survive rebuilds.
The project, runtime, configuration and Windows startup all use the F: drive.

For another project, set `projectName`, `projectPath` and `processes` in a separate
config file and launch with `JUSTWATCH_CONFIG_PATH` pointing to it. Set
`android.enabled: false` to hide emulator UI and disable Android restart hooks.
`JUSTWATCH_PROJECT_PATH` overrides the project root; `JUSTGAINS_DEV_DIR` remains
supported. The service manager itself has no required service IDs or commands.
One JustWatch instance manages one configurable catalog; entries may use different
working directories. The surrounding agent/build integrations remain separate.

```json
{
  "projectName": "Example project",
  "projectPath": "C:\\work\\example",
  "android": { "enabled": false },
  "processes": [{
    "id": "backend",
    "name": "Backend",
    "project": "Example",
    "showInTray": true,
    "command": "bun",
    "args": ["run", "dev"],
    "workingDirectory": "api",
    "port": 4100,
    "autoStart": true,
    "autoRestart": true,
    "agentRecovery": false,
    "reclaimPort": false,
    "showTrayDot": true,
    "trayColor": "#34A853"
  }]
}
```

Working directories may be absolute, relative to `projectPath`, or empty to use
the project root. Commands and argument arrays are executed directly, without
shell interpolation. Configure a shell explicitly if needed.

The editor also exposes environment variables, crash thresholds/windows,
restart output patterns, Android reload hooks, and optional `stopCommand` /
`stopArgs`. Stop commands use the same working directory and environment, run
before terminating the process tree, and have a 30-second timeout. These are
useful for Docker services whose containers live outside the Windows process
tree. Use `bun run stop` or the tray Exit action for orderly cleanup; force-killing
JustWatch cannot guarantee external-container cleanup.

## API

Service action/log routes accept any configured ID. **The list defaults to
compact active status** so agents do not receive rarely used, stopped services.
Failed services remain in the default response until explicitly stopped.
List polling projects safe status metadata directly, without cloning commands,
arguments or environment values. It does not probe or start stopped services.

Use a project filter when working in one repository. Use `scope=all` only to
discover or inspect parked services; use `detail=full` only when display metadata
is needed. Fetch `/api/services/{id}` for one known service, including a stopped
one. Invalid scope/detail values return 400 instead of expanding the response.


| Method | Route | Result |
|---|---|---|
| GET | `/api/services` | Compact active/failed service snapshots |
| GET | `/api/services?project=JustGains` | Compact active status for one project |
| GET | `/api/services?scope=all` | Discover all configured services, compact |
| GET | `/api/services?scope=all&project=CPF%20Loans` | Discover one project, including stopped services |
| GET | `/api/services?scope=all&detail=full` | All public display metadata (opt-in) |
| GET | `/api/services/{id}` | One snapshot |
| POST or GET | `/api/services/{id}/start`, `/stop`, `/restart` | Updated snapshot |
| GET | `/api/services/{id}/logs?lines=200` | Buffered output |
| GET | `/api/services/{id}/logs/stream` | SSE output |
| GET | `/api/services/configuration` | Editable services and revision |
| PUT | `/api/services/configuration` | Save `{ "revision": "…", "services": [...] }` |

Configuration endpoints require local, same-origin access and JSON for writes;
they do not enable wildcard CORS. Status snapshots omit commands/environment
secrets. Invalid definitions return 400, stale revisions 409, unknown services
404, and unsupported methods 405. A running status means the supervised process
is alive; it is not an HTTP readiness probe.

Use `web.host` / `web.port` to configure the listener. For stop/update scripts using
a non-default listener, set `JUSTWATCH_CONTROL_URL` to its local base URL.

## Configured projects

| ID | Service | Port | Startup |
|---|---|---|---|
| api | Core API | 5000 | Automatic |
| web | Web / Admin | 3000 | Automatic |
| mobile | Expo / Metro | 8081 | Automatic |
| funnel | Funnel | — | Automatic |
| nutrition | Nutrition admin | 4321 | Manual |
| crm | Creator CRM | 4330 | Manual |
| collab | Workout collaboration relay | 5055 | Manual |
| nutrition-search | Food search API | 8082 | Manual |
| transcriber | YouTube transcription / OCR worker | 8080 | Manual |
| wearables | Open Wearables Compose stack | 8000; dashboard 3001 | Manual |
| cpf-web | CPF Loans website | 4322 | Manual |
| cpf-api | CPF Loans API | 4331 | Manual |
| cpf-webadmin | CPF Loans Webadmin | 10099 | Manual |

The first ten entries belong to **JustGains**. The three `cpf-*` entries belong
to **CPF Loans** and run from `F:\cpfloans.com`. The website and API use
`FORM_DRY_RUN=true` for local development. Webadmin agent tasks require the
existing Windows Terminal Dev bridge; adding it does not start agent tasks.

Open Wearables includes its API, frontend, Celery worker/beat, Flower, Svix,
PostgreSQL and Redis. Its existing Compose file owns dependency ordering. It
requires Docker Desktop and its existing backend `.env`. The transcriber uses
`config/transcriber.compose.yml` to build and supervise an attached container.
These manual entries have port reclamation and automatic retries disabled.
Hosted media storage is not a local process; image-processing scripts are batch
tools rather than long-lived services.

## Verification

`dotnet run --project src/JustWatch.Test -- --service-tests` exercises
arbitrary projects, live configuration, restart/stop races, atomic persistence,
validation, revisions, method handling and local configuration API access.
Run `bun run update` after code changes to rebuild and relaunch the installed app.

Legacy `BUILDER_*` environment overrides remain supported for existing callers.
