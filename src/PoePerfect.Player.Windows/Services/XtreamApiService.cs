using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using APTV.Models;

namespace APTV.Services;

public sealed class XtreamApiService
{
    private static readonly Regex StreamPathRegex = new(
        "^/(?<prefix>.*?)(?<kind>live|movie|vod|series)/(?<username>[^/]+)/(?<password>[^/]+)/(?<streamId>[^/.?]+)(?<extension>\\.[^/?]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<CategoryRefreshResult?> TryLoadCategoryAsync(
        string source,
        BrowseSection section,
        string categoryName,
        IReadOnlyList<Channel> knownChannels,
        int? maxChannels = null,
        CancellationToken cancellationToken = default)
    {
        if (section == BrowseSection.Series)
        {
            return null;
        }

        var connection = TryCreateConnection(source, knownChannels);
        if (connection is null)
        {
            return null;
        }

        var categories = await GetCategoriesAsync(connection, section, cancellationToken);
        var matchedCategory = categories.FirstOrDefault(category =>
            string.Equals(category.NormalizedName, NormalizeCategoryName(categoryName, section), StringComparison.OrdinalIgnoreCase));

        if (matchedCategory is null)
        {
            return null;
        }

        var channels = section switch
        {
            BrowseSection.Live => await GetLiveChannelsAsync(connection, matchedCategory, maxChannels, cancellationToken),
            BrowseSection.Movies => await GetMovieChannelsAsync(connection, matchedCategory, null, maxChannels, cancellationToken),
            _ => [],
        };

        return new CategoryRefreshResult(channels, connection.DisplayName);
    }

    public async Task<CategoryRefreshResult?> TryLoadLatestAddedAsync(
        string source,
        BrowseSection section,
        IReadOnlyList<Channel> knownChannels,
        int maxChannels,
        CancellationToken cancellationToken = default)
    {
        if (section != BrowseSection.Movies)
        {
            return null;
        }

        var connection = TryCreateConnection(source, knownChannels);
        if (connection is null)
        {
            return null;
        }

        var categories = await GetCategoriesAsync(connection, section, cancellationToken);
        var channels = await GetMovieChannelsAsync(connection, category: null, categories, maxChannels: null, cancellationToken);
        var latestChannels = channels
            .Where(channel => channel.AddedAtUtc.HasValue)
            .OrderByDescending(channel => channel.AddedAtUtc)
            .Take(Math.Max(1, maxChannels))
            .ToList();

        return latestChannels.Count == 0
            ? null
            : new CategoryRefreshResult(latestChannels, connection.DisplayName);
    }

