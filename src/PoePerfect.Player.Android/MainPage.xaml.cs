#if ANDROID
using Android.Content;
using AndroidView = Android.Views.View;
using AndroidApplication = Android.App.Application;
using AndroidUri = Android.Net.Uri;
using Com.Google.Android.Exoplayer2.UI;
using CommunityToolkit.Maui.Core.Handlers;
using CommunityToolkit.Maui.Core.Views;
using System.Reflection;
#endif
using CommunityToolkit.Maui.Core.Primitives;
using PoePerfect.Player.Core.Models;
using PoePerfect.Player.Core.Services;
using System.Collections.ObjectModel;

namespace PoePerfect.Player.Android;

public partial class MainPage : ContentPage
{
    private const int SearchDebounceMilliseconds = 300;
    private const int InitialVisibleChannelBatchSize = 48;
    private const int IncrementalVisibleChannelBatchSize = 24;
    private const int InitialVisibleSeriesBatchSize = 36;
    private const int IncrementalVisibleSeriesBatchSize = 18;
    private const int LatestAddedCategoryLimit = 20;
    private const string PlaylistSourcePreferenceKey = "playlist_source";
    private const string XmlTvSourcePreferenceKey = "xmltv_source";
    private const string PlaybackLogTag = "PoePerfectPlayback";
    private const string FavoritesCategoryLabel = "Favoriter";
    private const string LatestCategoryLabel = "Senast tillagda";
    private const string RecentCategoryLabel = "Senast spelade";

