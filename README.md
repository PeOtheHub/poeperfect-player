# PoePerfect Player

`PoePerfect Player` is a private cross-platform IPTV/VOD player project that currently starts with a Windows desktop app built on `.NET 8`, `WPF`, and `LibVLCSharp`.

It supports:

- M3U playlists from URL or file
- Live TV, Movies, and Series sections
- category browsing and local category sorting/hiding
- favorites and recent playback
- fullscreen playback
- audio track and subtitle selection for VOD
- optional XMLTV/EPG for Live TV
- local caching for playlists, posters, and guide data
- Windows installer output

## Status Snapshot

| Area | Status |
| --- | --- |
| Windows desktop app | Implemented |
| Shared/core project | Planned |
| Android app | Planned |
| Raspberry Pi/Linux app | Planned |
| GitHub repository | Private |

## Repository Intent

This repository is meant to host multiple future platform versions of `PoePerfect Player` in one place.

Current status:

- Windows desktop app: implemented
- shared/core project: planned
- Android app: planned
- Raspberry Pi/Linux app: planned

## Current Platform

Right now the implemented app targets **Windows only**.

Current app technology:

- `.NET 8`
- `WPF`
- `LibVLCSharp.WPF`
- `VideoLAN.LibVLC.Windows`

## Repository Layout

- [PoePerfect.Player.sln](./PoePerfect.Player.sln)
- [src](./src)
- [src/PoePerfect.Player.Windows](./src/PoePerfect.Player.Windows)
- [installer](./installer)
- [docs](./docs)

Planned structure:

- `src/PoePerfect.Player.Windows`
- `src/PoePerfect.Player.Core`
- `src/PoePerfect.Player.Android`
- `src/PoePerfect.Player.RaspberryPi`

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

The installer build script is:

- [build-installer.ps1](./installer/build-installer.ps1)

Current installer output is written to:

- `dist\PoePerfectPlayer-Setup.exe`

## Local User Data

Runtime user data is stored under:

- `%AppData%\APTV`

That includes things like:

- saved playlist URL
- saved XMLTV URL
- favorites
- recent playback
- caches
- logs

These are **not** meant to be committed to the repository.

## Notes

- No personal playlist URLs should be stored in the repository.
- The app is designed so end users enter their own playlist and optional XMLTV source.
- Project history is documented in [poeperfect-player-history.md](./docs/poeperfect-player-history.md).
- Licensing discussion notes are documented in [licensing-handoff.md](./docs/licensing-handoff.md).
- Private repository/license notes are documented in [LICENSE.md](./LICENSE.md).

## Suggested GitHub Settings

- Repository name: `poeperfect-player`
- Visibility: `Private`
- Description: `Private cross-platform IPTV/VOD player project starting with the Windows desktop app.`
