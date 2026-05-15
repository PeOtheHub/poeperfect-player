using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PoePerfect.Player.Core.Models;

public sealed class PlaylistChannel : INotifyPropertyChanged
{
    private string? _artworkPath;
    private bool _isFavorite;

    public PlaylistChannel(
        string name,
        string url,
        string? group,
        string? logoUrl,
        string? tvgId = null,
        string? tvgName = null,
        IReadOnlyList<string>? mediaOptions = null,
        DateTimeOffset? addedAtUtc = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Unnamed channel" : name.Trim();
        Url = url;
        Group = string.IsNullOrWhiteSpace(group) ? "Okand grupp" : group.Trim();
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        TvgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim();
        TvgName = string.IsNullOrWhiteSpace(tvgName) ? null : tvgName.Trim();
        MediaOptions = mediaOptions?.Where(option => !string.IsNullOrWhiteSpace(option)).ToArray() ?? [];
        AddedAtUtc = addedAtUtc;
        ContentType = DetectContentType(Name, Url, Group);
        CategoryName = NormalizeCategory(Group, ContentType);
        DisplayName = CleanDisplayTitle(Name);
        MetadataChips = ExtractMetadataChips(Name, CategoryName, maxCount: 4);
        CompactMetadataChips = MetadataChips.Take(3).ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string DisplayName { get; }

    public string Url { get; }

    public string Group { get; }

    public string? LogoUrl { get; }

    public string? TvgId { get; }

    public string? TvgName { get; }

    public IReadOnlyList<string> MediaOptions { get; }

    public DateTimeOffset? AddedAtUtc { get; }

    public IReadOnlyList<string> MetadataChips { get; }

    public IReadOnlyList<string> CompactMetadataChips { get; }

    public ChannelContentType ContentType { get; }

    public string CategoryName { get; }

    public bool IsVod => ContentType is ChannelContentType.Movie or ChannelContentType.Series;

    public string? ArtworkPath
    {
        get => _artworkPath;
        set
        {
            if (string.Equals(_artworkPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _artworkPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasArtwork));
        }
    }

    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath);

    public string ArtworkPlaceholderText => GetArtworkPlaceholderText(DisplayName);

    public string ContentTypeLabel => ContentType switch
    {
        ChannelContentType.Movie => "Film",
        ChannelContentType.Series => "Serie",
        _ => "Live",
    };

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static ChannelContentType DetectContentType(string name, string url, string group)
    {
        var lowerUrl = url.ToLowerInvariant();
        if (lowerUrl.Contains("/series/", StringComparison.Ordinal)
            || lowerUrl.Contains("series_id", StringComparison.Ordinal)
            || lowerUrl.Contains("type=series", StringComparison.Ordinal))
        {
            return ChannelContentType.Series;
        }

        if (lowerUrl.Contains("/movie/", StringComparison.Ordinal)
            || lowerUrl.Contains("/vod/", StringComparison.Ordinal)
            || lowerUrl.Contains("movie_id", StringComparison.Ordinal)
            || lowerUrl.Contains("type=movie", StringComparison.Ordinal))
        {
            return ChannelContentType.Movie;
        }

        if (lowerUrl.Contains("/live/", StringComparison.Ordinal)
            || lowerUrl.Contains("type=live", StringComparison.Ordinal))
        {
            return ChannelContentType.Live;
        }

        var lowerName = name.ToLowerInvariant();
        var lowerGroup = group.ToLowerInvariant();
        if (ContainsAny(lowerName, "season", "sasong", "episode", "avsnitt")
            || StartsWithAny(lowerGroup, "series", "serier", "serie", "tv shows", "tv-shows"))
        {
            return ChannelContentType.Series;
        }

        if (StartsWithAny(lowerGroup, "vod", "movie", "movies", "film", "filmer")
            || IsLikelyMovieFile(lowerUrl))
        {
            return ChannelContentType.Movie;
        }

        return ChannelContentType.Live;
    }

