using System.Text.Json;

namespace PoePerfect.Player.Android;

public sealed class CategoryPreferencesStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public string FilePath { get; } = filePath;

    public async Task<List<CategoryDisplayPreference>> LoadAsync(CancellationToken cancellationToken = default)
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
                return await JsonSerializer.DeserializeAsync<List<CategoryDisplayPreference>>(stream, JsonOptions, cancellationToken)
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

    public async Task SaveAsync(IEnumerable<CategoryDisplayPreference> preferences, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var orderedPreferences = preferences
                .Where(preference => !string.IsNullOrWhiteSpace(preference.Key))
                .GroupBy(preference => $"{preference.Section}\u001F{preference.Key}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(preference => preference.SortOrder).First())
                .OrderBy(preference => preference.Section)
                .ThenBy(preference => preference.SortOrder)
                .ThenBy(preference => preference.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var temporaryFilePath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryFilePath))
            {
                await JsonSerializer.SerializeAsync(stream, orderedPreferences, JsonOptions, cancellationToken);
            }

            File.Move(temporaryFilePath, FilePath, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
