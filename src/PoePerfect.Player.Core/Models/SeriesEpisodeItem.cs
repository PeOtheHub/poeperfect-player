namespace PoePerfect.Player.Core.Models;

public sealed class SeriesEpisodeItem
{
    public required PlaylistChannel Channel { get; init; }

    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public int SortOrder { get; init; }
}
