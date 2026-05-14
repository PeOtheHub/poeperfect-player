# PoePerfect Player Web

React/Vite web MVP for browser playback and future webOS packaging.

The Web app is currently the fastest iteration path for bringing the player closer to a Netflix/YouTube-style browser experience while keeping the structure aligned with the Windows app.

For player and webOS direction, keep the shared project context in:

- [Web/webOS player context](../../docs/webos-player-context.md)

## Current capabilities

- playlist loading from M3U/Xtream-style sources
- playlist page with source management and category visibility/order preferences
- Windows-inspired start page for `Live`, `Film`, and `Serier`
- TV-style category browsing with latest-added, favorites, recent playback, debounced search, cleaned titles, metadata chips, poster placeholders, and custom scrollbars
- movie details with cleaned title, metadata chips, poster, plot, cast, rating, play/resume, and heart favorite toggle
- series details with seasons and episodes
- browser playback with direct media support and `hls.js`
- TV-style audio and subtitle picker panels, shown only when selectable tracks exist
- embedded audio/subtitle discovery for dev gateway playback
- subtitle timing adjustment with `-1s` and `+1s` controls
- click-to-play/pause on the video surface
- loading and buffering spinner for slow catalog/detail/player operations
- auto-hiding player top bar and controls while the pointer is idle
- continue-watching prompt backed by local watch-progress storage
- local storage for source, favorites, recent playback, watch progress, and category preferences

## Run locally

From this folder:

```powershell
npm install
npm run dev
```

The local server defaults to:

```text
http://127.0.0.1:5173
```

The Vite dev server includes local-only helper endpoints for browser testing:

- `/api/proxy?url=...` for catalog/API loading
- `/api/gateway/start`, `/api/gateway/stop`, and `/api/gateway/media/*` for MKV gateway testing
- `/api/subtitles/probe` and `/api/subtitles/extract` for local media-track discovery and diagnostics
- `/api/client-log` for player diagnostics in the Vite console

Build the web app with:

```powershell
npm run build
```

## Gateway playback

Normal playback still uses the browser media engine directly. For MKV files during local development, the app can also start a local gateway session from the movie detail view with `Spela med undertexter`.

The gateway:

- runs only in Vite dev mode
- uses local `ffmpeg` from `ffmpeg-static` or PATH
- probes embedded audio/subtitle tracks with local `ffprobe`
- transcodes the selected MKV video/audio/subtitle stream to browser-friendly HLS/WebVTT
- keeps audio/subtitle choices visible while the gateway restarts
- restarts the gateway at the requested absolute position when seeking or switching embedded audio/subtitle tracks, so the browser can seek across the full movie timeline without pre-converting the whole file

The gateway is not a production media service yet. A future hosted, TV, or webOS version needs the same idea behind a real backend/media-helper service.

## Embedded subtitles

Browsers do not expose all embedded MKV/MP4 subtitle streams the way LibVLC does in the Windows app. The web player supports:

- HLS subtitle renditions
- external `.vtt` subtitle files
- external `.srt` subtitle files, converted to WebVTT in the browser
- dev/server-side probing/extraction through `/api/subtitles/*` for diagnostics
- dev gateway conversion of the selected embedded MKV subtitle track to HLS/WebVTT

Embedded subtitle support is dev-only today. The current practical path for MKV subtitles in the browser is the gateway, because native browser video elements usually cannot read those embedded subtitle streams directly.
