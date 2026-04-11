using System.Text.Json;
using System.Text.RegularExpressions;
using APTV.Models;

namespace APTV.Services;

public sealed class XtreamApiService
{
    private static readonly Regex StreamPathRegex = new(
        "^/(?<prefix>.*?)(?<kind>live|movie|series)/(?<username>[^/]+)/(?<password>[^/]+)/(?<streamId>[^/.?]+)(?<extension>\\.[^/?]+)?",
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
            BrowseSection.Movies => await GetMovieChannelsAsync(connection, matchedCategory, maxChannels, cancellationToken),
            _ => [],
        };

        return new CategoryRefreshResult(channels, connection.DisplayName);
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
                GetString(element, "tvg_name") ?? name));

            if (maxChannels is not null && channels.Count >= maxChannels.Value)
            {
                break;
            }
        }

        return channels;
    }

    private async Task<IReadOnlyList<Channel>> GetMovieChannelsAsync(
        XtreamConnection connection,
        ApiCategory category,
        int? maxChannels,
        CancellationToken cancellationToken)
    {
        var uri = BuildPlayerApiUri(connection, "get_vod_streams", category.Id);
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
            channels.Add(new Channel(
                name,
                streamUri.ToString(),
                category.NormalizedName,
                GetString(element, "stream_icon"),
                null,
                null));

            if (maxChannels is not null && channels.Count >= maxChannels.Value)
            {
                break;
            }
        }

        return channels;
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

    private static Uri BuildPlayerApiUri(XtreamConnection connection, string action, string? categoryId)
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

    private sealed record XtreamConnection(
        Uri PlayerApiUri,
        string PathPrefix,
        string Username,
        string Password,
        string LiveOutputExtension,
        string DisplayName);

    private sealed record ApiCategory(string Id, string RawName, string NormalizedName);
}
