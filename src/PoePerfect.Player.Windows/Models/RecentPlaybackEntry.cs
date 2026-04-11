namespace APTV.Models;

public sealed record RecentPlaybackEntry(
    BrowseSection Section,
    string ChannelUrl,
    DateTimeOffset PlayedAtUtc);
