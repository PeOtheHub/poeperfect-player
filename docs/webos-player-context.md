# Web and webOS Player Context

This note captures current player/subtitle thinking so future work can continue without rediscovering the same constraints.

## Product Direction

The Web app is the fastest iteration path and the likely foundation for a future webOS/TV app. The desired experience is closer to a real IPTV TV app than a normal browser video page:

- TV-first navigation with large focus targets.
- Fast, predictable playback as the default.
- Advanced subtitle/audio handling available without making the basic player feel heavy.
- Settings that expose player/subtitle behavior clearly enough for real-world playlists.

Recent reference screenshots from a working LG/webOS IPTV app showed an important pattern: it separates `Native Player` and `Smart Player` modes for channel playback and VOD playback, and separately exposes stream format choices such as `Default`, `M3U8`, and `TS`. That strongly suggests we should not force one playback pipeline to solve every stream.

## Current UX Decision

Keep two playback paths in the Web app:

- `Spela`: clean/default playback. This should stay as close to the old/simple flow as possible and avoid subtitle-specific machinery unless the user asks for it.
- `Spela med undertexter`: opt-in smart subtitle path. For MKV/VOD dev testing this now uses the local gateway to keep selected embedded video/audio/subtitle handling in one HLS pipeline.

This preserves the better baseline UX while allowing deeper subtitle work.

## Player Strategy

Move toward explicit player backends behind one UI:

- `nativeVideoBackend`: direct browser/native playback, best default when it works.
- `hlsJsBackend`: HLS playback and track discovery in browsers where MSE/hls.js is useful.
- `smartSubtitleBackend`: external subtitle parsing and custom HTML overlay.
- Later: `shakaBackend` for webOS experiments.
- Later: `webOsBackend` or webOS-specific adapter if packaged TV behavior differs from desktop browsers.

The UI should eventually expose settings similar to:

- Live/Channels player: Native / Smart.
- VOD player: Native / Smart.
- Stream format preference: Default / M3U8 / TS.
- Subtitle settings: size, color, shadow/edge, window/background, opacity, font, position/margin.
- Metadata provider/API toggle: for example `Use IMDb API`, so poster/plot/rating enrichment can be enabled or disabled independently from playback.

## Subtitle Lessons

Native browser text tracks are fragile for our use case:

- Sync adjustments that mutate native cue times can drift or behave differently as HLS cues arrive over time.
- Seeking can recreate tracks/cues and make selected subtitle state flicker or reset.
- Placement near controls is difficult to control consistently.
- webOS may expose embedded subtitles/audio differently from desktop Chrome.

For external `.srt`/`.vtt` subtitles, the preferred direction is a custom overlay:

- Parse the subtitle file once when selected.
- Store cues in a sorted array.
- Compute `video.currentTime + subtitleOffset`.
- Find the active cue using a forward pointer plus binary search fallback.
- Render only the active cue as HTML overlay.
- Update DOM/React state only when the displayed cue changes.
- Do not mutate native cues for overlay subtitles.

This should be lightweight enough for TV hardware because it does very little work per frame.

## Current Implementation Snapshot

In `src/PoePerfect.Player.Web/src/components/VideoPlayer.tsx`:

- External subtitles now use a custom HTML overlay path.
- HLS/native subtitle tracks still use the browser/HLS text track path.
- Gateway MKV playback exposes embedded audio and subtitle tracks through large TV-style picker panels.
- Switching embedded gateway audio/subtitle tracks restarts the gateway at the current absolute timeline position instead of running a separate subtitle extraction while the movie plays.
- Subtitle offset is still shown in the player controls.
- Overlay subtitles use the offset directly in time calculation.
- Player controls and topbar auto-hide together, and the video surface toggles play/pause on click.
- Fullscreen is requested on `.player-screen`, so the header and controls are in the same fullscreen surface.
- A buffering spinner is shown on `loadstart`, `waiting`, `stalled`, seeking, and gateway restarts.
- Gateway playback keeps a remembered full timeline duration so the seek bar does not shrink after gateway restarts.
- Gateway track choices are kept visible while a session restart is buffering so the UI does not flicker.

