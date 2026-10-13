# Telegram Integration Setup

## 1. Create a Bot

1. Open Telegram and message [@BotFather](https://t.me/BotFather)
2. Send `/newbot`
3. Choose a name: `JustWatch`
4. Choose a username: `justwatch_bot` (must end in `bot`)
5. BotFather will reply with your **bot token** — copy it

## 2. Get Your Chat ID

1. Message your new bot in Telegram (send anything like "hello")
2. Open this URL in a browser (replace `YOUR_BOT_TOKEN`):
   ```
   https://api.telegram.org/botYOUR_BOT_TOKEN/getUpdates
   ```
3. Find `"chat":{"id":123456789}` in the response — that number is your **chat ID**
4. For a group chat: add the bot to the group, send a message, then check getUpdates. Group IDs are negative numbers like `-1001234567890`

## 3. Configure justwatch-config.json

Open `F:\JustWatch\config\justwatch-config.json` and fill in the telegram section:

```json
"telegram": {
    "enabled": true,
    "botToken": "7123456789:AAH...",
    "chatId": 123456789,
    "maxRecordingSeconds": 30,
    "recordingCrf": 35,
    "recordingFramerate": 10,
    "recordingResolution": "1280x720"
}
```

| Field | Description |
|---|---|
| `enabled` | Set to `true` to activate |
| `botToken` | The token from BotFather |
| `chatId` | Your chat/group ID from step 2 |
| `maxRecordingSeconds` | Max video length (default 30s, Telegram limit is 50MB) |
| `recordingCrf` | Video quality — higher = smaller file, lower quality (35 is good for Telegram) |
| `recordingFramerate` | FPS (10 is fine for demos) |
| `recordingResolution` | Output resolution (1280x720 keeps files small) |

## 4. Restart the App

Kill and relaunch `JustWatch.TrayApp.exe`. The orchestrator column will show "Telegram polling started" if configured correctly.

## How It Works

- **Send a message** to the bot in Telegram → the orchestrator receives it and processes it as if you typed it in the Agent tab
- **Send a voice note** → it's transcribed and handled like a typed message (the bot echoes back what it heard so you can spot mistakes)
- **Send a photo or file** → it's saved locally and the path is handed to the agent; add a caption to say what to do with it
- **Conversation replies** from the User Conversation panel are sent back to Telegram
- **Demo recordings** are automatically sent as video files with a caption
- You can send new tasks while others are running — they queue up
- The bot only responds to messages from the configured `chatId`

## Voice Transcription

Voice notes are transcribed via an OpenAI-compatible Whisper endpoint. Configure it in the telegram section of `justwatch-config.json`:

| Field | Description |
|---|---|
| `transcribeVoice` | `true` to enable voice transcription (default `true`) |
| `transcriptionApiKey` | API key for the transcription service — **required**, voice is ignored without it |
| `transcriptionModel` | Model name (default `whisper-1` for OpenAI; use `whisper-large-v3` for Groq) |
| `transcriptionBaseUrl` | API base URL (default `https://api.openai.com/v1`; Groq is `https://api.groq.com/openai/v1`) |

If no key is set, voice messages get a one-line reply telling you transcription isn't configured. Photos and documents work without any transcription key.

## Recording Quality for Telegram

Recordings sent to Telegram use lower quality settings to keep files under the 50MB upload limit:
- 10 FPS, 1280x720 resolution, CRF 35
- Max 30 seconds per recording
- If a video exceeds 50MB, it's sent as a document instead

## Example Messages

```
"test the web app login page"
"build ios and deploy to testflight"
"add a logout button to the profile page"
"record a demo of the workout flow on mobile"
```
