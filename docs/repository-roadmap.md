# Repository Roadmap

This note describes the intended repository direction now that `PoePerfect Player` is moving toward a multi-platform layout.

## Goal

Keep all platform versions in one private repository while separating:

- platform-specific UI/runtime code
- future shared/core logic
- installer/deployment assets
- product documentation

## Current Implemented Project

- `src/PoePerfect.Player.Windows`

## Planned Future Projects

- `src/PoePerfect.Player.Core`
- `src/PoePerfect.Player.Android`
- `src/PoePerfect.Player.RaspberryPi`

## Suggested Next Technical Refactor

When we start the next platform:

1. move reusable models/services from the Windows project into `PoePerfect.Player.Core`
2. keep WPF-specific code in `PoePerfect.Player.Windows`
3. let Android/Raspberry Pi consume the shared core rather than duplicating parsing/cache logic

## Why This Layout

- one product, one repository
- simpler issue tracking and documentation
- easier to share logic across platforms
- clearer separation between app shell and reusable domain logic
