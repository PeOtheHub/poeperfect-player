namespace PoePerfect.Player.Android;

public sealed record RecentPlaybackEntry(
    BrowseSection Section,
    string ChannelUrl,
    DateTimeOffset PlayedAtUtc);
