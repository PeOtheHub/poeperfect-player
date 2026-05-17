# Repository Roadmap

This note describes the intended repository direction now that `PoePerfect Player` is moving toward a multi-platform layout.

## Goal

Keep all platform versions in one private repository while separating:

- platform-specific UI/runtime code
- future shared/core logic
- installer/deployment assets
- product documentation

## Current Implemented Projects

- `src/PoePerfect.Player.Windows`
- `src/PoePerfect.Player.Core`
- `src/PoePerfect.Player.Web`
- `src/PoePerfect.Player.Android`

## Planned Future Projects

- `src/PoePerfect.Player.RaspberryPi`

## Current Technical Direction

The repository is now past the first-platform split. The current direction is:

1. keep WPF-specific code in `PoePerfect.Player.Windows`
2. keep Web/webOS-specific player gateway code in `PoePerfect.Player.Web`
3. keep Android UI/player integration code in `PoePerfect.Player.Android`
4. move reusable playlist, cache, Xtream, title/detail, and watch-progress logic into `PoePerfect.Player.Core` when at least two platforms need it
5. let future Raspberry Pi/Linux work consume the shared core rather than duplicating parsing/cache logic

## Why This Layout

- one product, one repository
- simpler issue tracking and documentation
- easier to share logic across platforms
- clearer separation between app shell and reusable domain logic
