using System.Collections.ObjectModel;
using PoePerfect.Player.Core.Models;
using PoePerfect.Player.Core.Services;
using AView = Android.Views.View;

namespace PoePerfect.Player.Android;

public partial class PlaylistEditorPage : ContentPage
{
    private readonly Dictionary<BrowseSection, ObservableCollection<PlaylistCategoryManagerItem>> _draftCategoryItems = [];
    private readonly Dictionary<BrowseSection, string> _savedSectionSignatures = [];
    private readonly Dictionary<BrowseSection, int> _cachedCategoryCounts = [];
    private readonly MainPage _owner;
    private readonly PlaylistCacheStore _playlistCacheStore;
    private PlaylistCategoryManagerItem? _draggedCategoryItem;
    private double _dragAutoScrollOffsetY;
    private double _dragCollectionBottomOnScreen;
    private double _dragCurrentTotalY;
    private double _dragPointerRawY;
    private double _dragCollectionTopOnScreen;
    private string _dragPreviewCountLabel = string.Empty;
    private string _dragPreviewLabel = string.Empty;
    private double _dragPreviewY;
    private int _dragStartIndex = -1;
    private int _dragTargetIndex = -1;
    private double _dragTouchOffsetWithinRow;
    private bool _isAutoScrollLoopRunning;
    private ObservableCollection<PlaylistCategoryManagerItem> _categoryItems = [];
    private string _busyMessage = "Jobbar...";
    private string _cacheStatusText = "Ingen lokal cache hittad än.";
    private string _categorySummaryText = "Lägg in en M3U-länk för att börja.";
    private string _draftPlaylistSource = string.Empty;
    private string _draftXmlTvSource = string.Empty;
    private bool _hasLoaded;
    private bool _hasPendingChanges;
    private bool _isBusyOverlayVisible;
    private bool _isCategoryEditorVisible;
    private bool _isInitializing;
    private bool _isUpdatingCategoryItems;
    private string _savedPlaylistSource = string.Empty;
    private string _savedXmlTvSource = string.Empty;
    private BrowseSection _selectedSection = BrowseSection.Live;
    private string _statusText = "Lägg in en M3U-länk och ladda spellistan för att hämta kategorier.";

    public PlaylistEditorPage(MainPage owner)
    {
        InitializeComponent();
        _owner = owner;
        _playlistCacheStore = new PlaylistCacheStore(Path.Combine(FileSystem.AppDataDirectory, "playlist-cache"));
        BindingContext = this;
    }

    public string DraftPlaylistSource
    {
        get => _draftPlaylistSource;
        set
        {
            if (_draftPlaylistSource == value)
            {
                return;
            }

            _draftPlaylistSource = value;
            OnPropertyChanged();
            HandleSourceDraftChanged();
        }
    }

    public string DraftXmlTvSource
    {
        get => _draftXmlTvSource;
        set
        {
            if (_draftXmlTvSource == value)
            {
                return;
            }

            _draftXmlTvSource = value;
            OnPropertyChanged();
            HandleSourceDraftChanged();
        }
    }