    private readonly CategoryPreferencesStore _categoryPreferencesStore;
    private readonly FavoritesStore _favoritesStore;
    private readonly PosterImageCacheService _posterImageCacheService;
    private readonly PlaylistCacheStore _playlistCacheStore;
    private readonly M3uPlaylistService _playlistService = new();
    private readonly RecentPlaybackStore _recentPlaybackStore;
    private readonly SeriesCatalogService _seriesCatalogService = new();
    private readonly XtreamApiService _xtreamApiService = new();
    private CancellationTokenSource? _loadPlaylistCancellationTokenSource;
    private CancellationTokenSource? _applyFiltersCancellationTokenSource;
    private CancellationTokenSource? _artworkLoadCancellationTokenSource;
    private CancellationTokenSource? _searchDebounceCancellationTokenSource;
    private CancellationTokenSource? _movieDetailCancellationTokenSource;
    private List<CategoryDisplayPreference> _categoryDisplayPreferences = [];
    private IReadOnlyList<PlaylistCategoryManagerItem> _categoryManagerItems = [];
    private bool _favoritesOnly;
    private bool _hasInitialized;
    private bool _isBusyLoading;
    private bool _isOpeningPlaylistEditorPage;
    private bool _isPreparingCatalog;
    private bool _isApplyingFilters;
    private bool _isCategoryManagerVisible;
    private bool _isSearchVisible;
    private bool _canSelectEmbeddedSubtitles;
    private bool _isEmbeddedPlayerLoading;
    private bool _isUpdatingCategoryManager;
    private bool _isUpdatingPlaylistEditorCategories;
    private bool _isPlaylistEditorVisible;
    private bool _showEmbeddedPlayer;
    private string _embeddedPlayerStatusText = "Öppnar stream...";
    private string _embeddedPlayerSubtitle = "Förbereder uppspelning";
    private string _embeddedPlayerTitle = "Spelar upp";
    private string _emptyStateText = "Ingen spellista laddad än.";
    private HashSet<string> _favoriteUrls = new(StringComparer.OrdinalIgnoreCase);
    private List<PlaylistChannel> _allChannels = [];
    private IReadOnlyList<BrowseCategoryChip> _categoryOptions = [];
    private IReadOnlyList<PlaylistCategoryManagerItem> _playlistEditorCategoryItems = [];
    private BrowseSection _playlistEditorSection = BrowseSection.Live;
    private string _playlistSource = string.Empty;
    private string _xmlTvSource = string.Empty;
    private List<RecentPlaybackEntry> _recentPlaybackEntries = [];
    private string _searchText = string.Empty;
    private PlaylistChannel? _pendingEmbeddedPlaybackChannel;
    private BrowseSection? _pendingBrowseSectionAfterLoad;
    private BrowseSection? _selectedBrowseSection;
    private string? _selectedCategoryKey;
    private PlaylistChannel? _selectedMovieChannel;
    private MovieDetailInfo? _selectedMovieDetail;
    private bool _isMovieDetailLoading;
    private SeriesGroupItem? _selectedSeriesGroup;
    private SeriesSeasonItem? _selectedSeriesSeason;
    private readonly object _seriesGroupCacheSync = new();
    private Dictionary<string, IReadOnlyList<SeriesGroupItem>> _seriesGroupCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<BrowseSection, SectionCatalog> _sectionCatalogs = [];
    private Dictionary<BrowseSection, SectionPreview> _sectionPreviews = [];
    private string _statusText = "Klistra in en M3U-länk för att komma igång.";
    private IReadOnlyList<PlaylistChannel> _visibleChannels = [];
    private IReadOnlyList<SeriesGroupItem> _visibleSeriesGroups = [];
    private Dictionary<BrowseSection, SectionStats> _sectionStats = [];
    private List<PlaylistChannel> _selectedCategoryVisibleChannels = [];
    private List<SeriesGroupItem> _selectedCategoryVisibleSeriesGroups = [];
    private int _visibleChannelRenderCount;
    private int _visibleSeriesGroupRenderCount;
    private bool _isAppendingVisibleChannels;
    private bool _isAppendingVisibleSeriesGroups;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        _categoryPreferencesStore = new CategoryPreferencesStore(Path.Combine(FileSystem.AppDataDirectory, "category-preferences.json"));
        _favoritesStore = new FavoritesStore(Path.Combine(FileSystem.AppDataDirectory, "favorites.json"));
        _posterImageCacheService = new PosterImageCacheService(Path.Combine(FileSystem.AppDataDirectory, "poster-cache"));
        _playlistCacheStore = new PlaylistCacheStore(Path.Combine(FileSystem.AppDataDirectory, "playlist-cache"));
        _recentPlaybackStore = new RecentPlaybackStore(Path.Combine(FileSystem.AppDataDirectory, "recent-playback.json"));
#if ANDROID
        EmbeddedMediaElement.HandlerChanged += OnEmbeddedMediaElementHandlerChanged;
#endif
        UpdatePlaylistEditorVisibility();
    }

    public string PlaylistSource
    {
        get => _playlistSource;
        set
        {
            if (_playlistSource == value)
            {
                return;
            }

            _playlistSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlaylistSource));
            OnPropertyChanged(nameof(PlaylistCardText));
            OnPropertyChanged(nameof(DashboardHintText));
            OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
            OnPropertyChanged(nameof(LiveCardSummary));
            OnPropertyChanged(nameof(MoviesCardSummary));
            OnPropertyChanged(nameof(SeriesCardSummary));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
        }
    }

    public string XmlTvSource
    {
        get => _xmlTvSource;
        set
        {
            if (_xmlTvSource == value)
            {
                return;
            }

            _xmlTvSource = value;
            OnPropertyChanged();
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (_favoritesOnly == value)
            {
                return;
            }

            _favoritesOnly = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusyLoading
    {
        get => _isBusyLoading;
        private set
        {
            if (_isBusyLoading == value)
            {
                return;
            }

            _isBusyLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserBusy));
            OnPropertyChanged(nameof(ShowCategoryBusyIndicator));
            OnPropertyChanged(nameof(ShowContentLoadingOverlay));
            OnPropertyChanged(nameof(DashboardHintText));
            OnPropertyChanged(nameof(BrowseHintText));
            OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
            OnPropertyChanged(nameof(LiveCardSummary));
            OnPropertyChanged(nameof(MoviesCardSummary));
            OnPropertyChanged(nameof(SeriesCardSummary));
        }
    }

    public bool IsPreparingCatalog
    {
        get => _isPreparingCatalog;
        private set
        {
            if (_isPreparingCatalog == value)
            {
                return;
            }

            _isPreparingCatalog = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserBusy));
            OnPropertyChanged(nameof(ShowCategoryBusyIndicator));
            OnPropertyChanged(nameof(ShowContentLoadingOverlay));
            OnPropertyChanged(nameof(DashboardHintText));
            OnPropertyChanged(nameof(BrowseHintText));
            OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
        }
    }

    public bool IsApplyingFilters
    {
        get => _isApplyingFilters;
        private set
        {
            if (_isApplyingFilters == value)
            {
                return;
            }

            _isApplyingFilters = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserBusy));
            OnPropertyChanged(nameof(ShowCategoryBusyIndicator));
            OnPropertyChanged(nameof(ShowContentLoadingOverlay));
            OnPropertyChanged(nameof(BrowseHintText));
            OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
        }
    }

    public bool IsEmbeddedPlayerLoading
    {
        get => _isEmbeddedPlayerLoading;
        private set
        {
            if (_isEmbeddedPlayerLoading == value)
            {
                return;
            }

            _isEmbeddedPlayerLoading = value;
            OnPropertyChanged();
        }
    }

    public bool CanSelectEmbeddedSubtitles
    {
        get => _canSelectEmbeddedSubtitles;
        private set
        {
            if (_canSelectEmbeddedSubtitles == value)
            {
                return;
            }

            _canSelectEmbeddedSubtitles = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaylistEditorVisible
    {
        get => _isPlaylistEditorVisible;
        private set
        {
            if (_isPlaylistEditorVisible == value)
            {
                return;
            }

            _isPlaylistEditorVisible = value;
            UpdatePlaylistEditorVisibility();
            OnPropertyChanged();
        }
    }

    public bool IsCategoryManagerVisible
    {
        get => _isCategoryManagerVisible;
        private set
        {
            if (_isCategoryManagerVisible == value)
            {
                return;
            }

            _isCategoryManagerVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoryManagerButtonText));
        }
    }

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        private set
        {
            if (_isSearchVisible == value)
            {
                return;
            }

            _isSearchVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SearchToggleButtonText));
        }
    }

    public bool HasPlaylistSource => !string.IsNullOrWhiteSpace(PlaylistSource.Trim());

    public bool ShowDashboard => _selectedBrowseSection is null;

    public bool ShowBrowserShell => _selectedBrowseSection is not null;

    public bool IsBrowserBusy => ShowBrowserShell && (IsBusyLoading || IsPreparingCatalog || IsApplyingFilters);

    public bool ShowCategoryBusyIndicator => IsBrowserBusy;

    public bool ShowContentLoadingOverlay => IsBrowserBusy && !ShowEmbeddedPlayer;

    public bool ShowEmbeddedPlayer
    {
        get => _showEmbeddedPlayer;
        private set
        {
            if (_showEmbeddedPlayer == value)
            {
                return;
            }

            _showEmbeddedPlayer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowContentLoadingOverlay));
        }
    }

    public string EmbeddedPlayerTitle
    {
        get => _embeddedPlayerTitle;
        private set
        {
            if (_embeddedPlayerTitle == value)
            {
                return;
            }

            _embeddedPlayerTitle = value;
            OnPropertyChanged();
        }
    }

    public string EmbeddedPlayerSubtitle
    {
        get => _embeddedPlayerSubtitle;
        private set
        {
            if (_embeddedPlayerSubtitle == value)
            {
                return;
            }

            _embeddedPlayerSubtitle = value;
            OnPropertyChanged();
        }
    }

    public string EmbeddedPlayerStatusText
    {
        get => _embeddedPlayerStatusText;
        private set
        {
            if (_embeddedPlayerStatusText == value)
            {
                return;
            }

            _embeddedPlayerStatusText = value;
            OnPropertyChanged();
        }
    }

    public string HeaderTitle => ShowDashboard ? "PoePerfect Player" : GetSectionLabel(_selectedBrowseSection);

    public string HeaderSubtitle => ShowDashboard
        ? "Välj vad du vill titta på"
        : GetSectionSubtitle();

    public string DashboardHintText
    {
        get
        {
            if (IsBusyLoading || IsPreparingCatalog)
            {
                return "Laddar katalogen i bakgrunden...";
            }

            if (!HasPlaylistSource)
            {
                return "Öppna Playlists och ange din M3U-länk för att komma igång.";
            }

            return _allChannels.Count > 0
                ? "Välj Live, Film eller Serier för att fortsätta."
                : "Spellistan är sparad. Ladda katalogen eller välj en sektion för att bygga den.";
        }
    }

    public string PlaylistCardText => HasPlaylistSource
        ? $"Aktiv playlist: {GetShortPlaylistDisplayName()}"
        : "Ingen playlist sparad än";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set
        {
            if (_emptyStateText == value)
            {
                return;
            }

            _emptyStateText = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<PlaylistChannel> VisibleChannels
    {
        get => _visibleChannels;
        private set
        {
            _visibleChannels = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SeriesGroupItem> VisibleSeriesGroups
    {
        get => _visibleSeriesGroups;
        private set
        {
            _visibleSeriesGroups = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SeriesSeasonItem> VisibleSeriesSeasons => _selectedSeriesGroup?.Seasons ?? [];

    public IReadOnlyList<SeriesEpisodeItem> CurrentSeriesEpisodes => _selectedSeriesSeason?.Episodes ?? [];

    public PlaylistChannel? SelectedMovieChannel => _selectedMovieChannel;

    public MovieDetailInfo? SelectedMovieDetail => _selectedMovieDetail;

    public bool IsMovieDetailLoading
    {
        get => _isMovieDetailLoading;
        private set
        {
            if (_isMovieDetailLoading == value)
            {
                return;
            }

            _isMovieDetailLoading = value;
            OnPropertyChanged();
            NotifyMovieDetailMetadataChanged();
        }
    }

    public string MovieDetailTitle => _selectedMovieDetail?.Title ?? _selectedMovieChannel?.DisplayName ?? "Film";

    public string MovieDetailCategoryText => _selectedMovieChannel?.CategoryName ?? "Film";

    public string MovieDetailPlotText => !string.IsNullOrWhiteSpace(_selectedMovieDetail?.Plot)
        ? _selectedMovieDetail.Plot
        : IsMovieDetailLoading
            ? "Hämtar metadata..."
            : "Ingen beskrivning hittades ännu.";

    public string MovieDetailGenreText => GetMovieDetailValue(_selectedMovieDetail?.Genre, _selectedMovieChannel?.CategoryName);

    public string MovieDetailDurationText => GetMovieDetailValue(_selectedMovieDetail?.Duration);

    public string MovieDetailRatingText => GetMovieDetailValue(_selectedMovieDetail?.Rating);

    public string MovieDetailReleaseDateText => GetMovieDetailValue(_selectedMovieDetail?.ReleaseDate);

    public string MovieDetailDirectorText => GetMovieDetailValue(_selectedMovieDetail?.Director);

    public string MovieDetailCastText => GetMovieDetailValue(_selectedMovieDetail?.Cast);

    public IReadOnlyList<BrowseCategoryChip> CategoryOptions
    {
        get => _categoryOptions;
        private set
        {
            _categoryOptions = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<PlaylistCategoryManagerItem> CategoryManagerItems
    {
        get => _categoryManagerItems;
        private set
        {
            _categoryManagerItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoryManagerHintText));
        }
    }

    public IReadOnlyList<PlaylistCategoryManagerItem> PlaylistEditorCategoryItems
    {
        get => _playlistEditorCategoryItems;
        private set
        {
            _playlistEditorCategoryItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
        }
    }

    public string LiveCardSummary => GetDashboardSectionSummary(BrowseSection.Live, "Live TV");

    public string MoviesCardSummary => GetDashboardSectionSummary(BrowseSection.Movies, "Film");

    public string SeriesCardSummary => GetDashboardSectionSummary(BrowseSection.Series, "Serier");

    public bool IsSeriesSection => _selectedBrowseSection == BrowseSection.Series;

    public bool HasSelectedMovie => _selectedMovieChannel is not null;

    public bool ShowStandardChannelList => !IsSeriesSection && !HasSelectedMovie;

    public bool ShowMovieDetail => _selectedBrowseSection == BrowseSection.Movies && HasSelectedMovie;

    public bool HasSelectedSeriesGroup => _selectedSeriesGroup is not null;

    public bool ShowSeriesOverview => IsSeriesSection && !HasSelectedSeriesGroup;

    public bool ShowSeriesDetail => IsSeriesSection && HasSelectedSeriesGroup;

    public bool ShowBrowserControls => ShowBrowserShell && !ShowMovieDetail && !ShowSeriesDetail;

    public string SeriesDetailTitle => _selectedSeriesGroup?.Title ?? "Serier";

    public string SeriesDetailSubtitle => _selectedSeriesGroup is null
        ? "Välj en serie för att se säsonger och avsnitt."
        : _selectedSeriesSeason is null
            ? _selectedSeriesGroup.SummaryText
            : $"{_selectedSeriesSeason.Label} - {_selectedSeriesSeason.EpisodeCount} avsnitt";

    public string BrowseHintText
    {
        get
        {
            if (IsBusyLoading)
            {
                return "Uppdaterar vald sektion...";
            }

            if (IsPreparingCatalog)
            {
                return "Laddar kategorier från cache...";
            }

            if (IsApplyingFilters)
            {
                return string.IsNullOrWhiteSpace(_selectedCategoryKey)
                    ? "Förbereder sektionen..."
                    : $"Förbereder {GetCategoryDisplayLabel(_selectedCategoryKey)}...";
            }

            if (_selectedBrowseSection is null)
            {
                return "Välj en sektion för att fortsätta.";
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return $"Söker i {GetSectionLabel(_selectedBrowseSection).ToLowerInvariant()}.";
            }

            if (IsSeriesSection && _selectedSeriesGroup is not null)
            {
                return _selectedSeriesSeason is null
                    ? $"Serie: {_selectedSeriesGroup.Title}"
                    : $"Serie: {_selectedSeriesGroup.Title} - {_selectedSeriesSeason.Label}";
            }

            if (!string.IsNullOrWhiteSpace(_selectedCategoryKey))
            {
                return $"Kategori: {GetCategoryDisplayLabel(_selectedCategoryKey)}";
            }

            return "Välj en kategori för att fortsätta.";
        }
    }

    public string CategoryManagerButtonText => IsCategoryManagerVisible ? "Stäng kategorier" : "Kategorier";

    public string SearchToggleButtonText => IsSearchVisible ? "×" : "🔍";

    public string CategoryManagerHintText
    {
        get
        {
            if (_selectedBrowseSection is null)
            {
                return "Välj en sektion för att ordna kategorier.";
            }

            if (CategoryManagerItems.Count == 0)
            {
                return $"Inga kategorier hittades i {GetSectionLabel(_selectedBrowseSection).ToLowerInvariant()}.";
            }

            var visibleCount = CategoryManagerItems.Count(item => item.IsVisible);
            return $"Visar {visibleCount} av {CategoryManagerItems.Count} kategorier. Synliga ligger överst.";
        }
    }

    public bool IsPlaylistEditorLiveSelected => _playlistEditorSection == BrowseSection.Live;

    public bool IsPlaylistEditorMoviesSelected => _playlistEditorSection == BrowseSection.Movies;

    public bool IsPlaylistEditorSeriesSelected => _playlistEditorSection == BrowseSection.Series;

    public string PlaylistEditorCategoryManagerHintText
    {
        get
        {
            if (!HasPlaylistSource)
            {
                return "Ange en playlist först för att ordna kategorier.";
            }

            if (IsBusyLoading)
            {
                return "Laddar spellista...";
            }

            if (IsPreparingCatalog)
            {
                return "Läser kategorier från cache...";
            }

            if (PlaylistEditorCategoryItems.Count == 0)
            {
                return $"Inga kategorier hittades i {GetSectionLabel(_playlistEditorSection).ToLowerInvariant()} än.";
            }

            var visibleCount = PlaylistEditorCategoryItems.Count(item => item.IsVisible);
            return $"Visar {visibleCount} av {PlaylistEditorCategoryItems.Count} kategorier. Synliga ligger överst.";
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        await InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _loadPlaylistCancellationTokenSource?.Cancel();
        _applyFiltersCancellationTokenSource?.Cancel();
        _artworkLoadCancellationTokenSource?.Cancel();
        _searchDebounceCancellationTokenSource?.Cancel();
        StopEmbeddedPlayback();
    }

    private async Task InitializeAsync()
    {
        _categoryDisplayPreferences = await _categoryPreferencesStore.LoadAsync();
        _favoriteUrls = await _favoritesStore.LoadAsync();
        _recentPlaybackEntries = await _recentPlaybackStore.LoadAsync();
        PlaylistSource = Preferences.Default.Get(PlaylistSourcePreferenceKey, string.Empty);
        XmlTvSource = Preferences.Default.Get(XmlTvSourcePreferenceKey, string.Empty);
        IsPlaylistEditorVisible = false;
        IsCategoryManagerVisible = false;

        if (HasPlaylistSource)
        {
            StatusText = "Spellistan är sparad. Välj Live, Film eller Serier för att ladda från cache.";
        }
        else
        {
            StatusText = "Klistra in en M3U-länk för att komma igång.";
        }
    }

    private async void OnLoadPlaylistClicked(object sender, EventArgs e)
    {
        await LoadPlaylistAsync(userInitiated: true, preferCache: false);
    }

    private async void OnOpenPlaylistEditorClicked(object sender, EventArgs e)
    {
        await OpenPlaylistEditorPageAsync();
    }

    private void OnClosePlaylistEditorClicked(object sender, EventArgs e)
    {
        IsPlaylistEditorVisible = false;
    }

    internal async Task OpenPlaylistEditorPageAsync()
    {
        if (_isOpeningPlaylistEditorPage)
        {
            return;
        }

        _isOpeningPlaylistEditorPage = true;
        try
        {
            await PreparePlaylistEditorAsync();

            if (Navigation.NavigationStack.LastOrDefault() is PlaylistEditorPage)
            {
                return;
            }

            await Navigation.PushAsync(new PlaylistEditorPage(this));
        }
        finally
        {
            _isOpeningPlaylistEditorPage = false;
        }
    }

    internal async Task PreparePlaylistEditorAsync()
    {
        if (_selectedBrowseSection is { } currentSection)
        {
            _playlistEditorSection = currentSection;
            NotifyPlaylistEditorSectionChanged();
        }

        await EnsurePlaylistEditorSectionReadyAsync(_playlistEditorSection);
    }

    internal string GetPlaylistSourceForEditor() => PlaylistSource;

    internal string GetXmlTvSourceForEditor() => XmlTvSource;

    internal BrowseSection GetPreferredPlaylistEditorSection() => _selectedBrowseSection ?? BrowseSection.Live;

    internal async Task<IReadOnlyList<PlaylistCategoryManagerItem>> GetPlaylistEditorCategorySnapshotAsync(BrowseSection section)
    {
        await EnsurePlaylistEditorSectionReadyAsync(section);

        return BuildPlaylistEditorCategoryItems(section)
            .Select(item => new PlaylistCategoryManagerItem
            {
                Key = item.Key,
                Label = item.Label,
                Count = item.Count,
                IsVisible = item.IsVisible,
            })
            .ToList();
    }

    internal async Task<bool> LoadPlaylistFromEditorAsync(string playlistSource, string xmlTvSource)
    {
        PlaylistSource = playlistSource;
        XmlTvSource = xmlTvSource;

        var didLoad = await LoadPlaylistAsync(userInitiated: true, preferCache: false);
        if (!didLoad)
        {
            return false;
        }

        Preferences.Default.Set(XmlTvSourcePreferenceKey, XmlTvSource.Trim());
        return true;
    }

    internal async Task ApplyPlaylistEditorDraftAsync(
        string playlistSource,
        string xmlTvSource,
        IReadOnlyDictionary<BrowseSection, IReadOnlyList<PlaylistCategoryManagerItem>> categoryDrafts)
    {
        PlaylistSource = playlistSource;
        XmlTvSource = xmlTvSource;

        Preferences.Default.Set(PlaylistSourcePreferenceKey, PlaylistSource.Trim());
        Preferences.Default.Set(XmlTvSourcePreferenceKey, XmlTvSource.Trim());

        foreach (var section in categoryDrafts.Keys)
        {
            _categoryDisplayPreferences.RemoveAll(preference => preference.Section == section);
        }

        foreach (var (section, items) in categoryDrafts)
        {
            _categoryDisplayPreferences.AddRange(
                items.Select((item, index) => new CategoryDisplayPreference(
                    section,
                    item.Key,
                    item.IsVisible,
                    index)));
        }

        await SaveCategoryPreferencesAsync();
        RefreshCategoryPresentation();
        StatusText = "Playlist-inställningarna sparades.";
    }

    internal async Task SelectPlaylistEditorSectionAsync(BrowseSection section)
    {
        if (_playlistEditorSection != section)
        {
            _playlistEditorSection = section;
            NotifyPlaylistEditorSectionChanged();
        }

        await EnsurePlaylistEditorSectionReadyAsync(section);
    }

    internal async Task ReloadPlaylistFromPlaylistEditorAsync()
    {
        await LoadPlaylistAsync(userInitiated: true, preferCache: false);
        await EnsurePlaylistEditorSectionReadyAsync(_playlistEditorSection);
    }

    internal async Task SetPlaylistEditorCategoryVisibilityAsync(PlaylistCategoryManagerItem item, bool isVisible)
    {
        if (_isUpdatingPlaylistEditorCategories)
        {
            return;
        }

        item.IsVisible = isVisible;
        await PersistPlaylistEditorCategoryPreferencesAsync();
    }

    internal async Task MovePlaylistEditorCategoryAsync(PlaylistCategoryManagerItem item, int direction)
    {
        if (MovePlaylistEditorCategoryItem(item, direction))
        {
            await PersistPlaylistEditorCategoryPreferencesAsync();
        }
    }

    internal async Task ShowAllPlaylistEditorCategoriesAsync()
    {
        if (PlaylistEditorCategoryItems.Count == 0)
        {
            return;
        }

        _isUpdatingPlaylistEditorCategories = true;
        try
        {
            foreach (var item in PlaylistEditorCategoryItems)
            {
                item.IsVisible = true;
            }
        }
        finally
        {
            _isUpdatingPlaylistEditorCategories = false;
        }

        await PersistPlaylistEditorCategoryPreferencesAsync();
    }

    internal async Task HideAllPlaylistEditorCategoriesAsync()
    {
        if (PlaylistEditorCategoryItems.Count == 0)
        {
            return;
        }

        _isUpdatingPlaylistEditorCategories = true;
        try
        {
            foreach (var item in PlaylistEditorCategoryItems)
            {
                item.IsVisible = false;
            }
        }
        finally
        {
            _isUpdatingPlaylistEditorCategories = false;
        }

        await PersistPlaylistEditorCategoryPreferencesAsync();
    }

    internal async Task ResetPlaylistEditorCategoryPreferencesAsync()
    {
        _categoryDisplayPreferences.RemoveAll(preference => preference.Section == _playlistEditorSection);
        await SaveCategoryPreferencesAsync();
        RefreshCategoryPresentation();
        RebuildPlaylistEditorCategoryItems();
    }

    private void OnToggleCategoryManagerClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is null)
        {
            return;
        }

        IsCategoryManagerVisible = !IsCategoryManagerVisible;
        if (IsCategoryManagerVisible)
        {
            RebuildCategoryManagerItems();
        }
    }

    private void OnCloseCategoryManagerClicked(object sender, EventArgs e)
    {
        IsCategoryManagerVisible = false;
    }

    private async void OnSectionCardTapped(object? sender, TappedEventArgs e)
    {
        if (!TryGetBrowseSection(sender, out var section))
        {
            return;
        }

        if (HasPlaylistSource)
        {
            await ShowInteractionLoadingAsync($"Öppnar {GetSectionLabel(section).ToLowerInvariant()}...");
        }

        await SelectSectionAsync(section);
    }

    private async void OnSectionCardClicked(object sender, EventArgs e)
    {
        if (!TryGetBrowseSection(sender, out var section))
        {
            return;
        }

        if (HasPlaylistSource)
        {
            await ShowInteractionLoadingAsync($"Öppnar {GetSectionLabel(section).ToLowerInvariant()}...");
        }

        await SelectSectionAsync(section);
    }

    private void OnBackToDashboardClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is null)
        {
            return;
        }

        StopEmbeddedPlayback();
        _applyFiltersCancellationTokenSource?.Cancel();
        _pendingBrowseSectionAfterLoad = null;
        _selectedBrowseSection = null;
        IsCategoryManagerVisible = false;
        IsSearchVisible = false;
        FavoritesOnly = false;
        SearchText = string.Empty;
        _selectedCategoryKey = null;
        ClearSelectedMovie();
        CategoryOptions = [];
        CategoryManagerItems = [];
        ResetIncrementalVisibleBuffers();
        VisibleChannels = [];
        ResetSeriesNavigation(clearGroups: true);
        EmptyStateText = "Välj Live, Film eller Serier för att fortsätta.";
        StatusText = "Tillbaka på startsidan.";
        NotifySectionStateChanged();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSelectedMovie();
        SearchText = e.NewTextValue ?? string.Empty;
        DebounceApplyFilters();
    }

    private void OnToggleSearchClicked(object sender, EventArgs e)
    {
        IsSearchVisible = !IsSearchVisible;
        if (IsSearchVisible || string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        ClearSelectedMovie();
        SearchText = string.Empty;
        ApplyFilters();
    }

    private async void OnFavoritesOnlyToggled(object sender, ToggledEventArgs e)
    {
        ClearSelectedMovie();
        FavoritesOnly = e.Value;
        await ShowInteractionLoadingAsync("Uppdaterar listan...");
        ApplyFilters();
    }

    private async void OnToggleFavoritesOnlyClicked(object sender, EventArgs e)
    {
        ClearSelectedMovie();
        FavoritesOnly = !FavoritesOnly;
        await ShowInteractionLoadingAsync("Uppdaterar listan...");
        ApplyFilters();
    }

    private async void OnCategoryChipClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        var selectedCategoryKey = key;
        var didChangeCategory = !string.Equals(_selectedCategoryKey, selectedCategoryKey, StringComparison.OrdinalIgnoreCase);
        var shouldRefreshCategory =
            _selectedBrowseSection is not null
            && !string.IsNullOrWhiteSpace(selectedCategoryKey)
            && !IsSpecialCategoryKey(selectedCategoryKey)
            && didChangeCategory;

        _selectedCategoryKey = selectedCategoryKey;
        if (didChangeCategory)
        {
            ClearSelectedMovie();
            ResetSeriesNavigation(clearGroups: false);
        }

        await ShowInteractionLoadingAsync($"Bygger {GetCategoryDisplayLabel(selectedCategoryKey)}...");
        RebuildCategoryOptions();

        if (_allChannels.Count == 0)
        {
            StatusText = $"Laddar {GetCategoryDisplayLabel(selectedCategoryKey)} från cache...";

            if (!IsBusyLoading)
            {
                var didLoad = await LoadPlaylistAsync(userInitiated: false, preferCache: true);
                if (!didLoad)
                {
                    return;
                }
            }
        }

        ApplyFilters();

        if (shouldRefreshCategory && _selectedBrowseSection is { } section)
        {
            await RefreshSelectedCategoryAsync(section, _selectedCategoryKey!);
        }
    }

    private async void OnCategoryVisibilityToggled(object sender, ToggledEventArgs e)
    {
        if (_selectedBrowseSection is not { } section
            || sender is not Switch { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        if (_isUpdatingCategoryManager)
        {
            return;
        }

        item.IsVisible = e.Value;
        await PersistCategoryPreferencesAsync(section);
    }

    private async void OnMoveCategoryUpClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is not { } section
            || sender is not Button { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        if (MoveCategoryManagerItem(item, -1))
        {
            await PersistCategoryPreferencesAsync(section);
        }
    }

    private async void OnMoveCategoryDownClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is not { } section
            || sender is not Button { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        if (MoveCategoryManagerItem(item, 1))
        {
            await PersistCategoryPreferencesAsync(section);
        }
    }

    private async void OnShowAllCategoriesClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is not { } section || CategoryManagerItems.Count == 0)
        {
            return;
        }

        _isUpdatingCategoryManager = true;
        try
        {
            foreach (var item in CategoryManagerItems)
            {
                item.IsVisible = true;
            }
        }
        finally
        {
            _isUpdatingCategoryManager = false;
        }

        await PersistCategoryPreferencesAsync(section);
    }

    private async void OnHideAllCategoriesClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is not { } section || CategoryManagerItems.Count == 0)
        {
            return;
        }

        _isUpdatingCategoryManager = true;
        try
        {
            foreach (var item in CategoryManagerItems)
            {
                item.IsVisible = false;
            }
        }
        finally
        {
            _isUpdatingCategoryManager = false;
        }

        await PersistCategoryPreferencesAsync(section);
    }

    private async void OnResetCategoryPreferencesClicked(object sender, EventArgs e)
    {
        if (_selectedBrowseSection is not { } section)
        {
            return;
        }

        _categoryDisplayPreferences.RemoveAll(preference => preference.Section == section);
        await SaveCategoryPreferencesAsync();
        RefreshCategoryPresentation();
    }

    private async void OnFavoriteClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: PlaylistChannel channel })
        {
            return;
        }

        channel.IsFavorite = !channel.IsFavorite;
        if (channel.IsFavorite)
        {
            _favoriteUrls.Add(channel.Url);
        }
        else
        {
            _favoriteUrls.Remove(channel.Url);
        }

        await _favoritesStore.SaveAsync(_favoriteUrls);
        InvalidateSeriesGroupCache();
        await ShowInteractionLoadingAsync("Uppdaterar favoriter...");
        ApplyFilters();
    }

    private async void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not PlaylistChannel channel)
        {
            return;
        }

        if (channel.ContentType == ChannelContentType.Movie || _selectedBrowseSection == BrowseSection.Movies)
        {
            await ShowInteractionLoadingAsync("Öppnar detaljer...");
            SelectMovieChannel(channel);
            IsApplyingFilters = false;
            return;
        }

        await OpenChannelAsync(channel);
    }

    private async void OnMovieBackClicked(object sender, EventArgs e)
    {
        try
        {
            await ShowInteractionLoadingAsync("Tillbaka till listan...");
            ClearSelectedMovie();
            if (_selectedBrowseSection is { } section)
            {
                StatusText = GetSectionReadyStatusText(section);
            }
        }
        finally
        {
            IsApplyingFilters = false;
        }
    }

    private async void OnMoviePlayClicked(object sender, EventArgs e)
    {
        if (_selectedMovieChannel is null)
        {
            return;
        }

        await OpenChannelAsync(_selectedMovieChannel);
    }

    private void OnVisibleChannelsRemainingItemsThresholdReached(object? sender, EventArgs e)
    {
        AppendVisibleChannelBatch();
    }

    private async void OnSeriesGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not SeriesGroupItem group)
        {
            return;
        }

        try
        {
            await ShowInteractionLoadingAsync("Öppnar serie...");
            SelectSeriesGroup(group);
        }
        finally
        {
            IsApplyingFilters = false;
        }
    }

    private void OnVisibleSeriesGroupsRemainingItemsThresholdReached(object? sender, EventArgs e)
    {
        AppendVisibleSeriesGroupBatch();
    }

    private async void OnSeriesEpisodeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView collectionView)
        {
            collectionView.SelectedItem = null;
        }

        if (e.CurrentSelection.FirstOrDefault() is not SeriesEpisodeItem episode)
        {
            return;
        }

        await OpenChannelAsync(episode.Channel);
    }

    private void OnSeriesBackClicked(object sender, EventArgs e)
    {
        SetSelectedSeriesGroup(null);
    }

    private async void OnSeriesSeasonClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SeriesSeasonItem season })
        {
            return;
        }

        try
        {
            await ShowInteractionLoadingAsync("Byter säsong...");
            SetSelectedSeriesSeason(season);
        }
        finally
        {
            IsApplyingFilters = false;
        }
    }

    private void OnCloseEmbeddedPlayerClicked(object sender, EventArgs e)
    {
        StopEmbeddedPlayback();
        StatusText = "Uppspelningen stängdes.";
    }

    private async void OnOpenEmbeddedSubtitleOptionsClicked(object sender, EventArgs e)
    {
#if ANDROID
        if (!ShowEmbeddedPlayer)
        {
            return;
        }

        if (TryShowEmbeddedSubtitleDialog())
        {
            return;
        }

        await DisplayAlert(
            "Undertexter",
            "Det gick inte att öppna undertextvalet ännu. Prova igen när uppspelningen har startat helt.",
            "OK");
#endif
    }

    private async Task SelectSectionAsync(BrowseSection section)
    {
        if (!HasPlaylistSource)
        {
            StatusText = "Ange din M3U i Playlists innan du väljer Live, Film eller Serier.";
            await OpenPlaylistEditorPageAsync();
            return;
        }

        _pendingBrowseSectionAfterLoad = section;
        _selectedBrowseSection = section;
        _selectedCategoryKey = GetDefaultCategoryKey(section);
        ClearSelectedMovie();
        SearchText = string.Empty;
        IsSearchVisible = false;
        FavoritesOnly = false;
        IsCategoryManagerVisible = false;
        IsPlaylistEditorVisible = false;
        ResetSeriesNavigation(clearGroups: true);
        NotifySectionStateChanged();
        RefreshCategoryPresentation();

        if (_allChannels.Count > 0)
        {
            _pendingBrowseSectionAfterLoad = null;
            StatusText = GetSectionReadyStatusText(section);
            return;
        }

        var didLoadPreview = await TryLoadSectionPreviewAsync(section);
        if (didLoadPreview)
        {
            _pendingBrowseSectionAfterLoad = null;
            if (ShouldLoadFullCatalogForDefaultCategory(section) && !HasLatestPreviewChannels(section))
            {
                StatusText = $"Laddar {LatestCategoryLabel.ToLowerInvariant()} från cache...";
                var didLoadDefaultCategory = await LoadPlaylistAsync(userInitiated: false, preferCache: true);
                if (didLoadDefaultCategory)
                {
                    StatusText = GetSectionReadyStatusText(section);
                }

                return;
            }

            StatusText = GetSectionReadyStatusText(section);
            return;
        }

        StatusText = $"Öppnar {GetSectionLabel(section).ToLowerInvariant()} så snart innehållet är klart.";

        if (IsBusyLoading)
        {
            return;
        }

        var didLoad = await LoadPlaylistAsync(userInitiated: true, preferCache: true);
        if (didLoad)
        {
            StatusText = GetSectionReadyStatusText(section);
        }
    }

    private async Task<bool> LoadPlaylistAsync(bool userInitiated, bool preferCache)
    {
        var source = PlaylistSource.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            StatusText = "Ange en M3U-länk eller fil först.";
            EmptyStateText = "Ingen spellista laddad än.";
            IsPlaylistEditorVisible = false;

            if (userInitiated)
            {
                await DisplayAlert("Spellista saknas", "Ange en M3U-länk eller fil innan du laddar.", "OK");
            }

            return false;
        }

        _loadPlaylistCancellationTokenSource?.Cancel();
        _loadPlaylistCancellationTokenSource?.Dispose();
        var loadCancellationTokenSource = new CancellationTokenSource();
        _loadPlaylistCancellationTokenSource = loadCancellationTokenSource;

        IsBusyLoading = true;
        StatusText = "Laddar spellista...";
        EmptyStateText = "Laddar spellista...";

        try
        {
            if (preferCache)
            {
                var cachedPlaylist = await _playlistCacheStore.TryLoadAsync(
                    source,
                    loadCancellationTokenSource.Token);

                if (cachedPlaylist is { Channels.Count: > 0 })
                {
                    await ApplyLoadedChannelsAsync(source, cachedPlaylist.Channels, loadCancellationTokenSource.Token);
                    await SaveCacheIndexBestEffortAsync(source, cachedPlaylist.Channels, loadCancellationTokenSource.Token);
                    StatusText = cachedPlaylist.Channels.Count == 1
                        ? "1 objekt laddat från lokal cache."
                        : $"{cachedPlaylist.Channels.Count} objekt laddade från lokal cache.";
                    return true;
                }
            }

            var progress = new Progress<M3uPlaylistService.LoadProgress>(progressValue =>
            {
                StatusText = $"Laddar... {progressValue.ChannelsParsed} kanaler";
            });

            var channels = (await _playlistService.LoadAsync(
                source,
                loadProgress: progress,
                cancellationToken: loadCancellationTokenSource.Token)).ToList();

            await ApplyLoadedChannelsAsync(source, channels, loadCancellationTokenSource.Token);

            try
            {
                await _playlistCacheStore.SaveAsync(source, channels, loadCancellationTokenSource.Token);
            }
            catch
            {
                // Cache write failures should not block the main playlist flow.
            }

            StatusText = channels.Count == 1
                ? "1 objekt laddat"
                : $"{channels.Count} objekt laddade";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Laddning avbruten.";
            return false;
        }
        catch (Exception ex)
        {
            _allChannels = [];
            _sectionCatalogs = [];
            _sectionPreviews = [];
            _sectionStats = [];
            InvalidateSeriesGroupCache();
            CategoryOptions = [];
            CategoryManagerItems = [];
            ResetIncrementalVisibleBuffers();
            VisibleChannels = [];
            ResetSeriesNavigation(clearGroups: true);
            EmptyStateText = "Kunde inte ladda spellistan.";
            StatusText = "Fel vid laddning.";
            NotifyCatalogChanged();

            if (userInitiated)
            {
                await DisplayAlert("Laddning misslyckades", GetFriendlyLoadErrorMessage(source, ex), "OK");
            }

            return false;
        }
        finally
        {
            if (ReferenceEquals(_loadPlaylistCancellationTokenSource, loadCancellationTokenSource))
            {
                _loadPlaylistCancellationTokenSource = null;
            }

            IsBusyLoading = false;
        }
    }

    private async Task RefreshSelectedCategoryAsync(BrowseSection section, string categoryKey)
    {
        var source = PlaylistSource.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var contentType = GetContentTypeForSection(section);
        if (contentType == ChannelContentType.Series)
        {
            StatusText = $"Visar {categoryKey} från lokal cache. Serier uppdateras inte per kategori ännu.";
            return;
        }

        _loadPlaylistCancellationTokenSource?.Cancel();
        _loadPlaylistCancellationTokenSource?.Dispose();
        var refreshCancellationTokenSource = new CancellationTokenSource();
        _loadPlaylistCancellationTokenSource = refreshCancellationTokenSource;

        try
        {
            IsBusyLoading = true;
            StatusText = $"Uppdaterar {categoryKey}...";

            var apiResult = await _xtreamApiService.TryLoadCategoryAsync(
                source,
                contentType,
                categoryKey,
                _allChannels,
                refreshCancellationTokenSource.Token);

            if (apiResult is null)
            {
                StatusText = $"Visar {categoryKey} från lokal cache. Den här playlisten stöder inte direkt kategoriuppdatering ännu.";
                return;
            }

            var previousCategoryChannels = GetCategoryChannelsForSection(section, categoryKey, _allChannels);
            var mergedChannels = MergeUpdatedCategoryIntoCatalog(section, categoryKey, apiResult.Channels);
            var updatedCategoryChannels = GetCategoryChannelsForSection(section, categoryKey, mergedChannels);
            var hadChanges = !PlaylistLooksUnchanged(previousCategoryChannels, updatedCategoryChannels);

            await ApplyLoadedChannelsAsync(source, mergedChannels, refreshCancellationTokenSource.Token);

            try
            {
                await _playlistCacheStore.SaveAsync(source, _allChannels, refreshCancellationTokenSource.Token);
            }
            catch
            {
                // Cache write failures should not block the main playlist flow.
            }

            StatusText = hadChanges
                ? $"{categoryKey} uppdaterades via {apiResult.ProviderName}."
                : $"{categoryKey} var redan uppdaterad.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Kategoriuppdateringen avbröts.";
        }
        catch (Exception ex)
        {
            StatusText = $"Kunde inte uppdatera {categoryKey}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadPlaylistCancellationTokenSource, refreshCancellationTokenSource))
            {
                _loadPlaylistCancellationTokenSource = null;
            }

            IsBusyLoading = false;
        }
    }

    private async Task ApplyLoadedChannelsAsync(
        string source,
        IReadOnlyCollection<PlaylistChannel> channels,
        CancellationToken cancellationToken)
    {
        IsPreparingCatalog = true;
        StatusText = "Förbereder katalog...";

        try
        {
            var preparedCatalog = await Task.Run(
                () => PrepareCatalog(channels, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            _allChannels = preparedCatalog.Channels;
            _sectionCatalogs = preparedCatalog.SectionCatalogs;
            _sectionPreviews = preparedCatalog.Previews;
            _sectionStats = preparedCatalog.SectionStats;
            InvalidateSeriesGroupCache();
            Preferences.Default.Set(PlaylistSourcePreferenceKey, source);
            IsPlaylistEditorVisible = false;
            ApplyPendingBrowseSectionAfterLoad();
            RefreshCategoryPresentation();
        }
        finally
        {
            IsPreparingCatalog = false;
        }
    }

    private async Task SaveCacheIndexBestEffortAsync(
        string source,
        IReadOnlyCollection<PlaylistChannel> channels,
        CancellationToken cancellationToken)
    {
        try
        {
            await _playlistCacheStore.SaveIndexAsync(source, channels, cancellationToken);
        }
        catch
        {
            // A stale index only affects startup speed; playback and browsing should continue.
        }
    }

    private void DebounceApplyFilters()
    {
        _searchDebounceCancellationTokenSource?.Cancel();
        _searchDebounceCancellationTokenSource?.Dispose();

        var debounceCancellationTokenSource = new CancellationTokenSource();
        _searchDebounceCancellationTokenSource = debounceCancellationTokenSource;
        _ = ApplyFiltersAfterDelayAsync(debounceCancellationTokenSource.Token);
    }

    private async Task ApplyFiltersAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ApplyFilters();
        }
    }

    private void InvalidateSeriesGroupCache()
    {
        lock (_seriesGroupCacheSync)
        {
            _seriesGroupCache.Clear();
        }
    }

    private void UpdateSectionStats()
    {
        _sectionStats = Enum
            .GetValues<BrowseSection>()
            .ToDictionary(
                section => section,
                BuildSectionStats);
    }

    private SectionStats GetSectionStats(BrowseSection section)
    {
        if (_sectionStats.TryGetValue(section, out var stats))
        {
            return stats;
        }

        stats = BuildSectionStats(section);
        _sectionStats[section] = stats;
        return stats;
    }

    private SectionStats BuildSectionStats(BrowseSection section)
    {
        if (_sectionCatalogs.TryGetValue(section, out var catalog))
        {
            return new SectionStats(
                catalog.Channels.Count,
                catalog.Channels.Count,
                catalog.CategorySummaries.Count);
        }

        if (_sectionPreviews.TryGetValue(section, out var preview))
        {
            return new SectionStats(
                preview.ItemCount,
                preview.ItemCount,
                preview.CategorySummaries.Count);
        }

        return new SectionStats(0, 0, 0);
    }

    private async Task<bool> TryLoadSectionPreviewAsync(BrowseSection section)
    {
        if (_sectionCatalogs.ContainsKey(section) || _sectionPreviews.ContainsKey(section))
        {
            return true;
        }

        var source = PlaylistSource.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        IsPreparingCatalog = true;
        StatusText = $"Laddar kategorier för {GetSectionLabel(section).ToLowerInvariant()} från cache...";

        try
        {
            var cachedIndex = await _playlistCacheStore.TryLoadIndexAsync(source);
            if (cachedIndex is null)
            {
                return false;
            }

            _sectionPreviews = BuildSectionPreviews(cachedIndex);
            ApplyFavoritesToPreviewChannels();
            UpdateSectionStats();
            RefreshCategoryPresentation();
            return _sectionPreviews.ContainsKey(section);
        }
        catch
        {
            return false;
        }
        finally
        {
            IsPreparingCatalog = false;
        }
    }

    private PreparedCatalog PrepareCatalog(
        IReadOnlyCollection<PlaylistChannel> channels,
        CancellationToken cancellationToken)
    {
        var allChannels = channels as List<PlaylistChannel> ?? channels.ToList();
        var favoriteUrls = new HashSet<string>(_favoriteUrls, StringComparer.OrdinalIgnoreCase);

        foreach (var channel in allChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            channel.IsFavorite = favoriteUrls.Contains(channel.Url);
        }

        var sectionCatalogs = Enum
            .GetValues<BrowseSection>()
            .ToDictionary(
                section => section,
                section => BuildSectionCatalog(allChannels.Where(channel => IsSectionMatch(channel, section)).ToList()));

        var previews = sectionCatalogs.ToDictionary(
            pair => pair.Key,
            pair => new SectionPreview(pair.Value.Channels.Count, pair.Value.CategorySummaries, []));

        var sectionStats = sectionCatalogs.ToDictionary(
            pair => pair.Key,
            pair => new SectionStats(
                pair.Value.Channels.Count,
                pair.Value.Channels.Count,
                pair.Value.CategorySummaries.Count));

        return new PreparedCatalog(allChannels, sectionCatalogs, previews, sectionStats);
    }

    private static SectionCatalog BuildSectionCatalog(IReadOnlyList<PlaylistChannel> channels)
    {
        var channelsByCategory = channels
            .GroupBy(channel => channel.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PlaylistChannel>)group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var categorySummaries = channelsByCategory
            .Select(pair => new CategorySummary(
                pair.Key,
                pair.Key,
                pair.Value.Count))
            .ToList();

        return new SectionCatalog(channels, channelsByCategory, categorySummaries);
    }

    private static Dictionary<BrowseSection, SectionPreview> BuildSectionPreviews(
        PlaylistCacheStore.CachedPlaylistIndex cachedIndex)
    {
        var previews = new Dictionary<BrowseSection, SectionPreview>();

        foreach (var sectionIndex in cachedIndex.Sections)
        {
            if (!TryMapContentTypeToSection(sectionIndex.ContentType, out var section))
            {
                continue;
            }

            previews[section] = new SectionPreview(
                sectionIndex.ItemCount,
                sectionIndex.Categories
                    .Select(category => new CategorySummary(category.Key, category.Label, category.Count))
                    .ToList(),
                sectionIndex.LatestChannels);
        }

        foreach (var section in Enum.GetValues<BrowseSection>())
        {
            previews.TryAdd(section, new SectionPreview(0, [], []));
        }

        return previews;
    }

    private void ApplyFavoritesToPreviewChannels()
    {
        foreach (var channel in _sectionPreviews.Values.SelectMany(preview => preview.LatestChannels))
        {
            channel.IsFavorite = _favoriteUrls.Contains(channel.Url);
        }
    }

    private static bool TryMapContentTypeToSection(ChannelContentType contentType, out BrowseSection section)
    {
        section = contentType switch
        {
            ChannelContentType.Live => BrowseSection.Live,
            ChannelContentType.Movie => BrowseSection.Movies,
            ChannelContentType.Series => BrowseSection.Series,
            _ => default,
        };

        return contentType is ChannelContentType.Live
            or ChannelContentType.Movie
            or ChannelContentType.Series;
    }

    private List<PlaylistChannel> MergeUpdatedCategoryIntoCatalog(
        BrowseSection section,
        string categoryKey,
        IReadOnlyList<PlaylistChannel> updatedChannels)
    {
        var mergedChannels = new List<PlaylistChannel>(_allChannels.Count + updatedChannels.Count);
        var insertedUpdatedCategory = false;

        foreach (var channel in _allChannels)
        {
            var belongsToSelectedCategory = IsCategoryMatch(channel, section, categoryKey);
            if (belongsToSelectedCategory)
            {
                if (!insertedUpdatedCategory)
                {
                    mergedChannels.AddRange(updatedChannels);
                    insertedUpdatedCategory = true;
                }

                continue;
            }

            mergedChannels.Add(channel);
        }

        if (!insertedUpdatedCategory)
        {
            mergedChannels.AddRange(updatedChannels);
        }

        return mergedChannels;
    }

    private static IReadOnlyList<PlaylistChannel> GetCategoryChannelsForSection(
        BrowseSection section,
        string categoryKey,
        IReadOnlyList<PlaylistChannel> channels)
    {
        return channels
            .Where(channel => IsSectionMatch(channel, section)
                && string.Equals(channel.CategoryName, categoryKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool PlaylistLooksUnchanged(
        IReadOnlyList<PlaylistChannel> previousChannels,
        IReadOnlyList<PlaylistChannel> updatedChannels)
    {
        if (previousChannels.Count != updatedChannels.Count)
        {
            return false;
        }

        for (var index = 0; index < previousChannels.Count; index++)
        {
            var previous = previousChannels[index];
            var updated = updatedChannels[index];
            if (!string.Equals(previous.Name, updated.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.Url, updated.Url, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.CategoryName, updated.CategoryName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.LogoUrl, updated.LogoUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.TvgId, updated.TvgId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyPendingBrowseSectionAfterLoad()
    {
        if (_pendingBrowseSectionAfterLoad is not { } pendingSection)
        {
            return;
        }

        _selectedBrowseSection = pendingSection;
        _selectedCategoryKey ??= GetDefaultCategoryKey(pendingSection);
        _pendingBrowseSectionAfterLoad = null;
        NotifySectionStateChanged();
    }

    private async Task EnsurePlaylistEditorSectionReadyAsync(BrowseSection section)
    {
        if (!HasPlaylistSource)
        {
            PlaylistEditorCategoryItems = [];
            return;
        }

        if (_sectionCatalogs.ContainsKey(section) || _sectionPreviews.ContainsKey(section) || _allChannels.Count > 0)
        {
            RebuildPlaylistEditorCategoryItems();
            return;
        }

        var didLoadPreview = await TryLoadSectionPreviewAsync(section);
        if (!didLoadPreview && _allChannels.Count == 0 && !IsBusyLoading)
        {
            await LoadPlaylistAsync(userInitiated: false, preferCache: true);
        }

        RebuildPlaylistEditorCategoryItems();
    }

    private void NotifyPlaylistEditorSectionChanged()
    {
        OnPropertyChanged(nameof(IsPlaylistEditorLiveSelected));
        OnPropertyChanged(nameof(IsPlaylistEditorMoviesSelected));
        OnPropertyChanged(nameof(IsPlaylistEditorSeriesSelected));
        OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
    }

    private bool MovePlaylistEditorCategoryItem(PlaylistCategoryManagerItem item, int direction)
    {
        var items = PlaylistEditorCategoryItems.ToList();
        var currentIndex = items.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        var sameVisibilityIndexes = items
            .Select((candidate, index) => new { Candidate = candidate, Index = index })
            .Where(entry => entry.Candidate.IsVisible == item.IsVisible)
            .Select(entry => entry.Index)
            .ToList();

        var groupPosition = sameVisibilityIndexes.IndexOf(currentIndex);
        if (groupPosition < 0)
        {
            return false;
        }

        var targetGroupPosition = groupPosition + direction;
        if (targetGroupPosition < 0 || targetGroupPosition >= sameVisibilityIndexes.Count)
        {
            return false;
        }

        var targetIndex = sameVisibilityIndexes[targetGroupPosition];
        (items[currentIndex], items[targetIndex]) = (items[targetIndex], items[currentIndex]);
        PlaylistEditorCategoryItems = items;
        return true;
    }

    private async Task PersistPlaylistEditorCategoryPreferencesAsync()
    {
        _categoryDisplayPreferences.RemoveAll(preference => preference.Section == _playlistEditorSection);
        _categoryDisplayPreferences.AddRange(
            PlaylistEditorCategoryItems.Select((item, index) => new CategoryDisplayPreference(
                _playlistEditorSection,
                item.Key,
                item.IsVisible,
                index)));

        await SaveCategoryPreferencesAsync();
        RefreshCategoryPresentation();
        RebuildPlaylistEditorCategoryItems();
    }

    private bool MoveCategoryManagerItem(PlaylistCategoryManagerItem item, int direction)
    {
        var items = CategoryManagerItems.ToList();
        var currentIndex = items.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        var sameVisibilityIndexes = items
            .Select((candidate, index) => new { Candidate = candidate, Index = index })
            .Where(entry => entry.Candidate.IsVisible == item.IsVisible)
            .Select(entry => entry.Index)
            .ToList();

        var groupPosition = sameVisibilityIndexes.IndexOf(currentIndex);
        if (groupPosition < 0)
        {
            return false;
        }

        var targetGroupPosition = groupPosition + direction;
        if (targetGroupPosition < 0 || targetGroupPosition >= sameVisibilityIndexes.Count)
        {
            return false;
        }

        var targetIndex = sameVisibilityIndexes[targetGroupPosition];
        (items[currentIndex], items[targetIndex]) = (items[targetIndex], items[currentIndex]);
        CategoryManagerItems = items;
        return true;
    }

    private async Task PersistCategoryPreferencesAsync(BrowseSection section)
    {
        _categoryDisplayPreferences.RemoveAll(preference => preference.Section == section);
        _categoryDisplayPreferences.AddRange(
            CategoryManagerItems.Select((item, index) => new CategoryDisplayPreference(
                section,
                item.Key,
                item.IsVisible,
                index)));

        await SaveCategoryPreferencesAsync();
        RefreshCategoryPresentation();
    }

    private async Task SaveCategoryPreferencesAsync()
    {
        await _categoryPreferencesStore.SaveAsync(_categoryDisplayPreferences);
    }

    private void RefreshCategoryPresentation()
    {
        InvalidateSeriesGroupCache();
        RebuildCategoryOptions();
        RebuildPlaylistEditorCategoryItems();
        ApplyFilters();
        if (IsCategoryManagerVisible)
        {
            RebuildCategoryManagerItems();
        }
        NotifyCatalogChanged();
    }

    private void RebuildCategoryManagerItems()
    {
        if (_selectedBrowseSection is not { } section)
        {
            CategoryManagerItems = [];
            return;
        }

        var preferenceMap = GetCategoryPreferenceMap(section);
        _isUpdatingCategoryManager = true;
        try
        {
            var items = GetCategorySummariesForSection(section, includeHidden: true)
                .Select(category =>
                {
                    preferenceMap.TryGetValue(category.Key, out var preference);

                    return new PlaylistCategoryManagerItem
                    {
                        Key = category.Key,
                        Label = category.Label,
                        Count = category.Count,
                        IsVisible = preference?.IsVisible ?? true,
                    };
                })
                .OrderByDescending(item => item.IsVisible)
                .ThenBy(item => preferenceMap.TryGetValue(item.Key, out var preference)
                    ? preference.SortOrder
                    : int.MaxValue)
                .ThenByDescending(item => item.Count)
                .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CategoryManagerItems = items;
        }
        finally
        {
            _isUpdatingCategoryManager = false;
        }
    }

    private void RebuildPlaylistEditorCategoryItems()
    {
        if (!HasPlaylistSource)
        {
            PlaylistEditorCategoryItems = [];
            return;
        }

        PlaylistEditorCategoryItems = BuildPlaylistEditorCategoryItems(_playlistEditorSection);
    }

    private List<PlaylistCategoryManagerItem> BuildPlaylistEditorCategoryItems(BrowseSection section)
    {
        var preferenceMap = GetCategoryPreferenceMap(section);

        return GetCategorySummariesForSection(section, includeHidden: true)
            .Select(category =>
            {
                preferenceMap.TryGetValue(category.Key, out var preference);

                return new PlaylistCategoryManagerItem
                {
                    Key = category.Key,
                    Label = category.Label,
                    Count = category.Count,
                    IsVisible = preference?.IsVisible ?? true,
                };
            })
            .OrderByDescending(item => item.IsVisible)
            .ThenBy(item => preferenceMap.TryGetValue(item.Key, out var preference)
                ? preference.SortOrder
                : int.MaxValue)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<CategorySummary> GetCategorySummariesForSection(BrowseSection section, bool includeHidden)
    {
        var categories = _sectionCatalogs.TryGetValue(section, out var sectionCatalog)
            ? sectionCatalog.CategorySummaries.ToList()
            : _sectionPreviews.TryGetValue(section, out var preview)
                ? preview.CategorySummaries.ToList()
                : [];

        return ApplyCategoryDisplayPreferences(section, categories, includeHidden);
    }

    private List<CategorySummary> BuildSpecialCategorySummaries(BrowseSection section)
    {
        var specialCategories = new List<CategorySummary>
        {
            new(
                BrowseCategoryChip.FavoritesKey,
                FavoritesCategoryLabel,
                GetDisplayCountForSection(section, GetFavoriteChannelsForSection(section))),
        };

        if (section != BrowseSection.Live)
        {
            specialCategories.Add(new(
                BrowseCategoryChip.LatestKey,
                LatestCategoryLabel,
                GetLatestAddedItemCountForSection(section)));

            specialCategories.Add(new(
                BrowseCategoryChip.RecentKey,
                RecentCategoryLabel,
                GetDisplayCountForSection(section, GetRecentChannelsForSection(section))));
        }

        return specialCategories;
    }

    private static string? GetDefaultCategoryKey(BrowseSection section)
    {
        return section is BrowseSection.Movies or BrowseSection.Series
            ? BrowseCategoryChip.LatestKey
            : null;
    }

    private static string? GetDefaultCategoryKey(BrowseSection section, IReadOnlyCollection<string> availableCategories)
    {
        var defaultCategoryKey = GetDefaultCategoryKey(section);
        return !string.IsNullOrWhiteSpace(defaultCategoryKey)
            && availableCategories.Contains(defaultCategoryKey, StringComparer.OrdinalIgnoreCase)
                ? defaultCategoryKey
                : null;
    }

    private bool ShouldLoadFullCatalogForDefaultCategory(BrowseSection section)
    {
        return section is BrowseSection.Movies or BrowseSection.Series
            && string.Equals(_selectedCategoryKey, BrowseCategoryChip.LatestKey, StringComparison.Ordinal);
    }

    private bool HasLatestPreviewChannels(BrowseSection section)
    {
        return _sectionPreviews.TryGetValue(section, out var preview)
            && preview.LatestChannels.Count > 0;
    }

    private string GetSectionReadyStatusText(BrowseSection section)
    {
        return section is BrowseSection.Movies or BrowseSection.Series
            && string.Equals(_selectedCategoryKey, BrowseCategoryChip.LatestKey, StringComparison.Ordinal)
                ? $"Visar de {LatestAddedCategoryLimit} senast tillagda i {GetSectionLabel(section).ToLowerInvariant()}."
                : $"Välj kategori i {GetSectionLabel(section).ToLowerInvariant()}.";
    }

    private IReadOnlyList<PlaylistChannel> GetFavoriteChannelsForSection(BrowseSection section)
    {
        return GetChannelsForSection(section, includeHiddenCategories: true)
            .Where(channel => channel.IsFavorite)
            .ToList();
    }

    private IReadOnlyList<PlaylistChannel> GetRecentChannelsForSection(BrowseSection section)
    {
        var recentLookup = _recentPlaybackEntries
            .Where(entry => entry.Section == section)
            .GroupBy(entry => entry.ChannelUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(entry => entry.PlayedAtUtc),
                StringComparer.OrdinalIgnoreCase);

        return GetChannelsForSection(section, includeHiddenCategories: true)
            .Where(channel => recentLookup.ContainsKey(channel.Url))
            .OrderByDescending(channel => recentLookup[channel.Url])
            .ThenBy(channel => channel.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int GetLatestAddedItemCountForSection(BrowseSection section)
    {
        if (section == BrowseSection.Series)
        {
            if (!_sectionCatalogs.ContainsKey(section)
                && _sectionPreviews.TryGetValue(section, out var seriesPreview)
                && seriesPreview.LatestChannels.Count > 0)
            {
                return GetLatestAddedSeriesChannelGroups(seriesPreview.LatestChannels).Count;
            }

            return GetLatestAddedSeriesChannelGroups(GetChannelsForSection(section, includeHiddenCategories: true)).Count;
        }

        if (!_sectionCatalogs.ContainsKey(section)
            && _sectionPreviews.TryGetValue(section, out var preview)
            && preview.LatestChannels.Count > 0)
        {
            return Math.Min(LatestAddedCategoryLimit, preview.LatestChannels.Count);
        }

        return Math.Min(
            LatestAddedCategoryLimit,
            GetChannelsForSection(section, includeHiddenCategories: true).Count());
    }

    private IReadOnlyList<PlaylistChannel> GetLatestAddedChannelsForSection(BrowseSection section)
    {
        if (!_sectionCatalogs.ContainsKey(section)
            && _sectionPreviews.TryGetValue(section, out var preview)
            && preview.LatestChannels.Count > 0)
        {
            return section == BrowseSection.Series
                ? GetLatestAddedSeriesChannelGroups(preview.LatestChannels)
                    .SelectMany(group => group)
                    .ToList()
                : preview.LatestChannels
                    .Take(LatestAddedCategoryLimit)
                    .ToList();
        }

        var sourceChannels = GetChannelsForSection(section, includeHiddenCategories: true);
        if (section == BrowseSection.Series)
        {
            return GetLatestAddedSeriesChannelGroups(sourceChannels)
                .SelectMany(group => group)
                .ToList();
        }

        return SortChannelsByLatestAdded(sourceChannels)
            .Take(LatestAddedCategoryLimit)
            .ToList();
    }

    private IReadOnlyList<PlaylistChannel> SortChannelsForSection(
        BrowseSection section,
        IEnumerable<PlaylistChannel> sourceChannels)
    {
        return section == BrowseSection.Live
            ? sourceChannels.ToList()
            : SortChannelsByLatestAdded(sourceChannels);
    }

    private static IReadOnlyList<PlaylistChannel> SortChannelsByLatestAdded(
        IEnumerable<PlaylistChannel> sourceChannels)
    {
        return sourceChannels
            .Select((channel, index) => new { Channel = channel, Index = index })
            .OrderByDescending(item => item.Channel.AddedAtUtc.HasValue)
            .ThenByDescending(item => item.Channel.AddedAtUtc)
            .ThenBy(item => item.Index)
            .Select(item => item.Channel)
            .ToList();
    }

    private List<IReadOnlyList<PlaylistChannel>> GetLatestAddedSeriesChannelGroups(
        IEnumerable<PlaylistChannel> sourceChannels,
        int? limit = LatestAddedCategoryLimit)
    {
        var groups = sourceChannels
            .Select((channel, index) => new { Channel = channel, Index = index })
            .Where(item => item.Channel.ContentType == ChannelContentType.Series)
            .GroupBy(item => _seriesCatalogService.GetSeriesKey(item.Channel), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                HasAddedAtUtc = group.Any(item => item.Channel.AddedAtUtc.HasValue),
                AddedAtUtc = group.Max(item => item.Channel.AddedAtUtc),
                FirstIndex = group.Min(item => item.Index),
                Channels = group
                    .OrderBy(item => item.Index)
                    .Select(item => item.Channel)
                    .ToList(),
            })
            .OrderByDescending(group => group.HasAddedAtUtc)
            .ThenByDescending(group => group.AddedAtUtc)
            .ThenBy(group => group.FirstIndex);

        var limitedGroups = limit is null
            ? groups
            : groups.Take(limit.Value);

        return limitedGroups
            .Select(group => (IReadOnlyList<PlaylistChannel>)group.Channels)
            .ToList();
    }

    private int GetDisplayCountForSection(BrowseSection section, IReadOnlyList<PlaylistChannel> channels)
    {
        return channels.Count;
    }

    private List<CategorySummary> ApplyCategoryDisplayPreferences(
        BrowseSection section,
        IEnumerable<CategorySummary> categories,
        bool includeHidden)
    {
        var preferenceMap = GetCategoryPreferenceMap(section);

        return categories
            .Where(category => includeHidden
                || !preferenceMap.TryGetValue(category.Key, out var preference)
                || preference.IsVisible)
            .OrderBy(category => preferenceMap.TryGetValue(category.Key, out var preference)
                ? preference.SortOrder
                : int.MaxValue)
            .ThenByDescending(category => category.Count)
            .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<string, CategoryDisplayPreference> GetCategoryPreferenceMap(BrowseSection section)
    {
        return _categoryDisplayPreferences
            .Where(preference => preference.Section == section)
            .GroupBy(preference => preference.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(preference => preference.SortOrder).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private void NotifyCatalogChanged()
    {
        if (ShowDashboard)
        {
            OnPropertyChanged(nameof(LiveCardSummary));
            OnPropertyChanged(nameof(MoviesCardSummary));
            OnPropertyChanged(nameof(SeriesCardSummary));
            OnPropertyChanged(nameof(DashboardHintText));
        }
        OnPropertyChanged(nameof(PlaylistCardText));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(BrowseHintText));
        OnPropertyChanged(nameof(CategoryManagerHintText));
        OnPropertyChanged(nameof(PlaylistEditorCategoryManagerHintText));
        OnPropertyChanged(nameof(SeriesDetailSubtitle));
    }

    private void ApplyFilters()
    {
        _ = ApplyFiltersAsync();
    }

    private async Task ShowInteractionLoadingAsync(string statusText)
    {
        StatusText = statusText;
        IsApplyingFilters = true;
        await Task.Yield();
    }

    private async Task ApplyFiltersAsync()
    {
        _applyFiltersCancellationTokenSource?.Cancel();
        _applyFiltersCancellationTokenSource?.Dispose();

        var applyFiltersCancellationTokenSource = new CancellationTokenSource();
        _applyFiltersCancellationTokenSource = applyFiltersCancellationTokenSource;

        if (_selectedBrowseSection is null)
        {
            ResetIncrementalVisibleBuffers();
            VisibleChannels = [];
            ResetSeriesNavigation(clearGroups: true);
            EmptyStateText = "Välj Live, Film eller Serier för att fortsätta.";
            IsApplyingFilters = false;
            return;
        }

        var section = _selectedBrowseSection.Value;
        var categoryKey = _selectedCategoryKey;
        if (string.IsNullOrWhiteSpace(categoryKey))
        {
            ResetIncrementalVisibleBuffers();
            VisibleChannels = [];
            ResetSeriesNavigation(clearGroups: true);
            EmptyStateText = GetEmptyStateText();
            OnPropertyChanged(nameof(HeaderSubtitle));
            OnPropertyChanged(nameof(BrowseHintText));
            IsApplyingFilters = false;
            return;
        }

        var searchText = SearchText;
        var favoritesOnly = FavoritesOnly;
        var preferredSeriesGroupKey = _selectedSeriesGroup?.Key;
        var preferredSeriesSeasonKey = _selectedSeriesSeason?.Key;

        IsApplyingFilters = true;
        try
        {
            var filterResult = await Task.Run(
                () => BuildFilterResult(
                    section,
                    categoryKey,
                    favoritesOnly,
                    searchText,
                    preferredSeriesGroupKey,
                    preferredSeriesSeasonKey,
                    applyFiltersCancellationTokenSource.Token),
                applyFiltersCancellationTokenSource.Token);

            if (applyFiltersCancellationTokenSource.IsCancellationRequested
                || _selectedBrowseSection != section
                || !string.Equals(_selectedCategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase)
                || FavoritesOnly != favoritesOnly
                || !string.Equals(SearchText, searchText, StringComparison.Ordinal))
            {
                return;
            }

            if (section == BrowseSection.Series)
            {
                _selectedCategoryVisibleChannels = [];
                _visibleChannelRenderCount = 0;
                VisibleChannels = [];
                ApplySeriesFilterResult(
                    filterResult.SeriesGroups,
                    filterResult.PreferredSeriesGroupKey,
                    filterResult.PreferredSeriesSeasonKey);
            }
            else
            {
                _selectedCategoryVisibleSeriesGroups = [];
                _visibleSeriesGroupRenderCount = 0;
                VisibleSeriesGroups = [];
                ApplyVisibleChannelFilterResult(filterResult.VisibleChannels);
                ResetSeriesNavigation(clearGroups: true);
            }

            EmptyStateText = GetEmptyStateText();
            OnPropertyChanged(nameof(HeaderSubtitle));
            OnPropertyChanged(nameof(BrowseHintText));
        }
        catch (OperationCanceledException)
        {
            // Ignore stale filter requests when the user keeps navigating.
        }
        finally
        {
            if (ReferenceEquals(_applyFiltersCancellationTokenSource, applyFiltersCancellationTokenSource))
            {
                _applyFiltersCancellationTokenSource = null;
                IsApplyingFilters = false;
            }
        }
    }

    private FilterResult BuildFilterResult(
        BrowseSection section,
        string categoryKey,
        bool favoritesOnly,
        string searchText,
        string? preferredSeriesGroupKey,
        string? preferredSeriesSeasonKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<PlaylistChannel> sourceChannels = categoryKey switch
        {
            BrowseCategoryChip.FavoritesKey => GetFavoriteChannelsForSection(section),
            BrowseCategoryChip.LatestKey => GetLatestAddedChannelsForSection(section),
            BrowseCategoryChip.RecentKey => GetRecentChannelsForSection(section),
            _ => GetChannelsForCategory(section, categoryKey),
        };

        IEnumerable<PlaylistChannel> query = sourceChannels;
        if (favoritesOnly)
        {
            query = query.Where(channel => channel.IsFavorite);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (section == BrowseSection.Series)
        {
            var baseChannels = SortChannelsForSection(section, query);
            var groups = GetOrBuildSeriesGroups(baseChannels, categoryKey, favoritesOnly, cancellationToken);
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var search = searchText.Trim();
                groups = groups
                    .Where(group => MatchesSeriesSearchQuery(group, search))
                    .ToList();
            }

            return new FilterResult(section, [], groups, preferredSeriesGroupKey, preferredSeriesSeasonKey);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var search = searchText.Trim();
            query = query.Where(channel =>
                Contains(channel.DisplayName, search)
                || channel.MetadataChips.Any(chip => Contains(chip, search))
                || Contains(channel.Group, search)
                || Contains(channel.CategoryName, search));
        }

        var visibleChannels = string.Equals(categoryKey, BrowseCategoryChip.RecentKey, StringComparison.Ordinal)
            ? query.ToList()
            : SortChannelsForSection(section, query);

        return new FilterResult(section, visibleChannels, [], preferredSeriesGroupKey, preferredSeriesSeasonKey);
    }

    private void ApplySeriesFilterResult(
        IReadOnlyList<SeriesGroupItem> groups,
        string? preferredSeriesGroupKey,
        string? preferredSeriesSeasonKey)
    {
        RestartArtworkLoadingScope();
        _selectedCategoryVisibleSeriesGroups = groups.ToList();
        _visibleSeriesGroupRenderCount = 0;
        VisibleSeriesGroups = new ObservableCollection<SeriesGroupItem>();
        AppendVisibleSeriesGroupBatch(initialLoad: true);

        if (string.IsNullOrWhiteSpace(preferredSeriesGroupKey))
        {
            SetSelectedSeriesGroup(null);
            return;
        }

        var selectedGroup = groups.FirstOrDefault(group =>
            string.Equals(group.Key, preferredSeriesGroupKey, StringComparison.OrdinalIgnoreCase));
        if (selectedGroup is null)
        {
            SetSelectedSeriesGroup(null);
            return;
        }

        SetSelectedSeriesGroup(selectedGroup, preferredSeriesSeasonKey);
    }

    private void ApplyVisibleChannelFilterResult(IReadOnlyList<PlaylistChannel> channels)
    {
        RestartArtworkLoadingScope();
        _selectedCategoryVisibleChannels = channels.ToList();
        _visibleChannelRenderCount = 0;
        VisibleChannels = new ObservableCollection<PlaylistChannel>();
        AppendVisibleChannelBatch(initialLoad: true);
    }

    private void AppendVisibleChannelBatch(bool initialLoad = false)
    {
        if (_isAppendingVisibleChannels || _selectedCategoryVisibleChannels.Count == 0)
        {
            return;
        }

        var visibleCollection = EnsureVisibleChannelCollection();
        if (_visibleChannelRenderCount >= _selectedCategoryVisibleChannels.Count)
        {
            return;
        }

        _isAppendingVisibleChannels = true;
        try
        {
            var batchSize = initialLoad ? InitialVisibleChannelBatchSize : IncrementalVisibleChannelBatchSize;
            var nextBatch = _selectedCategoryVisibleChannels
                .Skip(_visibleChannelRenderCount)
                .Take(batchSize)
                .ToArray();

            foreach (var channel in nextBatch)
            {
                visibleCollection.Add(channel);
            }

            _visibleChannelRenderCount += nextBatch.Length;
            QueueArtworkLoading(nextBatch);
        }
        finally
        {
            _isAppendingVisibleChannels = false;
        }
    }

    private void AppendVisibleSeriesGroupBatch(bool initialLoad = false)
    {
        if (_isAppendingVisibleSeriesGroups || _selectedCategoryVisibleSeriesGroups.Count == 0)
        {
            return;
        }

        var visibleCollection = EnsureVisibleSeriesGroupCollection();
        if (_visibleSeriesGroupRenderCount >= _selectedCategoryVisibleSeriesGroups.Count)
        {
            return;
        }

        _isAppendingVisibleSeriesGroups = true;
        try
        {
            var batchSize = initialLoad ? InitialVisibleSeriesBatchSize : IncrementalVisibleSeriesBatchSize;
            var nextBatch = _selectedCategoryVisibleSeriesGroups
                .Skip(_visibleSeriesGroupRenderCount)
                .Take(batchSize)
                .ToArray();

            foreach (var group in nextBatch)
            {
                visibleCollection.Add(group);
            }

            _visibleSeriesGroupRenderCount += nextBatch.Length;
            QueueArtworkLoading(nextBatch.Select(group => group.RepresentativeChannel));
        }
        finally
        {
            _isAppendingVisibleSeriesGroups = false;
        }
    }

    private void RestartArtworkLoadingScope()
    {
        _artworkLoadCancellationTokenSource?.Cancel();
        _artworkLoadCancellationTokenSource?.Dispose();
        _artworkLoadCancellationTokenSource = new CancellationTokenSource();
    }

    private void QueueArtworkLoading(IEnumerable<PlaylistChannel> channels)
    {
        var candidates = channels
            .Where(channel =>
                !string.IsNullOrWhiteSpace(channel.LogoUrl)
                && string.IsNullOrWhiteSpace(channel.ArtworkPath))
            .GroupBy(
                channel => channel.LogoUrl ?? channel.Url,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        if (_artworkLoadCancellationTokenSource is null)
        {
            RestartArtworkLoadingScope();
        }

        var cancellationToken = _artworkLoadCancellationTokenSource?.Token ?? CancellationToken.None;
        _ = LoadArtworkPathsAsync(candidates, cancellationToken);
    }

    private async Task LoadArtworkPathsAsync(
        IReadOnlyList<PlaylistChannel> channels,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(channel.LogoUrl)
                    || !string.IsNullOrWhiteSpace(channel.ArtworkPath))
                {
                    continue;
                }

                var artworkPath = await _posterImageCacheService.LoadPathAsync(channel.LogoUrl, cancellationToken);
                if (string.IsNullOrWhiteSpace(artworkPath))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        channel.ArtworkPath = artworkPath;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore stale artwork work when the user changes section, category, or search.
        }
    }

    private ObservableCollection<PlaylistChannel> EnsureVisibleChannelCollection()
    {
        if (_visibleChannels is ObservableCollection<PlaylistChannel> existingCollection)
        {
            return existingCollection;
        }

        var collection = new ObservableCollection<PlaylistChannel>();
        VisibleChannels = collection;
        return collection;
    }

    private ObservableCollection<SeriesGroupItem> EnsureVisibleSeriesGroupCollection()
    {
        if (_visibleSeriesGroups is ObservableCollection<SeriesGroupItem> existingCollection)
        {
            return existingCollection;
        }

        var collection = new ObservableCollection<SeriesGroupItem>();
        VisibleSeriesGroups = collection;
        return collection;
    }

    private void ResetIncrementalVisibleBuffers()
    {
        _artworkLoadCancellationTokenSource?.Cancel();
        _artworkLoadCancellationTokenSource?.Dispose();
        _artworkLoadCancellationTokenSource = null;
        _selectedCategoryVisibleChannels = [];
        _selectedCategoryVisibleSeriesGroups = [];
        _visibleChannelRenderCount = 0;
        _visibleSeriesGroupRenderCount = 0;
        _isAppendingVisibleChannels = false;
        _isAppendingVisibleSeriesGroups = false;
    }

    private IReadOnlyList<SeriesGroupItem> GetOrBuildSeriesGroups(
        IReadOnlyList<PlaylistChannel> channels,
        string categoryKey,
        bool favoritesOnly,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{categoryKey}::{favoritesOnly}";

        lock (_seriesGroupCacheSync)
        {
            if (_seriesGroupCache.TryGetValue(cacheKey, out var cachedGroups))
            {
                return cachedGroups;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = _seriesCatalogService.BuildGroups(channels);

        lock (_seriesGroupCacheSync)
        {
            _seriesGroupCache[cacheKey] = groups;
        }

        return groups;
    }

    private void SelectSeriesGroup(SeriesGroupItem group)
    {
        ClearSelectedMovie();
        SetSelectedSeriesGroup(group);
    }

    private void SelectMovieChannel(PlaylistChannel channel)
    {
        ResetSeriesNavigation(clearGroups: false);
        _selectedMovieChannel = channel;
        _selectedMovieDetail = null;
        StatusText = $"Visar {channel.DisplayName}.";
        RestartArtworkLoadingScope();
        QueueArtworkLoading([channel]);
        NotifyMovieDetailChanged();
        StartMovieDetailLoad(channel);
    }

    private void ClearSelectedMovie()
    {
        _movieDetailCancellationTokenSource?.Cancel();
        _movieDetailCancellationTokenSource?.Dispose();
        _movieDetailCancellationTokenSource = null;

        if (_selectedMovieChannel is null && _selectedMovieDetail is null && !IsMovieDetailLoading)
        {
            return;
        }

        _selectedMovieChannel = null;
        _selectedMovieDetail = null;
        IsMovieDetailLoading = false;
        NotifyMovieDetailChanged();
    }

    private void StartMovieDetailLoad(PlaylistChannel channel)
    {
        _movieDetailCancellationTokenSource?.Cancel();
        _movieDetailCancellationTokenSource?.Dispose();

        var cancellationTokenSource = new CancellationTokenSource();
        _movieDetailCancellationTokenSource = cancellationTokenSource;
        IsMovieDetailLoading = true;

        _ = LoadMovieDetailAsync(channel, cancellationTokenSource.Token);
    }

    private async Task LoadMovieDetailAsync(PlaylistChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _xtreamApiService.TryLoadMovieDetailAsync(
                PlaylistSource.Trim(),
                channel,
                _allChannels,
                cancellationToken).ConfigureAwait(false);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || _selectedMovieChannel is null
                    || !string.Equals(_selectedMovieChannel.Url, channel.Url, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedMovieDetail = detail;
                IsMovieDetailLoading = false;
                NotifyMovieDetailChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (_selectedMovieChannel is null
                    || !string.Equals(_selectedMovieChannel.Url, channel.Url, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedMovieDetail = null;
                IsMovieDetailLoading = false;
                NotifyMovieDetailChanged();
            });
        }
    }

    private void ResetSeriesNavigation(bool clearGroups)
    {
        SetSelectedSeriesGroup(null);
        if (clearGroups)
        {
            _selectedCategoryVisibleSeriesGroups = [];
            _visibleSeriesGroupRenderCount = 0;
            _isAppendingVisibleSeriesGroups = false;
            VisibleSeriesGroups = [];
        }
    }

    private void SetSelectedSeriesGroup(SeriesGroupItem? group, string? preferredSeasonKey = null)
    {
        _selectedSeriesGroup = group;

        if (group is null)
        {
            SetSelectedSeriesSeasonCore(null);
        }
        else
        {
            var preferredSeason = !string.IsNullOrWhiteSpace(preferredSeasonKey)
                ? group.Seasons.FirstOrDefault(season => string.Equals(season.Key, preferredSeasonKey, StringComparison.OrdinalIgnoreCase))
                : null;
            SetSelectedSeriesSeasonCore(preferredSeason ?? group.Seasons.FirstOrDefault());
        }

        NotifySeriesNavigationChanged();
    }

    private void SetSelectedSeriesSeason(SeriesSeasonItem? season)
    {
        if (_selectedSeriesGroup is null)
        {
            return;
        }

        var matchingSeason = season is null
            ? null
            : _selectedSeriesGroup.Seasons.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, season.Key, StringComparison.OrdinalIgnoreCase));

        SetSelectedSeriesSeasonCore(matchingSeason ?? _selectedSeriesGroup.Seasons.FirstOrDefault());
        NotifySeriesNavigationChanged();
    }

    private void SetSelectedSeriesSeasonCore(SeriesSeasonItem? season)
    {
        if (_selectedSeriesGroup is not null)
        {
            foreach (var candidate in _selectedSeriesGroup.Seasons)
            {
                candidate.IsSelected = season is not null
                    && string.Equals(candidate.Key, season.Key, StringComparison.OrdinalIgnoreCase);
            }
        }

        _selectedSeriesSeason = season;
    }

    private void NotifySeriesNavigationChanged()
    {
        OnPropertyChanged(nameof(VisibleSeriesSeasons));
        OnPropertyChanged(nameof(CurrentSeriesEpisodes));
        OnPropertyChanged(nameof(HasSelectedSeriesGroup));
        OnPropertyChanged(nameof(ShowSeriesOverview));
        OnPropertyChanged(nameof(ShowSeriesDetail));
        OnPropertyChanged(nameof(ShowBrowserControls));
        OnPropertyChanged(nameof(SeriesDetailTitle));
        OnPropertyChanged(nameof(SeriesDetailSubtitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(BrowseHintText));
        EmptyStateText = GetEmptyStateText();
    }

    private void NotifyMovieDetailChanged()
    {
        OnPropertyChanged(nameof(SelectedMovieChannel));
        OnPropertyChanged(nameof(SelectedMovieDetail));
        NotifyMovieDetailMetadataChanged();
        OnPropertyChanged(nameof(HasSelectedMovie));
        OnPropertyChanged(nameof(ShowStandardChannelList));
        OnPropertyChanged(nameof(ShowMovieDetail));
        OnPropertyChanged(nameof(ShowBrowserControls));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(BrowseHintText));
        EmptyStateText = GetEmptyStateText();
    }

    private void NotifyMovieDetailMetadataChanged()
    {
        OnPropertyChanged(nameof(MovieDetailTitle));
        OnPropertyChanged(nameof(MovieDetailCategoryText));
        OnPropertyChanged(nameof(MovieDetailPlotText));
        OnPropertyChanged(nameof(MovieDetailGenreText));
        OnPropertyChanged(nameof(MovieDetailDurationText));
        OnPropertyChanged(nameof(MovieDetailRatingText));
        OnPropertyChanged(nameof(MovieDetailReleaseDateText));
        OnPropertyChanged(nameof(MovieDetailDirectorText));
        OnPropertyChanged(nameof(MovieDetailCastText));
    }

    private static bool MatchesSeriesSearchQuery(SeriesGroupItem group, string query)
    {
        return Contains(group.Title, query)
            || Contains(group.CategoryName, query)
            || group.Seasons.Any(season =>
                Contains(season.Label, query)
                || season.Episodes.Any(episode =>
                    Contains(episode.Title, query)
                    || Contains(episode.Subtitle, query)
                    || Contains(episode.Channel.Name, query)
                    || Contains(episode.Channel.CategoryName, query)));
    }

    private void RebuildCategoryOptions()
    {
        if (_selectedBrowseSection is null)
        {
            CategoryOptions = [];
            return;
        }

        var categorySummaries = GetCategorySummariesForSection(_selectedBrowseSection.Value, includeHidden: false);
        var specialCategories = BuildSpecialCategorySummaries(_selectedBrowseSection.Value);
        var availableCategories = specialCategories
            .Concat(categorySummaries)
            .Select(category => category.Key)
            .ToList();

        if (string.IsNullOrWhiteSpace(_selectedCategoryKey))
        {
            _selectedCategoryKey = GetDefaultCategoryKey(_selectedBrowseSection.Value, availableCategories);
        }

        if (!string.IsNullOrWhiteSpace(_selectedCategoryKey)
            && !availableCategories.Contains(_selectedCategoryKey, StringComparer.OrdinalIgnoreCase))
        {
            _selectedCategoryKey = GetDefaultCategoryKey(_selectedBrowseSection.Value, availableCategories);
        }

        var items = new List<BrowseCategoryChip>();
        items.AddRange(specialCategories.Concat(categorySummaries).Select(category =>
            new BrowseCategoryChip(
                category.Key,
                category.Label,
                category.Count,
                string.Equals(category.Key, _selectedCategoryKey, StringComparison.OrdinalIgnoreCase))));

        CategoryOptions = items;
    }

    private IEnumerable<PlaylistChannel> GetChannelsForSection(BrowseSection section, bool includeHiddenCategories = false)
    {
        if (!_sectionCatalogs.TryGetValue(section, out var sectionCatalog))
        {
            return Enumerable.Empty<PlaylistChannel>();
        }

        return includeHiddenCategories
            ? sectionCatalog.Channels
            : sectionCatalog.Channels.Where(channel => IsCategoryVisible(section, channel.CategoryName));
    }

    private IReadOnlyList<PlaylistChannel> GetChannelsForCategory(BrowseSection section, string categoryKey)
    {
        if (!_sectionCatalogs.TryGetValue(section, out var sectionCatalog))
        {
            return [];
        }

        return sectionCatalog.ChannelsByCategory.TryGetValue(categoryKey, out var channels)
            ? channels
            : [];
    }

    private static ChannelContentType GetContentTypeForSection(BrowseSection section)
    {
        return section switch
        {
            BrowseSection.Live => ChannelContentType.Live,
            BrowseSection.Movies => ChannelContentType.Movie,
            BrowseSection.Series => ChannelContentType.Series,
            _ => ChannelContentType.Live,
        };
    }

    private static bool IsSpecialCategoryKey(string? categoryKey)
    {
        return string.Equals(categoryKey, BrowseCategoryChip.FavoritesKey, StringComparison.Ordinal)
            || string.Equals(categoryKey, BrowseCategoryChip.LatestKey, StringComparison.Ordinal)
            || string.Equals(categoryKey, BrowseCategoryChip.RecentKey, StringComparison.Ordinal);
    }

    private static string GetCategoryDisplayLabel(string categoryKey)
    {
        return categoryKey switch
        {
            BrowseCategoryChip.FavoritesKey => FavoritesCategoryLabel,
            BrowseCategoryChip.LatestKey => LatestCategoryLabel,
            BrowseCategoryChip.RecentKey => RecentCategoryLabel,
            _ => categoryKey,
        };
    }

    private static bool IsCategoryMatch(PlaylistChannel channel, BrowseSection section, string categoryKey)
    {
        return IsSectionMatch(channel, section)
            && string.Equals(channel.CategoryName, categoryKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCategoryVisible(BrowseSection section, string categoryKey)
    {
        var preference = _categoryDisplayPreferences
            .Where(item => item.Section == section)
            .OrderBy(item => item.SortOrder)
            .FirstOrDefault(item => string.Equals(item.Key, categoryKey, StringComparison.OrdinalIgnoreCase));

        return preference?.IsVisible ?? true;
    }

    private static bool IsSectionMatch(PlaylistChannel channel, BrowseSection section)
    {
        return section switch
        {
            BrowseSection.Live => channel.ContentType == ChannelContentType.Live,
            BrowseSection.Movies => channel.ContentType == ChannelContentType.Movie,
            BrowseSection.Series => channel.ContentType == ChannelContentType.Series,
            _ => false,
        };
    }

    private sealed record SectionCatalog(
        IReadOnlyList<PlaylistChannel> Channels,
        IReadOnlyDictionary<string, IReadOnlyList<PlaylistChannel>> ChannelsByCategory,
        IReadOnlyList<CategorySummary> CategorySummaries);

    private sealed record SectionPreview(
        int ItemCount,
        IReadOnlyList<CategorySummary> CategorySummaries,
        IReadOnlyList<PlaylistChannel> LatestChannels);

    private sealed record PreparedCatalog(
        List<PlaylistChannel> Channels,
        Dictionary<BrowseSection, SectionCatalog> SectionCatalogs,
        Dictionary<BrowseSection, SectionPreview> Previews,
        Dictionary<BrowseSection, SectionStats> SectionStats);

    private sealed record FilterResult(
        BrowseSection Section,
        IReadOnlyList<PlaylistChannel> VisibleChannels,
        IReadOnlyList<SeriesGroupItem> SeriesGroups,
        string? PreferredSeriesGroupKey,
        string? PreferredSeriesSeasonKey);

    private sealed record SectionStats(int ItemCount, int DisplayCount, int CategoryCount);

    private sealed record CategorySummary(string Key, string Label, int Count);

    private string GetDashboardSectionSummary(BrowseSection section, string emptyLabel)
    {
        if (!HasPlaylistSource)
        {
            return "Ingen playlist vald";
        }

        var stats = GetSectionStats(section);
        var count = stats.DisplayCount;
        if (count == 0)
        {
            return IsBusyLoading || IsPreparingCatalog ? "Bygger katalog..." : emptyLabel;
        }

        if (section == BrowseSection.Series)
        {
            return count == 1 ? "1 serie" : $"{count} serier";
        }

        return count == 1 ? "1 objekt" : $"{count} objekt";
    }

    private string GetSectionSubtitle()
    {
        if (_selectedBrowseSection is null)
        {
            return "Live, Film eller Serier";
        }

        var section = _selectedBrowseSection.Value;
        var stats = GetSectionStats(section);
        var categoryCount = stats.CategoryCount;

        if (section == BrowseSection.Movies && _selectedMovieChannel is not null)
        {
            return _selectedMovieChannel.CategoryName;
        }

        if (section == BrowseSection.Series)
        {
            if (_selectedSeriesGroup is not null)
            {
                return _selectedSeriesSeason is null
                    ? _selectedSeriesGroup.SummaryText
                    : $"{_selectedSeriesGroup.Title} - {_selectedSeriesSeason.Label}";
            }

            var seriesCount = stats.DisplayCount;
            return seriesCount == 1
                ? $"1 serie - {categoryCount} kategorier"
                : $"{seriesCount} serier - {categoryCount} kategorier";
        }

        return $"{stats.ItemCount} objekt - {categoryCount} kategorier";
    }

    private async Task OpenChannelAsync(PlaylistChannel channel)
    {
        if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var streamUri))
        {
            StatusText = "Kanalens streamadress är ogiltig.";
            await DisplayAlert("Ogiltig adress", "Kanalens streamadress är ogiltig.", "OK");
            return;
        }

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            var message = GetNoInternetPlaybackMessage();
            StatusText = "Ingen internetanslutning på telefonen.";
            await DisplayAlert("Ingen nätverksanslutning", message, "OK");
            return;
        }

        if (EmbeddedMediaElement is not null)
        {
            try
            {
                LogPlaybackDiagnostic("Starting embedded playback", channel, streamUri);
                _pendingEmbeddedPlaybackChannel = channel;
                CanSelectEmbeddedSubtitles = false;
                EmbeddedPlayerTitle = channel.DisplayName;
                EmbeddedPlayerSubtitle = string.IsNullOrWhiteSpace(channel.CategoryName)
                    ? GetSectionLabel(_selectedBrowseSection)
                    : $"{GetSectionLabel(_selectedBrowseSection)} - {channel.CategoryName}";
                EmbeddedPlayerStatusText = $"Öppnar {channel.DisplayName}...";
                IsEmbeddedPlayerLoading = true;
                ShowEmbeddedPlayer = true;

                EmbeddedMediaElement.Stop();
                EmbeddedMediaElement.Source = null;
                EmbeddedMediaElement.MetadataTitle = channel.DisplayName;
                EmbeddedMediaElement.MetadataArtist = channel.CategoryName;
                EmbeddedMediaElement.Source = streamUri;
                EmbeddedMediaElement.Play();

                StatusText = $"Öppnar {channel.DisplayName} i spelaren.";
                return;
            }
            catch (Exception ex)
            {
                LogPlaybackDiagnostic("Embedded playback startup failed", channel, streamUri, ex);
                _pendingEmbeddedPlaybackChannel = null;
                IsEmbeddedPlayerLoading = false;
                ShowEmbeddedPlayer = false;
                StatusText = "Den inbyggda spelaren kunde inte startas.";
                await DisplayAlert("Uppspelning misslyckades", ex.Message, "OK");
                return;
            }
        }

        if (await TryOpenStreamExternallyAsync(streamUri))
        {
            await TrackRecentPlaybackAsync(channel);
            StatusText = $"Öppnar {channel.DisplayName} externt.";
            return;
        }

        StatusText = $"Kunde inte öppna {channel.DisplayName}.";
        await DisplayAlert("Uppspelning misslyckades", "Kunde inte öppna streamen i extern spelare eller webbläsare.", "OK");
    }

    private static async Task<bool> TryOpenStreamExternallyAsync(Uri streamUri)
    {
        try
        {
            if (await Launcher.Default.OpenAsync(streamUri))
            {
                return true;
            }
        }
        catch
        {
            // Vissa Android-enheter rapporterar fel här trots att en ACTION_VIEW-intent fungerar.
        }

#if ANDROID
        return TryOpenStreamWithAndroidIntent(streamUri);
#else
        return false;
#endif
    }

#if ANDROID
    private static bool TryOpenStreamWithAndroidIntent(Uri streamUri)
    {
        var androidUri = AndroidUri.Parse(streamUri.AbsoluteUri);

        foreach (var mimeType in GetAndroidStreamMimeTypes(streamUri))
        {
            var viewIntent = new Intent(Intent.ActionView);
            if (mimeType is null)
            {
                viewIntent.SetData(androidUri);
            }
            else
            {
                viewIntent.SetDataAndType(androidUri, mimeType);
            }

            viewIntent.AddFlags(ActivityFlags.NewTask);

            var chooserIntent = Intent.CreateChooser(viewIntent, "Öppna stream");
            if (chooserIntent is null)
            {
                continue;
            }

            chooserIntent.AddFlags(ActivityFlags.NewTask);

            try
            {
                AndroidApplication.Context.StartActivity(chooserIntent);
                return true;
            }
            catch
            {
                // Prova nästa MIME-typ innan vi ger upp.
            }
        }

        return false;
    }

    private static IEnumerable<string?> GetAndroidStreamMimeTypes(Uri streamUri)
    {
        var extension = Path.GetExtension(streamUri.AbsolutePath).ToLowerInvariant();

        yield return extension switch
        {
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".ts" => "video/mp2t",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            _ => "video/*",
        };
        yield return "application/x-mpegURL";
        yield return "application/vnd.apple.mpegurl";
        yield return "video/*";
        yield return null;
    }
#endif

    private async void OnEmbeddedMediaOpened(object? sender, EventArgs e)
    {
        IsEmbeddedPlayerLoading = false;
        ConfigureEmbeddedSubtitleControls();
        EmbeddedPlayerStatusText = "Uppspelningen är igång.";
        StatusText = "Uppspelning startad.";

        if (_pendingEmbeddedPlaybackChannel is { } channel)
        {
            LogPlaybackDiagnostic("Embedded playback opened", channel, TryCreateChannelUri(channel));
            _pendingEmbeddedPlaybackChannel = null;
            await TrackRecentPlaybackAsync(channel);
        }
    }

    private void OnEmbeddedMediaEnded(object? sender, EventArgs e)
    {
        IsEmbeddedPlayerLoading = false;
        EmbeddedPlayerStatusText = "Uppspelningen är klar.";
        StatusText = "Uppspelningen är klar.";
    }

    private async void OnEmbeddedMediaFailed(object? sender, MediaFailedEventArgs e)
    {
        var failedChannel = _pendingEmbeddedPlaybackChannel;
        var failedStreamUri = TryCreateChannelUri(failedChannel);
        LogPlaybackDiagnostic("Embedded playback failed", failedChannel, failedStreamUri, rawError: e.ErrorMessage);

        _pendingEmbeddedPlaybackChannel = null;
        IsEmbeddedPlayerLoading = false;
        CanSelectEmbeddedSubtitles = false;

        var message = GetFriendlyPlaybackErrorMessage(e.ErrorMessage);

        EmbeddedPlayerStatusText = message;
        StatusText = "Uppspelning misslyckades.";

        if (failedChannel is not null && failedStreamUri is not null && await TryOpenStreamExternallyAsync(failedStreamUri))
        {
            ShowEmbeddedPlayer = false;
            EmbeddedPlayerStatusText = "Den inbyggda spelaren kunde inte starta streamen. Öppnar externt.";
            StatusText = $"Öppnar {failedChannel.Name} i extern spelare.";
            await TrackRecentPlaybackAsync(failedChannel);
            return;
        }

        await DisplayAlert("Uppspelning misslyckades", message, "OK");
    }

    private void StopEmbeddedPlayback()
    {
        _pendingEmbeddedPlaybackChannel = null;
        IsEmbeddedPlayerLoading = false;
        CanSelectEmbeddedSubtitles = false;
        ShowEmbeddedPlayer = false;
        EmbeddedPlayerStatusText = "Uppspelningen stoppades.";

        if (EmbeddedMediaElement is null)
        {
            return;
        }

        try
        {
            EmbeddedMediaElement.Stop();
            EmbeddedMediaElement.Source = null;
        }
        catch
        {
            // We still want the player overlay to close even if the native player is in a bad state.
        }
    }

    private string GetEmptyStateText()
    {
        if (_selectedBrowseSection is null)
        {
            return "Välj Live, Film eller Serier för att fortsätta.";
        }

        if (string.IsNullOrWhiteSpace(_selectedCategoryKey))
        {
            return IsBusyLoading || IsPreparingCatalog
                ? "Laddar kategorier..."
                : "Välj en kategori för att fortsätta.";
        }

        var sectionCount = GetChannelsForSection(_selectedBrowseSection.Value).Count();
        if (sectionCount == 0)
        {
            return IsBusyLoading || IsPreparingCatalog || IsApplyingFilters
                ? "Laddar innehåll för vald sektion..."
                : "Inga objekt finns i den här sektionen än.";
        }

        if (_selectedBrowseSection == BrowseSection.Series && _selectedSeriesGroup is not null)
        {
            return CurrentSeriesEpisodes.Count == 0
                ? "Inga avsnitt hittades i den här serien."
                : "Välj ett avsnitt för att fortsätta.";
        }

        if (FavoritesOnly || !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(_selectedCategoryKey))
        {
            return _selectedBrowseSection == BrowseSection.Series
                ? "Inga serier matchar filtret."
                : "Inga objekt matchar filtret.";
        }

        return "Sektionen innehaller inga objekt.";
    }

    private string GetShortPlaylistDisplayName()
    {
        var source = PlaylistSource.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return Path.GetFileName(source);
    }

    private async Task TrackRecentPlaybackAsync(PlaylistChannel channel)
    {
        if (_selectedBrowseSection is not { } section)
        {
            return;
        }

        _recentPlaybackEntries.RemoveAll(entry =>
            entry.Section == section
            && string.Equals(entry.ChannelUrl, channel.Url, StringComparison.OrdinalIgnoreCase));
        _recentPlaybackEntries.Insert(0, new RecentPlaybackEntry(section, channel.Url, DateTimeOffset.UtcNow));
        InvalidateSeriesGroupCache();

        try
        {
            await _recentPlaybackStore.SaveAsync(_recentPlaybackEntries);
        }
        catch
        {
            // Playback should still continue even if the recent-playback file can not be updated.
        }

        if (string.Equals(_selectedCategoryKey, BrowseCategoryChip.RecentKey, StringComparison.Ordinal))
        {
            RefreshCategoryPresentation();
        }
        else
        {
            RebuildCategoryOptions();
            NotifyCatalogChanged();
        }
    }

    private static string GetFriendlyPlaybackErrorMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "Spelaren kunde inte starta streamen.";
        }

        if (rawMessage.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("no network", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("Unable to resolve host", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("No address associated with hostname", StringComparison.OrdinalIgnoreCase))
        {
            return GetNoInternetPlaybackMessage();
        }

        return rawMessage;
    }

    private static Uri? TryCreateChannelUri(PlaylistChannel? channel) =>
        channel is not null && Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri)
            ? uri
            : null;

    private static void LogPlaybackDiagnostic(
        string message,
        PlaylistChannel? channel = null,
        Uri? streamUri = null,
        Exception? exception = null,
        string? rawError = null)
    {
#if ANDROID
        var channelName = string.IsNullOrWhiteSpace(channel?.Name) ? "(unknown)" : channel.Name;
        var categoryName = string.IsNullOrWhiteSpace(channel?.CategoryName) ? "(none)" : channel.CategoryName;
        var source = streamUri is null ? "(none)" : GetSafePlaybackUriForLog(streamUri);
        var detail = $"{message}; channel={channelName}; category={categoryName}; source={source}";

        if (!string.IsNullOrWhiteSpace(rawError))
        {
            detail += $"; rawError={rawError}";
        }

        if (exception is null)
        {
            global::Android.Util.Log.Info(PlaybackLogTag, detail);
        }
        else
        {
            global::Android.Util.Log.Error(PlaybackLogTag, detail, Java.Lang.Throwable.FromException(exception));
        }
#endif
    }

    private static string GetSafePlaybackUriForLog(Uri uri)
    {
        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        var pathHint = string.IsNullOrWhiteSpace(lastSegment) ? string.Empty : $"/.../{lastSegment}";
        var queryHint = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?query";
        return $"{uri.Scheme}://{uri.Host}{pathHint}{queryHint}";
    }

    private static string GetNoInternetPlaybackMessage()
    {
        return "Telefonen saknar fungerande internetanslutning just nu, eller så når den inte streamvärden via DNS/VPN. Spellistan kan visas från cache, men uppspelning kräver nätanslutning.";
    }

    private static string GetSectionLabel(BrowseSection? section)
    {
        return section switch
        {
            BrowseSection.Live => "Live",
            BrowseSection.Movies => "Film",
            BrowseSection.Series => "Serier",
            _ => "PoePerfect Player",
        };
    }

    private void NotifySectionStateChanged()
    {
        UpdatePlaylistEditorVisibility();
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowBrowserShell));
        OnPropertyChanged(nameof(IsBrowserBusy));
        OnPropertyChanged(nameof(ShowCategoryBusyIndicator));
        OnPropertyChanged(nameof(ShowContentLoadingOverlay));
        OnPropertyChanged(nameof(IsSeriesSection));
        OnPropertyChanged(nameof(HasSelectedMovie));
        OnPropertyChanged(nameof(ShowStandardChannelList));
        OnPropertyChanged(nameof(ShowMovieDetail));
        OnPropertyChanged(nameof(ShowBrowserControls));
        OnPropertyChanged(nameof(ShowSeriesOverview));
        OnPropertyChanged(nameof(ShowSeriesDetail));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(BrowseHintText));
        OnPropertyChanged(nameof(SeriesDetailTitle));
        OnPropertyChanged(nameof(SeriesDetailSubtitle));

        if (ShowDashboard)
        {
            OnPropertyChanged(nameof(LiveCardSummary));
            OnPropertyChanged(nameof(MoviesCardSummary));
            OnPropertyChanged(nameof(SeriesCardSummary));
            OnPropertyChanged(nameof(DashboardHintText));
        }
    }

    private void UpdatePlaylistEditorVisibility()
    {
        if (DashboardPlaylistEditor is not null)
        {
            DashboardPlaylistEditor.IsVisible = _isPlaylistEditorVisible && ShowDashboard;
        }

        if (BrowserPlaylistEditor is not null)
        {
            BrowserPlaylistEditor.IsVisible = _isPlaylistEditorVisible && ShowBrowserShell;
        }
    }

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMovieDetailValue(string? value, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? "Saknas" : fallback.Trim();
    }

    private static bool TryGetBrowseSection(object? sender, out BrowseSection section)
    {
        section = default;

        var parameter = sender switch
        {
            TapGestureRecognizer { CommandParameter: string value } => value,
            Button { CommandParameter: string value } => value,
            _ => null,
        };

        return parameter is not null
            && Enum.TryParse(parameter, ignoreCase: true, out section);
    }

    private static string GetFriendlyLoadErrorMessage(string source, Exception ex)
    {
        if (ex is HttpRequestException)
        {
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "Kunde inte ansluta till spellistan via HTTP. Kontrollera länken och prova igen. Om servern svarar långsamt kan du också testa igen om en stund.";
            }

            return "Kunde inte ansluta till spellistan. Kontrollera länken, nätverket och eventuella inloggningsuppgifter och prova igen.";
        }

        return ex.Message;
    }

#if ANDROID
    private void OnEmbeddedMediaElementHandlerChanged(object? sender, EventArgs e)
    {
        ConfigureEmbeddedSubtitleControls();
    }

    private bool ConfigureEmbeddedSubtitleControls()
    {
        var playerView = GetEmbeddedStyledPlayerView();
        if (playerView is null)
        {
            CanSelectEmbeddedSubtitles = false;
            return false;
        }

        try
        {
            playerView.UseController = true;
            playerView.SetShowSubtitleButton(true);
            playerView.SetShowBuffering(1);

            var subtitleView = playerView.SubtitleView;
            subtitleView?.SetApplyEmbeddedStyles(true);
            subtitleView?.SetApplyEmbeddedFontSizes(true);
            subtitleView?.SetUserDefaultStyle();
            subtitleView?.SetUserDefaultTextSize();

            CanSelectEmbeddedSubtitles = FindEmbeddedSubtitleButton(playerView) is not null;
            return CanSelectEmbeddedSubtitles;
        }
        catch
        {
            CanSelectEmbeddedSubtitles = false;
            return false;
        }
    }

    private bool TryShowEmbeddedSubtitleDialog()
    {
        var playerView = GetEmbeddedStyledPlayerView();
        if (playerView is null)
        {
            CanSelectEmbeddedSubtitles = false;
            return false;
        }

        playerView.SetShowSubtitleButton(true);
        playerView.ShowController();

        var subtitleButton = FindEmbeddedSubtitleButton(playerView);
        if (subtitleButton is null)
        {
            CanSelectEmbeddedSubtitles = false;
            return false;
        }

        CanSelectEmbeddedSubtitles = true;
        return subtitleButton.PerformClick();
    }

#pragma warning disable CS0618
    private StyledPlayerView? GetEmbeddedStyledPlayerView()
    {
        if (EmbeddedMediaElement?.Handler is not MediaElementHandler handler)
        {
            return null;
        }

        var mediaManagerField = typeof(MediaElementHandler).GetField("mediaManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var mediaManager = mediaManagerField?.GetValue(handler) as MediaManager;
        var playerViewProperty = typeof(MediaManager).GetProperty("PlayerView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return playerViewProperty?.GetValue(mediaManager) as StyledPlayerView;
    }

    private static AndroidView? FindEmbeddedSubtitleButton(StyledPlayerView playerView) =>
        playerView.FindViewById(global::PoePerfect.Player.Android.Resource.Id.exo_subtitle);
#pragma warning restore CS0618
#endif
}
