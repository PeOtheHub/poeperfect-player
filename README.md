<p align="center">
  <img src="./docs/assets/banner.svg" alt="PoePerfect Player banner" width="100%" />
</p>

<h1 align="center">PoePerfect Player</h1>

<p align="center">
  Private multi-platform IPTV/VOD player project for Windows, Web, Android, and future TV/Linux targets.
</p>

<p align="center">
  Windows desktop app - Web/browser app - Android MVP - Shared core services
</p>

## Overview

`PoePerfect Player` is being built as one product with multiple platform frontends in the same private repository.

The main working product today is the Windows desktop app. A browser-based Web version now exists as the fastest iteration path and as the foundation for a future webOS/TV version. There is also an Android MVP and a shared Core project for reusable playlist/domain logic.

## Current Status

| Area | Status | Notes |
| --- | --- | --- |
| Windows desktop app | Active | Main WPF player with playlist browsing, VOD details, playback, favorites, EPG/cache support, and installer output |
| Web app | Active MVP | React/Vite browser version with Live/Film/Serier browsing, playlist management, search, details, and video playback |
| Android app | MVP | .NET MAUI app for playlist loading, browsing, search/filtering, favorites, and opening streams |
| Shared Core | Active | Shared playlist parsing, models, favorites, cache and Xtream-related services |
| Raspberry Pi/Linux | Planned | Future lightweight/player target |
| GitHub repository | Private | Product repo for all versions |

## Implemented Capabilities

### Windows

- M3U playlists from URL or file
- Xtream-style playlist/API support where available
- Live TV, Film, and Serier sections
- latest-added category for Film and Serier
- category browsing with local sort/hide preferences
- favorites and recent playback
- movie detail view with larger poster, description, metadata, rating, cast, play, and heart favorite toggle
- series grouping with seasons and episodes
- fullscreen playback
- audio track and subtitle selection for VOD
- optional XMLTV/EPG for Live TV
- local caching for playlists, posters, and guide data
- custom Windows app icon
- Windows installer output

### Web

- React/Vite browser app under `src/PoePerfect.Player.Web`
- M3U/Xtream source loading
- playlist page for loading sources and sorting/hiding undercategories
- Windows-inspired start page with Live, Film, and Serier
- category browser with latest-added, favorites, recent playback, and search
- movie detail view with poster, metadata, description, rating, cast, play, and heart favorite toggle
- series detail view with seasons and episodes
- video player using browser playback plus `hls.js`
- audio track and subtitle selectors in the player
- local browser storage for source, favorites, recent playback, and category preferences
- Vite dev proxy for catalog/API loading during local testing
- intended as the base for a future webOS/TV package

### Android

- .NET MAUI Android MVP
- load playlist from M3U URL or file path
- browse channels
- search/filter channels
- save favorites locally
- open selected streams in an external player/browser on Android

### Shared Core

- shared channel/content models
- M3U parsing
- favorites persistence
- playlist/cache-related services
- series grouping helpers
- Xtream API helpers

## Repository Layout

- [PoePerfect.Player.sln](./PoePerfect.Player.sln)
- [src/PoePerfect.Player.Windows](./src/PoePerfect.Player.Windows)
- [src/PoePerfect.Player.Web](./src/PoePerfect.Player.Web)
- [src/PoePerfect.Player.Android](./src/PoePerfect.Player.Android)
- [src/PoePerfect.Player.Core](./src/PoePerfect.Player.Core)
- [src/PoePerfect.Player.RaspberryPi](./src/PoePerfect.Player.RaspberryPi)
- [installer](./installer)
- [docs](./docs)

## Run Locally

### Windows

From the repository root:

```powershell
dotnet run --project .\src\PoePerfect.Player.Windows\PoePerfect.Player.Windows.csproj
```

Or start the built app directly after a Debug build:

```powershell
C:\projectpeo\APTV\src\PoePerfect.Player.Windows\bin\Debug\net8.0-windows\PoePerfectPlayer.exe
```

### Web

From the repository root:

```powershell
cd .\src\PoePerfect.Player.Web
npm install
npm run dev
```

The local dev server defaults to:

```text
http://127.0.0.1:5173
```

Build the web app with:

```powershell
npm run build
```

### Android

Build the Android project with:

```powershell
dotnet build .\src\PoePerfect.Player.Android\PoePerfect.Player.Android.csproj
```

Running on a device/emulator is usually easiest from Visual Studio with the Android workload installed.

## Build

Build the .NET solution:

```powershell
dotnet build .\PoePerfect.Player.sln
```

Build the Web app:

```powershell
cd .\src\PoePerfect.Player.Web
npm run build
```

## Installer

The Windows installer build script is:

- [build-installer.ps1](./installer/build-installer.ps1)

Installer output is written to:

- `dist\PoePerfectPlayer-Setup.exe`

## Local User Data

Windows runtime user data is stored under:

- `%AppData%\APTV`

That includes:

- saved playlist URL
- saved XMLTV URL
- favorites
- recent playback
- caches
- logs

The Web app uses browser local storage for:

- saved source URL
- favorites
- recent playback
- category visibility/order preferences

These are runtime values and should not be committed to the repository.

## Screenshots

This repository is ready for a proper screenshots section, but real product captures have not been added yet.

Recommended screenshots to add next:

- Windows home screen with `Live`, `Film`, and `Serier`
- Web home screen with the three main category cards
- category browsing view for `Film` or `Serier`
- movie detail view
- fullscreen player with audio/subtitle controls
- `Live` list view with channel icons and EPG

## Platform Roadmap

| Platform | Stage | Direction |
| --- | --- | --- |
| Windows | Active | Continue polishing the main app |
| Web | Active MVP | Bring structure and behavior closer to Windows, then use it as the webOS foundation |
| Android | MVP | Stabilize browsing/playback and align UX with Windows/Web |
| Shared Core | Active | Move more reusable logic out of platform-specific projects |
| Raspberry Pi/Linux | Planned | Evaluate after Web/Core are further along |
| webOS/TV | Planned | Package the browser app for TV after Web is mature enough |

## Documentation

- [Project history](./docs/poeperfect-player-history.md)
- [Licensing handoff](./docs/licensing-handoff.md)
- [Repository roadmap](./docs/repository-roadmap.md)
- [Release checklist](./docs/release-checklist.md)
- [Private repository notice](./LICENSE.md)

## Notes

- No personal playlist URLs, usernames, passwords, or tokens should be stored in the repository.
- The app is designed so end users enter their own playlist and optional XMLTV source.
- `node_modules`, `dist`, logs, build outputs, and local runtime caches are intentionally ignored.
- This repository is private and intended to hold all future `PoePerfect Player` platform versions.