    public async Task<MovieInfoResult?> TryLoadMovieInfoAsync(
        string source,
        Channel channel,
        IReadOnlyList<Channel> knownChannels,
        CancellationToken cancellationToken = default)
    {
        if (channel.ContentType != ChannelContentType.Movie)
        {
            return null;
        }

        var connection = TryCreateConnection(source, knownChannels);
        if (connection is null || !TryGetStreamId(channel.Url, out var streamId))
        {
            return null;
        }

        var uri = BuildPlayerApiUri(
            connection,
            "get_vod_info",
            categoryId: null,
            extraQueryParts:
            [
                new KeyValuePair<string, string>("vod_id", streamId),
            ]);

        using var response = await BrowserHttpClient.SendGetAsync(uri, "Xtream movie info request", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var info = root.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object
            ? infoElement
            : root;
        var movieData = root.TryGetProperty("movie_data", out var movieDataElement) && movieDataElement.ValueKind == JsonValueKind.Object
            ? movieDataElement
            : root;

        var title = CleanText(GetString(movieData, "name")
            ?? GetString(info, "name")
            ?? GetString(movieData, "title")
            ?? GetString(info, "title"));
        var posterUrl = FirstNonEmpty(
            GetString(info, "movie_image"),
            GetString(info, "cover_big"),
            GetString(info, "poster_path"),
            GetString(movieData, "stream_icon"));

        return new MovieInfoResult(
            string.IsNullOrWhiteSpace(title) ? channel.Name : title,
            posterUrl,
            CleanText(GetString(info, "plot") ?? GetString(info, "description")),
            CleanText(GetString(info, "genre")),
            CleanText(GetString(info, "cast") ?? GetString(info, "actors")),
            CleanText(GetString(info, "director")),
            CleanText(GetString(info, "rating") ?? GetString(info, "rating_5based")),
            NormalizeDuration(GetString(info, "duration_secs"), GetString(info, "duration"), GetString(movieData, "duration")),
            CleanText(GetString(info, "releasedate") ?? GetString(info, "release_date")),
            ParseAddedAt(GetString(movieData, "added") ?? GetString(info, "added")));
    }

    private async Task<IReadOnlyList<ApiCategory>> GetCategoriesAsync(
        XtreamConnection connection,
        BrowseSection section,
        CancellationToken cancellationToken)
    {
        var action = section switch
        {
            BrowseSection.Live => "get_live_categories",
            BrowseSection.Movies => "get_vod_categories",
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(action))
        {
            return [];
        }

        var uri = BuildPlayerApiUri(connection, action, categoryId: null);
        using var response = await BrowserHttpClient.SendGetAsync(uri, "Xtream categories request", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var categories = new List<ApiCategory>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = GetString(element, "category_id");
            var rawName = GetString(element, "category_name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            categories.Add(new ApiCategory(id, rawName.Trim(), NormalizeCategoryName(rawName, section)));
        }

        return categories;
    }

    private async Task<IReadOnlyList<Channel>> GetLiveChannelsAsync(
        XtreamConnection connection,
        ApiCategory category,
        int? maxChannels,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlayerApiUri(connection, "get_live_streams", category.Id);
        using var response = await BrowserHttpClient.SendGetAsync(uri, "Xtream live request", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var channels = new List<Channel>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = GetString(element, "name") ?? GetString(element, "title");
            var streamId = GetString(element, "stream_id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(streamId))
            {
                continue;
            }

            var streamUri = BuildStreamUri(connection, "live", streamId, connection.LiveOutputExtension);
            channels.Add(new Channel(
                name,
                streamUri.ToString(),
                category.NormalizedName,
                GetString(element, "stream_icon"),
                GetString(element, "epg_channel_id") ?? GetString(element, "tvg_id"),
                GetString(element, "tvg_name") ?? name,
                null,
                ParseAddedAt(GetString(element, "added") ?? GetString(element, "added_at") ?? GetString(element, "created_at"))));

            if (maxChannels is not null && channels.Count >= maxChannels.Value)
            {
                break;
            }
        }

        return channels;
    }

    private async Task<IReadOnlyList<Channel>> GetMovieChannelsAsync(
        XtreamConnection connection,
        ApiCategory? category,
        IReadOnlyList<ApiCategory>? categories,
        int? maxChannels,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlayerApiUri(connection, "get_vod_streams", category?.Id);
        using var response = await BrowserHttpClient.SendGetAsync(uri, "Xtream movie request", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var categoryById = categories?
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var channels = new List<Channel>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = GetString(element, "name") ?? GetString(element, "title");
            var streamId = GetString(element, "stream_id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(streamId))
            {
                continue;
            }

            var extension = GetString(element, "container_extension");
            var streamUri = BuildStreamUri(connection, "movie", streamId, extension);
            var categoryName = category?.NormalizedName ?? GetMovieCategoryName(element, categoryById);
            channels.Add(new Channel(
                name,
                streamUri.ToString(),
                categoryName,
                GetString(element, "stream_icon"),
                null,
                null,
                null,
                ParseAddedAt(GetString(element, "added") ?? GetString(element, "added_at") ?? GetString(element, "created_at"))));

            if (maxChannels is not null && channels.Count >= maxChannels.Value)
            {
                break;
            }
        }

        return channels;
    }

    private static string GetMovieCategoryName(
        JsonElement element,
        IReadOnlyDictionary<string, ApiCategory>? categoryById)
    {
        var categoryId = GetString(element, "category_id");
        if (!string.IsNullOrWhiteSpace(categoryId)
            && categoryById is not null
            && categoryById.TryGetValue(categoryId, out var category))
        {
            return category.NormalizedName;
        }

        var rawCategoryName = GetString(element, "category_name");
        return string.IsNullOrWhiteSpace(rawCategoryName)
            ? "Filmer"
            : NormalizeCategoryName(rawCategoryName, BrowseSection.Movies);
    }

    private static XtreamConnection? TryCreateConnection(string source, IReadOnlyList<Channel> knownChannels)
    {
        if (TryCreateConnectionFromSource(source, out var sourceConnection))
        {
            return sourceConnection;
        }

        foreach (var channel in knownChannels)
        {
            if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var channelUri))
            {
                continue;
            }

            var match = StreamPathRegex.Match(channelUri.AbsolutePath);
            if (!match.Success)
            {
                continue;
            }

            var pathPrefix = match.Groups["prefix"].Value;
            if (!string.IsNullOrEmpty(pathPrefix) && !pathPrefix.EndsWith("/", StringComparison.Ordinal))
            {
                pathPrefix += "/";
            }

            var extension = match.Groups["extension"].Success
                ? match.Groups["extension"].Value.TrimStart('.')
                : null;

            return new XtreamConnection(
                new UriBuilder(channelUri.Scheme, channelUri.Host, channelUri.Port)
                {
                    Path = $"{pathPrefix}player_api.php",
                }.Uri,
                EnsurePathPrefix(pathPrefix),
                Uri.UnescapeDataString(match.Groups["username"].Value),
                Uri.UnescapeDataString(match.Groups["password"].Value),
                extension ?? "ts",
                channelUri.Host);
        }

        return null;
    }

    private static bool TryCreateConnectionFromSource(string source, out XtreamConnection? connection)
    {
        connection = null;

        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
            || (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var query = ParseQueryString(sourceUri.Query);
        if (!query.TryGetValue("username", out var username)
            || !query.TryGetValue("password", out var password)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var sourcePath = sourceUri.AbsolutePath.TrimStart('/');
        var directory = sourcePath.Contains('/')
            ? sourcePath[..(sourcePath.LastIndexOf('/') + 1)]
            : string.Empty;

        connection = new XtreamConnection(
            new UriBuilder(sourceUri.Scheme, sourceUri.Host, sourceUri.Port)
            {
                Path = $"{directory}player_api.php",
            }.Uri,
            EnsurePathPrefix(directory),
            username,
            password,
            query.TryGetValue("output", out var output) && !string.IsNullOrWhiteSpace(output) ? output : "ts",
            sourceUri.Host);

        return true;
    }

    private static Uri BuildPlayerApiUri(
        XtreamConnection connection,
        string action,
        string? categoryId,
        IReadOnlyList<KeyValuePair<string, string>>? extraQueryParts = null)
    {
        var queryParts = new List<string>
        {
            $"username={Uri.EscapeDataString(connection.Username)}",
            $"password={Uri.EscapeDataString(connection.Password)}",
            $"action={Uri.EscapeDataString(action)}",
        };

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            queryParts.Add($"category_id={Uri.EscapeDataString(categoryId)}");
        }

        if (extraQueryParts is not null)
        {
            foreach (var extraQueryPart in extraQueryParts)
            {
                if (string.IsNullOrWhiteSpace(extraQueryPart.Key) || string.IsNullOrWhiteSpace(extraQueryPart.Value))
                {
                    continue;
                }

                queryParts.Add($"{Uri.EscapeDataString(extraQueryPart.Key)}={Uri.EscapeDataString(extraQueryPart.Value)}");
            }
        }

        return new UriBuilder(connection.PlayerApiUri)
        {
            Query = string.Join("&", queryParts),
        }.Uri;
    }

    private static Uri BuildStreamUri(XtreamConnection connection, string contentKind, string streamId, string? extension)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? (contentKind == "live" ? connection.LiveOutputExtension : "mp4")
            : extension.Trim().TrimStart('.');

        var builder = new UriBuilder(connection.PlayerApiUri.Scheme, connection.PlayerApiUri.Host, connection.PlayerApiUri.Port)
        {
            Path = $"{connection.PathPrefix}{contentKind}/{Uri.EscapeDataString(connection.Username)}/{Uri.EscapeDataString(connection.Password)}/{streamId}.{normalizedExtension}",
        };

        return builder.Uri;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static bool TryGetStreamId(string url, out string streamId)
    {
        streamId = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var channelUri))
        {
            return false;
        }

        var match = StreamPathRegex.Match(channelUri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        streamId = Uri.UnescapeDataString(match.Groups["streamId"].Value);
        return !string.IsNullOrWhiteSpace(streamId);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        var withoutMarkup = Regex.Replace(decoded, "<.*?>", " ");
        return Regex.Replace(withoutMarkup, "\\s+", " ").Trim();
    }

    private static string NormalizeDuration(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return FormatDuration(TimeSpan.FromSeconds(seconds));
            }

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var timeSpan)
                && timeSpan > TimeSpan.Zero)
            {
                return FormatDuration(timeSpan);
            }

