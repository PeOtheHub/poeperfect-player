# PoePerfect.Player.Android

Android MVP of `PoePerfect Player`.

Implemented here right now:

- load playlist from M3U url or file path
- browse Live, Film, and Serier
- search/filter channels and VOD
- save favorites locally
- heart favorite toggles in lists, film details, and episodes
- category visibility/order preferences
- latest-added and recent playback categories
- Film and Serier open directly on the 20 latest-added items
- cached latest-added previews for fast Film/Serier opens after the playlist cache has an index
- compact search toggle that expands only when needed
- cleaned VOD titles with compact metadata chips
- movie detail step before playback with poster, plot, genre, duration, rating, release date, director, cast, play, and heart favorite toggle
- loading feedback while opening sections, categories, details, and returning to lists
- series grouping with seasons and episodes
- poster caching with local placeholders
- embedded Android playback with external player/browser fallback

Current Android follow-up path:

- add continue-watching/resume prompts for VOD
- expose embedded audio/subtitle selection when the Android player integration supports it
- keep tuning small-screen spacing and landscape layout
- decide when external fallback can be demoted once embedded playback is consistently reliable
