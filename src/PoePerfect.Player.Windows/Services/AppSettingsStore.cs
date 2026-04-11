using System.IO;
using System.Text.Json;
using APTV.Models;

namespace APTV.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public AppSettingsStore()
    {
        FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV",
            "app-settings.json");
    }

    public string FilePath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryFilePath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        await using (var stream = File.Create(temporaryFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFilePath, FilePath, true);
    }

    public sealed class AppSettings
    {
        public string PlaylistSource { get; set; } = string.Empty;

        public string XmlTvSource { get; set; } = string.Empty;

        public bool LoadFirstHundredOnly { get; set; } = true;

        public List<CategoryDisplayPreference> CategoryDisplayPreferences { get; set; } = [];

        public List<string> FavoriteSeasonKeys { get; set; } = [];

        public List<RecentPlaybackEntry> RecentPlaybackEntries { get; set; } = [];
    }
}
