# WebRTC Meet — .NET 8

Simple WebRTC meeting app with:

- WebRTC video/audio calls
- Server-side recording storage
- Automatic recording recovery after disconnect
- Offline Whisper transcription with Whisper.net
- SQLite persistence
- Transcript `.json` and `.txt` exports
- **No FFmpeg required**

## Run

Requirements:

- .NET 8 SDK
- Modern Chromium/Edge/Firefox browser
- No FFmpeg installation

```bash
cd WebrtcMeet
dotnet restore
dotnet run -c Release
```

Open:

```text
http://localhost:8000
```

For camera/microphone from another device, use HTTPS or a secure tunnel/reverse proxy.

## How recording works

1. Browser composites the meeting into WebM.
2. WebM is uploaded every 2 seconds.
3. The server appends chunks directly to `data/recordings/`.
4. SQLite stores recording metadata and the latest received chunk time.
5. When the user stops recording, the server marks the file ready.
6. If the browser crashes, network disconnects, or the WebSocket disappears, the background recovery service automatically marks the recording ready after `RECORDING_STALE_SECONDS`.
7. No FFmpeg remux step is required.

This means the last successfully uploaded chunk is preserved even if the connection suddenly dies.

## Automatic transcription

While recording, every participant's microphone is processed automatically:

```text
microphone
   ↓
browser MediaRecorder
   ↓
browser converts WebM/Opus → 16 kHz mono WAV
   ↓
POST /api/transcribe
   ↓
Whisper.net
   ↓
SQLite + data/transcripts/<room>.json
       + data/transcripts/<room>.txt
   ↓
live transcript broadcast to everyone
```

The conversion happens in the browser because Whisper.net accepts standard WAV/PCM directly; the server no longer needs FFmpeg. Whisper.net documents direct WAV-stream processing through `WhisperProcessor.ProcessAsync(Stream)`. citeturn0search0turn0search1

### Whisper model

The first run downloads the configured model into:

```text
data/models/
```

Default:

```env
WHISPER_MODEL=small
```

For faster CPU transcription:

```env
WHISPER_MODEL=base
```

For better accuracy but more CPU/RAM:

```env
WHISPER_MODEL=medium
```

Optional language:

```env
WHISPER_LANGUAGE=en
```

Hindi:

```env
WHISPER_LANGUAGE=hi
```

Marathi:

```env
WHISPER_LANGUAGE=mr
```

## SQLite

The database is:

```text
data/meetings.db
```

It persists:

- scheduled meetings
- recordings
- recording status
- recording size
- recording timestamps
- transcript segments
- speaker names
- transcript timestamps

SQLite uses WAL mode, so the app does not need a separate database server.

## Important files

```text
WebrtcMeet/
├── Program.cs
├── WebrtcMeet.csproj
├── Config/
│   └── AppSettings.cs
├── Models/
│   └── Models.cs
├── Services/
│   ├── Signaling.cs
│   ├── SqliteStore.cs
│   ├── Stores.cs
│   └── WhisperService.cs
├── static/
│   ├── index.html
│   ├── room.html
│   ├── call.js
│   └── features.js
└── data/
    ├── meetings.db
    ├── recordings/
    ├── transcripts/
    └── models/
```

## FFmpeg

FFmpeg has been completely removed.

Do not install it and do not configure `FFMPEG_PATH`.

## Notes

The project can be built with:

```bash
dotnet restore
dotnet build -c Release
```

The uploaded environment did not contain the .NET SDK, so the modified project was not locally compiled in this session. The code is structured for .NET 8 and uses the documented Whisper.net WAV-stream API. citeturn0search0turn0search12

## Transcription reliability changes
- CC works independently of recording; each connected browser with a microphone uploads 8-second clips.
- Leave button stops the current clip and **awaits** the last transcription upload before closing media or signaling.
- Each saved transcript segment is written to SQLite and exported to JSON/TXT.
- A browser crash, forced tab close, network failure or process kill cannot upload unsent microphone audio; only already uploaded segments are recoverable.
- Check `GET /api/whisper-status` for `ready` before testing. Model download may take time.
- Ordinary room state is retained for reconnects in the same server process; SQLite transcript data persists across restarts.
- Closing a tab directly is not guaranteed to flush pending transcription; use **Leave**.