    public ObservableCollection<PlaylistCategoryManagerItem> CategoryItems
    {
        get => _categoryItems;
        private set
        {
            if (ReferenceEquals(_categoryItems, value))
            {
                return;
            }

            _categoryItems = value;
            if (_draggedCategoryItem is not null && !_categoryItems.Contains(_draggedCategoryItem))
            {
                CancelCategoryDrag(resetStatus: false);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCategoryItems));
            OnPropertyChanged(nameof(HasNoCategoryItems));
            UpdateCategorySummary();
            UpdatePendingChanges();
        }
    }

    public bool IsBusyOverlayVisible
    {
        get => _isBusyOverlayVisible;
        private set
        {
            if (_isBusyOverlayVisible == value)
            {
                return;
            }

            _isBusyOverlayVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanLoadPlaylist));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanOpenCategoryEditor));
            OnPropertyChanged(nameof(CanPickFile));
            OnPropertyChanged(nameof(PendingStateText));
        }
    }

    public bool IsCategoryEditorVisible
    {
        get => _isCategoryEditorVisible;
        private set
        {
            if (_isCategoryEditorVisible == value)
            {
                return;
            }

            _isCategoryEditorVisible = value;
            OnPropertyChanged();
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set
        {
            if (_busyMessage == value)
            {
                return;
            }

            _busyMessage = value;
            OnPropertyChanged();
        }
    }

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

    public string ActiveSourceText => string.IsNullOrWhiteSpace(NormalizeValue(DraftPlaylistSource))
        ? "Ingen aktiv playlist vald."
        : $"Aktiv playlist: {GetShortSourceDisplayName(DraftPlaylistSource)}";

    public string CacheStatusText
    {
        get => _cacheStatusText;
        private set
        {
            if (_cacheStatusText == value)
            {
                return;
            }

            _cacheStatusText = value;
            OnPropertyChanged();
        }
    }

    public string CategorySummaryText
    {
        get => _categorySummaryText;
        private set
        {
            if (_categorySummaryText == value)
            {
                return;
            }

            _categorySummaryText = value;
            OnPropertyChanged();
        }
    }

    public string CategoryEditorHintText => _draggedCategoryItem is null
        ? "Håll länge på ☰ och dra raden till rätt plats. Ändringarna sparas först när du trycker Spara."
        : $"Flyttar {_draggedCategoryItem.Label}. Släpp raden där du vill placera den.";

    public bool HasCategoryItems => CategoryItems.Count > 0;

    public bool HasNoCategoryItems => !HasCategoryItems;

    public bool IsDragPreviewVisible => _draggedCategoryItem is not null;

    public double DragPreviewY
    {
        get => _dragPreviewY;
        private set
        {
            if (Math.Abs(_dragPreviewY - value) < 0.01)
            {
                return;
            }

            _dragPreviewY = value;
            OnPropertyChanged();
        }
    }

    public string DragPreviewLabel
    {
        get => _dragPreviewLabel;
        private set
        {
            if (_dragPreviewLabel == value)
            {
                return;
            }

            _dragPreviewLabel = value;
            OnPropertyChanged();
        }
    }

    public string DragPreviewCountLabel
    {
        get => _dragPreviewCountLabel;
        private set
        {
            if (_dragPreviewCountLabel == value)
            {
                return;
            }

            _dragPreviewCountLabel = value;
            OnPropertyChanged();
        }
    }

    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set
        {
            if (_hasPendingChanges == value)
            {
                return;
            }

            _hasPendingChanges = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(PendingStateText));
        }
    }

    public bool CanLoadPlaylist => !IsBusyOverlayVisible && !string.IsNullOrWhiteSpace(NormalizeValue(DraftPlaylistSource));

    public bool CanSave => HasPendingChanges && !IsBusyOverlayVisible && !SourceDraftRequiresReload;

    public bool CanOpenCategoryEditor =>
        !IsBusyOverlayVisible
        && !string.IsNullOrWhiteSpace(NormalizeValue(DraftPlaylistSource))
        && !SourceDraftRequiresReload;

    public bool CanPickFile => !IsBusyOverlayVisible;

    public bool IsLiveSelected => _selectedSection == BrowseSection.Live;

    public bool IsMoviesSelected => _selectedSection == BrowseSection.Movies;

    public bool IsSeriesSelected => _selectedSection == BrowseSection.Series;

    public string PendingStateText => IsBusyOverlayVisible
        ? "Arbetar..."
        : SourceDraftRequiresReload
            ? "Ladda först"
            : HasPendingChanges
                ? "Osparade ändringar"
                : "Allt sparat";

    private bool SourceDraftRequiresReload =>
        !string.Equals(NormalizeValue(DraftPlaylistSource), NormalizeValue(_owner.GetPlaylistSourceForEditor()), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(NormalizeValue(DraftXmlTvSource), NormalizeValue(_owner.GetXmlTvSourceForEditor()), StringComparison.OrdinalIgnoreCase);

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_hasLoaded)
        {
            return;
        }

        _hasLoaded = true;
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte öppna playlist-sidan.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (IsCategoryEditorVisible)
        {
            CancelCategoryDrag(resetStatus: false);
            IsCategoryEditorVisible = false;
            StatusText = "Ändringarna är lokala tills du trycker Spara.";
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            _savedPlaylistSource = NormalizeValue(_owner.GetPlaylistSourceForEditor());
            _savedXmlTvSource = NormalizeValue(_owner.GetXmlTvSourceForEditor());
            _draftPlaylistSource = _savedPlaylistSource;
            _draftXmlTvSource = _savedXmlTvSource;
            SetSelectedSection(_owner.GetPreferredPlaylistEditorSection());

            OnPropertyChanged(nameof(DraftPlaylistSource));
            OnPropertyChanged(nameof(DraftXmlTvSource));
            OnPropertyChanged(nameof(ActiveSourceText));
            OnPropertyChanged(nameof(CanLoadPlaylist));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanOpenCategoryEditor));
            OnPropertyChanged(nameof(CanPickFile));
            OnPropertyChanged(nameof(PendingStateText));

            RefreshVisibleCategoriesForCurrentSource();
            UpdatePendingChanges();
            await RefreshCacheStatusAsync();

            if (string.IsNullOrWhiteSpace(_savedPlaylistSource))
            {
                StatusText = "Lägg in en M3U-länk och tryck Ladda för att hämta kategorier.";
                return;
            }

            StatusText = "Ladda om spellistan om du ändrar länkarna. Sortera kategorier när du är redo.";
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void HandleSourceDraftChanged()
    {
        CancelCategoryDrag(resetStatus: false);
        OnPropertyChanged(nameof(ActiveSourceText));
        OnPropertyChanged(nameof(CanLoadPlaylist));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanOpenCategoryEditor));
        OnPropertyChanged(nameof(PendingStateText));

        _cachedCategoryCounts.Clear();
        RefreshVisibleCategoriesForCurrentSource();
        UpdatePendingChanges();

        if (_hasLoaded && !_isInitializing)
        {
            StatusText = SourceDraftRequiresReload
                ? "Tryck Ladda för att hämta kategorier från de nya länkarna."
                : "Ändringarna är redo att sparas.";
            _ = RefreshCacheStatusAsync();
        }
    }

    private void RefreshVisibleCategoriesForCurrentSource()
    {
        if (string.IsNullOrWhiteSpace(NormalizeValue(DraftPlaylistSource)) || SourceDraftRequiresReload)
        {
            CancelCategoryDrag(resetStatus: false);
            if (IsCategoryEditorVisible)
            {
                IsCategoryEditorVisible = false;
            }

            CategoryItems = [];
            return;
        }

        if (_draftCategoryItems.TryGetValue(_selectedSection, out var existingItems))
        {
            CategoryItems = existingItems;
            return;
        }

        CategoryItems = [];
    }

    private async Task EnsureSectionDraftLoadedAsync(BrowseSection section, bool forceRefresh = false)
    {
        if (!forceRefresh && _draftCategoryItems.TryGetValue(section, out var existingItems))
        {
            CategoryItems = existingItems;
            UpdateCategorySummary();
            return;
        }

        if (string.IsNullOrWhiteSpace(NormalizeValue(_owner.GetPlaylistSourceForEditor())))
        {
            CategoryItems = [];
            UpdateCategorySummary();
            return;
        }

        await RunBusyAsync(
            $"Laddar kategorier för {GetSectionLabel(section).ToLowerInvariant()}...",
            async () =>
            {
                var items = await CreateDraftCollectionAsync(section);
                _draftCategoryItems[section] = items;
                _savedSectionSignatures[section] = BuildCategorySignature(items);

                if (_selectedSection == section)
                {
                    CategoryItems = items;
                }
            });
    }

    private async Task<ObservableCollection<PlaylistCategoryManagerItem>> CreateDraftCollectionAsync(BrowseSection section)
    {
        var snapshot = await _owner.GetPlaylistEditorCategorySnapshotAsync(section);
        return new ObservableCollection<PlaylistCategoryManagerItem>(snapshot.Select(CloneCategoryItem));
    }

    private async Task RefreshCacheStatusAsync()
    {
        var source = NormalizeValue(DraftPlaylistSource);
        if (string.IsNullOrWhiteSpace(source))
        {
            _cachedCategoryCounts.Clear();
            CacheStatusText = "Ingen lokal cache hittad än.";
            UpdateCategorySummary();
            return;
        }

        var cacheIndex = await _playlistCacheStore.TryLoadIndexAsync(source);
        if (!string.Equals(source, NormalizeValue(DraftPlaylistSource), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cachedCategoryCounts.Clear();
        if (cacheIndex is null)
        {
            CacheStatusText = SourceDraftRequiresReload
                ? "Ingen lokal cache hittad för den här källan än."
                : "Ingen lokal cache hittad än.";
            UpdateCategorySummary();
            return;
        }

        foreach (var section in cacheIndex.Sections)
        {
            if (TryMapBrowseSection(section.ContentType, out var browseSection))
            {
                _cachedCategoryCounts[browseSection] = section.Categories.Count;
            }
        }

        var cachedAt = cacheIndex.CachedAtUtc.LocalDateTime;
        var baseText = $"Cache: hittad lokal lista från {cachedAt:yyyy-MM-dd HH:mm}.";
        CacheStatusText = SourceDraftRequiresReload
            ? $"{baseText} Tryck Ladda för att börja använda den här källan."
            : baseText;

        UpdateCategorySummary();
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        BusyMessage = message;
        IsBusyOverlayVisible = true;

        try
        {
            await action();
        }
        finally
        {
            IsBusyOverlayVisible = false;
        }
    }

    private void SetSelectedSection(BrowseSection section)
    {
        if (_selectedSection == section)
        {
            return;
        }

        _selectedSection = section;
        OnPropertyChanged(nameof(IsLiveSelected));
        OnPropertyChanged(nameof(IsMoviesSelected));
        OnPropertyChanged(nameof(IsSeriesSelected));
        UpdateCategorySummary();
    }

    private void UpdateCategorySummary()
    {
        var sectionLabel = GetSectionLabel(_selectedSection);
        var normalizedSource = NormalizeValue(DraftPlaylistSource);

        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            CategorySummaryText = "Lägg in en M3U-länk för att börja.";
            return;
        }

        if (SourceDraftRequiresReload)
        {
            CategorySummaryText = $"Ladda spellistan för att visa rätt kategorier i {sectionLabel}.";
            return;
        }

        if (CategoryItems.Count > 0)
        {
            var visibleCount = CategoryItems.Count(item => item.IsVisible);
            CategorySummaryText = $"Visar {visibleCount} av {CategoryItems.Count} kategorier i {sectionLabel}.";
            return;
        }

        if (_cachedCategoryCounts.TryGetValue(_selectedSection, out var cachedCount))
        {
            CategorySummaryText = cachedCount > 0
                ? $"{cachedCount} kategorier redo i {sectionLabel}. Öppna Sortera kategorier för att ordna dem."
                : $"Inga kategorier i {sectionLabel} än.";
            return;
        }

        CategorySummaryText = $"Öppna Sortera kategorier för att ladda {sectionLabel.ToLowerInvariant()} från cache.";
    }

    private void UpdatePendingChanges()
    {
        var hasSourceChanges =
            !string.Equals(NormalizeValue(DraftPlaylistSource), _savedPlaylistSource, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeValue(DraftXmlTvSource), _savedXmlTvSource, StringComparison.OrdinalIgnoreCase);

        var hasCategoryChanges = _draftCategoryItems.Any(entry =>
        {
            _savedSectionSignatures.TryGetValue(entry.Key, out var savedSignature);
            return !string.Equals(BuildCategorySignature(entry.Value), savedSignature, StringComparison.Ordinal);
        });

        HasPendingChanges = hasSourceChanges || hasCategoryChanges;
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (IsCategoryEditorVisible)
        {
            CancelCategoryDrag(resetStatus: false);
            IsCategoryEditorVisible = false;
            StatusText = "Ändringarna är lokala tills du trycker Spara.";
            return;
        }

        if (HasPendingChanges)
        {
            var shouldClose = await DisplayAlert(
                "Osparade ändringar",
                "Du har osparade ändringar. Vill du stänga sidan utan att spara?",
                "Stäng ändå",
                "Avbryt");

            if (!shouldClose)
            {
                return;
            }
        }

        await Navigation.PopAsync();
    }

    private async void OnSectionClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string sectionValue }
            || !Enum.TryParse<BrowseSection>(sectionValue, ignoreCase: true, out var section))
        {
            return;
        }

        try
        {
            CancelCategoryDrag(resetStatus: false);
            SetSelectedSection(section);

            if (SourceDraftRequiresReload)
            {
                CategoryItems = [];
                StatusText = "Tryck Ladda för att hämta kategorier från de nya länkarna.";
                return;
            }

            if (_draftCategoryItems.TryGetValue(section, out var existingItems))
            {
                CategoryItems = existingItems;
                StatusText = IsCategoryEditorVisible
                    ? "Ändringarna är lokala tills du trycker Spara."
                    : "Öppna Sortera kategorier när du vill ordna sektionen.";
                return;
            }

            if (!IsCategoryEditorVisible)
            {
                CategoryItems = [];
                StatusText = "Öppna Sortera kategorier när du vill ordna sektionen.";
                return;
            }

            await EnsureSectionDraftLoadedAsync(section);
            StatusText = "Ändringarna är lokala tills du trycker Spara.";
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte läsa in sektionen.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private async void OnOpenCategoryEditorClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NormalizeValue(DraftPlaylistSource)))
        {
            await DisplayAlert("Playlists", "Lägg in en M3U-länk först.", "OK");
            return;
        }

        if (SourceDraftRequiresReload)
        {
            await DisplayAlert("Ladda först", "Tryck Ladda innan du sorterar kategorier för den här källan.", "OK");
            return;
        }

        try
        {
            await EnsureSectionDraftLoadedAsync(_selectedSection);
            CancelCategoryDrag(resetStatus: false);
            IsCategoryEditorVisible = true;
            StatusText = "Ändringarna är lokala tills du trycker Spara.";
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte öppna kategorierna.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private void OnCloseCategoryEditorClicked(object sender, EventArgs e)
    {
        CancelCategoryDrag(resetStatus: false);
        IsCategoryEditorVisible = false;
        StatusText = "Ändringarna är lokala tills du trycker Spara.";
    }

    private async void OnPickFileClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Välj M3U-fil",
            });

            if (result is null)
            {
                return;
            }

            var importedPath = string.Empty;
            await RunBusyAsync(
                "Importerar lokal spellista...",
                async () => importedPath = await ImportPickedPlaylistAsync(result));

            if (string.IsNullOrWhiteSpace(importedPath))
            {
                return;
            }

            DraftPlaylistSource = importedPath;
            StatusText = "Lokal spellista vald. Tryck Ladda för att läsa in den.";
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte välja filen.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private async Task<string> ImportPickedPlaylistAsync(FileResult result)
    {
        var extension = Path.GetExtension(result.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".m3u";
        }

        var importDirectory = Path.Combine(FileSystem.AppDataDirectory, "playlist-imports");
        Directory.CreateDirectory(importDirectory);

        var safeFileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        var destinationPath = Path.Combine(importDirectory, safeFileName);

        await using var sourceStream = await result.OpenReadAsync();
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream);

        return destinationPath;
    }

    private async void OnLoadPlaylistClicked(object sender, EventArgs e)
    {
        var playlistSource = NormalizeValue(DraftPlaylistSource);
        var xmlTvSource = NormalizeValue(DraftXmlTvSource);

        if (string.IsNullOrWhiteSpace(playlistSource))
        {
            return;
        }

        try
        {
            var didLoad = false;
            await RunBusyAsync(
                "Laddar spellistan och förbereder kategorier...",
                async () =>
                {
                    didLoad = await _owner.LoadPlaylistFromEditorAsync(playlistSource, xmlTvSource);
                    if (!didLoad)
                    {
                        return;
                    }

                    _draftCategoryItems.Clear();
                    _savedSectionSignatures.Clear();
                    _cachedCategoryCounts.Clear();
                    CancelCategoryDrag(resetStatus: false);

                    if (IsCategoryEditorVisible)
                    {
                        await EnsureSectionDraftLoadedAsync(_selectedSection, forceRefresh: true);
                    }
                    else
                    {
                        CategoryItems = [];
                    }
                });

            if (!didLoad)
            {
                StatusText = "Spellistan kunde inte laddas.";
                return;
            }

            await RefreshCacheStatusAsync();
            StatusText = IsCategoryEditorVisible
                ? "Spellistan laddades. Justera kategorierna och tryck Spara när du är klar."
                : "Spellistan laddades. Öppna Sortera kategorier när du vill justera ordningen.";
            UpdatePendingChanges();
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte ladda spellistan.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        if (SourceDraftRequiresReload)
        {
            await DisplayAlert("Ladda först", "Tryck Ladda innan du sparar nya länkar.", "OK");
            return;
        }

        try
        {
            CancelCategoryDrag(resetStatus: false);
            var playlistSource = NormalizeValue(DraftPlaylistSource);
            var xmlTvSource = NormalizeValue(DraftXmlTvSource);
            var categoryDrafts = _draftCategoryItems.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<PlaylistCategoryManagerItem>)entry.Value
                    .Select(CloneCategoryItem)
                    .ToList());

            await RunBusyAsync(
                "Sparar playlist-inställningarna...",
                async () => await _owner.ApplyPlaylistEditorDraftAsync(playlistSource, xmlTvSource, categoryDrafts));

            _savedPlaylistSource = playlistSource;
            _savedXmlTvSource = xmlTvSource;

            foreach (var (section, items) in _draftCategoryItems)
            {
                _savedSectionSignatures[section] = BuildCategorySignature(items);
            }

            await RefreshCacheStatusAsync();
            StatusText = "Inställningarna sparades.";
            UpdatePendingChanges();
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte spara ändringarna.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private void OnCategoryVisibilityToggled(object sender, ToggledEventArgs e)
    {
        if (_isUpdatingCategoryItems || sender is not Switch { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        item.IsVisible = e.Value;
        UpdateCategorySummary();
        UpdatePendingChanges();
    }

    private void OnCategoryRowSizeChanged(object sender, EventArgs e)
    {
        if (sender is not Border { BindingContext: PlaylistCategoryManagerItem item } rowBorder)
        {
            return;
        }

        if (rowBorder.Height > 0)
        {
            item.RowHeight = rowBorder.Height + 8;
        }
    }

    private void OnCategoryHandleLongPressed(object? sender, LongPressDragEventArgs e)
    {
        if (sender is not LongPressBorder { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        BeginCategoryDrag(item, sender, e);
    }

    private void OnCategoryHandleDragMoved(object? sender, LongPressDragEventArgs e)
    {
        if (sender is not LongPressBorder { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        if (!ReferenceEquals(_draggedCategoryItem, item))
        {
            return;
        }

        _dragPointerRawY = e.RawY;
        _dragCurrentTotalY = e.TotalY;
        DragPreviewY = CalculateDragPreviewY(e.RawY);
        AutoScrollDuringDrag();
        UpdateCategoryDragTarget(item, _dragCurrentTotalY + _dragAutoScrollOffsetY);
    }

    private void OnCategoryHandleDragFinished(object? sender, LongPressDragEventArgs e)
    {
        if (sender is not LongPressBorder { BindingContext: PlaylistCategoryManagerItem item })
        {
            return;
        }

        if (!ReferenceEquals(_draggedCategoryItem, item))
        {
            return;
        }

        _dragPointerRawY = e.RawY;
        _dragCurrentTotalY = e.TotalY;
        DragPreviewY = CalculateDragPreviewY(e.RawY);
        UpdateCategoryDragTarget(item, _dragCurrentTotalY + _dragAutoScrollOffsetY);
        CompleteCategoryDrag(item);
    }

    private void MoveCategoryToTarget(
        PlaylistCategoryManagerItem draggedItem,
        PlaylistCategoryManagerItem targetItem,
        double? dropY,
        double targetHeight)
    {
        var sourceIndex = CategoryItems.IndexOf(draggedItem);
        var targetIndex = CategoryItems.IndexOf(targetItem);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        var insertAfterTarget = dropY.HasValue && targetHeight > 0 && dropY.Value > targetHeight / 2;
        var destinationIndex = targetIndex;
        if (insertAfterTarget)
        {
            destinationIndex++;
        }

        if (sourceIndex < destinationIndex)
        {
            destinationIndex--;
        }

        destinationIndex = Math.Max(0, Math.Min(destinationIndex, CategoryItems.Count - 1));
        if (destinationIndex == sourceIndex)
        {
            return;
        }

        CategoryItems.Move(sourceIndex, destinationIndex);
        UpdatePendingChanges();
    }

    private void BeginCategoryDrag(PlaylistCategoryManagerItem item, object? sender, LongPressDragEventArgs e)
    {
        CancelCategoryDrag(resetStatus: false);

        _draggedCategoryItem = item;
        _dragStartIndex = CategoryItems.IndexOf(item);
        _dragTargetIndex = _dragStartIndex;
        _dragAutoScrollOffsetY = 0;
        _dragCurrentTotalY = 0;
        _dragPointerRawY = e.RawY;
        item.IsBeingDragged = true;
        item.DragTranslationY = 0;
        DragPreviewLabel = item.Label;
        DragPreviewCountLabel = item.CountLabel;

        CaptureDragBounds(sender, e.RawY);
        DragPreviewY = CalculateDragPreviewY(e.RawY);
        UpdateCategoryDropTarget();
        StartAutoScrollLoop();
        OnPropertyChanged(nameof(CategoryEditorHintText));
        OnPropertyChanged(nameof(IsDragPreviewVisible));
        StatusText = $"Flyttar {item.Label}. Släpp raden där du vill placera den.";
    }

    private void UpdateCategoryDragTarget(PlaylistCategoryManagerItem item, double totalY)
    {
        var rowHeight = item.RowHeight > 0 ? item.RowHeight : 72;
        var estimatedOffset = (int)Math.Round(totalY / rowHeight, MidpointRounding.AwayFromZero);
        var targetIndex = Math.Max(0, Math.Min(_dragStartIndex + estimatedOffset, CategoryItems.Count - 1));

        if (_dragTargetIndex == targetIndex)
        {
            return;
        }

        _dragTargetIndex = targetIndex;
        UpdateCategoryDropTarget();
    }

    private void CompleteCategoryDrag(PlaylistCategoryManagerItem item)
    {
        var sourceIndex = _dragStartIndex;
        var targetIndex = _dragTargetIndex;
        var movedLabel = item.Label;
        var moved = false;

        item.IsBeingDragged = false;

        if (sourceIndex >= 0
            && targetIndex >= 0
            && sourceIndex < CategoryItems.Count
            && targetIndex < CategoryItems.Count
            && sourceIndex != targetIndex)
        {
            CategoryItems.Move(sourceIndex, targetIndex);
            moved = true;
        }

        ClearDropTargets();
        _draggedCategoryItem = null;
        _dragAutoScrollOffsetY = 0;
        _dragCollectionBottomOnScreen = 0;
        _dragCurrentTotalY = 0;
        _dragPointerRawY = 0;
        _dragCollectionTopOnScreen = 0;
        _dragTouchOffsetWithinRow = 0;
        DragPreviewCountLabel = string.Empty;
        DragPreviewLabel = string.Empty;
        DragPreviewY = 0;
        _dragStartIndex = -1;
        _dragTargetIndex = -1;
        OnPropertyChanged(nameof(CategoryEditorHintText));
        OnPropertyChanged(nameof(IsDragPreviewVisible));

        if (moved)
        {
            StatusText = $"Flyttade {movedLabel}.";
            UpdatePendingChanges();
        }
        else
        {
            StatusText = "Ingen ändring i ordningen.";
        }
    }

    private void CancelCategoryDrag(bool resetStatus = true)
    {
        if (_draggedCategoryItem is not null)
        {
            _draggedCategoryItem.IsBeingDragged = false;
        }

        ClearDropTargets();
        _draggedCategoryItem = null;
        _dragAutoScrollOffsetY = 0;
        _dragCollectionBottomOnScreen = 0;
        _dragCurrentTotalY = 0;
        _dragPointerRawY = 0;
        _dragCollectionTopOnScreen = 0;
        _dragTouchOffsetWithinRow = 0;
        DragPreviewCountLabel = string.Empty;
        DragPreviewLabel = string.Empty;
        DragPreviewY = 0;
        _dragStartIndex = -1;
        _dragTargetIndex = -1;
        OnPropertyChanged(nameof(CategoryEditorHintText));
        OnPropertyChanged(nameof(IsDragPreviewVisible));

        if (resetStatus)
        {
            StatusText = "Ändringarna är lokala tills du trycker Spara.";
        }
    }

    private void UpdateCategoryDropTarget()
    {
        ClearDropTargets();

        if (_draggedCategoryItem is null)
        {
            return;
        }

        var gapHeight = _draggedCategoryItem.RowHeight > 0 ? _draggedCategoryItem.RowHeight : 80;
        for (var index = 0; index < CategoryItems.Count; index++)
        {
            var item = CategoryItems[index];
            if (ReferenceEquals(item, _draggedCategoryItem))
            {
                item.LayoutTranslationY = 0;
                continue;
            }

            item.LayoutTranslationY = 0;

            if (_dragTargetIndex > _dragStartIndex)
            {
                if (index > _dragStartIndex && index <= _dragTargetIndex)
                {
                    item.LayoutTranslationY = -gapHeight;
                }
            }
            else if (_dragTargetIndex < _dragStartIndex)
            {
                if (index >= _dragTargetIndex && index < _dragStartIndex)
                {
                    item.LayoutTranslationY = gapHeight;
                }
            }
        }

        if (_dragTargetIndex >= 0 && _dragTargetIndex < CategoryItems.Count)
        {
            var targetItem = CategoryItems[_dragTargetIndex];
            if (!ReferenceEquals(targetItem, _draggedCategoryItem))
            {
                targetItem.IsDropTarget = true;
            }
        }
    }

    private void ClearDropTargets()
    {
        foreach (var item in CategoryItems)
        {
            item.IsDropTarget = false;
            item.LayoutTranslationY = 0;
        }
    }

    private void CaptureDragBounds(object? sender, double rawY)
    {
        _dragCollectionTopOnScreen = 0;
        _dragCollectionBottomOnScreen = 0;
        _dragTouchOffsetWithinRow = 0;

        if (!TryGetScreenBounds(CategoryScrollView, out var collectionTop, out var collectionBottom))
        {
            return;
        }

        _dragCollectionTopOnScreen = collectionTop;
        _dragCollectionBottomOnScreen = collectionBottom;

        var rowBorder = FindCategoryRowBorder(sender);
        if (rowBorder is not null && TryGetScreenBounds(rowBorder, out var rowTop, out _))
        {
            _dragTouchOffsetWithinRow = Math.Max(0, rawY - rowTop);
        }
    }

    private double CalculateDragPreviewY(double rawY)
    {
        var previewY = rawY - _dragCollectionTopOnScreen - _dragTouchOffsetWithinRow;
        return Math.Max(0, previewY);
    }

    private void StartAutoScrollLoop()
    {
        if (_isAutoScrollLoopRunning)
        {
            return;
        }

        _isAutoScrollLoopRunning = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(95), () =>
        {
            if (_draggedCategoryItem is null)
            {
                _isAutoScrollLoopRunning = false;
                return false;
            }

            AutoScrollDuringDrag();
            return true;
        });
    }

    private void AutoScrollDuringDrag()
    {
        if (_draggedCategoryItem is null
            || _dragCollectionTopOnScreen <= 0
            || _dragCollectionBottomOnScreen <= _dragCollectionTopOnScreen)
        {
            return;
        }

        var threshold = 96d;
        var rowHeight = _draggedCategoryItem.RowHeight > 0 ? _draggedCategoryItem.RowHeight : 80;
        if (_dragPointerRawY > _dragCollectionBottomOnScreen - threshold && _dragTargetIndex < CategoryItems.Count - 1)
        {
            var maxY = Math.Max(0, CategoryScrollView.ContentSize.Height - CategoryScrollView.Height);
            var nextY = Math.Min(maxY, CategoryScrollView.ScrollY + rowHeight * 0.75);
            if (nextY > CategoryScrollView.ScrollY + 0.5)
            {
                _dragAutoScrollOffsetY += nextY - CategoryScrollView.ScrollY;
                _ = CategoryScrollView.ScrollToAsync(0, nextY, false);
            }
            UpdateCategoryDragTarget(_draggedCategoryItem, _dragCurrentTotalY + _dragAutoScrollOffsetY);
        }
        else if (_dragPointerRawY < _dragCollectionTopOnScreen + threshold && _dragTargetIndex > 0)
        {
            var nextY = Math.Max(0, CategoryScrollView.ScrollY - rowHeight * 0.75);
            if (nextY < CategoryScrollView.ScrollY - 0.5)
            {
                _dragAutoScrollOffsetY -= CategoryScrollView.ScrollY - nextY;
                _ = CategoryScrollView.ScrollToAsync(0, nextY, false);
            }
            UpdateCategoryDragTarget(_draggedCategoryItem, _dragCurrentTotalY + _dragAutoScrollOffsetY);
        }
    }

    private static Border? FindCategoryRowBorder(object? sender)
    {
        var element = sender as Element;
        while (element is not null)
        {
            if (element is Border border
                && border is not LongPressBorder
                && border.BindingContext is PlaylistCategoryManagerItem)
            {
                return border;
            }

            element = element.Parent;
        }

        return null;
    }

    private static bool TryGetScreenBounds(VisualElement element, out double top, out double bottom)
    {
        top = 0;
        bottom = 0;

        if (element.Handler?.PlatformView is not AView platformView)
        {
            return false;
        }

        var location = new int[2];
        platformView.GetLocationOnScreen(location);
        top = location[1];
        bottom = location[1] + platformView.Height;
        return platformView.Height > 0;
    }

    private void OnShowAllCategoriesClicked(object sender, EventArgs e)
    {
        if (CategoryItems.Count == 0)
        {
            return;
        }

        CancelCategoryDrag(resetStatus: false);
        _isUpdatingCategoryItems = true;
        try
        {
            foreach (var item in CategoryItems)
            {
                item.IsVisible = true;
            }
        }
        finally
        {
            _isUpdatingCategoryItems = false;
        }

        UpdateCategorySummary();
        UpdatePendingChanges();
    }

    private void OnHideAllCategoriesClicked(object sender, EventArgs e)
    {
        if (CategoryItems.Count == 0)
        {
            return;
        }

        CancelCategoryDrag(resetStatus: false);
        _isUpdatingCategoryItems = true;
        try
        {
            foreach (var item in CategoryItems)
            {
                item.IsVisible = false;
            }
        }
        finally
        {
            _isUpdatingCategoryItems = false;
        }

        UpdateCategorySummary();
        UpdatePendingChanges();
    }

    private async void OnResetCategoryPreferencesClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NormalizeValue(_owner.GetPlaylistSourceForEditor())))
        {
            CategoryItems = [];
            UpdateCategorySummary();
            return;
        }

        try
        {
            CancelCategoryDrag(resetStatus: false);
            await RunBusyAsync(
                $"Återställer {GetSectionLabel(_selectedSection).ToLowerInvariant()}...",
                async () =>
                {
                    var items = await CreateDraftCollectionAsync(_selectedSection);
                    _draftCategoryItems[_selectedSection] = items;
                    _savedSectionSignatures[_selectedSection] = BuildCategorySignature(items);
                    CategoryItems = items;
                });

            StatusText = $"{GetSectionLabel(_selectedSection)} återställd till sparat läge.";
            UpdatePendingChanges();
        }
        catch (Exception ex)
        {
            StatusText = "Kunde inte återställa sektionen.";
            await DisplayAlert("Playlists", ex.Message, "OK");
        }
    }

    private static PlaylistCategoryManagerItem CloneCategoryItem(PlaylistCategoryManagerItem item)
    {
        return new PlaylistCategoryManagerItem
        {
            Key = item.Key,
            Label = item.Label,
            Count = item.Count,
            IsVisible = item.IsVisible,
        };
    }

    private static string BuildCategorySignature(IEnumerable<PlaylistCategoryManagerItem> items)
    {
        return string.Join(
            '\n',
            items.Select((item, index) => $"{index}:{item.Key.Trim().ToUpperInvariant()}:{item.IsVisible}"));
    }

    private PlaylistCategoryManagerItem? ResolveDraggedCategoryItem(DataPackagePropertySet properties)
    {
        if (_draggedCategoryItem is not null && CategoryItems.Contains(_draggedCategoryItem))
        {
            return _draggedCategoryItem;
        }

        if (properties.TryGetValue("PlaylistCategoryKey", out var keyValue)
            && keyValue is string categoryKey)
        {
            return CategoryItems.FirstOrDefault(item =>
                string.Equals(item.Key, categoryKey, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private PlaylistCategoryManagerItem? ResolveDraggedCategoryItem(DataPackagePropertySetView properties)
    {
        if (_draggedCategoryItem is not null && CategoryItems.Contains(_draggedCategoryItem))
        {
            return _draggedCategoryItem;
        }

        if (properties.TryGetValue("PlaylistCategoryKey", out var keyValue)
            && keyValue is string categoryKey)
        {
            return CategoryItems.FirstOrDefault(item =>
                string.Equals(item.Key, categoryKey, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool TryMapBrowseSection(ChannelContentType contentType, out BrowseSection browseSection)
    {
        browseSection = contentType switch
        {
            ChannelContentType.Live => BrowseSection.Live,
            ChannelContentType.Movie => BrowseSection.Movies,
            ChannelContentType.Series => BrowseSection.Series,
            _ => BrowseSection.Live,
        };

        return contentType is ChannelContentType.Live or ChannelContentType.Movie or ChannelContentType.Series;
    }

    private static string GetShortSourceDisplayName(string source)
    {
        var normalizedSource = NormalizeValue(source);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return "Ingen vald";
        }

        if (File.Exists(normalizedSource))
        {
            return Path.GetFileName(normalizedSource);
        }

        if (Uri.TryCreate(normalizedSource, UriKind.Absolute, out var uri))
        {
            if (!string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return uri.AbsoluteUri;
        }

        return normalizedSource;
    }

    private static string NormalizeValue(string? value) => value?.Trim() ?? string.Empty;

    private static string GetSectionLabel(BrowseSection section)
    {
        return section switch
        {
            BrowseSection.Live => "Live",
            BrowseSection.Movies => "Film",
            BrowseSection.Series => "Serier",
            _ => "Playlists",
        };
    }
}
