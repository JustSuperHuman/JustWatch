import { $ } from "bun";
import { stopServices } from "./stop-services";
console.log("Stopping JustWatch...");
await stopServices();
await $`taskkill /F /IM JustWatch.exe`.quiet().nothrow();
console.log("JustWatch stopped.");
