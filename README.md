<p align="center">
  <img src="./docs/assets/banner.svg" alt="PoePerfect Player banner" width="100%" />
</p>

<h1 align="center">PoePerfect Player</h1>

<p align="center">
  Private cross-platform IPTV/VOD player project, starting with the Windows desktop app.
</p>

<p align="center">
  Windows desktop app implemented • Shared core planned • Android planned • Raspberry Pi/Linux planned
</p>

## Overview

`PoePerfect Player` is being built as one product with multiple future platform versions in the same private repository.

The currently implemented version is a Windows desktop app built with:

- `.NET 8`
- `WPF`
- `LibVLCSharp.WPF`
- `VideoLAN.LibVLC.Windows`

Current capabilities:

- M3U playlists from URL or file
- Live TV, Movies, and Series sections
- category browsing and local category sorting/hiding
- favorites and recent playback
- fullscreen playback
- audio track and subtitle selection for VOD
- optional XMLTV/EPG for Live TV
- local caching for playlists, posters, and guide data
- Windows installer output

## Status

| Area | Status | Notes |
| --- | --- | --- |
| Windows desktop app | Implemented | Main working product today |
| Shared/core project | Planned | Target for reusable parsing/cache/domain logic |
| Android app | Planned | Likely next serious platform |
| Raspberry Pi/Linux app | Planned | Future lightweight/player target |
| GitHub repository | Private | Product repo for all versions |

## Screenshots

This repository is now ready for a proper screenshots section, but real product captures have not been added to the repo yet.

Recommended screenshots to add next:

- Windows home screen with `Live`, `Film`, and `Serier`
- category browsing view for `Film` or `Serier`
- fullscreen player with overlay controls
- `Live` list view with channel icons and EPG

When you want, we can add a `docs/assets/screenshots` folder and wire real product images into this README.

## Platform Roadmap

| Platform | Stage | Direction |
| --- | --- | --- |
| Windows | Active | Continue polishing the main app |
| Shared Core | Planned | Move reusable logic out of the Windows project |
| Android | Planned | Likely first major port after Windows |
| Raspberry Pi/Linux | Planned | Evaluate after shared core is extracted |

Recommended next technical refactor before a second platform:

1. move reusable models and services into a shared core project
2. keep WPF-specific UI/runtime logic inside the Windows project
3. let future Android and Raspberry Pi versions consume the shared core

## Repository Layout

- [PoePerfect.Player.sln](./PoePerfect.Player.sln)
- [src](./src)
- [src/PoePerfect.Player.Windows](./src/PoePerfect.Player.Windows)
- [src/PoePerfect.Player.Core](./src/PoePerfect.Player.Core)
- [src/PoePerfect.Player.Android](./src/PoePerfect.Player.Android)
- [src/PoePerfect.Player.RaspberryPi](./src/PoePerfect.Player.RaspberryPi)
- [installer](./installer)
- [docs](./docs)

## Run Locally

From the repository root:

```powershell
dotnet run --project .\src\PoePerfect.Player.Windows\PoePerfect.Player.Windows.csproj
```

Or start the built app directly:

```powershell
C:\projectpeo\APTV\src\PoePerfect.Player.Windows\bin\Debug\net8.0-windows\PoePerfectPlayer.exe
```

## Build

```powershell
dotnet build .\PoePerfect.Player.sln
```

## Installer

The Windows installer build script is:

- [build-installer.ps1](./installer/build-installer.ps1)

Installer output is written to:

- `dist\PoePerfectPlayer-Setup.exe`

## Local User Data

Runtime user data is stored under:

- `%AppData%\APTV`

That includes:

- saved playlist URL
- saved XMLTV URL
- favorites
- recent playback
- caches
- logs

These are not meant to be committed to the repository.

## Documentation

- [Project history](./docs/poeperfect-player-history.md)
- [Licensing handoff](./docs/licensing-handoff.md)
- [Repository roadmap](./docs/repository-roadmap.md)
- [Release checklist](./docs/release-checklist.md)
- [Private repository notice](./LICENSE.md)

## Notes

- No personal playlist URLs should be stored in the repository.
- The app is designed so end users enter their own playlist and optional XMLTV source.
- This repository is private and intended to hold all future `PoePerfect Player` platform versions.
