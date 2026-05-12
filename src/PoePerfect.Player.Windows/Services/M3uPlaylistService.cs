using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using APTV.Models;

namespace APTV.Services;

public sealed class M3uPlaylistService
{
    public sealed record LoadProgress(int ChannelsParsed, long BytesRead, long? TotalBytes);

    private static readonly Regex AttributeRegex = new(
        "(?<key>[A-Za-z0-9_-]+)=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    private static readonly AppLogger Logger = AppLogger.Instance;

    public async Task<IReadOnlyList<Channel>> LoadAsync(
        string source,
        int? maxChannels = null,
        int batchSize = 200,
        IProgress<IReadOnlyList<Channel>>? batchProgress = null,
        IProgress<LoadProgress>? loadProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Playlist source can not be empty.", nameof(source));
        }

        Logger.Info($"M3U load requested. Source={Logger.DescribeSource(source)}, MaxChannels={(maxChannels?.ToString() ?? "all")}, BatchSize={batchSize}");

        var playlistSource = await OpenPlaylistStreamAsync(source.Trim(), cancellationToken).ConfigureAwait(false);
        await using var countingStream = new CountingStream(playlistSource.Stream);
        using var reader = new StreamReader(countingStream);

        var channels = await ParseAsync(
            reader,
            currentBytesRead: () => countingStream.BytesRead,
            totalBytes: playlistSource.TotalBytes,
            maxChannels: maxChannels,
            batchSize: batchSize,
            batchProgress: batchProgress,
            loadProgress: loadProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Logger.Info($"M3U load completed. Source={Logger.DescribeSource(source)}, Channels={channels.Count}");
        return channels;
    }

    private static async Task<PlaylistSource> OpenPlaylistStreamAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            var fileInfo = new FileInfo(source);
            Logger.Info($"Opening local playlist file. File={Logger.DescribeSource(source)}, Size={fileInfo.Length} bytes");
            return new PlaylistSource(
                new FileStream(
                    source,
                    new FileStreamOptions
                    {
                        Access = FileAccess.Read,
                        Mode = FileMode.Open,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        Share = FileShare.Read,
                    }),
                fileInfo.Length);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Logger.Info($"Opening remote playlist stream. Source={Logger.DescribeSource(source)}");
            var response = await BrowserHttpClient.SendGetAsync(
                uri,
                "M3U request",
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new PlaylistSource(
                new HttpResponseStream(stream, response),
                response.Content.Headers.ContentLength);
        }

        throw new FileNotFoundException("The playlist could not be found as a local file or a http/https link.", source);
    }

