using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PoePerfect.Player.Android;

public sealed class PosterImageCacheService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan FailedSourceRetryDelay = TimeSpan.FromMinutes(2);
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, string?> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedSources = new(StringComparer.OrdinalIgnoreCase);

    public PosterImageCacheService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<string?> LoadPathAsync(string? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = source.Trim();

        if (_memoryCache.TryGetValue(source, out var cachedPath))
        {
            if (string.IsNullOrWhiteSpace(cachedPath) || File.Exists(cachedPath))
            {
                return cachedPath;
            }

            _memoryCache.TryRemove(source, out _);
        }

        if (_failedSources.TryGetValue(source, out var failedAt)
            && DateTimeOffset.UtcNow - failedAt < FailedSourceRetryDelay)
        {
            return null;
        }

        try
        {
            var path = await LoadCoreAsync(source, cancellationToken);
            if (path is null)
            {
                _failedSources[source] = DateTimeOffset.UtcNow;
                return null;
            }

            _memoryCache[source] = path;
            _failedSources.TryRemove(source, out _);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _failedSources[source] = DateTimeOffset.UtcNow;
            return null;
        }
    }

    private async Task<string?> LoadCoreAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            return source;
        }

        var cachePath = GetCachePath(source);
        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return null;
        }

        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            var tempPath = $"{cachePath}.download";
            TryDeleteFile(tempPath);

            try
            {
                using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await networkStream.CopyToAsync(fileStream, cancellationToken);
                    await fileStream.FlushAsync(cancellationToken);
                }

                File.Move(tempPath, cachePath, overwrite: true);
                return cachePath;
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private string GetCachePath(string source)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(source));
        var hash = Convert.ToHexString(hashBytes);
        return Path.Combine(_cacheDirectory, $"{hash}.img");
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("PoePerfectPlayer-Android/1.0");
        return client;
    }
}
