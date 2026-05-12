using System.Text.Json;

namespace PoePerfect.Player.Android;

public sealed class RecentPlaybackStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public string FilePath { get; } = filePath;

    public async Task<List<RecentPlaybackEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<RecentPlaybackEntry>>(stream, JsonOptions, cancellationToken)
                    ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<RecentPlaybackEntry> entries, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var orderedEntries = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ChannelUrl))
                .OrderByDescending(entry => entry.PlayedAtUtc)
                .Take(200)
                .ToList();

            var temporaryFilePath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryFilePath))
            {
                await JsonSerializer.SerializeAsync(stream, orderedEntries, JsonOptions, cancellationToken);
            }

            File.Move(temporaryFilePath, FilePath, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
