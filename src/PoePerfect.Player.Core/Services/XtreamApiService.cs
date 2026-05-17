using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoePerfect.Player.Core.Models;

namespace PoePerfect.Player.Core.Services;

public sealed class XtreamApiService
{
    private static readonly Regex StreamPathRegex = new(
        "^/(?<prefix>.*?)(?<kind>live|movie|series)/(?<username>[^/]+)/(?<password>[^/]+)/(?<streamId>[^/.?]+)(?<extension>\\.[^/?]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<MovieDetailInfo?> TryLoadMovieDetailAsync(
        string source,
        PlaylistChannel channel,
        IReadOnlyList<PlaylistChannel> knownChannels,
        CancellationToken cancellationToken = default)
    {
        if (channel.ContentType != ChannelContentType.Movie)
        {
            return null;
        }

        var connection = TryCreateConnection(source, knownChannels);
        if (connection is null || !TryGetMovieStreamId(channel, out var streamId))
        {
            return null;
        }

        var uri = BuildPlayerApiUri(connection, "get_vod_info", categoryId: null, ("vod_id", streamId));
        using var response = await SendGetAsync(uri, cancellationToken).ConfigureAwait(false);
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
        var info = TryGetObject(root, "info");
        var movieData = TryGetObject(root, "movie_data");
        var durationSeconds = NormalizeDurationSeconds(
            GetString(info, "duration_secs"),
            GetString(info, "duration"),
            GetString(movieData, "duration"));

        return new MovieDetailInfo(
            CleanText(GetString(movieData, "name") ?? GetString(info, "name") ?? channel.DisplayName),
            GetString(info, "movie_image") ?? GetString(info, "cover_big") ?? GetString(info, "poster_path") ?? channel.LogoUrl,
            CleanText(GetString(info, "plot") ?? GetString(info, "description")),
            CleanText(GetString(info, "genre")),
            CleanText(GetString(info, "cast") ?? GetString(info, "actors")),
            CleanText(GetString(info, "director")),
            CleanText(GetString(info, "rating") ?? GetString(info, "rating_5based")),
            NormalizeDuration(
                GetString(info, "duration_secs"),
                GetString(info, "duration"),
                GetString(movieData, "duration")),
            durationSeconds,
            CleanText(GetString(info, "releasedate") ?? GetString(info, "release_date")));
    }

    public async Task<CategoryRefreshResult?> TryLoadCategoryAsync(
        string source,
        ChannelContentType contentType,
        string categoryName,
        IReadOnlyList<PlaylistChannel> knownChannels,
        CancellationToken cancellationToken = default)
    {
        if (contentType == ChannelContentType.Series || string.IsNullOrWhiteSpace(categoryName))
        {
            return null;
        }

        var connection = TryCreateConnection(source, knownChannels);
        if (connection is null)
        {
            return null;
        }

        var categories = await GetCategoriesAsync(connection, contentType, cancellationToken).ConfigureAwait(false);
        var matchedCategory = categories.FirstOrDefault(category =>
            string.Equals(category.NormalizedName, NormalizeCategoryName(categoryName, contentType), StringComparison.OrdinalIgnoreCase));

        if (matchedCategory is null)
        {
            return null;
        }

        var channels = contentType switch
        {
            ChannelContentType.Live => await GetLiveChannelsAsync(connection, matchedCategory, cancellationToken).ConfigureAwait(false),
            ChannelContentType.Movie => await GetMovieChannelsAsync(connection, matchedCategory, cancellationToken).ConfigureAwait(false),
            _ => [],
        };

        return new CategoryRefreshResult(channels, connection.DisplayName);
    }

    private async Task<IReadOnlyList<ApiCategory>> GetCategoriesAsync(
        XtreamConnection connection,
        ChannelContentType contentType,
        CancellationToken cancellationToken)
    {
        var action = contentType switch
        {
            ChannelContentType.Live => "get_live_categories",
            ChannelContentType.Movie => "get_vod_categories",
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(action))
        {
            return [];
        }

        var uri = BuildPlayerApiUri(connection, action, categoryId: null);
        using var response = await SendGetAsync(uri, cancellationToken).ConfigureAwait(false);
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

            categories.Add(new ApiCategory(id, NormalizeCategoryName(rawName, contentType)));
        }

