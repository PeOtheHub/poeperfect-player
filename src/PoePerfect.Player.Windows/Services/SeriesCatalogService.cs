using System.Net;
using System.Text.RegularExpressions;
using APTV.Models;

namespace APTV.Services;

public sealed class SeriesCatalogService
{
    private static readonly Regex StandardEpisodeRegex = new(
        "^(?<title>.+?)(?:\\s*[-|:]\\s*)?S(?:0|O)?(?<season>\\d{1,3})\\s*E(?<episode>\\d{1,4})(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CrossEpisodeRegex = new(
        "^(?<title>.+?)(?:\\s*[-|:]\\s*)?(?<season>\\d{1,3})x(?<episode>\\d{1,4})(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LongEpisodeRegex = new(
        "^(?<title>.+?)(?:\\s*[-|:]\\s*)?Season\\s*(?<season>\\d{1,3})\\s*Episode\\s*(?<episode>\\d{1,4})(?<suffix>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiSpaceRegex = new(
        "\\s{2,}",
        RegexOptions.Compiled);

    private static readonly Regex TitleAndWordRegex = new(
        "\\b(?:och|and)\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<SeriesGroupItem> BuildGroups(IEnumerable<Channel> channels)
    {
        var parsedEpisodes = channels
            .Select((channel, index) => ParseEpisode(channel, index))
            .GroupBy(parsed => parsed.SeriesKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSeriesGroup(group.Key, group.ToList()))
            .ToList();

        return parsedEpisodes;
    }

    public string GetSeriesKey(Channel channel)
    {
        return ParseEpisode(channel, originalIndex: 0).SeriesKey;
    }

    public string? TryGetSeasonFavoriteKey(Channel channel)
    {
        var parsed = ParseEpisode(channel, originalIndex: 0);
        return string.IsNullOrWhiteSpace(parsed.SeriesKey) || string.IsNullOrWhiteSpace(parsed.SeasonKey)
            ? null
            : $"{parsed.Channel.CategoryName.ToLowerInvariant()}::{parsed.SeriesKey}::{parsed.SeasonKey}";
    }

    private static SeriesGroupItem BuildSeriesGroup(string seriesKey, List<ParsedEpisode> parsedEpisodes)
    {
        var orderedEpisodes = parsedEpisodes
            .OrderBy(episode => episode.SeasonSortOrder)
            .ThenBy(episode => episode.EpisodeSortOrder)
            .ThenBy(episode => episode.OriginalIndex)
            .ToList();

        var representativeChannel = orderedEpisodes
            .Select(episode => episode.Channel)
            .FirstOrDefault(channel => !string.IsNullOrWhiteSpace(channel.LogoUrl))
            ?? orderedEpisodes[0].Channel;

        var seasons = orderedEpisodes
            .GroupBy(
                episode => episode.SeasonKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var seasonEpisodes = group
                    .OrderBy(episode => episode.EpisodeSortOrder)
                    .ThenBy(episode => episode.OriginalIndex)
                    .Select((episode, index) => new SeriesEpisodeItem
                    {
                        Channel = episode.Channel,
                        Title = episode.EpisodeTitle,
                        Subtitle = episode.EpisodeSubtitle,
                        SeasonNumber = episode.SeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        SortOrder = episode.EpisodeSortOrder == int.MaxValue
                            ? index
                            : episode.EpisodeSortOrder,
                    })
                    .ToList();

                var firstEpisode = group.First();
                return new SeriesSeasonItem
                {
                    Key = firstEpisode.SeasonKey,
                    Label = firstEpisode.SeasonLabel,
                    FavoriteKey = $"{firstEpisode.Channel.CategoryName.ToLowerInvariant()}::{firstEpisode.SeriesKey}::{firstEpisode.SeasonKey}",
                    SeasonNumber = firstEpisode.SeasonNumber,
                    SortOrder = firstEpisode.SeasonSortOrder,
                    Episodes = seasonEpisodes,
                };
            })
            .OrderBy(season => season.SortOrder)
            .ThenBy(season => season.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SeriesGroupItem
        {
            Key = seriesKey,
            Title = orderedEpisodes[0].SeriesTitle,
            CategoryName = orderedEpisodes[0].Channel.CategoryName,
            RepresentativeChannel = representativeChannel,
            Seasons = seasons,
        };
    }

    private static ParsedEpisode ParseEpisode(Channel channel, int originalIndex)
    {
        var normalizedName = NormalizeWhitespace(channel.Name);
        var match = StandardEpisodeRegex.Match(normalizedName);
        if (!match.Success)
        {
            match = CrossEpisodeRegex.Match(normalizedName);
        }

        if (!match.Success)
        {
            match = LongEpisodeRegex.Match(normalizedName);
        }

        if (match.Success)
        {
            var seriesTitle = CleanSegment(match.Groups["title"].Value);
            var seasonNumber = ParseNullableInt(match.Groups["season"].Value) ?? 1;
            var episodeNumber = ParseNullableInt(match.Groups["episode"].Value);
            var suffix = CleanOptionalSegment(match.Groups["suffix"].Value);
            var episodeTitle = !string.IsNullOrWhiteSpace(suffix)
                ? suffix
                : episodeNumber is > 0
                    ? $"Avsnitt {episodeNumber.Value}"
                    : channel.Name;
            var subtitle = episodeNumber is > 0
                ? $"S{seasonNumber:00}E{episodeNumber.Value:00}"
                : $"Säsong {seasonNumber:00}";

            return new ParsedEpisode(
                channel,
                originalIndex,
                ToKey(seriesTitle),
                seriesTitle,
                $"season-{seasonNumber:000}",
                $"Säsong {seasonNumber}",
                seasonNumber,
                seasonNumber,
                episodeNumber,
                episodeNumber ?? int.MaxValue,
                episodeTitle,
                subtitle);
        }

        var fallbackTitle = CleanSegment(normalizedName);
        return new ParsedEpisode(
            channel,
            originalIndex,
            ToKey(fallbackTitle),
            fallbackTitle,
            "season-001",
            "Säsong 1",
            1,
            1,
            null,
            int.MaxValue,
            channel.Name,
            "Okänd episod");
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, out var result) ? result : null;
    }

    private static string CleanSegment(string value)
    {
        var cleaned = CleanOptionalSegment(value);

        return string.IsNullOrWhiteSpace(cleaned)
            ? "Okänd serie"
            : cleaned;
    }

    private static string CleanOptionalSegment(string value)
    {
        return NormalizeWhitespace(WebUtility.HtmlDecode(value))
            .Trim()
            .Trim('-', '|', ':', '/', '\\')
            .Trim();
    }

    private static string NormalizeWhitespace(string value)
    {
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }

    private static string ToKey(string value)
    {
        var normalized = CleanSegment(value);
        normalized = TitleAndWordRegex.Replace(normalized, "&");
        normalized = normalized.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" & ", "&", StringComparison.Ordinal);
        normalized = normalized.Replace("&", " & ", StringComparison.Ordinal);
        normalized = NormalizeWhitespace(normalized)
            .Trim('-', '|', ':', '/', '\\')
            .Trim();

        return normalized.ToLowerInvariant();
    }

    private sealed record ParsedEpisode(
        Channel Channel,
        int OriginalIndex,
        string SeriesKey,
        string SeriesTitle,
        string SeasonKey,
        string SeasonLabel,
        int? SeasonNumber,
        int SeasonSortOrder,
        int? EpisodeNumber,
        int EpisodeSortOrder,
        string EpisodeTitle,
        string EpisodeSubtitle);
}
