import { join, resolve } from "node:path";
import { existsSync } from "node:fs";
const root = resolve(import.meta.dir, "..");
const exe = join(root, "app", "JustWatch.exe");
if (!existsSync(exe)) throw new Error("JustWatch is not built yet. Run bun run update.");
const proc = Bun.spawn([exe], {
  cwd: join(root, "app"), stdio: ["ignore", "ignore", "ignore"], detached: true,
});
proc.unref();
await Bun.sleep(1500);
if (proc.exitCode !== null && proc.exitCode !== 0) throw new Error(`JustWatch exited (${proc.exitCode})`);
console.log(`JustWatch started: ${exe}`);
