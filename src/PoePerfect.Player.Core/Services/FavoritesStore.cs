using System.Text.Json;

namespace PoePerfect.Player.Core.Services;

public sealed class FavoritesStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string FilePath { get; } = filePath;

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
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var cleanedFavorites = favorites
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, cleanedFavorites, JsonOptions, cancellationToken);
    }
}
