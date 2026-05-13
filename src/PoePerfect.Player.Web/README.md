# PoePerfect Player Web

React/Vite web MVP for browser playback and future webOS packaging.

The Web app is currently the fastest iteration path for bringing the player closer to a Netflix/YouTube-style browser experience while keeping the structure aligned with the Windows app.

## Current capabilities

- playlist loading from M3U/Xtream-style sources
- playlist page with source management and category visibility/order preferences
- Windows-inspired start page for `Live`, `Film`, and `Serier`
- category browsing with latest-added, favorites, recent playback, and search
- movie details with poster, plot, metadata, cast, rating, play, and heart favorite toggle
- series details with seasons and episodes
- browser playback with direct media support and `hls.js`
- audio and subtitle selectors, shown only when selectable tracks exist
- subtitle timing adjustment with `-1s` and `+1s` controls
- auto-hiding player top bar and controls while the pointer is idle
- local storage for source, favorites, recent playback, and category preferences

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
- `/api/client-log` for player diagnostics in the Vite console

Build the web app with:

```powershell
npm run build
```

## Gateway playback

Normal playback still uses the browser media engine directly. For MKV files during local development, the app can also start a local gateway session from the movie detail view with `Gateway-test`.

The gateway:

- runs only in Vite dev mode
- uses local `ffmpeg` from `ffmpeg-static` or PATH
- transcodes the selected MKV video/audio stream to browser-friendly HLS
- exposes a Swedish embedded subtitle stream as an HLS/WebVTT subtitle track when available
- restarts the gateway at the requested position when seeking, so the browser can seek across the full movie timeline without pre-converting the whole file

The gateway is not a production media service yet. A future hosted, TV, or webOS version needs the same idea behind a real backend/media-helper service.

## Embedded subtitles

Browsers do not expose all embedded MKV/MP4 subtitle streams the way LibVLC does in the Windows app. The web player supports:

- HLS subtitle renditions
- external `.vtt` subtitle files
- external `.srt` subtitle files, converted to WebVTT in the browser
- dev/server-side probing/extraction through `/api/subtitles/*` for diagnostics
- dev gateway conversion of a preferred embedded MKV subtitle track to HLS/WebVTT

Embedded subtitle support is dev-only today. The current practical path for MKV subtitles in the browser is the gateway, because native browser video elements usually cannot read those embedded subtitle streams directly.
