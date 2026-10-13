import { $ } from "bun";
import { stopServices } from "./stop-services";
import { join, resolve } from "node:path";
import { copyFileSync, writeFileSync, existsSync, rmSync } from "node:fs";
const ROOT = resolve(import.meta.dir, "..");
const BUILD_OUT = join(ROOT, "src", "JustWatch.TrayApp", "bin", "Debug", "net10.0-windows");
const DEPLOY_DIR = join(ROOT, "app");
const DEPLOY_EXE = join(DEPLOY_DIR, "JustWatch.exe");
const RUN_KEY = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run";
async function main() {
  console.log("JustWatch — Update");
  await stopServices();
  for (const name of ["JustWatch.exe", "JustGains.Builder.TrayApp.exe", "JustGains.Builder.Watcher.exe"]) {
    await $`taskkill /F /IM ${name}`.quiet().nothrow();
  }
  await Bun.sleep(2000);
  const build = await $`dotnet build ${join(ROOT, "JustWatch.slnx")} -c Debug`.nothrow();
  if (build.exitCode !== 0) throw new Error("Build failed; startup entry was not changed.");
  // Mirror only the dedicated runtime folder, never the repository or config.
  if (resolve(DEPLOY_DIR) !== resolve(ROOT, "app")) throw new Error("Invalid runtime path");
  const copy = await $`robocopy ${BUILD_OUT} ${DEPLOY_DIR} /MIR /NFL /NDL /NJH /NJS /NP`.quiet().nothrow();
  if (copy.exitCode >= 8) throw new Error(`Deployment failed: ${copy.exitCode}`);
  // Fresh clones have no local config yet; start from the example.
  const config = join(ROOT, "config", "justwatch-config.json");
  if (!existsSync(config)) copyFileSync(join(ROOT, "config", "justwatch-config.example.json"), config);
  copyFileSync(config, join(DEPLOY_DIR, "justwatch-config.json"));
  writeFileSync(join(DEPLOY_DIR, "config-source.txt"), join(ROOT, "config", "justwatch-config.json"));
  await $`reg add ${RUN_KEY} /v JustWatch /t REG_SZ /d ${`"${DEPLOY_EXE}" --minimized`} /f`.quiet();
  await $`reg delete ${RUN_KEY} /v JustGainsBuilder /f`.quiet().nothrow();
  const startup = join(process.env.APPDATA ?? "", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
  for (const name of ["JustGains Builder.lnk", "JustWatch.lnk"]) {
    const shortcut = join(startup, name);
    if (existsSync(shortcut)) rmSync(shortcut);
  }
  const legacy = await $`sc.exe query JustGains.Builder.Watcher`.quiet().nothrow();
  if (legacy.exitCode === 0) {
    await $`sc.exe stop JustGains.Builder.Watcher`.quiet().nothrow();
    const disabled = await $`sc.exe config JustGains.Builder.Watcher start= disabled`.quiet().nothrow();
    if (disabled.exitCode !== 0) console.warn("Legacy watcher could not be disabled; run scripts/uninstall-service.ps1 as administrator.");
  }
  const proc = Bun.spawn([DEPLOY_EXE], {
    cwd: DEPLOY_DIR, stdio: ["ignore", "ignore", "ignore"], detached: true,
  });
  proc.unref();
  await Bun.sleep(2000);
  if (proc.exitCode !== null) throw new Error(`JustWatch exited during launch (${proc.exitCode})`);
  console.log(`JustWatch running: ${DEPLOY_EXE}\nStartup: HKCU Run → JustWatch\nConfig: ${join(ROOT, "config", "justwatch-config.json")}`);
}
main().catch(e => { console.error(e.message); process.exitCode = 1; });