        return categories;
    }

    private async Task<IReadOnlyList<PlaylistChannel>> GetLiveChannelsAsync(
        XtreamConnection connection,
        ApiCategory category,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlayerApiUri(connection, "get_live_streams", category.Id);
        using var response = await SendGetAsync(uri, cancellationToken).ConfigureAwait(false);
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

        var channels = new List<PlaylistChannel>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = GetString(element, "name") ?? GetString(element, "title");
            var streamId = GetString(element, "stream_id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(streamId))
            {
                continue;
            }

            var streamUri = BuildStreamUri(connection, "live", streamId, connection.LiveOutputExtension);
            channels.Add(new PlaylistChannel(
                name,
                streamUri.ToString(),
                category.NormalizedName,
                GetString(element, "stream_icon"),
                GetString(element, "epg_channel_id") ?? GetString(element, "tvg_id"),
                GetString(element, "tvg_name") ?? name,
                null,
                ParseAddedAt(GetString(element, "added") ?? GetString(element, "added_at") ?? GetString(element, "created_at"))));
        }

        return channels;
    }

    private async Task<IReadOnlyList<PlaylistChannel>> GetMovieChannelsAsync(
        XtreamConnection connection,
        ApiCategory category,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlayerApiUri(connection, "get_vod_streams", category.Id);
        using var response = await SendGetAsync(uri, cancellationToken).ConfigureAwait(false);
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

        var channels = new List<PlaylistChannel>();
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
            channels.Add(new PlaylistChannel(
                name,
                streamUri.ToString(),
                category.NormalizedName,
                GetString(element, "stream_icon"),
                null,
                null,
                null,
                ParseAddedAt(GetString(element, "added") ?? GetString(element, "added_at") ?? GetString(element, "created_at"))));
        }

        return channels;
    }

    private static XtreamConnection? TryCreateConnection(string source, IReadOnlyList<PlaylistChannel> knownChannels)
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
        params (string Key, string Value)[] additionalQuery)
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

        foreach (var (key, value) in additionalQuery)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
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

        return new UriBuilder(connection.PlayerApiUri.Scheme, connection.PlayerApiUri.Host, connection.PlayerApiUri.Port)
        {
            Path = $"{connection.PathPrefix}{contentKind}/{Uri.EscapeDataString(connection.Username)}/{Uri.EscapeDataString(connection.Password)}/{streamId}.{normalizedExtension}",
        }.Uri;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Android) PoePerfectPlayer/1.0");
        request.Headers.Accept.ParseAdd("application/json, application/x-mpegURL, application/vnd.apple.mpegurl, audio/x-mpegurl, text/plain, */*");
        request.Headers.AcceptLanguage.ParseAdd("sv-SE, sv;q=0.9, en-US;q=0.8, en;q=0.7");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.ConnectionClose = true;

        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            request.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority));
        }

        return await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            AllowAutoRedirect = true,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(propertyName, out var property))
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

    private static JsonElement TryGetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? property
            : default;
    }

    private static bool TryGetMovieStreamId(PlaylistChannel channel, out string streamId)
    {
        streamId = string.Empty;
        if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = ParseQueryString(uri.Query);
        foreach (var key in new[] { "vod_id", "stream_id", "movie_id", "id" })
        {
            if (query.TryGetValue(key, out var queryValue) && !string.IsNullOrWhiteSpace(queryValue))
            {
                streamId = queryValue.Trim();
                return true;
            }
        }

        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return false;
        }

        var extension = Path.GetExtension(lastSegment);
        streamId = string.IsNullOrWhiteSpace(extension)
            ? Uri.UnescapeDataString(lastSegment)
            : Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(lastSegment));

        return !string.IsNullOrWhiteSpace(streamId);
    }

    private static int? NormalizeDurationSeconds(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return seconds;
            }

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var timeSpan)
                && timeSpan.TotalSeconds > 0)
            {
                return (int)Math.Round(timeSpan.TotalSeconds);
            }
        }

        return null;
    }

    private static string NormalizeDuration(params string?[] values)
    {
        var durationSeconds = NormalizeDurationSeconds(values);
        if (durationSeconds is { } seconds)
        {
            var timeSpan = TimeSpan.FromSeconds(seconds);
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours} h {timeSpan.Minutes} min";
            }

            return $"{timeSpan.Minutes} min";
        }

        foreach (var value in values)
        {
            var cleaned = CleanText(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return string.Empty;
    }

    private static string CleanText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), "\\s+", " ");
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

    private static string NormalizeCategoryName(string categoryName, ChannelContentType contentType)
    {
        var normalized = categoryName.Trim();
        var lowerName = normalized.ToLowerInvariant();

        string[] prefixes = contentType switch
        {
            ChannelContentType.Live => ["live", "tv", "channels", "channel", "kanaler"],
            ChannelContentType.Movie => ["vod", "movie", "movies", "film", "filmer"],
            ChannelContentType.Series => ["series", "serier", "serie", "tv shows", "tv-shows"],
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

    public sealed record CategoryRefreshResult(IReadOnlyList<PlaylistChannel> Channels, string ProviderName);

    private sealed record XtreamConnection(
        Uri PlayerApiUri,
        string PathPrefix,
        string Username,
        string Password,
        string LiveOutputExtension,
        string DisplayName);

    private sealed record ApiCategory(string Id, string NormalizedName);
}