    internal static async Task<IReadOnlyList<Channel>> ParseAsync(
        TextReader reader,
        Func<long> currentBytesRead,
        long? totalBytes = null,
        int? maxChannels = null,
        int batchSize = 200,
        IProgress<IReadOnlyList<Channel>>? batchProgress = null,
        IProgress<LoadProgress>? loadProgress = null,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Max(1, batchSize);

        var channels = new List<Channel>();
        var currentBatch = new List<Channel>(batchSize);
        PendingEntry? pendingEntry = null;
        var unnamedCounter = 1;
        var processedLines = 0;
        var lastReportedBytes = 0L;
        var lastReportedChannels = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (rawLine is null)
            {
                break;
            }

            processedLines++;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                ReportProgressIfNeeded(force: false);
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingEntry = ParseExtInf(line);
                ReportProgressIfNeeded(force: false);
                continue;
            }

            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                pendingEntry ??= new PendingEntry();
                pendingEntry.Group = line["#EXTGRP:".Length..].Trim();
                ReportProgressIfNeeded(force: false);
                continue;
            }

            if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                pendingEntry ??= new PendingEntry();
                pendingEntry.MediaOptions.Add(line["#EXTVLCOPT:".Length..].Trim());
                ReportProgressIfNeeded(force: false);
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                ReportProgressIfNeeded(force: false);
                continue;
            }

            var resolvedEntry = pendingEntry ?? new PendingEntry();
            var channelName = !string.IsNullOrWhiteSpace(resolvedEntry.Name)
                ? resolvedEntry.Name!
                : $"Channel {unnamedCounter++}";

            var channel = new Channel(
                channelName,
                line,
                resolvedEntry.Group,
                resolvedEntry.LogoUrl,
                resolvedEntry.TvgId,
                resolvedEntry.TvgName,
                resolvedEntry.MediaOptions,
                resolvedEntry.AddedAtUtc);

            channels.Add(channel);
            currentBatch.Add(channel);

            if (currentBatch.Count >= batchSize)
            {
                batchProgress?.Report(currentBatch.ToArray());
                currentBatch.Clear();
            }

            ReportProgressIfNeeded(force: false);

            if (maxChannels is not null && channels.Count >= maxChannels.Value)
            {
                break;
            }

            pendingEntry = null;
        }

        if (currentBatch.Count > 0)
        {
            batchProgress?.Report(currentBatch.ToArray());
        }

        ReportProgressIfNeeded(force: true);

        return channels;

        void ReportProgressIfNeeded(bool force)
        {
            if (loadProgress is null)
            {
                return;
            }

            var currentBytes = currentBytesRead();
            var bytesThresholdReached = currentBytes - lastReportedBytes >= 256 * 1024;
            var channelThresholdReached = channels.Count - lastReportedChannels >= 50;
            var lineThresholdReached = processedLines % 500 == 0;

            if (!force && !bytesThresholdReached && !channelThresholdReached && !lineThresholdReached)
            {
                return;
            }

            lastReportedBytes = currentBytes;
            lastReportedChannels = channels.Count;
            loadProgress.Report(new LoadProgress(channels.Count, currentBytes, totalBytes));
        }
    }

    private static PendingEntry ParseExtInf(string line)
    {
        var entry = new PendingEntry();
        var commaIndex = FindDisplayNameSeparator(line);
        var metadataPart = commaIndex >= 0 ? line[..commaIndex] : line;
        var displayName = commaIndex >= 0 ? line[(commaIndex + 1)..].Trim() : string.Empty;

        foreach (Match match in AttributeRegex.Matches(metadataPart))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();

            switch (key.ToLowerInvariant())
            {
                case "group-title":
                    entry.Group = value;
                    break;
                case "tvg-logo":
                    entry.LogoUrl = value;
                    break;
                case "tvg-id":
                    entry.TvgId = value;
                    break;
                case "tvg-name":
                    entry.TvgName = value;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        entry.Name = value;
                    }
                    break;
                case "added":
                case "added-at":
                case "added_at":
                case "date-added":
                case "date_added":
                case "created":
                case "created-at":
                case "created_at":
                    entry.AddedAtUtc = ParseAddedAt(value);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            entry.Name = displayName;
        }

        return entry;
    }

    private static int FindDisplayNameSeparator(string line)
    {
        var isInsideQuotedAttribute = false;

        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"')
            {
                isInsideQuotedAttribute = !isInsideQuotedAttribute;
                continue;
            }

            if (current == ',' && !isInsideQuotedAttribute)
            {
                return index;
            }
        }

        return -1;
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

    private sealed class PendingEntry
    {
        public string? Name { get; set; }

        public string? Group { get; set; }

        public string? LogoUrl { get; set; }

        public string? TvgId { get; set; }

        public string? TvgName { get; set; }

        public DateTimeOffset? AddedAtUtc { get; set; }

        public List<string> MediaOptions { get; } = [];
    }

    private sealed record PlaylistSource(Stream Stream, long? TotalBytes);

    private sealed class CountingStream(Stream innerStream) : Stream
    {
        public long BytesRead { get; private set; }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = innerStream.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = innerStream.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ReadAsyncCore(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);

        public override void SetLength(long value) => innerStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => innerStream.Write(buffer);

        public override ValueTask DisposeAsync() => innerStream.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerStream.Dispose();
            }

            base.Dispose(disposing);
        }

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
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
