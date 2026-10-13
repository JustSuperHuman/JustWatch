# MuMu Player integration

JustWatch manages MuMu Player through its installed `MuMuManager.exe`. Android SDK
platform-tools and build tools are still used; Android Studio AVDs are not used.
No AVDs or user data are migrated, renamed, or deleted by this change.

`android` configuration in `config/justwatch-config.json`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `muMuManagerPath` | empty | Discover the running MuMu installation, registry, or standard install location. Set an absolute path for a custom installation. |
| `coreInstanceIndex` | `0` | Protected source instance; cannot be deleted. |
| `emulatorNamePrefix` | `JG` | Manage the core and instances named `JG-<number>`. Other MuMu instances are left alone. |
| `adbPath` | `adb` | Shared Android SDK ADB client. Use the same client for development scripts. |

Names come directly from MuMu (`JG-1` through `JG-4` on this machine). API IDs are
`mumu-0` through `mumu-3`; `index` is MuMu's zero-based index. Friendly names and old
`JustGains-1` style aliases are accepted by JustWatch action endpoints. `avdName` is
retained as a compatibility field containing the stable ID; it is not an AVD name.

Existing `/api/android` routes are retained. Read `/emulators` for names, serials,
`provider: "mumu"`, `isRunning`, `isReady`, idle time, and last command. `isRunning`
means the MuMu process exists; `isReady` means its Android ADB connection is ready.
Read `/dashboard` for host resources, process stats, guest/app memory, and tuning.

Launch/start-all wait for Android boot and ADB readiness. Restart-all restarts only
instances that were running. Discovery connects each running MuMu instance to its
reported local ADB port; old AVDs and unrelated attached devices are excluded.
Metro and the local API (port 5000) use `adb reverse` and `127.0.0.1`, with both
tunnels recreated after launch and before app reload. Local Android Metro sessions
use `http://127.0.0.1:5000/api`; remote-device sessions retain their configured API.
Restarting the JustWatch-managed mobile service reloads apps.

Clone Core briefly stops the core, copies it using MuMu, and restores its previous
running state even on failure. The new clone starts stopped. Deletion is restricted
to managed non-core instances and waits for MuMu to finish removal. Tuning uses
MuMu settings: RAM in MB, CPU cores, and Vulkan (`vk`) / DirectX (`dx`). It is
validated before writing, verified afterward, and applies on the next MuMu restart.

The tray, native emulator panel, browser panel, and mobile recording window picker
use MuMu. `bun run mobile:android:all` in `J:\justgains` builds once and installs to
ready MuMu devices returned by JustWatch. The default single-device command picks a
ready MuMu device; explicit `--device` still works. JustWatch must be running.

Validation:

```powershell
dotnet run --project src/JustWatch.Test -- --android-tests
bun run update
Invoke-RestMethod http://localhost:6767/api/android/emulators
```

Mobile installer regression tests: `bun test scripts/run-android.test.js` from
`J:\justgains\JustGains-Mobile`. Use a disposable clone for lifecycle/tuning tests;
never delete an existing user instance to test removal.
