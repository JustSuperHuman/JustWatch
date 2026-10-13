# Telegram status updates

The JustWatch posts messages and files to any configured Telegram group, so
scripts, CI steps and agents can announce a build/deploy/result without ever
holding a bot token.

```
POST http://localhost:6767/api/telegram/messages   text
POST http://localhost:6767/api/telegram/files      photo / video / audio / document
GET  http://localhost:6767/api/telegram/groups     what's configured
```

## Send

```bash
bun run tg "Deploy finished"                    # default group
bun run tg --group events "Build 104 is live"   # a named group
bun run tg --group me,events "Nightly job done" # several at once
bun run tg --group all "Heads up"               # every group
bun run tg --list                               # what's configured
```

Flags: `--group/-g <id[,id]>` · `--silent` (no notification sound) ·
`--no-preview` (suppress link previews) · `--html` (parse HTML tags) ·
`--url <base>` (default `http://localhost:6767`, or `$JUSTWATCH_URL`).

Or straight HTTP:

```bash
curl -X POST http://localhost:6767/api/telegram/messages \
  -H "Content-Type: application/json" \
  -d '{"group":"events","text":"Build 104 is live","disableWebPagePreview":true}'
```

Every field except `text` is optional:

| Field | Default | Notes |
| --- | --- | --- |
| `text` (or `message`) | — | Required. Chunked automatically past Telegram's 4096-char limit. |
| `group` / `groups` | `telegram.defaultGroup` | A string, an array, or `"all"`. |
| `parseMode` | `none` | `HTML` or `MarkdownV2`. Leave off for plain text. |
| `disableWebPagePreview` | `false` | Suppresses the link preview card. |
| `silent` | `false` | Delivers without a notification sound. |

The same fields also work as query parameters (`?group=events&text=...`) for
one-liners that would rather not build a JSON body.

Responses: `200` all sent, `207` some sent, `502` every destination rejected,
`400` unknown group or missing text, `503` no groups configured.

```json
{ "ok": true, "sent": [{ "group": "events", "messageId": 2412 }], "failed": [] }
```

## Send a file or media

```bash
bun run tg --file shot.png "Login screen"                 # photo; the message becomes the caption
bun run tg -g events --file build.apk "Build 105"         # document
bun run tg --file 'J:\ads\final.mp4' "Final cut"          # video; >50 MB arrives as an m.justgains.com link
bun run tg --file https://…/clip.mp4 "Demo"               # Telegram fetches the URL itself
```

`--type photo|video|animation|audio|voice|document` overrides detection. Any
path style works, relative or absolute.

### Size — automatic

| Size | What happens |
| --- | --- |
| ≤ 50 MB | Uploaded to Telegram as a real photo/video/document. |
| > 50 MB | Uploaded to the `telegram.storage` bucket (Tigris `justgains`, key `telegram/yyyy/MM/dd/…`) and posted as `caption + 📎 name (size) + link`. `sent[].type` is `"link"`, `sent[].url` the link. |
| `url` Telegram can't fetch (> 20 MB, > 5 MB photo, …) | Posted as a link instead. |

Uploads go at your uplink speed (~3 Mbps: 35 MB ≈ 100 s), so the request holds
until it's done.

### Videos keep their shape

Before sending a video or GIF, the JustWatch runs ffmpeg (`chat.ffmpegPath`) to
read its display width/height (rotation and non-square pixels applied), its
duration, and a 320px thumbnail, and passes them to Telegram. Without them
Telegram previews portrait video squashed into a landscape box. If ffmpeg can't
read the file, the video still goes out without the hints.

### The route

`POST /api/telegram/files` accepts the file however it's easiest to send:

