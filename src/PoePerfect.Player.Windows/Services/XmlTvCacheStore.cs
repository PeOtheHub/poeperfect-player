using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using APTV.Models;

namespace APTV.Services;

public sealed class XmlTvCacheStore
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public XmlTvCacheStore()
    {
        CacheDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV",
            "xmltv-cache");
    }

    public string CacheDirectoryPath { get; }

    public async Task<CachedGuide?> TryLoadAsync(
        string playlistSource,
        string xmlTvSource,
        CancellationToken cancellationToken = default)
    {
        var cacheFilePath = GetCacheFilePath(playlistSource, xmlTvSource);
        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cacheFilePath);
            var cacheEntry = await JsonSerializer.DeserializeAsync<XmlTvCacheEntry>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (cacheEntry is null
                || cacheEntry.SchemaVersion != CurrentSchemaVersion
                || !string.Equals(cacheEntry.PlaylistSource, playlistSource, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cacheEntry.XmlTvSource, xmlTvSource, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (File.Exists(xmlTvSource))
            {
                var currentLastWriteUtc = File.GetLastWriteTimeUtc(xmlTvSource);
                if (cacheEntry.XmlTvSourceLastWriteUtc != currentLastWriteUtc)
                {
                    return null;
                }
            }

            var guidesByChannelUrl = cacheEntry.Guides
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ChannelUrl))
                .ToDictionary(
                    entry => entry.ChannelUrl,
                    ToGuideInfo,
                    StringComparer.OrdinalIgnoreCase);

            return new CachedGuide(guidesByChannelUrl, cacheEntry.CachedAtUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        string playlistSource,
        string xmlTvSource,
        IReadOnlyDictionary<string, ChannelGuideInfo> guidesByChannelUrl,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CacheDirectoryPath);

        var cacheEntry = new XmlTvCacheEntry
        {
            SchemaVersion = CurrentSchemaVersion,
            PlaylistSource = playlistSource,
            XmlTvSource = xmlTvSource,
            CachedAtUtc = DateTimeOffset.UtcNow,
            XmlTvSourceLastWriteUtc = File.Exists(xmlTvSource) ? File.GetLastWriteTimeUtc(xmlTvSource) : null,
            Guides = guidesByChannelUrl
                .Select(pair => new CachedGuideEntry
                {
                    ChannelUrl = pair.Key,
                    Current = ToCachedProgramme(pair.Value.Current),
                    Next = ToCachedProgramme(pair.Value.Next),
                    IconUrl = pair.Value.IconUrl,
                })
                .ToList(),
        };

        var cacheFilePath = GetCacheFilePath(playlistSource, xmlTvSource);
        var temporaryFilePath = $"{cacheFilePath}.tmp";

        await using (var stream = File.Create(temporaryFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, cacheEntry, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFilePath, cacheFilePath, true);
    }

    private string GetCacheFilePath(string playlistSource, string xmlTvSource)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{playlistSource.Trim()}\n{xmlTvSource.Trim()}"));
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(CacheDirectoryPath, $"{hash}.json");
    }

    private static ChannelGuideInfo ToGuideInfo(CachedGuideEntry entry)
    {
        return new ChannelGuideInfo(ToProgrammeInfo(entry.Current), ToProgrammeInfo(entry.Next), entry.IconUrl);
    }

    private static CachedProgramme? ToCachedProgramme(EpgProgrammeInfo? programme)
    {
        return programme is null
            ? null
            : new CachedProgramme
            {
                Title = programme.Title,
                Start = programme.Start,
                Stop = programme.Stop,
                Description = programme.Description,
            };
    }

    private static EpgProgrammeInfo? ToProgrammeInfo(CachedProgramme? programme)
    {
        return programme is null
            ? null
            : new EpgProgrammeInfo(
                programme.Title,
                programme.Start,
                programme.Stop,
                programme.Description);
    }

    public sealed record CachedGuide(
        IReadOnlyDictionary<string, ChannelGuideInfo> GuidesByChannelUrl,
        DateTimeOffset CachedAtUtc);

    private sealed class XmlTvCacheEntry
    {
        public int SchemaVersion { get; set; }

        public string PlaylistSource { get; set; } = string.Empty;

        public string XmlTvSource { get; set; } = string.Empty;

        public DateTimeOffset CachedAtUtc { get; set; }

        public DateTimeOffset? XmlTvSourceLastWriteUtc { get; set; }

        public List<CachedGuideEntry> Guides { get; set; } = [];
    }

    private sealed class CachedGuideEntry
    {
        public string ChannelUrl { get; set; } = string.Empty;

        public CachedProgramme? Current { get; set; }

        public CachedProgramme? Next { get; set; }

        public string? IconUrl { get; set; }
    }

    private sealed class CachedProgramme
    {
        public string Title { get; set; } = string.Empty;

        public DateTimeOffset Start { get; set; }

        public DateTimeOffset Stop { get; set; }

        public string? Description { get; set; }
    }
}