            return CleanText(trimmed);
        }

        return string.Empty;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
            : $"{duration.Minutes} min";
    }

    private static DateTimeOffset? ParseAddedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixValue))
        {
            try
            {
                return unixValue > 9_999_999_999L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue).ToUniversalTime()
                    : DateTimeOffset.FromUnixTimeSeconds(unixValue).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            values[key] = value;
        }

        return values;
    }

    private static string NormalizeCategoryName(string categoryName, BrowseSection section)
    {
        var normalized = categoryName.Trim();
        var lowerName = normalized.ToLowerInvariant();

        string[] prefixes = section switch
        {
            BrowseSection.Live => ["live", "tv", "channels", "channel", "kanaler"],
            BrowseSection.Movies => ["vod", "movie", "movies", "film", "filmer"],
            BrowseSection.Series => ["series", "serier", "serie", "tv shows", "tv-shows"],
            _ => [],
        };

        foreach (var prefix in prefixes)
        {
            if (!lowerName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            normalized = normalized[prefix.Length..].TrimStart('|', '-', '/', '\\', ':', '>', ' ');
            break;
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? categoryName.Trim()
            : normalized.Trim();
    }

    private static string EnsurePathPrefix(string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return string.Empty;
        }

        var normalized = pathPrefix.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : $"{normalized}/";
    }

    public sealed record CategoryRefreshResult(IReadOnlyList<Channel> Channels, string ProviderName);

    public sealed record MovieInfoResult(
        string Title,
        string PosterUrl,
        string Plot,
        string Genre,
        string Cast,
        string Director,
        string Rating,
        string Duration,
        string ReleaseDate,
        DateTimeOffset? AddedAtUtc);

    private sealed record XtreamConnection(
        Uri PlayerApiUri,
        string PathPrefix,
        string Username,
        string Password,
        string LiveOutputExtension,
        string DisplayName);

    private sealed record ApiCategory(string Id, string RawName, string NormalizedName);
}
