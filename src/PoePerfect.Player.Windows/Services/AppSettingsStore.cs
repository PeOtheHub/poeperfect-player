using System.IO;
using System.Text.Json;
using APTV.Models;

namespace APTV.Services;

public sealed class AppSettingsStore
{
    private const int CurrentSchemaVersion = 1;
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
            return CreateDefaultSettings();
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return ApplyMigrations(settings ?? CreateDefaultSettings());
        }
        catch (JsonException)
        {
            return CreateDefaultSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryFilePath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        await using (var stream = File.Create(temporaryFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFilePath, FilePath, true);
    }

    private static AppSettings CreateDefaultSettings()
    {
        return new AppSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            SortVodCategoriesByLatest = true,
        };
    }

    private static AppSettings ApplyMigrations(AppSettings settings)
    {
        if (settings.SchemaVersion < 1)
        {
            settings.SortVodCategoriesByLatest = true;
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return settings;
    }

    public sealed class AppSettings
    {
        public int SchemaVersion { get; set; }

        public string PlaylistSource { get; set; } = string.Empty;

        public string XmlTvSource { get; set; } = string.Empty;

        public bool LoadFirstHundredOnly { get; set; } = true;

        public bool SortVodCategoriesByLatest { get; set; } = true;

        public List<CategoryDisplayPreference> CategoryDisplayPreferences { get; set; } = [];

        public List<string> FavoriteSeasonKeys { get; set; } = [];

        public List<RecentPlaybackEntry> RecentPlaybackEntries { get; set; } = [];
    }
}
