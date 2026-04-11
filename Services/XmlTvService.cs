using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml;
using APTV.Models;

namespace APTV.Services;

public sealed class XmlTvService
{
    private static readonly AppLogger Logger = AppLogger.Instance;

    public async Task<GuideLoadResult> LoadGuideAsync(
        string source,
        IReadOnlyCollection<Channel> channels,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("XMLTV source can not be empty.", nameof(source));
        }

        var liveChannels = channels
            .Where(channel => channel.ContentType == ChannelContentType.Live)
            .ToArray();

        Logger.Info($"XMLTV guide load requested. Source={Logger.DescribeSource(source)}, LiveChannels={liveChannels.Length}");

        if (liveChannels.Length == 0)
        {
            return new GuideLoadResult(new Dictionary<string, ChannelGuideInfo>(StringComparer.OrdinalIgnoreCase), 0);
        }

        var channelsByTvgId = BuildLookup(liveChannels, channel => channel.TvgId, NormalizeIdKey);
        var channelsByTvgName = BuildLookup(liveChannels, channel => channel.TvgName, NormalizeNameKey);
        var channelsByName = BuildLookup(liveChannels, channel => channel.Name, NormalizeNameKey);
        var matchesByXmlChannelId = new Dictionary<string, IReadOnlyList<Channel>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in channelsByTvgId)
        {
            matchesByXmlChannelId[pair.Key] = pair.Value;
        }

        var guideAccumulators = new Dictionary<string, GuideAccumulator>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        await using var stream = await OpenSourceStreamAsync(source.Trim(), cancellationToken).ConfigureAwait(false);
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name.Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                var channelDefinition = await ReadChannelDefinitionAsync(reader, cancellationToken).ConfigureAwait(false);
                if (channelDefinition is null || string.IsNullOrWhiteSpace(channelDefinition.Id))
                {
                    continue;
                }

                if (!matchesByXmlChannelId.ContainsKey(channelDefinition.Id))
                {
                    var matches = ResolveChannelMatches(
                        channelDefinition.Id,
                        channelDefinition.DisplayNames,
                        channelsByTvgId,
                        channelsByTvgName,
                        channelsByName);

                    if (matches.Count > 0)
                    {
                        matchesByXmlChannelId[channelDefinition.Id] = matches;
                    }
                }

                if (matchesByXmlChannelId.TryGetValue(channelDefinition.Id, out var matchedDefinitionChannels))
                {
                    foreach (var channel in matchedDefinitionChannels)
                    {
                        if (!guideAccumulators.TryGetValue(channel.Url, out var accumulator))
                        {
                            accumulator = new GuideAccumulator();
                            guideAccumulators[channel.Url] = accumulator;
                        }

                        accumulator.TrySetIconUrl(channelDefinition.IconUrl);
                    }
                }

                continue;
            }

            if (!reader.Name.Equals("programme", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var programme = await ReadProgrammeAsync(reader, cancellationToken).ConfigureAwait(false);
            if (programme is null
                || string.IsNullOrWhiteSpace(programme.ChannelId)
                || programme.Stop <= now
                || !matchesByXmlChannelId.TryGetValue(programme.ChannelId, out var matchedChannels)
                || matchedChannels.Count == 0)
            {
                continue;
            }

            foreach (var channel in matchedChannels)
            {
                if (!guideAccumulators.TryGetValue(channel.Url, out var accumulator))
                {
                    accumulator = new GuideAccumulator();
                    guideAccumulators[channel.Url] = accumulator;
                }

                accumulator.Consider(new EpgProgrammeInfo(
                    programme.Title,
                    programme.Start,
                    programme.Stop,
                    programme.Description), now);
            }
        }

        var guidesByChannelUrl = guideAccumulators
            .Where(pair => pair.Value.HasAnyContent)
            .ToDictionary(
                pair => pair.Key,
                pair => new ChannelGuideInfo(pair.Value.Current, pair.Value.Next, pair.Value.IconUrl),
                StringComparer.OrdinalIgnoreCase);

        Logger.Info($"XMLTV guide load completed. Source={Logger.DescribeSource(source)}, MatchedChannels={guidesByChannelUrl.Count}");
        return new GuideLoadResult(guidesByChannelUrl, guidesByChannelUrl.Count);
    }

    private static Dictionary<string, IReadOnlyList<Channel>> BuildLookup(
        IEnumerable<Channel> channels,
        Func<Channel, string?> keySelector,
        Func<string, string?> normalize)
    {
        var lookup = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            var normalizedKey = normalize(keySelector(channel) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            if (!lookup.TryGetValue(normalizedKey, out var bucket))
            {
                bucket = [];
                lookup[normalizedKey] = bucket;
            }

            bucket.Add(channel);
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Channel>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Channel> ResolveChannelMatches(
        string channelId,
        IReadOnlyList<string> displayNames,
        IReadOnlyDictionary<string, IReadOnlyList<Channel>> channelsByTvgId,
        IReadOnlyDictionary<string, IReadOnlyList<Channel>> channelsByTvgName,
        IReadOnlyDictionary<string, IReadOnlyList<Channel>> channelsByName)
    {
        var matchesByUrl = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);

        AddMatches(matchesByUrl, channelsByTvgId, NormalizeIdKey(channelId));
        if (matchesByUrl.Count > 0)
        {
            return matchesByUrl.Values.ToArray();
        }

        foreach (var displayName in displayNames)
        {
            AddMatches(matchesByUrl, channelsByTvgName, NormalizeNameKey(displayName));
        }

        if (matchesByUrl.Count == 0)
        {
            foreach (var displayName in displayNames)
            {
                AddMatches(matchesByUrl, channelsByName, NormalizeNameKey(displayName));
            }
        }

        if (matchesByUrl.Count == 0)
        {
            AddMatches(matchesByUrl, channelsByName, NormalizeNameKey(channelId));
        }

        return matchesByUrl.Values.ToArray();
    }

    private static void AddMatches(
        IDictionary<string, Channel> matchesByUrl,
        IReadOnlyDictionary<string, IReadOnlyList<Channel>> lookup,
        string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !lookup.TryGetValue(key, out var matches))
        {
            return;
        }

        foreach (var channel in matches)
        {
            matchesByUrl[channel.Url] = channel;
        }
    }

    private static async Task<ChannelDefinition?> ReadChannelDefinitionAsync(XmlReader reader, CancellationToken cancellationToken)
    {
        var channelId = reader.GetAttribute("id");
        var displayNames = new List<string>();
        string? iconUrl = null;

        using var subtree = reader.ReadSubtree();
        while (await subtree.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(iconUrl)
                && subtree.Name.Equals("icon", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = subtree.GetAttribute("src")?.Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    iconUrl = candidate;
                }

                continue;
            }

            if (!subtree.Name.Equals("display-name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var displayName = (await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                displayNames.Add(displayName);
            }
        }

        reader.Skip();
        return new ChannelDefinition(channelId, displayNames, iconUrl);
    }

    private static async Task<ProgrammeDefinition?> ReadProgrammeAsync(XmlReader reader, CancellationToken cancellationToken)
    {
        var channelId = reader.GetAttribute("channel");
        var startRaw = reader.GetAttribute("start");
        var stopRaw = reader.GetAttribute("stop");

        string? title = null;
        string? description = null;

        using var subtree = reader.ReadSubtree();
        while (await subtree.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (title is null && subtree.Name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                title = (await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false)).Trim();
                continue;
            }

            if (description is null && subtree.Name.Equals("desc", StringComparison.OrdinalIgnoreCase))
            {
                description = (await subtree.ReadElementContentAsStringAsync().ConfigureAwait(false)).Trim();
            }
        }

        reader.Skip();

        if (string.IsNullOrWhiteSpace(channelId)
            || string.IsNullOrWhiteSpace(title)
            || !TryParseTimestamp(startRaw, out var start)
            || !TryParseTimestamp(stopRaw, out var stop)
            || stop <= start)
        {
            return null;
        }

        return new ProgrammeDefinition(channelId, start, stop, title, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private static async Task<Stream> OpenSourceStreamAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            Logger.Info($"Opening local XMLTV file. File={Logger.DescribeSource(source)}");
            return new FileStream(
                source,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    Share = FileShare.Read,
                });
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Logger.Info($"Opening remote XMLTV stream. Source={Logger.DescribeSource(source)}");
            var response = await BrowserHttpClient.SendGetAsync(
                uri,
                "XMLTV request",
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseStream(stream, response);
        }

        throw new FileNotFoundException("The XMLTV source could not be found as a local file or a http/https link.", source);
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var normalizedOffset = NormalizeOffset(parts[1]);
            if (normalizedOffset is not null)
            {
                var withOffset = $"{parts[0]} {normalizedOffset}";
                if (DateTimeOffset.TryParseExact(
                    withOffset,
                    ["yyyyMMddHHmmss zzz", "yyyyMMddHHmm zzz"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out timestamp))
                {
                    return true;
                }
            }
        }

        return DateTimeOffset.TryParseExact(
            trimmed,
            ["yyyyMMddHHmmss", "yyyyMMddHHmm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out timestamp);
    }

    private static string? NormalizeOffset(string rawOffset)
    {
        var offset = rawOffset.Trim();
        if (offset.Equals("UTC", StringComparison.OrdinalIgnoreCase) || offset == "Z")
        {
            return "+00:00";
        }

        if (offset.Length == 5 && (offset[0] == '+' || offset[0] == '-'))
        {
            return $"{offset[..3]}:{offset[3..]}";
        }

        if (offset.Length == 6 && (offset[0] == '+' || offset[0] == '-') && offset[3] == ':')
        {
            return offset;
        }

        return null;
    }

    private static string? NormalizeIdKey(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? NormalizeNameKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            builder.Append(' ');
            previousWasSeparator = true;
        }

        var normalized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public sealed record GuideLoadResult(
        IReadOnlyDictionary<string, ChannelGuideInfo> GuidesByChannelUrl,
        int MatchedChannelCount);

    private sealed record ChannelDefinition(string? Id, IReadOnlyList<string> DisplayNames, string? IconUrl);

    private sealed record ProgrammeDefinition(
        string ChannelId,
        DateTimeOffset Start,
        DateTimeOffset Stop,
        string Title,
        string? Description);

    private sealed class GuideAccumulator
    {
        public EpgProgrammeInfo? Current { get; private set; }

        public EpgProgrammeInfo? Next { get; private set; }

        public string? IconUrl { get; private set; }

        public bool HasAnyContent => Current is not null || Next is not null || !string.IsNullOrWhiteSpace(IconUrl);

        public void TrySetIconUrl(string? iconUrl)
        {
            if (string.IsNullOrWhiteSpace(IconUrl) && !string.IsNullOrWhiteSpace(iconUrl))
            {
                IconUrl = iconUrl.Trim();
            }
        }

        public void Consider(EpgProgrammeInfo programme, DateTimeOffset now)
        {
            if (programme.Stop <= now)
            {
                return;
            }

            if (programme.Start <= now && programme.Stop > now)
            {
                if (Current is null || programme.Start > Current.Start)
                {
                    Current = programme;
                }

                return;
            }

            if (programme.Start > now
                && (Next is null || programme.Start < Next.Start))
            {
                Next = programme;
            }
        }
    }

    private sealed class HttpResponseStream(Stream innerStream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => innerStream.CanRead;

        public override bool CanSeek => innerStream.CanSeek;

        public override bool CanWrite => innerStream.CanWrite;

        public override long Length => innerStream.Length;

        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush() => innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) => innerStream.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => innerStream.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => innerStream.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => innerStream.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);

        public override void SetLength(long value) => innerStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => innerStream.Write(buffer);

        public override ValueTask DisposeAsync()
        {
            response.Dispose();
            return innerStream.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerStream.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
