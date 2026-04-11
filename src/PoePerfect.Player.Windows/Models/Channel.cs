using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace APTV.Models;

public sealed class Channel : INotifyPropertyChanged
{
    private bool _isFavorite;
    private ChannelGuideInfo? _guideInfo;
    private ImageSource? _posterImageSource;

    public Channel(
        string name,
        string url,
        string? group,
        string? logoUrl,
        string? tvgId = null,
        string? tvgName = null,
        IReadOnlyList<string>? mediaOptions = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Unnamed channel" : name.Trim();
        Url = url;
        Group = string.IsNullOrWhiteSpace(group) ? "Okänd grupp" : group.Trim();
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        TvgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim();
        TvgName = string.IsNullOrWhiteSpace(tvgName) ? null : tvgName.Trim();
        MediaOptions = mediaOptions?.Where(option => !string.IsNullOrWhiteSpace(option)).ToArray() ?? [];
        ContentType = DetectContentType(Name, Url, Group);
        CategoryName = NormalizeCategory(Group, ContentType);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Url { get; }

    public string Group { get; }

    public string? LogoUrl { get; }

    public string? TvgId { get; }

    public string? TvgName { get; }

    public IReadOnlyList<string> MediaOptions { get; }

    public ChannelContentType ContentType { get; }

    public string CategoryName { get; }

    public bool IsVod => ContentType is ChannelContentType.Movie or ChannelContentType.Series;

    public ImageSource? PosterImageSource
    {
        get => _posterImageSource;
        set
        {
            if (ReferenceEquals(_posterImageSource, value))
            {
                return;
            }

            _posterImageSource = value;
            OnPropertyChanged();
        }
    }

    public ChannelGuideInfo? GuideInfo
    {
        get => _guideInfo;
        set
        {
            if (Equals(_guideInfo, value))
            {
                return;
            }

            _guideInfo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentProgramme));
            OnPropertyChanged(nameof(NextProgramme));
            OnPropertyChanged(nameof(HasCurrentProgramme));
            OnPropertyChanged(nameof(HasNextProgramme));
            OnPropertyChanged(nameof(CardSubtitleText));
            OnPropertyChanged(nameof(FullscreenSubtitleText));
            OnPropertyChanged(nameof(FullscreenGuideText));
        }
    }

    public EpgProgrammeInfo? CurrentProgramme => GuideInfo?.Current;

    public EpgProgrammeInfo? NextProgramme => GuideInfo?.Next;

    public bool HasCurrentProgramme => CurrentProgramme is not null;

    public bool HasNextProgramme => NextProgramme is not null;

    public string CardSubtitleText => ContentType == ChannelContentType.Live
        ? CurrentProgramme?.SummaryText ?? CategoryName
        : CategoryName;

    public string FullscreenSubtitleText => ContentType == ChannelContentType.Live
        ? CurrentProgramme?.SummaryText ?? CategoryName
        : CategoryName;

    public string FullscreenGuideText => NextProgramme is null
        ? string.Empty
        : $"Nästa: {NextProgramme.SummaryText}";

    public string ContentTypeLabel => ContentType switch
    {
        ChannelContentType.Movie => "Film",
        ChannelContentType.Series => "Serier",
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
        if (ContainsAny(lowerName, "season", "säsong", "episode", "avsnitt")
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
}
