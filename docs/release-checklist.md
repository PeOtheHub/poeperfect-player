# Release Checklist

Use this checklist when preparing a new `PoePerfect Player` release.

## Before Build

- confirm current branch is clean
- review recent changes
- verify no personal playlist or XMLTV URLs are committed
- verify `%AppData%\APTV` data is not part of the repository

## Verify App

- build solution
- run Windows app locally
- verify playlist loading
- verify Live playback
- verify Film playback
- verify Series playback
- verify fullscreen controls
- verify audio track and subtitle selection
- verify installer script still works

## Installer

- run [build-installer.ps1](C:/projectpeo/APTV/installer/build-installer.ps1)
- verify installer starts correctly
- verify install over existing version works
- verify app launches after install

## Release Notes

- summarize user-facing changes
- mention notable fixes
- mention known issues if any remain

## Git / GitHub

- commit all intended changes
- push `main`
- tag release if desired
- upload installer to release or distribution channel