```bash
# local file by path — no JSON, no escaping (easiest from any shell)
curl -F 'path=J:\ads\final.mp4' -F group=me -F caption="Final cut" http://localhost:6767/api/telegram/files

# upload the bytes (other fields ride along)
curl -F file=@shot.png -F caption="Login screen" http://localhost:6767/api/telegram/files

# JSON — Content-Type optional; forward slashes, Git Bash /j/…, and even
# unescaped backslashes ({"path":"J:\ads\x.mp4"}) are all understood
curl -d '{"path":"J:/ads/final.mp4","group":"me"}' http://localhost:6767/api/telegram/files
curl -d '{"url":"https://…/clip.mp4","group":"events"}' http://localhost:6767/api/telegram/files

# raw body — name via ?name= (or the Content-Type is used to guess)
curl --data-binary @shot.png "http://localhost:6767/api/telegram/files?name=shot.png"
```

| Field | Default | Notes |
| --- | --- | --- |
| `file` / `path` / `url` / body | — | One source. `path` is read on the JustWatch machine (not accepted from browser pages); `url` must be http(s). |
| `name` | from the upload / path / URL | Filename shown in Telegram; drives type detection. |
| `type` | `auto` | `photo`, `video`, `animation`, `audio`, `voice`, `document`. |
| `caption` (or `text`) | — | Past Telegram's 1024-char caption limit it follows as a separate message. |
| `group` / `groups`, `parseMode`, `silent` | as for messages | |

Any field can also be a query parameter. `auto` maps by extension:
jpg/png/webp → photo · gif → animation · mp4/mov/m4v → video ·
mp3/m4a/aac/flac/wav → audio · ogg/opus → voice · anything else → document. If
Telegram rejects the media type it's retried once as a document.

```json
{ "ok": true, "sent": [{ "group": "me", "messageId": 144, "type": "video" }], "failed": [] }
{ "ok": true, "sent": [{ "group": "me", "messageId": 145, "type": "link", "url": "https://m.justgains.com/telegram/…" }], "failed": [] }
```

Uploaded bytes (`file` / raw body) are buffered in memory and capped at 512 MB;
`path` streams from disk and has no cap. Without `telegram.storage` configured,
anything over 50 MB gets a `413`/`502` explaining that.

### Storage config

```jsonc
"telegram": {
  "storage": {
    "serviceUrl": "https://t3.storage.dev",    // any S3-compatible endpoint
    "accessKey": "…", "secretKey": "…",        // same Tigris keys as the JustGains API
    "bucket": "justgains",
    "publicBaseUrl": "https://m.justgains.com", // public URL = this + "/" + key
    "keyPrefix": "telegram"
  }
}
```

## Adding a group

Add an entry under `telegram.groups` in `config/justwatch-config.json` and restart
(`bun run update`). No code change:

```jsonc
{
  "id": "ops",                  // what callers pass as "group"
  "name": "Ops Alerts",         // label only
  "chatId": "-1001234567890",   // numeric id, -100… supergroup/channel, or "@publicname"
  "botToken": "",               // optional — omit to use the shared telegram.botToken
  "threadId": 0,                // optional forum-topic id for supergroups with topics
  "excludeFromAll": false       // true keeps it out of {"group":"all"}
}
```

`telegram.defaultGroup` names the group used when a request doesn't pick one.

**The per-group `botToken` is the important knob.** A bot can only post to chats
it has been added to, so a destination owned by a different bot (the JustGains
API's events bot, for instance) carries its own token here rather than forcing
every group onto one bot.

### Finding a chat id

Add the bot to the group/channel as an admin, post any message there, then:

```bash
curl -s "https://api.telegram.org/bot<TOKEN>/getUpdates" | grep -o '"chat":{"id":[-0-9]*'
```

Verify before wiring it in — `getChat` is read-only and confirms the bot can see
the destination:

```bash
curl -s "https://api.telegram.org/bot<TOKEN>/getChat?chat_id=<CHAT_ID>"
```

## Notes

- The route is unauthenticated and bound to `localhost` by default, same as the
  rest of the JustWatch control API (`web.host` in config).
- It works whether or not `telegram.enabled` is on — that flag gates the
  conversational bot (polling, agent relay, screen recordings), not this relay.
- Groups are read from config on every request, so a `defaultGroup` or group edit
  needs only a JustWatch restart, never a rebuild of callers.
