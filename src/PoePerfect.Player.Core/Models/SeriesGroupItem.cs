namespace PoePerfect.Player.Core.Models;

public sealed class SeriesGroupItem
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required string CategoryName { get; init; }

    public required PlaylistChannel RepresentativeChannel { get; init; }

    public required IReadOnlyList<SeriesSeasonItem> Seasons { get; init; }

    public int SeasonCount => Seasons.Count;

    public int EpisodeCount => Seasons.Sum(season => season.EpisodeCount);

    public string SummaryText => SeasonCount <= 1
        ? $"{EpisodeCount} avsnitt"
        : $"{SeasonCount} säsonger - {EpisodeCount} avsnitt";
}
