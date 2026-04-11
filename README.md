# PoePerfect Player

`PoePerfect Player` is a Windows desktop IPTV/VOD player built with `.NET 8`, `WPF`, and `LibVLCSharp`.

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

## Current Platform

Right now this project targets **Windows only**.

Core app technology:

- `.NET 8`
- `WPF`
- `LibVLCSharp.WPF`
- `VideoLAN.LibVLC.Windows`

## Project Layout

- [APTV.csproj](./APTV.csproj)
- [MainWindow.xaml](./MainWindow.xaml)
- [MainWindow.xaml.cs](./MainWindow.xaml.cs)
- [Services](./Services)
- [Models](./Models)
- [installer](./installer)
- [docs](./docs)

## Run Locally

From the project folder:

```powershell
dotnet run
```

Or start the built app directly:

```powershell
C:\projectpeo\APTV\bin\Debug\net8.0-windows\PoePerfectPlayer.exe
```

## Build

```powershell
dotnet build
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
