using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APTV.Models;

namespace APTV.Services;

public sealed class PlaylistCacheStore
{
    private const int CurrentSchemaVersion = 3;
    private static readonly AppLogger Logger = AppLogger.Instance;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public PlaylistCacheStore()
    {
        CacheDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV",
            "playlist-cache");
    }

    public string CacheDirectoryPath { get; }

    public async Task<CachedPlaylist?> TryLoadAsync(
        string source,
        int? maxChannels = null,
        CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath(source);
        if (!File.Exists(cacheFilePath))
        {
            Logger.Info($"Playlist cache file not found. Source={Logger.DescribeSource(source)}");
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cacheFilePath);
            var cacheEntry = await JsonSerializer.DeserializeAsync<PlaylistCacheEntry>(stream, cancellationToken: cancellationToken);
            if (cacheEntry is null
                || cacheEntry.SchemaVersion != CurrentSchemaVersion
                || !string.Equals(cacheEntry.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"Playlist cache rejected due to schema or source mismatch. Source={Logger.DescribeSource(source)}");
                return null;
            }

            if (File.Exists(source))
            {
                var currentLastWriteUtc = File.GetLastWriteTimeUtc(source);
                if (cacheEntry.SourceLastWriteUtc != currentLastWriteUtc)
                {
                    Logger.Info($"Playlist cache invalidated because local file changed. Source={Logger.DescribeSource(source)}");
                    return null;
                }
            }

            var channels = cacheEntry.Channels
                .Select(ToChannel)
                .ToList();

            if (maxChannels is not null && channels.Count > maxChannels.Value)
            {
                channels = channels.Take(maxChannels.Value).ToList();
            }

            Logger.Info($"Playlist cache loaded. Source={Logger.DescribeSource(source)}, Channels={channels.Count}");
            return new CachedPlaylist(channels, cacheEntry.CachedAtUtc);
        }
        catch (JsonException)
        {
            Logger.Warning($"Playlist cache could not be parsed and will be ignored. Source={Logger.DescribeSource(source)}");
            return null;
        }
    }

    public async Task SaveAsync(
        string source,
        IReadOnlyCollection<Channel> channels,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CacheDirectoryPath);

        var cacheEntry = new PlaylistCacheEntry
        {
            SchemaVersion = CurrentSchemaVersion,
            Source = source,
            CachedAtUtc = DateTimeOffset.UtcNow,
            SourceLastWriteUtc = File.Exists(source) ? File.GetLastWriteTimeUtc(source) : null,
            Channels = channels.Select(ToCachedChannel).ToList(),
        };

        var cacheFilePath = GetCacheFilePath(source);
        var temporaryFilePath = $"{cacheFilePath}.tmp";

        await using (var stream = File.Create(temporaryFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, cacheEntry, JsonOptions, cancellationToken);
        }

        File.Move(temporaryFilePath, cacheFilePath, true);
        Logger.Info($"Playlist cache saved. Source={Logger.DescribeSource(source)}, Channels={channels.Count}");
    }

    private string GetCacheFilePath(string source)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source.Trim());
        var hashBytes = SHA256.HashData(sourceBytes);
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(CacheDirectoryPath, $"{hash}.json");
    }

    private static CachedChannel ToCachedChannel(Channel channel)
    {
        return new CachedChannel
        {
            Name = channel.Name,
            Url = channel.Url,
            Group = channel.Group,
            LogoUrl = channel.LogoUrl,
            TvgId = channel.TvgId,
            TvgName = channel.TvgName,
            AddedAtUtc = channel.AddedAtUtc,
            MediaOptions = channel.MediaOptions.ToList(),
        };
    }

    private static Channel ToChannel(CachedChannel cachedChannel)
    {
        return new Channel(
            cachedChannel.Name,
            cachedChannel.Url,
            cachedChannel.Group,
            cachedChannel.LogoUrl,
            cachedChannel.TvgId,
            cachedChannel.TvgName,
            cachedChannel.MediaOptions,
            cachedChannel.AddedAtUtc);
    }

    public sealed record CachedPlaylist(IReadOnlyList<Channel> Channels, DateTimeOffset CachedAtUtc);

    private sealed class PlaylistCacheEntry
    {
        public int SchemaVersion { get; set; }

        public string Source { get; set; } = string.Empty;

        public DateTimeOffset CachedAtUtc { get; set; }

        public DateTimeOffset? SourceLastWriteUtc { get; set; }

        public List<CachedChannel> Channels { get; set; } = [];
    }

    private sealed class CachedChannel
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string? LogoUrl { get; set; }

        public string? TvgId { get; set; }

        public string? TvgName { get; set; }

        public DateTimeOffset? AddedAtUtc { get; set; }

        public List<string> MediaOptions { get; set; } = [];
    }
}
