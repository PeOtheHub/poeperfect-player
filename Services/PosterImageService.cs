using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace APTV.Services;

public sealed class PosterImageService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan FailedSourceRetryDelay = TimeSpan.FromMinutes(2);
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, ImageSource?> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedSources = new(StringComparer.OrdinalIgnoreCase);

    public PosterImageService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV",
            "poster-cache");

        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<ImageSource?> LoadAsync(string? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = source.Trim();

        if (_memoryCache.TryGetValue(source, out var cachedImage))
        {
            return cachedImage;
        }

        if (_failedSources.TryGetValue(source, out var failedAt))
        {
            if (DateTimeOffset.UtcNow - failedAt < FailedSourceRetryDelay)
            {
                return null;
            }

            _failedSources.TryRemove(source, out _);
        }

        try
        {
            var loadedImage = await LoadCoreAsync(source, cancellationToken);
            if (loadedImage is null)
            {
                _failedSources[source] = DateTimeOffset.UtcNow;
                return null;
            }

            _memoryCache[source] = loadedImage;
            _failedSources.TryRemove(source, out _);
            return loadedImage;
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

    private async Task<ImageSource?> LoadCoreAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            return await LoadBitmapFromFileAsync(source, cancellationToken);
        }

        var cachePath = GetCachePath(source);
        if (File.Exists(cachePath))
        {
            var cachedBitmap = await LoadBitmapFromFileAsync(cachePath, cancellationToken);
            if (cachedBitmap is not null)
            {
                return cachedBitmap;
            }

            TryDeleteFile(cachePath);
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
                var cachedBitmap = await LoadBitmapFromFileAsync(cachePath, cancellationToken);
                if (cachedBitmap is not null)
                {
                    return cachedBitmap;
                }

                TryDeleteFile(cachePath);
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

        return await LoadBitmapFromFileAsync(cachePath, cancellationToken);
    }

    private static async Task<ImageSource?> LoadBitmapFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = 220;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
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
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("APTV/1.0");
        return client;
    }
}
