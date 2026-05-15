using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoePerfect.Player.Core.Models;

namespace PoePerfect.Player.Core.Services;

public sealed class PlaylistCacheStore(string cacheDirectoryPath)
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public string CacheDirectoryPath { get; } = cacheDirectoryPath;

    public async Task<CachedPlaylist?> TryLoadAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath(source);
        if (!File.Exists(cacheFilePath))
        {
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
                return null;
            }

            if (File.Exists(source))
            {
                var currentLastWriteUtc = File.GetLastWriteTimeUtc(source);
                if (cacheEntry.SourceLastWriteUtc != currentLastWriteUtc)
                {
                    return null;
                }
            }

            var channels = cacheEntry.Channels
                .Select(ToPlaylistChannel)
                .ToList();

            return new CachedPlaylist(channels, cacheEntry.CachedAtUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<CachedPlaylistIndex?> TryLoadIndexAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var cacheIndexFilePath = GetCacheIndexFilePath(source);
        if (!File.Exists(cacheIndexFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cacheIndexFilePath);
            var cacheIndexEntry = await JsonSerializer.DeserializeAsync<PlaylistCacheIndexEntry>(stream, cancellationToken: cancellationToken);
            if (!IsValidCacheEntry(cacheIndexEntry, source))
            {
                return null;
            }

            var sections = cacheIndexEntry!.Sections
                .Select(section => new CachedSectionIndex(
                    section.ContentType,
                    section.ItemCount,
                    section.Categories
                        .Select(category => new CachedCategoryIndex(category.Key, category.Label, category.Count))
                        .ToList()))
                .ToList();

            return new CachedPlaylistIndex(sections, cacheIndexEntry.CachedAtUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        string source,
        IReadOnlyCollection<PlaylistChannel> channels,
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
        var cacheIndexEntry = BuildCacheIndexEntry(cacheEntry, channels);

        var cacheFilePath = GetCacheFilePath(source);
        var temporaryFilePath = $"{cacheFilePath}.tmp";
        var cacheIndexFilePath = GetCacheIndexFilePath(source);
        var temporaryIndexFilePath = $"{cacheIndexFilePath}.tmp";

        await using (var stream = File.Create(temporaryFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, cacheEntry, JsonOptions, cancellationToken);
        }

        await using (var stream = File.Create(temporaryIndexFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, cacheIndexEntry, JsonOptions, cancellationToken);
        }

        File.Move(temporaryFilePath, cacheFilePath, true);
        File.Move(temporaryIndexFilePath, cacheIndexFilePath, true);
    }

    private string GetCacheFilePath(string source)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source.Trim());
        var hashBytes = SHA256.HashData(sourceBytes);
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(CacheDirectoryPath, $"{hash}.json");
    }

    private string GetCacheIndexFilePath(string source)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source.Trim());
        var hashBytes = SHA256.HashData(sourceBytes);
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(CacheDirectoryPath, $"{hash}.index.json");
    }

    private static bool IsValidCacheEntry(PlaylistCacheEntry? cacheEntry, string source)
    {
        if (cacheEntry is null
            || cacheEntry.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(cacheEntry.Source, source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(source))
        {
            var currentLastWriteUtc = File.GetLastWriteTimeUtc(source);
            if (cacheEntry.SourceLastWriteUtc != currentLastWriteUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static PlaylistCacheIndexEntry BuildCacheIndexEntry(
        PlaylistCacheEntry cacheEntry,
        IReadOnlyCollection<PlaylistChannel> channels)
    {
        return new PlaylistCacheIndexEntry
        {
            SchemaVersion = cacheEntry.SchemaVersion,
            Source = cacheEntry.Source,
            CachedAtUtc = cacheEntry.CachedAtUtc,
            SourceLastWriteUtc = cacheEntry.SourceLastWriteUtc,
            Sections = channels
                .GroupBy(channel => channel.ContentType)
                .Select(group => new CachedSection
                {
                    ContentType = group.Key,
                    ItemCount = group.Count(),
                    Categories = group
                        .GroupBy(channel => channel.CategoryName, StringComparer.OrdinalIgnoreCase)
                        .Select(categoryGroup => new CachedCategory
                        {
                            Key = categoryGroup.Key,
                            Label = categoryGroup.Key,
                            Count = categoryGroup.Count(),
                        })
                        .OrderByDescending(category => category.Count)
                        .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                })
                .ToList(),
        };
    }

    private static CachedChannel ToCachedChannel(PlaylistChannel channel)
    {
        return new CachedChannel
        {
            Name = channel.Name,
            Url = channel.Url,
            Group = channel.Group,
            LogoUrl = channel.LogoUrl,
            TvgId = channel.TvgId,
            TvgName = channel.TvgName,
            MediaOptions = channel.MediaOptions.ToList(),
            AddedAtUtc = channel.AddedAtUtc,
        };
    }

    private static PlaylistChannel ToPlaylistChannel(CachedChannel cachedChannel)
    {
        return new PlaylistChannel(
            cachedChannel.Name,
            cachedChannel.Url,
            cachedChannel.Group,
            cachedChannel.LogoUrl,
            cachedChannel.TvgId,
            cachedChannel.TvgName,
            cachedChannel.MediaOptions,
            cachedChannel.AddedAtUtc);
    }

    public sealed record CachedPlaylist(IReadOnlyList<PlaylistChannel> Channels, DateTimeOffset CachedAtUtc);

    public sealed record CachedPlaylistIndex(IReadOnlyList<CachedSectionIndex> Sections, DateTimeOffset CachedAtUtc);

    public sealed record CachedSectionIndex(
        ChannelContentType ContentType,
        int ItemCount,
        IReadOnlyList<CachedCategoryIndex> Categories);

    public sealed record CachedCategoryIndex(string Key, string Label, int Count);

    private class PlaylistCacheEntry
    {
        public int SchemaVersion { get; set; }

        public string Source { get; set; } = string.Empty;

        public DateTimeOffset CachedAtUtc { get; set; }

        public DateTimeOffset? SourceLastWriteUtc { get; set; }

        public List<CachedChannel> Channels { get; set; } = [];
    }

    private sealed class PlaylistCacheIndexEntry : PlaylistCacheEntry
    {
        public List<CachedSection> Sections { get; set; } = [];
    }

    private sealed class CachedChannel
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? Group { get; set; }

        public string? LogoUrl { get; set; }

        public string? TvgId { get; set; }

        public string? TvgName { get; set; }

        public DateTimeOffset? AddedAtUtc { get; set; }

        public List<string> MediaOptions { get; set; } = [];
    }

    private sealed class CachedSection
    {
        public ChannelContentType ContentType { get; set; }

        public int ItemCount { get; set; }

        public List<CachedCategory> Categories { get; set; } = [];
    }

    private sealed class CachedCategory
    {
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}
