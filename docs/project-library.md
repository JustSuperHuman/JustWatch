# Project library

Open **Projects** from the tray flyout, desktop status bar, or web navigation. The library is also at `http://localhost:6767/projects` (using the configured JustWatch port).

Add an absolute watch folder, such as `F:\` or `F:\Projects`. Folders are saved in `watchFolders` in `config/justwatch-config.json`. JustWatch scans recursively on launch, when folders change, on **Rescan**, and every two minutes. Results appear during large scans. Overlapping roots are deduplicated, and unavailable drives are retried on the next scan.

The Applications library shows only projects with detected web server scripts. Compact cards show the app name, framework, directory, and local favicon (or a monogram). Apps with favicons appear first in every sort mode, followed by name, framework, or running status within each group. Unreadable icons fall back to a monogram. Search supports package/display names, paths, frameworks, package managers, and script names. Grid/list layouts, folder/running filters, and light/dark system appearance are supported.

Use the script selector and **Start** or **Stop**. Hover the selector to see the package-manager command. The terminal button opens logs; **Open** opens the running app. The menu beside Start also offers **Start & Open**, which reserves a browser tab and opens the app when its URL is reported. URL polling continues while that tab is in the foreground. If browser popups are blocked, use Open after startup. The URL comes from server output or an existing configured service URL.

The trash button **permanently deletes the project folder from disk**. A confirmation dialog shows the exact folder and the number of nested discovered apps and affected managed services; type the exact package name to enable Delete. Confirmation expires after five minutes and is invalidated when the manifest or service configuration changes. JustWatch stops and removes every managed service whose working directory is inside that folder, then deletes the contents, including dependencies, Git data, and nested projects. Junctions and symlinks are removed without traversing their targets. Drive roots, watch-folder roots, and JustWatch/configuration locations are protected. An error reports remaining files if deletion can only partially complete; services remain removed. This is separate from removing a watch folder, which preserves files and services.

Discovery never runs scripts. Starting a project revalidates its manifest, reuses a matching managed service when available, or saves a new service. New services are pinned in the tray but do not auto-start, restart after a crash, invoke agent recovery, or reclaim another process's port. They remain configurable in **Service settings** after removing a watch folder.

Package managers are detected from `packageManager`, the nearest lockfile, or a workspace ancestor. Bun, npm, pnpm, and Yarn must already be installed. Standard Windows npm/pnpm/Yarn installations run through their Node entrypoint so paths and script names are passed as arguments without shell interpolation. A missing/unsupported installation reports a useful error.

Detection checks recognized web commands (Next.js, Vite, Astro, and others), resolves local script aliases, and inspects custom Node/Bun entrypoints and their local imports for HTTP listeners. Script names or framework dependencies alone are insufficient. Build/test/lifecycle commands, CLI tools, compiler watchers, mobile-only Expo commands, and aggregate workspace tasks are excluded. Expo is included only with an explicit web launch command. Source inspection is bounded and conservative: unusually indirect or generated launchers may require a direct web script in package.json to appear. No project commands execute during discovery.

Scanning skips dependency folders, common build/cache/temp directories, Git internals, system folders, and junctions/symbolic links. Malformed/unreadable manifests are counted and reported. Limits are 30 watch folders, 200,000 traversed directories, 10,000 projects, 1 MB per manifest, and 512 KB per icon. A scan limit is reported rather than silently presenting results as complete. Icons are read from common public/static/app/assets locations; there are no external image requests. Project APIs are restricted to local callers at the JustWatch origin.

API routes:

- `GET /api/projects`: scan progress, folders, projects, scripts, and current process states.
- `POST /api/projects/folders` or `DELETE /api/projects/folders`: JSON `{ "path": "F:\\" }`.
- `POST /api/projects/scan`: request a rescan (send an empty body).
- `POST /api/projects/{id}/start` or `/stop`: JSON `{ "script": "dev" }`.
- `GET /api/projects/{id}/logs?script=dev`: buffered output.
- `GET /api/projects/{id}/icon`: discovered local icon, with an isolated SVG policy.
- `GET /api/projects/{id}/delete-preview`: exact folder, affected apps/services, and an expiring confirmation token.
- `DELETE /api/projects/{id}`: JSON `{ "token": "from-preview", "confirmation": "exact-package-name" }`; permanently deletes the confirmed project directory.

Validation: `dotnet run --project src/JustWatch.Test -- --project-tests`. For interactive testing with disposable projects and an isolated server, use `--project-test-host`, then press Enter to stop and clean up. The suite starts real HTTP servers with all four package managers and verifies their listeners close after Stop. Each service has a nested Windows job to terminate its descendants; see [Microsoft's nested-job documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs).