In `src/PoePerfect.Player.Web/src/App.tsx`:

- `Spela` starts normal playback without injecting subtitle tracks.
- `Spela med undertexter` starts the subtitle-capable path when relevant.
- Search is debounced and requires at least two characters before filtering to avoid rendering very large one-letter result sets.
- Movie cards clean bracket metadata such as `[PRE]`, `[2026]`, `[Multi-Sub]`, and show it as chips instead of repeating it in titles.
- Watch progress is saved in local storage and a continue-watching prompt lets the user resume or start from the beginning.

## Important Limits

External subtitle overlay only helps when the app has a separate subtitle URL or can extract a subtitle file through a helper service.

Embedded subtitles inside MKV/MP4/HLS are harder:

- Desktop browsers often do not expose embedded MKV subtitle streams directly.
- HLS subtitle renditions may be exposed through hls.js/native text tracks, but behavior can differ by browser and TV.
- A webOS TV may expose tracks differently from desktop, so testing on real LG hardware is required.

Working IPTV apps on LG/webOS prove the problem is solvable, but they likely use a more specialized player strategy than a generic browser `<video>` wrapper. We should treat webOS as a real target with adapter-specific diagnostics and settings.

## webOS Investigation Plan

Build diagnostics before making big assumptions:

- Log `video.audioTracks` and `video.textTracks` on real LG TV.
- Log hls.js audio/subtitle track lists where hls.js is used.
- Log stream URL type and selected backend.
- Log duration and seek behavior before and after seeking.
- Verify whether M3U/Xtream sources expose external subtitle URLs, HLS subtitle groups, or only embedded subtitles.
- Compare Native vs Smart behavior per stream type.

Do not rely on emulator/simulator behavior for final media playback decisions.

## TV UI Notes From Reference App

Useful patterns from the working webOS IPTV app screenshots:

- Home screen uses large, few, highly focusable tiles: Live TV, Movies, Series, EPG Catch-up, Playlists.
- Settings are dense but TV-readable, with a left category rail and a right detail panel.
- Player settings are explicit and backend-oriented: Native Player, Smart Player, stream format.
- Subtitle settings focus on presentation: size, edge/shadow, text color, window color, window opacity, font.
- Metadata enrichment is user-controlled through a `Use IMDb API` toggle. This is useful because metadata fetching affects load time, external dependencies, and privacy/API-key choices.
- In VOD Smart Player mode, audio and subtitle selection work from simple player-chrome buttons (`Audio`, `Subtitle`) rather than always-visible dropdowns.
- Audio selection opens a side panel with one large focusable row per track. Example embedded audio tracks shown: Swedish, Japanese, Danish, Norwegian, Finnish.
- Subtitle selection opens an overlay/side panel with large focusable rows, arranged in one or two columns depending on count. Example embedded subtitle tracks shown: Spanish-Castilian, Turkish, Arabic, Swedish, Portuguese, German, Finnish, Croatian, Italian, plus Swedish 1 and Swedish 2.
- The VOD control bar is compact: play/pause, current time, seek bar, total duration, `Audio`, `Subtitle`, settings, and `Start from the beginning`.
- Track selection is clearly a modal/overlay task, not part of the always-visible transport controls. This is likely better for TV remote UX than desktop-style selects.
- Account/device info is visible but should be kept out of screenshots or masked when shared.

## Near-Term Next Steps

- Add a Web settings surface for Player Settings and Subtitle Settings, even if backed by local storage first.
- Add a metadata setting such as `Use IMDb/TMDB metadata` so enriched posters, plot, cast, rating, and release date can be toggled separately from playback.
- Add a small diagnostics panel or dev-only log export for track discovery on webOS.
- Keep `Spela` as the stable path and iterate on `Spela med undertexter` as the smart path.
- Continue hardening the large modal track pickers for remote control, focus restore, and very long subtitle lists.
- Keep improving embedded-track labels with language normalization and duplicate labels such as `Swedish 1` / `Swedish 2` instead of collapsing them.
- Improve overlay subtitle styling with configurable size, bottom margin, shadow, color, and background.
- Investigate Shaka Player as a separate backend rather than replacing the existing player globally.
