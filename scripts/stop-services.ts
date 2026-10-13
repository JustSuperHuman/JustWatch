// Let external resources (such as Docker containers) run their configured stop
// commands before the tray process and its Windows job are terminated.
export async function stopServices() {
  const base = (process.env.JUSTWATCH_CONTROL_URL ?? process.env.BUILDER_CONTROL_URL ?? "http://localhost:6767").replace(/\/$/, "");
  let response: Response;
  try { response = await fetch(`${base}/api/services`, { signal: AbortSignal.timeout(2000) }); }
  catch { return; } // JustWatch is offline.
  if (!response.ok) throw new Error(`Cannot read service status: HTTP ${response.status}`);
  const { services } = await response.json() as { services: { id: string; status: string }[] };
  for (const service of services.filter(s => s.status !== "stopped")) {
    const result = await fetch(`${base}/api/services/${encodeURIComponent(service.id)}/stop`, {
      method: "POST", body: "", signal: AbortSignal.timeout(40000),
    });
    await result.text();
    if (!result.ok) throw new Error(`Could not stop ${service.id}: HTTP ${result.status}. JustWatch remains running.`);
  }
}