    private static string NormalizeCategory(string group, ChannelContentType contentType)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return GetFallbackCategory(contentType);
        }

        var normalized = group.Trim();
        var lowerGroup = normalized.ToLowerInvariant();

        string[] prefixes = contentType switch
        {
            ChannelContentType.Movie => ["vod", "movie", "movies", "film", "filmer"],
            ChannelContentType.Series => ["series", "serier", "serie", "tv shows", "tv-shows"],
            _ => ["live", "tv", "channels", "channel", "kanaler"],
        };

        foreach (var prefix in prefixes)
        {
            if (!lowerGroup.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            normalized = normalized[prefix.Length..].Trim();
            normalized = normalized.TrimStart('|', '-', '/', '\\', ':', '>', ' ');
            break;
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? GetFallbackCategory(contentType)
            : normalized.Trim();
    }

    private static string GetFallbackCategory(ChannelContentType contentType)
    {
        return contentType switch
        {
            ChannelContentType.Movie => "Filmer",
            ChannelContentType.Series => "Serier",
            _ => "Kanaler",
        };
    }

    private static bool StartsWithAny(string input, params string[] values)
    {
        return values.Any(value => input.StartsWith(value, StringComparison.Ordinal));
    }

    private static bool ContainsAny(string input, params string[] values)
    {
        return values.Any(value => input.Contains(value, StringComparison.Ordinal));
    }

    private static bool IsLikelyMovieFile(string lowerUrl)
    {
        var extension = Path.GetExtension(lowerUrl);
        return extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".m4v" or ".wmv" or ".mpeg" or ".mpg" or ".webm";
    }

    private static string CleanDisplayTitle(string value)
    {
        var withoutBracketMetadata = Regex.Replace(value, "\\[[^\\]]+\\]", " ");
        var cleaned = Regex.Replace(withoutBracketMetadata, "\\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? value : cleaned;
    }

    private static IReadOnlyList<string> ExtractMetadataChips(string name, string categoryName, int maxCount)
    {
        var items = new List<string>();
        var text = $"{name} {categoryName}";
        var bracketItems = Regex
            .Matches(name, "\\[([^\\]]+)\\]")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item));

        var yearMatch = Regex.Match(name, "\\b(19\\d{2}|20\\d{2})\\b");
        if (yearMatch.Success)
        {
            items.Add(yearMatch.Groups[1].Value);
        }

        foreach (var item in bracketItems)
        {
            var normalized = NormalizeMetadataChip(item);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !Regex.IsMatch(normalized, "^(19\\d{2}|20\\d{2})$"))
            {
                items.Add(normalized);
            }
        }

        if (Regex.IsMatch(text, "\\b(?:4k|uhd|2160p)\\b", RegexOptions.IgnoreCase))
        {
            items.Add("4K");
        }
        else if (Regex.IsMatch(text, "\\b1080p\\b", RegexOptions.IgnoreCase))
        {
            items.Add("1080p");
        }

        if (Regex.IsMatch(text, "dolby\\s*vision|\\bdv\\b", RegexOptions.IgnoreCase))
        {
            items.Add("Dolby Vision");
        }

        if (Regex.IsMatch(text, "multi[-\\s]*sub|multi\\s*subtitle", RegexOptions.IgnoreCase))
        {
            items.Add("Multi-Sub");
        }

        if (Regex.IsMatch(text, "multi[-\\s]*audio|multi\\s*audio", RegexOptions.IgnoreCase))
        {
            items.Add("Multi-Audio");
        }

        if (Regex.IsMatch(text, "\\b(?:hdr10?|hdr)\\b", RegexOptions.IgnoreCase))
        {
            items.Add("HDR");
        }

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToArray();
    }

    private static string NormalizeMetadataChip(string value)
    {
        var normalized = Regex.Replace(value.Trim(), "\\s+", " ");
        var lower = normalized.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (Regex.IsMatch(normalized, "^(multi[-\\s]*sub|multi\\s*subtitle)s?$", RegexOptions.IgnoreCase))
        {
            return "Multi-Sub";
        }

        if (Regex.IsMatch(normalized, "^multi[-\\s]*audio$", RegexOptions.IgnoreCase))
        {
            return "Multi-Audio";
        }

        if (Regex.IsMatch(normalized, "^dolby\\s*vision$", RegexOptions.IgnoreCase))
        {
            return "Dolby Vision";
        }

        if (lower == "pre")
        {
            return "PRE";
        }

        if (Regex.IsMatch(normalized, "^(4k|uhd|2160p)$", RegexOptions.IgnoreCase))
        {
            return "4K";
        }

        if (Regex.IsMatch(normalized, "^(1080p|720p|hdr|hdr10)$", RegexOptions.IgnoreCase))
        {
            return normalized.ToUpperInvariant();
        }

        return normalized.Length <= 18 ? normalized : string.Empty;
    }

    private static string GetArtworkPlaceholderText(string name)
    {
        var parts = name
            .Split([' ', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0]))
            .ToArray();

        if (parts.Length == 0)
        {
            return "TV";
        }

        return new string(parts);
    }
}
