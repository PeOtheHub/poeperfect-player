# Cross-Platform UX Porting Plan

This document captures which UX improvements from the Web player and catalog should be carried into the Windows, Android, and future TV/webOS versions of PoePerfect Player. It is intended as durable project context for later threads and implementation passes.

## Summary

The Web version has become the fastest place to test player and browsing ideas: cleaned titles, metadata chips, better category lists, poster placeholders, debounced search, continue watching, TV-style track pickers, loading/buffering feedback, and improved subtitle behavior.

The goal is to reuse the product direction across platforms without copying Web-specific technical workarounds where they do not belong. In particular, the Web/WebOS subtitle gateway strategy should stay separate from Windows and Android player implementations.

## Shared Direction

- Reuse the successful UX patterns across platforms: cleaned titles, metadata chips, poster placeholders, debounced search, continue watching, better audio/subtitle selection, and loading/buffering feedback.
- Keep player-track implementations platform-owned:
  - Windows should use native LibVLC track APIs.
  - Android should use the selected Android/media player track APIs when exposed.
  - Web and future webOS should follow the backend strategy documented in [Web/webOS player context](./webos-player-context.md).
- Prefer moving common title parsing and watch-progress logic into `PoePerfect.Player.Core` once both Windows and Android need the same behavior.
- Treat TV/webOS as a remote-first experience with large focus states, clear navigation, and settings that can be operated comfortably from a sofa.

## Windows Priorities

Windows is the main desktop app and should receive the richer version of the shared UX first.

- Clean movie and episode titles by removing bracket metadata from the displayed title.
- Extract bracket/file metadata into chips, for example year, `PRE`, `Multi-Sub`, `Multi-Audio`, `4K`, Dolby Vision, HDR, and similar technical markers.
- Avoid repeating the same title multiple times on detail pages.
- Add a styled broken-poster placeholder so missing images do not show raw browser/control failure UI.
- Add continue watching:
  - save playback position periodically and when playback closes;
  - offer resume or start from beginning next time;
  - clear or ignore progress when the item is nearly finished.
- Add debounced search with a minimum of 2 characters before filtering starts.
- Improve audio/subtitle picker UX using native LibVLC track APIs, including stable labels and selections that do not disappear during buffering.
- Add loading/buffering feedback around player start, seek, and track changes where the native player exposes useful state.

## Android Priorities

Android should receive a compact/mobile version of the same product direction. The existing Android project already has useful hooks such as `RecentPlaybackStore` and `PosterImageCacheService`.

- Apply cleaned titles and compact metadata chips in browsing and details.
- Limit visible chips on small screens so cards stay readable.
- Add a styled poster fallback through the image cache/loading path.
- Add continue watching through the existing recent playback storage or a dedicated watch-progress store.
- Add debounced search with a minimum of 2 characters before filtering starts.
- Use bottom-sheet style audio/subtitle selection if the Android player exposes embedded tracks.
- Keep tap-to-play/pause as a player requirement when the internal Android player flow is implemented.
- Keep external-player behavior available until embedded playback is strong enough to replace it.

## webOS / TV Direction

The detailed Web/webOS player strategy lives in [Web/webOS player context](./webos-player-context.md). This document only captures the cross-platform relationship.

- Use the Web app as the webOS foundation after the browser version is mature enough.
- Keep the TV player focused on remote navigation, clear focus states, large audio/subtitle pickers, visible loading/buffering feedback, and subtitle styling options.
- Do not assume the Web gateway approach should be copied to Windows or Android.
- Preserve the useful Smart Player inspiration:
  - obvious audio and subtitle buttons in the player;
  - large selectable track lists;
  - subtitle styling settings;
  - clear category rails and focus treatment.

## Rollout Order

1. Low-risk catalog polish:
   - title cleanup;
   - metadata chips;
   - poster placeholders;
   - search debounce and 2-character minimum.
2. Viewing continuity:
   - continue watching storage;
   - resume/start-over prompt;
   - near-end progress cleanup.
3. Player UX:
   - tap/click video to pause/play;
   - loading and buffering spinner;
   - audio/subtitle menus;
   - subtitle styling where the platform supports it.
4. Core extraction:
   - move shared title parsing and watch-progress models into `PoePerfect.Player.Core` after duplicated platform logic appears.

## Shared Concepts

Future shared title parsing should use this shape:

- Input: raw playlist/movie/episode title.
- Output: clean display title plus ordered metadata chips.
- Metadata examples: year, `PRE`, `Multi-Sub`, `Multi-Audio`, `4K`, Dolby Vision, HDR, audio hints, subtitle hints, and release/source markers.

Future shared watch progress should use this shape:

- content identifier or stream URL;
- last position;
- duration;
- updated timestamp;
- finished or near-finished handling.

The shared layer should not own platform-specific player-track APIs. It may describe selected track identity or user preference later, but track discovery and switching should remain inside each platform player integration.

## Test Plan

- Verify bracket metadata is removed from displayed titles and shown as chips.
- Verify detail pages show the primary title once.
- Verify broken or missing posters show a styled placeholder.
- Verify one-character search does not filter or freeze the UI.
- Verify two-character search filters after debounce.
- Verify continue watching prompts after partially watched content.
- Verify starting from beginning clears or overrides resume behavior.
- Verify progress is cleared or ignored near the end of a movie.
- Verify Windows audio/subtitle choices do not disappear or reset during buffering.
- Verify Android layouts remain usable on small screens.

## Assumptions

- This document is planning context only and does not change runtime behavior.
- Windows and Android should copy the successful UX patterns, not the Web-specific subtitle gateway mechanics.
- Windows is likely the first implementation target, Android second, and webOS remains guided by the Web context document.
