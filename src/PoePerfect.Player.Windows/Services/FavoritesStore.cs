using System.IO;
using System.Text.Json;

namespace APTV.Services;

public sealed class FavoritesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public FavoritesStore()
    {
        var storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV");

        FilePath = Path.Combine(storageDirectory, "favorites.json");
    }

    public string FilePath { get; }

    public async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(FilePath);
        var items = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: cancellationToken) ?? [];

        return new HashSet<string>(
            items.Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(IEnumerable<string> favorites, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var cleanedFavorites = favorites
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, cleanedFavorites, JsonOptions, cancellationToken);
    }
}
