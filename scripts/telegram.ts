/**
 * Post a status update to Telegram through the JustWatch's relay.
 *
 *   bun run tg "Deploy finished"                  → default group
 *   bun run tg --group events "Build 104 is live"
 *   bun run tg --group all "Nightly job finished"
 *   bun run tg --list                             → show configured groups
 *   bun run tg --file shot.png "Login screen"     → photo with caption
 *   bun run tg --file J:\ads\big.mp4 "Final cut"  → >50 MB is hosted on m.justgains.com, sent as a link
 *   bun run tg --file https://…/clip.mp4 "Demo"   → Telegram fetches the URL (S3 etc.)
 *
 * Flags: --group/-g <id[,id]>  --file/-f <path|url>  --type <photo|video|audio|document|…>
 *        --silent  --no-preview  --html  --url <base>
 *
 * Groups are configured in config/justwatch-config.json under telegram.groups.
 * The JustWatch holds the bot tokens; nothing here reads or prints them.
 */
import { basename, resolve } from "node:path";

const BASE = process.env.JUSTWATCH_URL ?? process.env.BUILDER_URL ?? "http://localhost:6767";

const argv = Bun.argv.slice(2);
const opts = {
  group: [] as string[], silent: false, preview: true, html: false, list: false, base: BASE,
  file: "", type: "",
};
const words: string[] = [];

for (let i = 0; i < argv.length; i++) {
  const arg = argv[i];
  if (arg === "--group" || arg === "-g") opts.group.push(...(argv[++i] ?? "").split(","));
  else if (arg.startsWith("--group=")) opts.group.push(...arg.slice(8).split(","));
  else if (arg === "--silent") opts.silent = true;
  else if (arg === "--no-preview") opts.preview = false;
  else if (arg === "--html") opts.html = true;
  else if (arg === "--list") opts.list = true;
  else if (arg === "--file" || arg === "-f") opts.file = argv[++i] ?? "";
  else if (arg === "--type") opts.type = argv[++i] ?? "";
  else if (arg === "--url") opts.base = argv[++i] ?? BASE;
  else words.push(arg);
}

async function main() {
  if (opts.list) {
    const res = await fetch(`${opts.base}/api/telegram/groups`);
    const body: any = await res.json();
    if (!res.ok) throw new Error(body?.error ?? `HTTP ${res.status}`);
    for (const g of body.groups ?? [])
      console.log(`${g.id.padEnd(12)} ${g.name}${g.isDefault ? "  (default)" : ""}`);
    if (!body.groups?.length) console.log("No groups configured (telegram.groups in justwatch-config.json).");
    return;
  }

  const text = words.join(" ").trim();
  if (!text && !opts.file) {
    console.log('Usage: bun run tg [--group <id>] [--file <path|url>] "message"   ·   bun run tg --list');
    process.exit(1);
  }

  const res = opts.file ? await sendFile(text) : await fetch(`${opts.base}/api/telegram/messages`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      text,
      groups: opts.group,
      silent: opts.silent,
      disableWebPagePreview: !opts.preview,
      parseMode: opts.html ? "HTML" : "none",
    }),
  });

  const body: any = await res.json().catch(() => null);
  if (!res.ok || !body?.ok) {
    const detail = body?.error ?? (body?.failed ?? []).map((f: any) => `${f.group}: ${f.error}`).join("; ");
    throw new Error(`Telegram relay failed (HTTP ${res.status}): ${detail || "unknown error"}`);
  }
  console.log(`sent → ${(body.sent ?? []).map((s: any) => (s.type ? `${s.group} (${s.type})` : s.group)).join(", ")}`);
  const link = body.sent?.find((s: any) => s.url)?.url;
  if (link) console.log(`link: ${link}`);
}

/**
 * URLs are fetched by Telegram itself. Local files go as an absolute path when the
 * JustWatch is on this machine (it streams from disk, and >50 MB goes to storage
 * without buffering), otherwise as an upload.
 */
async function sendFile(caption: string) {
  const fields: Record<string, string | string[]> = {
    groups: opts.group,
    silent: String(opts.silent),
    parseMode: opts.html ? "HTML" : "none",
  };
  if (caption) fields.caption = caption;
  if (opts.type) fields.type = opts.type;

  const isUrl = /^https?:\/\//i.test(opts.file);
  const isLocalJustWatch = /^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/i.test(opts.base);

  if (!isUrl) {
    const file = Bun.file(opts.file);
    if (!(await file.exists())) throw new Error(`File not found: ${opts.file}`);
  }

  if (isUrl || isLocalJustWatch) {
    const source = isUrl ? { url: opts.file } : { path: resolve(opts.file) };
    return fetch(`${opts.base}/api/telegram/files`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...fields, ...source }),
    });
  }

  const form = new FormData();
  form.set("file", Bun.file(opts.file), basename(opts.file));
  for (const [key, value] of Object.entries(fields))
    for (const v of [value].flat()) form.append(key === "groups" ? "group" : key, v);
  return fetch(`${opts.base}/api/telegram/files`, { method: "POST", body: form });
}

main().catch((err) => {
  const hint = String(err).includes("fetch")
    ? "\nIs the JustWatch running? Start it with: bun run start"
    : "";
  console.error(`${err instanceof Error ? err.message : err}${hint}`);
  process.exit(1);
});
