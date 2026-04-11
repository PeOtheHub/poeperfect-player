using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using APTV.Collections;
using APTV.Models;
using APTV.Services;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaImageSource = System.Windows.Media.ImageSource;

namespace APTV;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int TestChannelLimit = 100;
    private const int UiBatchSize = 200;
    private const int SearchDebounceMilliseconds = 300;
    private const int LatestItemLimit = 12;
    private const int LoadUiRefreshIntervalMilliseconds = 500;
    private const int InitialVisibleChannelBatchSize = 48;
    private const int IncrementalVisibleChannelBatchSize = 24;
    private const int InitialVisibleSeriesBatchSize = 36;
    private const int IncrementalVisibleSeriesBatchSize = 18;
    private const int RecentPlaybackLimit = 120;
    private const int XmlTvRefreshMaxAttempts = 3;
    private const int AudioDefaultsMaxAttempts = 8;
    private static readonly TimeSpan AudioDefaultsRetryDelay = TimeSpan.FromMilliseconds(450);
    private const string FavoritesCategoryKey = "__special__:favorites";
    private const string RecentCategoryKey = "__special__:recent";
    private const double IncrementalScrollLoadThreshold = 500d;
    private readonly AppSettingsStore _appSettingsStore = new();
    private readonly AppLogger _logger = AppLogger.Instance;
    private readonly FavoritesStore _favoritesStore = new();
    private readonly PosterImageService _posterImageService = new();
    private readonly PlaylistCacheStore _playlistCacheStore = new();
    private readonly SeriesCatalogService _seriesCatalogService = new();
    private readonly XmlTvCacheStore _xmlTvCacheStore = new();
    private readonly XmlTvService _xmlTvService = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _fullscreenChromeTimer;
    private readonly DispatcherTimer _fullscreenCursorTrackerTimer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly M3uPlaylistService _playlistService = new();
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly XtreamApiService _xtreamApiService = new();
    private readonly IReadOnlyList<BrowseSectionOption> _browseSections =
    [
        new(BrowseSection.Live, "Live"),
        new(BrowseSection.Movies, "Film"),
        new(BrowseSection.Series, "Serier"),
    ];

    private IReadOnlyList<Channel> _catalogChannels = [];
    private List<CategoryDisplayPreference> _categoryDisplayPreferences = [];
    private long _bytesRead;
    private RangeObservableCollection<CategoryOption> _categoryOptions = [];
    private RangeObservableCollection<Channel> _channels = [];
    private ICollectionView _channelsView;
    private HashSet<string> _favoriteUrls = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _favoriteSeasonKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _favoritesOnly;
    private bool _hasPlaybackSession;
    private bool _isBusy;
    private bool _isFullscreen;
    private bool _isLoadProgressIndeterminate;
    private bool _isLoadProgressVisible;
    private bool _isMuted;
    private bool _isPlaylistEditorVisible;
    private bool _isCategoryManagerVisible;
    private bool _isBrowserContentLoading;
    private bool _isSearchPanelVisible;
    private bool _isSearchResultsActive;
    private bool _isSeekInteractionActive;
    private bool _isSeriesAppending;
    private bool _isVisibleChannelListReady;
    private bool _isAppendingVisibleChannels;
    private RangeObservableCollection<Channel> _latestItems = [];
    private RangeObservableCollection<PlaylistCategoryItem> _playlistCategoryItems = [];
    private RangeObservableCollection<SeriesGroupItem> _seriesGroups = [];
    private IReadOnlyList<PlaybackTrackOption> _audioTrackOptions = [];
    private IReadOnlyList<PlaybackTrackOption> _subtitleTrackOptions = [];
    private bool _loadFirstHundredOnly = true;
    private CancellationTokenSource? _loadPlaylistCancellationTokenSource;
    private CancellationTokenSource? _xmlTvRefreshCancellationTokenSource;
    private CancellationTokenSource? _posterLoadCancellationTokenSource;
    private string _loadProgressStage = string.Empty;
    private string _loadProgressText = string.Empty;
    private double _loadProgressValue;
    private int _parsedChannelCount;
    private long _playbackDurationMilliseconds;
    private long _playbackPositionMilliseconds;
    private string _playlistSource = string.Empty;
    private string _xmlTvSource = string.Empty;
    private string _cacheStatusText = "Cache: ingen spellista laddad än.";
    private string _xmlTvStatusText = "XMLTV: ingen EPG-källa sparad.";
    private string _searchText = string.Empty;
    private bool _showFullscreenChrome = true;
    private string _appliedSearchText = string.Empty;
    private bool _scrollChannelGridToTopOnAttach;
    private IReadOnlyList<Channel> _selectedCategoryChannels = [];
    private IReadOnlyList<SeriesGroupItem> _selectedCategorySeriesGroups = [];
    private List<RecentPlaybackEntry> _recentPlaybackEntries = [];
    private BrowseSectionOption? _selectedBrowseSectionOption;
    private BrowseSection? _pendingBrowseSectionAfterLoad;
    private BrowseSectionOption? _selectedCategoryManagerSectionOption;
    private CategoryOption? _selectedCategoryOption;
    private Channel? _selectedChannel;
    private PlaybackTrackOption? _selectedAudioTrackOption;
    private PlaybackTrackOption? _selectedSubtitleTrackOption;
    private SeriesGroupItem? _selectedSeriesGroup;
    private SeriesSeasonItem? _selectedSeriesSeason;
    private ScrollViewer? _channelGridScrollViewer;
    private bool _suppressCategorySelectionChanged;
    private bool _suppressTrackSelectionChanged;
    private string _statusMessage = "Klistra in en M3U-link och klicka Ladda.";
    private int _suspendChannelCollectionNotifications;
    private long? _totalBytes;
    private int _visibleChannelCount;
    private int _visibleChannelRenderCount;
    private int _visibleSeriesGroupRenderCount;
    private int _volume = 100;
    private Dictionary<string, ChannelGuideInfo> _guideByChannelUrl = new(StringComparer.OrdinalIgnoreCase);
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;
    private WindowState _restoreWindowState;
    private WindowStyle _restoreWindowStyle;
    private Rect _restoreBounds;
    private double _fullscreenChromeWidth;
    private double _fullscreenControlsPopupLeft;
    private double _fullscreenControlsPopupTop;
    private double _fullscreenControlsPopupHeight = 170;
    private System.Drawing.Point _lastFullscreenCursorPosition;

    public MainWindow()
    {
        Core.Initialize();
        InitializeComponent();
        _logger.Info("Main window initialized.");

        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = _volume;
        VideoView.MediaPlayer = _mediaPlayer;

        _channelsView = CreateChannelsView(_channels);
        _channels.CollectionChanged += Channels_CollectionChanged;

        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
        };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        _fullscreenChromeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _fullscreenChromeTimer.Tick += FullscreenChromeTimer_Tick;

        _fullscreenCursorTrackerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _fullscreenCursorTrackerTimer.Tick += FullscreenCursorTrackerTimer_Tick;

        _categoryOptions = [];
        _playlistCategoryItems = [];
        _selectedCategoryOption = null;
        _selectedCategoryManagerSectionOption = _browseSections.FirstOrDefault(option => option.Section == BrowseSection.Live);

        DataContext = this;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<BrowseSectionOption> BrowseSections => _browseSections;

    public RangeObservableCollection<Channel> Channels
    {
        get => _channels;
        private set
        {
            if (ReferenceEquals(_channels, value))
            {
                return;
            }

            _channels.CollectionChanged -= Channels_CollectionChanged;
            _channels = value;
            _channels.CollectionChanged += Channels_CollectionChanged;
            OnPropertyChanged();
        }
    }

    public ICollectionView ChannelsView
    {
        get => _channelsView;
        private set => SetProperty(ref _channelsView, value);
    }

    public RangeObservableCollection<Channel> LatestItems
    {
        get => _latestItems;
        private set
        {
            if (ReferenceEquals(_latestItems, value))
            {
                return;
            }

            _latestItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLatestItems));
        }
    }

    public RangeObservableCollection<SeriesGroupItem> SeriesGroups
    {
        get => _seriesGroups;
        private set
        {
            if (ReferenceEquals(_seriesGroups, value))
            {
                return;
            }

            _seriesGroups = value;
            OnPropertyChanged();
        }
    }

    public RangeObservableCollection<CategoryOption> CategoryOptions
    {
        get => _categoryOptions;
        private set
        {
            if (ReferenceEquals(_categoryOptions, value))
            {
                return;
            }

            _categoryOptions = value;
            OnPropertyChanged();
        }
    }

    public RangeObservableCollection<PlaylistCategoryItem> PlaylistCategoryItems
    {
        get => _playlistCategoryItems;
        private set
        {
            if (ReferenceEquals(_playlistCategoryItems, value))
            {
                return;
            }

            _playlistCategoryItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlaylistCategoryItems));
            OnPropertyChanged(nameof(PlaylistCategoryManagerHintText));
        }
    }

    public SeriesGroupItem? SelectedSeriesGroup
    {
        get => _selectedSeriesGroup;
        set
        {
            if (!SetProperty(ref _selectedSeriesGroup, value))
            {
                return;
            }

            SelectedSeriesSeason = value?.Seasons.FirstOrDefault();
            RefreshChannelsView();
            NotifyDiscoveryProperties();
            OnPropertyChanged(nameof(ShowSeriesOverviewGrid));
            OnPropertyChanged(nameof(ShowSeriesDetailView));
            OnPropertyChanged(nameof(HasSelectedSeriesGroup));
            OnPropertyChanged(nameof(SeriesDetailTitle));
            OnPropertyChanged(nameof(SeriesDetailSubtitle));
            OnPropertyChanged(nameof(CurrentSeriesEpisodes));
            OnPropertyChanged(nameof(CurrentBrowserSelectionText));
        }
    }

    public SeriesSeasonItem? SelectedSeriesSeason
    {
        get => _selectedSeriesSeason;
        set
        {
            if (!SetProperty(ref _selectedSeriesSeason, value))
            {
                return;
            }

            RefreshChannelsView();
            NotifyDiscoveryProperties();
            OnPropertyChanged(nameof(CurrentSeriesEpisodes));
            OnPropertyChanged(nameof(CurrentBrowserSelectionText));
        }
    }

    public BrowseSectionOption? SelectedBrowseSectionOption
    {
        get => _selectedBrowseSectionOption;
        set
        {
            if (!SetProperty(ref _selectedBrowseSectionOption, value))
            {
                return;
            }

            ResetSearchState(clearText: true, hidePanel: true);
            _suppressCategorySelectionChanged = true;
            SelectedCategoryOption = null;
            _suppressCategorySelectionChanged = false;
            IsVisibleChannelListReady = false;
            ResetChannels();
            RebuildCategoryOptions(resetSelection: true);
            RefreshLatestItems();
            RefreshChannelsView();
            OnPropertyChanged(nameof(HasSelectedBrowseSection));
            OnPropertyChanged(nameof(CanBrowseCategories));
            OnPropertyChanged(nameof(ShowLatestSection));
            OnPropertyChanged(nameof(ShowDashboard));
            OnPropertyChanged(nameof(ShowBrowserShell));
            OnPropertyChanged(nameof(ShowDashboardBusyOverlay));
            OnPropertyChanged(nameof(CanSearchCurrentSection));
            OnPropertyChanged(nameof(SearchPanelTitle));
            OnPropertyChanged(nameof(ShowChannelList));
            OnPropertyChanged(nameof(ShowLiveChannelList));
            OnPropertyChanged(nameof(ShowPosterChannelGrid));
            OnPropertyChanged(nameof(ShowSeriesOverviewGrid));
            OnPropertyChanged(nameof(ShowSeriesDetailView));
            OnPropertyChanged(nameof(ShowChannelListPlaceholder));
            OnPropertyChanged(nameof(ShowGridLoadingOverlay));
            OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelSelectionHintText));
            OnPropertyChanged(nameof(DashboardHintText));
            StatusMessage = value is null
                ? "Välj Live, Film eller Serier för att fortsätta."
                : $"Välj en kategori i {value.Label} för att hämta aktuell lista.";
            NotifyDiscoveryProperties();
        }
    }

    public CategoryOption? SelectedCategoryOption
    {
        get => _selectedCategoryOption;
        set
        {
            if (!SetProperty(ref _selectedCategoryOption, value))
            {
                return;
            }

            if (value is not null && IsSearchResultsActive)
            {
                ResetSearchState(clearText: true, hidePanel: false);
            }

            OnPropertyChanged(nameof(HasSelectedCategory));
            OnPropertyChanged(nameof(ShowChannelList));
            OnPropertyChanged(nameof(ShowLiveChannelList));
            OnPropertyChanged(nameof(ShowPosterChannelGrid));
            OnPropertyChanged(nameof(ShowSeriesOverviewGrid));
            OnPropertyChanged(nameof(ShowSeriesDetailView));
            OnPropertyChanged(nameof(ShowChannelListPlaceholder));
            OnPropertyChanged(nameof(ShowGridLoadingOverlay));
            OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelSelectionHintText));
            NotifyDiscoveryProperties();

            if (_suppressCategorySelectionChanged || value is null || string.IsNullOrWhiteSpace(PlaylistSource))
            {
                return;
            }

            if (IsSpecialCategory(value))
            {
                _ = LoadSelectedCategoryFromCacheAsync(value);
                return;
            }

            _ = RefreshSelectedCategoryAsync(value);
        }
    }

    public BrowseSectionOption? SelectedCategoryManagerSectionOption
    {
        get => _selectedCategoryManagerSectionOption;
        set
        {
            if (!SetProperty(ref _selectedCategoryManagerSectionOption, value))
            {
                return;
            }

            RebuildPlaylistCategoryItems();
            OnPropertyChanged(nameof(PlaylistCategoryManagerHintText));
        }
    }

    public string PlaylistSource
    {
        get => _playlistSource;
        set
        {
            if (!SetProperty(ref _playlistSource, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPlaylistSource));
            OnPropertyChanged(nameof(PlaylistCardText));
            OnPropertyChanged(nameof(DashboardHintText));
        }
    }

    public string XmlTvSource
    {
        get => _xmlTvSource;
        set
        {
            if (!SetProperty(ref _xmlTvSource, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasXmlTvSource));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool IsSearchPanelVisible
    {
        get => _isSearchPanelVisible;
        private set
        {
            if (!SetProperty(ref _isSearchPanelVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SearchPanelVisibility));
        }
    }

    public bool IsSearchResultsActive
    {
        get => _isSearchResultsActive;
        private set
        {
            if (!SetProperty(ref _isSearchResultsActive, value))
            {
                return;
            }

            NotifyDiscoveryProperties();
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                RefreshChannelsView();
                NotifyDiscoveryProperties();
            }
        }
    }

    public Channel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (!SetProperty(ref _selectedChannel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NowPlayingText));
            OnPropertyChanged(nameof(ShowVideoPlaceholder));
            OnPropertyChanged(nameof(ShowFullscreenPlayer));
            OnPropertyChanged(nameof(ShowFullscreenChromePopup));
            OnPropertyChanged(nameof(ShowPlaybackTrackSelectors));
            NotifyPlaybackProperties();

            if (value is null)
            {
                ResetPlaybackTrackOptions();
                if (IsFullscreen)
                {
                    RestoreFromFullscreen();
                }

                _mediaPlayer.Stop();
                ResetPlaybackState();
                return;
            }

            ResetPlaybackProgress();
            ResetPlaybackTrackOptions();
            PlayChannel(value);
        }
    }

    public IReadOnlyList<PlaybackTrackOption> AudioTrackOptions
    {
        get => _audioTrackOptions;
        private set
        {
            if (ReferenceEquals(_audioTrackOptions, value))
            {
                return;
            }

            _audioTrackOptions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectAudioTrack));
            OnPropertyChanged(nameof(ShowAudioTrackSelector));
            OnPropertyChanged(nameof(ShowPlaybackTrackSelectors));
        }
    }

    public IReadOnlyList<PlaybackTrackOption> SubtitleTrackOptions
    {
        get => _subtitleTrackOptions;
        private set
        {
            if (ReferenceEquals(_subtitleTrackOptions, value))
            {
                return;
            }

            _subtitleTrackOptions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectSubtitleTrack));
            OnPropertyChanged(nameof(ShowSubtitleTrackSelector));
            OnPropertyChanged(nameof(ShowPlaybackTrackSelectors));
        }
    }

    public PlaybackTrackOption? SelectedAudioTrackOption
    {
        get => _selectedAudioTrackOption;
        private set => SetProperty(ref _selectedAudioTrackOption, value);
    }

    public PlaybackTrackOption? SelectedSubtitleTrackOption
    {
        get => _selectedSubtitleTrackOption;
        private set => SetProperty(ref _selectedSubtitleTrackOption, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string CacheStatusText
    {
        get => _cacheStatusText;
        set => SetProperty(ref _cacheStatusText, value);
    }

    public string XmlTvStatusText
    {
        get => _xmlTvStatusText;
        set => SetProperty(ref _xmlTvStatusText, value);
    }

    public string LoadButtonText => IsBusy ? "Avbryt" : "Ladda";

    public bool CanPickPlaylistFile => !IsBusy;

    public bool HasPlaylistSource => !string.IsNullOrWhiteSpace(PlaylistSource);

    public bool HasXmlTvSource => !string.IsNullOrWhiteSpace(XmlTvSource);

    public bool LoadFirstHundredOnly
    {
        get => _loadFirstHundredOnly;
        set
        {
            if (SetProperty(ref _loadFirstHundredOnly, value))
            {
                _ = SaveCurrentSettingsAsync();
            }
        }
    }

    public bool IsLoadProgressIndeterminate
    {
        get => _isLoadProgressIndeterminate;
        set => SetProperty(ref _isLoadProgressIndeterminate, value);
    }

    public bool IsLoadProgressVisible
    {
        get => _isLoadProgressVisible;
        set => SetProperty(ref _isLoadProgressVisible, value);
    }

    public string LoadProgressText
    {
        get => _loadProgressText;
        set => SetProperty(ref _loadProgressText, value);
    }

    public double LoadProgressValue
    {
        get => _loadProgressValue;
        set => SetProperty(ref _loadProgressValue, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(LoadButtonText));
                OnPropertyChanged(nameof(CanPickPlaylistFile));
                OnPropertyChanged(nameof(ShowDashboardBusyOverlay));
                OnPropertyChanged(nameof(ShowGridLoadingOverlay));
                OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
                OnPropertyChanged(nameof(DashboardHintText));
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
            }
        }
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (SetProperty(ref _isFullscreen, value))
            {
                OnPropertyChanged(nameof(FullscreenButtonText));
                OnPropertyChanged(nameof(ShowFullscreenPlayer));
                OnPropertyChanged(nameof(ShowFullscreenChromePopup));
                if (!value)
                {
                    SetFullscreenChromeVisible(true);
                }
            }
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, normalized))
            {
                return;
            }

            _mediaPlayer.Volume = normalized;

            if (normalized > 0 && IsMuted)
            {
                _mediaPlayer.Mute = false;
                IsMuted = false;
            }
        }
    }

    public long PlaybackPositionMilliseconds
    {
        get => _playbackPositionMilliseconds;
        set
        {
            if (SetProperty(ref _playbackPositionMilliseconds, value))
            {
                OnPropertyChanged(nameof(CurrentTimeText));
            }
        }
    }

    public long PlaybackDurationMilliseconds
    {
        get => _playbackDurationMilliseconds;
        set
        {
            if (SetProperty(ref _playbackDurationMilliseconds, value))
            {
                OnPropertyChanged(nameof(CanSeek));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(PlaybackModeText));
            }
        }
    }

    public bool CanControlPlayback => SelectedChannel is not null;

    public bool CanSeek => SelectedChannel?.IsVod == true && PlaybackDurationMilliseconds > 0;

    public bool CanSelectAudioTrack => SelectedChannel?.IsVod == true && AudioTrackOptions.Count > 1;

    public bool CanSelectSubtitleTrack => SelectedChannel?.IsVod == true && SubtitleTrackOptions.Count > 1;

    public string PlayPauseButtonText => _mediaPlayer.IsPlaying ? "Pause" : "Play";

    public string MuteButtonText => IsMuted ? "Ljud pa" : "Ljud av";

    public string FullscreenButtonText => IsFullscreen ? "Exit fullscreen" : "Fullscreen";

    public string CurrentTimeText => FormatPlaybackTime(PlaybackPositionMilliseconds);

    public string DurationText => PlaybackDurationMilliseconds > 0
        ? FormatPlaybackTime(PlaybackDurationMilliseconds)
        : SelectedChannel?.ContentType == ChannelContentType.Live
            ? "LIVE"
            : "--:--";

    public string PlaybackModeText
    {
        get
        {
            if (SelectedChannel is null)
            {
                return "Ingen media vald";
            }

            if (SelectedChannel.ContentType == ChannelContentType.Live)
            {
                return _mediaPlayer.IsPlaying ? "Live stream" : "Live redo";
            }

            return CanSeek ? "Film/serie med seek" : "VOD laddar metadata";
        }
    }

    public string PlayerControlHint
    {
        get
        {
            if (SelectedChannel is null)
            {
                return "Välj en kanal, film eller serie för att visa kontrollerna.";
            }

            return SelectedChannel.IsVod
                ? "Film och serier kan pausas och hoppas i tidslinjen nar streamen rapporterar langd."
                : "Live-kanaler fungerar bäst med play, stop, fullscreen och volym. Pause och seek är ofta begränsat på live.";
        }
    }

    public bool HasLatestItems => LatestItems.Count > 0;

    public bool HasSelectedBrowseSection => SelectedBrowseSectionOption is not null;

    public bool HasSelectedCategory => SelectedCategoryOption is not null;

    public bool IsLiveSection => CurrentSection == BrowseSection.Live;

    public bool HasPlaylistCategoryItems => PlaylistCategoryItems.Count > 0;

    public bool ShowDashboard => !HasSelectedBrowseSection;

    public bool ShowBrowserShell => HasSelectedBrowseSection;

    public bool ShowDashboardBusyOverlay => ShowDashboard && IsBusy;

    public bool CanBrowseCategories => HasSelectedBrowseSection && CategoryOptions.Count > 0;

    public bool ShowLatestSection => HasSelectedBrowseSection && HasLatestItems;

    public bool IsPlaylistEditorVisible
    {
        get => _isPlaylistEditorVisible;
        private set
        {
            if (!SetProperty(ref _isPlaylistEditorVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PlaylistPanelVisibility));
        }
    }

    public Visibility PlaylistPanelVisibility => IsPlaylistEditorVisible ? Visibility.Visible : Visibility.Collapsed;

    public bool IsBrowserContentLoading
    {
        get => _isBrowserContentLoading;
        private set
        {
            if (!SetProperty(ref _isBrowserContentLoading, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowGridLoadingOverlay));
            OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
        }
    }

    public bool IsCategoryManagerVisible
    {
        get => _isCategoryManagerVisible;
        private set
        {
            if (!SetProperty(ref _isCategoryManagerVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CategoryManagerVisibility));
        }
    }

    public Visibility CategoryManagerVisibility => IsCategoryManagerVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SearchPanelVisibility => IsSearchPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    public bool IsVisibleChannelListReady
    {
        get => _isVisibleChannelListReady;
        private set
        {
            if (!SetProperty(ref _isVisibleChannelListReady, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowChannelList));
            OnPropertyChanged(nameof(ShowLiveChannelList));
            OnPropertyChanged(nameof(ShowPosterChannelGrid));
            OnPropertyChanged(nameof(ShowSeriesOverviewGrid));
            OnPropertyChanged(nameof(ShowSeriesDetailView));
            OnPropertyChanged(nameof(ShowChannelListPlaceholder));
            OnPropertyChanged(nameof(ShowGridLoadingOverlay));
            OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelListPlaceholderText));
            OnPropertyChanged(nameof(ChannelSelectionHintText));
        }
    }

    public bool ShowChannelList => (HasSelectedCategory || IsSearchResultsActive)
        && IsVisibleChannelListReady
        && HasLoadedBrowseItems;

    public bool ShowChannelListPlaceholder => !ShowChannelList;

    public bool ShowGridLoadingOverlay => ShowBrowserShell
        && HasSelectedCategory
        && IsBrowserContentLoading;

    public bool ShowChannelListPlaceholderText => ShowChannelListPlaceholder && !IsBrowserContentLoading;

    public bool ShowLiveChannelList => ShowChannelList && IsLiveSection;

    public bool IsSeriesSection => CurrentSection == BrowseSection.Series;

    public bool HasSelectedSeriesGroup => SelectedSeriesGroup is not null;

    public bool ShowPosterChannelGrid => ShowChannelList && !IsLiveSection && !IsSeriesSection;

    public bool ShowSeriesOverviewGrid => ShowChannelList && IsSeriesSection && !HasSelectedSeriesGroup;

    public bool ShowSeriesDetailView => ShowChannelList && IsSeriesSection && HasSelectedSeriesGroup;

    public IReadOnlyList<SeriesEpisodeItem> CurrentSeriesEpisodes => SelectedSeriesSeason?.Episodes ?? [];

    public bool HasMoreVisibleChannels => IsSeriesSection && !HasSelectedSeriesGroup
        ? _visibleSeriesGroupRenderCount < _selectedCategorySeriesGroups.Count
        : _visibleChannelRenderCount < _selectedCategoryChannels.Count;

    public string ChannelCountText
    {
        get
        {
            if (IsSeriesSection)
            {
                if (HasSelectedSeriesGroup)
                {
                    var totalEpisodes = SelectedSeriesGroup?.EpisodeCount ?? 0;
                    return $"{_visibleChannelCount} / {totalEpisodes} avsnitt";
                }

                return $"{_visibleChannelCount} / {_selectedCategorySeriesGroups.Count} serier";
            }

            var totalCount = HasSelectedCategory
                ? _selectedCategoryChannels.Count
                : Channels.Count;

            return $"{_visibleChannelCount} / {totalCount} objekt";
        }
    }

    public string NowPlayingText => SelectedChannel is null ? "Ingen kanal vald" : $"Spelar: {SelectedChannel.Name}";

    public string FavoritesStorageText => $"Favoriter: {Path.GetDirectoryName(_favoritesStore.FilePath)}";

    public bool ShowVideoPlaceholder => SelectedChannel is null;

    public bool ShowFullscreenPlayer => IsFullscreen && SelectedChannel is not null;

    public bool ShowAudioTrackSelector => SelectedChannel?.IsVod == true && AudioTrackOptions.Count > 1;

    public bool ShowSubtitleTrackSelector => SelectedChannel?.IsVod == true && SubtitleTrackOptions.Count > 1;

    public bool ShowPlaybackTrackSelectors => ShowAudioTrackSelector || ShowSubtitleTrackSelector;

    public bool ShowFullscreenChrome
    {
        get => _showFullscreenChrome;
        private set => SetProperty(ref _showFullscreenChrome, value);
    }

    public bool ShowFullscreenChromePopup => ShowFullscreenPlayer && ShowFullscreenChrome;

    public double FullscreenChromeWidth => _fullscreenChromeWidth;

    public double FullscreenControlsPopupLeft => _fullscreenControlsPopupLeft;

    public double FullscreenControlsPopupTop => _fullscreenControlsPopupTop;

    public string DashboardHintText
    {
        get
        {
            if (IsBusy)
            {
                return "Laddar katalogen i bakgrunden...";
            }

            if (!HasPlaylistSource)
            {
                return "Öppna Playlists och ange din M3U-länk för att komma igång.";
            }

            return _catalogChannels.Count > 0
                ? "Välj Live, Film eller Serier för att fortsätta."
                : "Spellistan är sparad. Ladda katalogen eller välj en sektion för att bygga den.";
        }
    }

    public string PlaylistCardText => HasPlaylistSource
        ? $"Aktiv playlist: {GetShortPlaylistDisplayName()}"
        : "Ingen playlist sparad än";

    public string LiveCardSummary => GetDashboardSectionSummary(BrowseSection.Live, "Live TV");

    public string MoviesCardSummary => GetDashboardSectionSummary(BrowseSection.Movies, "Film");

    public string SeriesCardSummary => GetDashboardSectionSummary(BrowseSection.Series, "Serier");

    public string CurrentSectionSummary
    {
        get
        {
            if (!HasSelectedBrowseSection)
            {
                return "Välj Live, Film eller Serier för att visa kategorier.";
            }

            var sectionChannels = GetChannelsForCurrentSection().ToList();
            var categoryCount = sectionChannels
                .Select(channel => channel.CategoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return $"{GetCurrentSectionLabel()} - {sectionChannels.Count} objekt - {categoryCount} kategorier";
        }
    }

    public string BrowseHeaderTitle
    {
        get
        {
            if (IsSearchResultsActive && !string.IsNullOrWhiteSpace(_appliedSearchText))
            {
                return $"Sökresultat: {_appliedSearchText}";
            }

            if (SelectedCategoryOption is not null)
            {
                return SelectedCategoryOption.Label;
            }

            return HasSelectedBrowseSection ? GetCurrentSectionLabel() : "Välj sektion";
        }
    }

    public string LatestSectionTitle => CurrentSection switch
    {
        BrowseSection.Live => "Snabbval för live",
        BrowseSection.Movies => "Senast tillagda filmer",
        BrowseSection.Series => "Senast tillagda serier",
        _ => "Senast tillagda",
    };

    public string LatestSectionHint => "Baserat pa ordningen i spellistan.";

    public string CategorySectionTitle => $"Kategorier i {GetCurrentSectionLabel()}";

    public string CategorySectionSubtitle
    {
        get
        {
            if (!HasSelectedBrowseSection)
            {
                return "Välj först om du vill se Live, Film eller Serier.";
            }

            if (CategoryOptions.Count == 0)
            {
                return "Inga cachade kategorier hittade an sa lange.";
            }

            if (IsSearchResultsActive && !string.IsNullOrWhiteSpace(_appliedSearchText))
            {
                return $"Söker i hela {GetCurrentSectionLabel().ToLowerInvariant()}.";
            }

            return SelectedCategoryOption is not null
                ? $"Vald kategori: {SelectedCategoryOption.Label}"
                : $"Visar {CategoryOptions.Count} kategorier i sektionen.";
        }
    }

    public string ChannelListTitle
    {
        get
        {
            if (IsSeriesSection && SelectedSeriesGroup is not null)
            {
                return SelectedSeriesGroup.Title;
            }

            if (IsSearchResultsActive && !string.IsNullOrWhiteSpace(_appliedSearchText))
            {
                return $"{GetCurrentSectionLabel()} / Sök";
            }

            if (SelectedCategoryOption is not null)
            {
                return $"{GetCurrentSectionLabel()} / {SelectedCategoryOption.Label}";
            }

            return HasSelectedBrowseSection ? GetCurrentSectionLabel() : "Välj sektion";
        }
    }

    public string ChannelListSubtitle
    {
        get
        {
            if (IsSeriesSection && SelectedSeriesGroup is not null)
            {
                var seasonText = SelectedSeriesSeason is null
                    ? "Välj en säsong."
                    : $"{SelectedSeriesSeason.Label} - {CurrentSeriesEpisodes.Count} avsnitt";
                return $"{SelectedSeriesGroup.SummaryText} - {seasonText}";
            }

            if (!HasSelectedCategory)
            {
                return IsSearchResultsActive
                    ? $"Visar lokala sökresultat för {_appliedSearchText}."
                    : "VÄlj en kategori fÖr att bygga listan.";
            }

            if (IsBusy)
            {
                return "Uppdaterar vald kategori från källan...";
            }

            var totalCount = _selectedCategoryChannels.Count;
            var parts = new List<string>();

            if (totalCount > 0)
            {
                parts.Add($"visar {Channels.Count} av {totalCount}");
            }
            else
            {
                parts.Add($"{_visibleChannelCount} matchar nuvarande urval");
            }

            if (!string.IsNullOrWhiteSpace(_appliedSearchText))
            {
                parts.Add($"sök: {_appliedSearchText}");
            }

            if (FavoritesOnly)
            {
                parts.Add("bara favoriter");
            }

            if (HasMoreVisibleChannels)
            {
                parts.Add("fler laddas vid scroll");
            }

            return string.Join(" - ", parts);
        }
    }

    public string SeriesDetailTitle => SelectedSeriesGroup?.Title ?? "Välj serie";

    public string SeriesDetailSubtitle
    {
        get
        {
            if (SelectedSeriesGroup is null)
            {
                return "Välj en serie för att se säsonger och avsnitt.";
            }

            return SelectedSeriesSeason is null
                ? $"{SelectedSeriesGroup.SummaryText} - välj en säsong."
                : $"{SelectedSeriesGroup.SummaryText} - {SelectedSeriesSeason.Label}";
        }
    }

    public string CurrentBrowserSelectionText
    {
        get
        {
            if (IsSeriesSection && SelectedSeriesGroup is not null)
            {
                return SelectedSeriesSeason is null
                    ? SelectedSeriesGroup.Title
                    : $"{SelectedSeriesGroup.Title} - {SelectedSeriesSeason.Label}";
            }

            return SelectedChannel?.Name ?? "Ingen titel vald";
        }
    }

    public string SearchPanelTitle => HasSelectedBrowseSection
        ? $"Sök i {SelectedBrowseSectionOption!.Label}"
        : "SÖk";

    public bool CanSearchCurrentSection => HasSelectedBrowseSection && CurrentSection is not null;

    private bool HasLoadedBrowseItems => IsSeriesSection
        ? HasSelectedSeriesGroup
            ? CurrentSeriesEpisodes.Count > 0
            : SeriesGroups.Count > 0
        : Channels.Count > 0;

    public string ChannelSelectionHintText => IsLiveSection
        ? "Välj en kanal i listan för att starta uppspelning direkt i fullscreen. Escape tar dig tillbaka hit."
        : IsSeriesSection
            ? HasSelectedSeriesGroup
                ? "Välj säsong och avsnitt för att starta uppspelning direkt i fullscreen. Escape tar dig tillbaka hit."
                : "Välj en serie för att öppna säsonger och avsnitt."
            : "Välj en titel i griden för att starta uppspelning direkt i fullscreen. Escape tar dig tillbaka hit.";

    public string PlaylistCategoryManagerHintText
    {
        get
        {
            if (_catalogChannels.Count == 0)
            {
                return "Ladda katalogen först så att appen kan läsa in kategorierna.";
            }

            if (SelectedCategoryManagerSectionOption is null)
            {
                return "Välj sektion fÖr att hantera kategorier.";
            }

            if (!HasPlaylistCategoryItems)
            {
                return $"Inga kategorier hittades i {SelectedCategoryManagerSectionOption.Label}.";
            }

            var visibleCount = PlaylistCategoryItems.Count(item => item.IsVisible);
            return $"Visar {visibleCount} av {PlaylistCategoryItems.Count} kategorier i {SelectedCategoryManagerSectionOption.Label}.";
        }
    }

    public string ChannelListPlaceholderText
    {
        get
        {
            if (!HasSelectedBrowseSection)
            {
                return "VÄlj Live, Film eller Serier fÖr att komma vidare.";
            }

            if (CategoryOptions.Count == 0)
            {
                return "Inga cachade kategorier finns än. Ladda listan för att bygga upp dem.";
            }

            if (IsSearchResultsActive)
            {
                return string.IsNullOrWhiteSpace(_appliedSearchText)
                    ? $"Skriv vad du vill söka efter i {SelectedBrowseSectionOption!.Label}."
                    : $"Inga träffar för \"{_appliedSearchText}\" i {SelectedBrowseSectionOption!.Label}.";
            }

            if (!HasSelectedCategory)
            {
                return "Välj en kategori för att hämta den senaste listan.";
            }

            if (IsBusy)
            {
                return "Uppdaterar vald kategori från källan...";
            }

            return "Inga objekt hittades i den valda kategorin.";
        }
    }

    private BrowseSection? CurrentSection => SelectedBrowseSectionOption?.Section;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _logger.Info("MainWindow_Loaded started.");
        _favoriteUrls = await _favoritesStore.LoadAsync();
        var settings = await _appSettingsStore.LoadAsync();
        PlaylistSource = settings.PlaylistSource;
        XmlTvSource = settings.XmlTvSource;
        LoadFirstHundredOnly = settings.LoadFirstHundredOnly;
        _favoriteSeasonKeys = new HashSet<string>(
            settings.FavoriteSeasonKeys.Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);
        _recentPlaybackEntries = settings.RecentPlaybackEntries
            .Where(entry => entry.Section is BrowseSection.Live or BrowseSection.Movies or BrowseSection.Series)
            .OrderByDescending(entry => entry.PlayedAtUtc)
            .Take(RecentPlaybackLimit)
            .ToList();
        _categoryDisplayPreferences = settings.CategoryDisplayPreferences
            .Where(preference => preference.Section is BrowseSection.Live or BrowseSection.Movies or BrowseSection.Series)
            .ToList();
        RebuildPlaylistCategoryItems();
        NotifyDiscoveryProperties();
        NotifyPlaybackProperties();
        StatusMessage = HasPlaylistSource
            ? "Välj Live, Film eller Serier. Playlists finns alltid tillgängligt om du vill byta källa."
            : "Välj Playlists och ange din M3U-länk för att komma igång.";
        CacheStatusText = HasPlaylistSource
            ? "Cache: försöker läsa in den senast sparade katalogen."
            : "Cache: väntar på första laddningen.";
        XmlTvStatusText = HasXmlTvSource
            ? "XMLTV: sparad EPG-källa hittades och används för livekanaler."
            : "XMLTV: ingen EPG-källa sparad. Live visas utan Nu/Nästa.";

        _logger.Info(
            $"Settings loaded. Playlist={_logger.DescribeSource(PlaylistSource)}, XmlTvConfigured={HasXmlTvSource}, TestMode={LoadFirstHundredOnly}, Favorites={_favoriteUrls.Count}, Recent={_recentPlaybackEntries.Count}, CategoryPreferences={_categoryDisplayPreferences.Count}");

        if (HasPlaylistSource)
        {
            _logger.Info("Autoloading playlist from saved settings.");
            await LoadPlaylistAsync();
        }
        else
        {
            _logger.Info("No saved playlist source found on startup.");
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _logger.Info("Main window closing.");
        _searchDebounceTimer.Stop();
        _playbackTimer.Stop();
        _fullscreenChromeTimer.Stop();
        _fullscreenCursorTrackerTimer.Stop();
        _loadPlaylistCancellationTokenSource?.Cancel();
        _loadPlaylistCancellationTokenSource?.Dispose();
        _xmlTvRefreshCancellationTokenSource?.Cancel();
        _xmlTvRefreshCancellationTokenSource?.Dispose();
        _posterLoadCancellationTokenSource?.Cancel();
        _posterLoadCancellationTokenSource?.Dispose();
        Mouse.OverrideCursor = null;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private async void ChannelGridListBox_Loaded(object sender, RoutedEventArgs e)
    {
        AttachChannelGridScrollViewer();

        try
        {
            var cancellationToken = _posterLoadCancellationTokenSource?.Token ?? CancellationToken.None;
            if (IsSeriesSection && !HasSelectedSeriesGroup)
            {
                await EnsureVisibleSeriesGroupBufferAsync(cancellationToken);
            }
            else
            {
                await EnsureVisibleChannelBufferAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ChannelGridListBox_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachChannelGridScrollViewer();
    }

    private async void ChannelGridScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_channelGridScrollViewer is null
            || (_isAppendingVisibleChannels || _isSeriesAppending)
            || !HasMoreVisibleChannels)
        {
            return;
        }

        var remainingScroll = _channelGridScrollViewer.ScrollableHeight - _channelGridScrollViewer.VerticalOffset;
        if (remainingScroll > IncrementalScrollLoadThreshold)
        {
            return;
        }

        try
        {
            var cancellationToken = _posterLoadCancellationTokenSource?.Token ?? CancellationToken.None;
            var appended = IsSeriesSection && !HasSelectedSeriesGroup
                ? await AppendVisibleSeriesGroupBatchAsync(IncrementalVisibleSeriesBatchSize, cancellationToken)
                : await AppendVisibleChannelBatchAsync(IncrementalVisibleChannelBatchSize, cancellationToken);

            if (appended)
            {
                if (IsSeriesSection && !HasSelectedSeriesGroup)
                {
                    await EnsureVisibleSeriesGroupBufferAsync(cancellationToken);
                }
                else
                {
                    await EnsureVisibleChannelBufferAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FullscreenOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        RevealFullscreenChrome();
    }

    private void FullscreenHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFullscreenPopupLayout();
    }

    private void FullscreenControlsChrome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(_fullscreenControlsPopupHeight - e.NewSize.Height) < 0.5)
        {
            return;
        }

        _fullscreenControlsPopupHeight = e.NewSize.Height;
        UpdateFullscreenPopupLayout();
    }

    private void FullscreenChromeTimer_Tick(object? sender, EventArgs e)
    {
        if (!ShowFullscreenPlayer)
        {
            _fullscreenChromeTimer.Stop();
            SetFullscreenChromeVisible(true);
            return;
        }

        if (_isSeekInteractionActive)
        {
            return;
        }

        _fullscreenChromeTimer.Stop();
        SetFullscreenChromeVisible(false);
    }

    private void FullscreenCursorTrackerTimer_Tick(object? sender, EventArgs e)
    {
        if (!ShowFullscreenPlayer)
        {
            _fullscreenCursorTrackerTimer.Stop();
            return;
        }

        var currentCursorPosition = GetCursorScreenPosition();
        if (currentCursorPosition == _lastFullscreenCursorPosition)
        {
            return;
        }

        _lastFullscreenCursorPosition = currentCursorPosition;
        RevealFullscreenChrome();
    }

    private async void DashboardSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowseSection section })
        {
            return;
        }

        var sectionLabel = _browseSections.FirstOrDefault(option => option.Section == section)?.Label ?? section.ToString();
        _logger.Info($"Dashboard section clicked: {sectionLabel}. Busy={IsBusy}, CatalogCount={_catalogChannels.Count}");

        if (!HasPlaylistSource)
        {
            IsPlaylistEditorVisible = true;
            _logger.Warning($"Section click ignored because no playlist source is configured. RequestedSection={sectionLabel}");
            StatusMessage = "Ange din M3U i Playlists innan du väljer Live, Film eller Serier.";
            return;
        }

        if (IsBusy && _catalogChannels.Count == 0)
        {
            _pendingBrowseSectionAfterLoad = section;
            _logger.Info($"Section selection queued until initial catalog load completes. Section={sectionLabel}");
            var pendingSectionLabel = _browseSections.FirstOrDefault(option => option.Section == section)?.Label ?? "sektionen";
            StatusMessage = $"Laddar katalogen. {pendingSectionLabel} öppnas automatiskt när listan är klar.";
            return;
        }

        if (_catalogChannels.Count == 0)
        {
            _pendingBrowseSectionAfterLoad = section;
            _logger.Info($"No catalog loaded yet. Triggering playlist load before opening {sectionLabel}.");
            var didLoad = await LoadPlaylistAsync();
            if (!didLoad)
            {
                _logger.Warning($"Section {sectionLabel} could not open because playlist load returned false.");
                return;
            }

            if (SelectedBrowseSectionOption?.Section == section)
            {
                return;
            }
        }

        SelectedBrowseSectionOption = _browseSections.FirstOrDefault(option => option.Section == section);
        _logger.Info($"Browse section activated: {sectionLabel}");
    }

    private void BackToDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            CancelPlaylistLoad();
        }

        ResetSearchState(clearText: true, hidePanel: true);
        FavoritesOnly = false;
        SelectedChannel = null;
        SelectedBrowseSectionOption = null;
        StatusMessage = "Välj Live, Film eller Serier för att fortsätta.";
    }

    private void OpenPlaylistEditor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCategoryManagerSectionOption is null)
        {
            SelectedCategoryManagerSectionOption = _browseSections.FirstOrDefault(option => option.Section == BrowseSection.Live);
        }

        RebuildPlaylistCategoryItems();
        IsPlaylistEditorVisible = true;
    }

    private void OpenCategoryManager_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCategoryManagerSectionOption is null)
        {
            SelectedCategoryManagerSectionOption = _browseSections.FirstOrDefault(option => option.Section == BrowseSection.Live);
        }

        RebuildPlaylistCategoryItems();
        IsCategoryManagerVisible = true;
    }

    private async void LoadDashboardCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPlaylistSource)
        {
            IsPlaylistEditorVisible = true;
            StatusMessage = "Ange din M3U i Playlists innan du laddar katalogen.";
            return;
        }

        var didLoad = await LoadPlaylistAsync();
        if (didLoad)
        {
            IsPlaylistEditorVisible = false;
        }
    }

    private void ClosePlaylistEditor_Click(object sender, RoutedEventArgs e)
    {
        IsCategoryManagerVisible = false;
        IsPlaylistEditorVisible = false;
    }

    private void CloseCategoryManager_Click(object sender, RoutedEventArgs e)
    {
        IsCategoryManagerVisible = false;
    }

    private async void SavePlaylistSettings_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentSettingsAsync();
        _logger.Info($"Playlist settings saved by user. Playlist={_logger.DescribeSource(PlaylistSource)}, XmlTvConfigured={HasXmlTvSource}");
        CacheStatusText = "Cache: playlistinställningen är sparad lokalt.";
        StatusMessage = "Spellistan sparades lokalt. Ladda katalogen när du är redo.";
        IsPlaylistEditorVisible = false;
    }

    private async void CategoryVisibilityCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistCategoryItem item })
        {
            return;
        }

        await PersistPlaylistCategoryPreferencesAsync(item.Section);
    }

    private async void MoveCategoryUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistCategoryItem item })
        {
            return;
        }

        if (MovePlaylistCategoryItem(item, -1))
        {
            await PersistPlaylistCategoryPreferencesAsync(item.Section);
        }
    }

    private async void MoveCategoryDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PlaylistCategoryItem item })
        {
            return;
        }

        if (MovePlaylistCategoryItem(item, 1))
        {
            await PersistPlaylistCategoryPreferencesAsync(item.Section);
        }
    }

    private async void ResetCategoryPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCategoryManagerSectionOption is null)
        {
            return;
        }

        _categoryDisplayPreferences.RemoveAll(preference => preference.Section == SelectedCategoryManagerSectionOption.Section);
        RebuildPlaylistCategoryItems();
        await PersistPlaylistCategoryPreferencesAsync(SelectedCategoryManagerSectionOption.Section);
    }

    private void SeriesBack_Click(object sender, RoutedEventArgs e)
    {
        SelectedSeriesGroup = null;
        StatusMessage = SelectedCategoryOption is null
            ? "Välj en kategori för att visa serier."
            : $"Visar serier i {SelectedCategoryOption.Label}.";
        _scrollChannelGridToTopOnAttach = true;
        ScrollChannelGridToTop();
    }

    private void SeriesEpisodePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SeriesEpisodeItem episode })
        {
            return;
        }

        SelectedChannel = episode.Channel;
    }

    private async void SeriesSeasonFavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SeriesSeasonItem season })
        {
            return;
        }

        if (season.IsFavorite)
        {
            _favoriteSeasonKeys.Add(season.FavoriteKey);
        }
        else
        {
            _favoriteSeasonKeys.Remove(season.FavoriteKey);
        }

        await SaveCurrentSettingsAsync();
        RefreshDiscoveryData(resetCategorySelection: false);

        if (SelectedCategoryOption is not null && IsFavoritesCategory(SelectedCategoryOption))
        {
            await LoadSelectedCategoryFromCacheAsync(SelectedCategoryOption);
        }
        else
        {
            RefreshChannelsView();
            NotifyDiscoveryProperties();
        }
    }

    private void ResetSearchState(bool clearText, bool hidePanel)
    {
        _searchDebounceTimer.Stop();
        _appliedSearchText = string.Empty;
        IsSearchResultsActive = false;

        if (clearText)
        {
            SearchText = string.Empty;
        }

        if (hidePanel)
        {
            IsSearchPanelVisible = false;
        }

        OnPropertyChanged(nameof(BrowseHeaderTitle));
        OnPropertyChanged(nameof(CategorySectionSubtitle));
        OnPropertyChanged(nameof(ChannelListTitle));
        OnPropertyChanged(nameof(ChannelListSubtitle));
        OnPropertyChanged(nameof(ChannelListPlaceholderText));
    }

    private async Task ExecuteSectionSearchAsync()
    {
        if (!CanSearchCurrentSection || CurrentSection is null)
        {
            return;
        }

        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = $"Skriv vad du vill söka efter i {SelectedBrowseSectionOption!.Label}.";
            return;
        }

        _appliedSearchText = query;
        IsSearchResultsActive = true;
        IsSearchPanelVisible = true;
        SelectedChannel = null;
        SelectedSeriesGroup = null;
        _suppressCategorySelectionChanged = true;
        SelectedCategoryOption = null;
        _suppressCategorySelectionChanged = false;
        ResetChannels();
        IsVisibleChannelListReady = false;
        IsLoadProgressVisible = false;

        var section = CurrentSection.Value;
        if (section == BrowseSection.Series)
        {
            var groups = _seriesCatalogService.BuildGroups(GetChannelsForSection(section))
                .Where(group => MatchesSearchQuery(group, query))
                .ToList();

            ApplySeriesSeasonFavorites(groups);
            await AddSeriesGroupsInBatchesAsync(groups, CancellationToken.None, $"Sök: {query}");
            StatusMessage = groups.Count == 0
                ? $"Inga serier matchade \"{query}\" i {SelectedBrowseSectionOption!.Label}."
                : $"Hittade {groups.Count} serier for \"{query}\" i {SelectedBrowseSectionOption!.Label}.";
            IsVisibleChannelListReady = groups.Count > 0;
        }
        else
        {
            var channels = GetChannelsForSection(section)
                .Where(channel => MatchesSearchQuery(channel, query))
                .ToList();

            ApplyFavorites(channels);
            await AddChannelsInBatchesAsync(channels, CancellationToken.None, $"Sök: {query}");
            StatusMessage = channels.Count == 0
                ? $"Inga objekt matchade \"{query}\" i {SelectedBrowseSectionOption!.Label}."
                : $"Hittade {channels.Count} objekt for \"{query}\" i {SelectedBrowseSectionOption!.Label}.";
            IsVisibleChannelListReady = channels.Count > 0;
        }

        CacheStatusText = $"Cache: visar lokala sökresultat i {SelectedBrowseSectionOption!.Label}.";
        IsLoadProgressVisible = false;
        RefreshChannelsView();
        NotifyDiscoveryProperties();
    }

    private static bool MatchesSearchQuery(Channel channel, string query)
    {
        return channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || channel.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || channel.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearchQuery(SeriesGroupItem group, string query)
    {
        return group.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || group.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || group.Seasons.Any(season => season.Label.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private async void MarkAllCategories_Click(object sender, RoutedEventArgs e)
    {
        await SetAllPlaylistCategoryVisibilityAsync(true);
    }

    private async void ClearAllCategories_Click(object sender, RoutedEventArgs e)
    {
        await SetAllPlaylistCategoryVisibilityAsync(false);
    }

    private async void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            CancelPlaylistLoad();
            return;
        }

        var didLoad = await LoadPlaylistAsync();
        if (didLoad)
        {
            IsPlaylistEditorVisible = false;
        }
    }

    private void ToggleSearchPanel_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSearchCurrentSection)
        {
            return;
        }

        IsSearchPanelVisible = !IsSearchPanelVisible;
        if (IsSearchPanelVisible)
        {
            Dispatcher.InvokeAsync(() =>
            {
                BrowseSearchTextBox.Focus();
                BrowseSearchTextBox.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }

    private void CloseSearchPanel_Click(object sender, RoutedEventArgs e)
    {
        IsSearchPanelVisible = false;
    }

    private async void ExecuteSearch_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteSectionSearchAsync();
    }

    private async void BrowseSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ExecuteSectionSearchAsync();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ResetSearchState(clearText: true, hidePanel: false);
        SelectedSeriesGroup = null;
        SelectedChannel = null;
        ResetChannels();
        IsVisibleChannelListReady = false;
        StatusMessage = HasSelectedBrowseSection
            ? $"Välj en kategori i {SelectedBrowseSectionOption!.Label} för att hämta aktuell lista."
            : "Välj Live, Film eller Serier för att fortsätta.";
        NotifyDiscoveryProperties();
    }

    private void PickPlaylistFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "M3U playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|Alla filer (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            PlaylistSource = dialog.FileName;
        }
    }

    private async void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        var channel = sender switch
        {
            FrameworkElement { DataContext: Channel directChannel } => directChannel,
            FrameworkElement { DataContext: SeriesEpisodeItem episode } => episode.Channel,
            _ => null,
        };

        if (channel is null)
        {
            return;
        }

        if (channel.IsFavorite)
        {
            _favoriteUrls.Add(channel.Url);
        }
        else
        {
            _favoriteUrls.Remove(channel.Url);
        }

        await SaveFavoritesAsync();
        RefreshDiscoveryData(resetCategorySelection: false);

        if (SelectedCategoryOption is not null && IsFavoritesCategory(SelectedCategoryOption))
        {
            await LoadSelectedCategoryFromCacheAsync(SelectedCategoryOption);
        }
        else
        {
            RefreshChannelsView();
            NotifyDiscoveryProperties();
        }
    }

    private async Task<bool> LoadPlaylistAsync()
    {
        if (IsBusy)
        {
            _logger.Warning("LoadPlaylistAsync ignored because another load is already in progress.");
            return false;
        }

        var source = PlaylistSource.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            StatusMessage = "Ange en M3U-länk eller välj en lokal fil för att fortsätta.";
            return false;
        }

        _logger.Info($"Starting playlist load. Source={_logger.DescribeSource(source)}, TestMode={LoadFirstHundredOnly}, PendingSection={_pendingBrowseSectionAfterLoad?.ToString() ?? "<none>"}");
        await SaveCurrentSettingsAsync();
        CancelXmlTvGuideRefresh();

        using var loadCancellationTokenSource = new CancellationTokenSource();
        _loadPlaylistCancellationTokenSource = loadCancellationTokenSource;

        try
        {
            IsBusy = true;
            BeginLoadProgress("Forbereder laddning...");
            StatusMessage = "Letar efter lokal cache för listan...";
            CacheStatusText = "Cache: kontrollerar om lokal lista finns.";
            await Dispatcher.Yield(DispatcherPriority.Render);

            int? maxChannels = LoadFirstHundredOnly ? TestChannelLimit : null;
            var cachedPlaylist = await _playlistCacheStore.TryLoadAsync(source, maxChannels, loadCancellationTokenSource.Token);

            _suppressCategorySelectionChanged = true;
            SelectedBrowseSectionOption = null;
            SelectedCategoryOption = null;
            _suppressCategorySelectionChanged = false;
            SelectedChannel = null;
            ResetChannels();
            SetCatalogChannels([]);
            IsVisibleChannelListReady = false;

            if (cachedPlaylist is { Channels.Count: > 0 })
            {
                ApplyFavorites(cachedPlaylist.Channels);
                SetCatalogChannels(cachedPlaylist.Channels);
                await RestoreXmlTvGuideAsync(loadCancellationTokenSource.Token);
                StartXmlTvGuideRefresh();
                ApplyPendingBrowseSectionAfterLoad();
                _logger.Info($"Playlist cache hit. Channels={cachedPlaylist.Channels.Count}");
                CacheStatusText = $"Cache: hittad lokal lista frÅn {cachedPlaylist.CachedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}.";
                StatusMessage = $"Cache laddad från {cachedPlaylist.CachedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. VÄlj Live, Film eller Serier.";
                CompleteLoadProgress();
                return true;
            }

            StatusMessage = "Ingen cache hittades. Läser källan för att bygga kategorier.";
            _logger.Info("Playlist cache miss. Loading catalog from source.");
            CacheStatusText = "Cache: ingen lokal lista hittades, bygger en ny lokal katalog.";
            var loadedChannels = await LoadCatalogFromSourceAsync(
                source,
                maxChannels,
                "Läser in källan",
                loadCancellationTokenSource.Token);

            ApplyFavorites(loadedChannels);
            SetCatalogChannels(loadedChannels);
            await RestoreXmlTvGuideAsync(loadCancellationTokenSource.Token);
            StartXmlTvGuideRefresh();
            ApplyPendingBrowseSectionAfterLoad();
            RefreshChannelsView();
            OnPropertyChanged(nameof(ShowVideoPlaceholder));

            if (!LoadFirstHundredOnly)
            {
                await _playlistCacheStore.SaveAsync(source, loadedChannels, loadCancellationTokenSource.Token);
                CacheStatusText = "Cache: lokal lista uppdaterad för snabbare omladdning.";
            }
            else
            {
                CacheStatusText = "Cache: testläget aktivt, ingen ny cache sparades.";
            }

            StatusMessage = loadedChannels.Count == 0
                ? "Spellistan laddades men innehöll inga spelbara objekt."
                : "Kategorierna är klara. Välj Live, Film eller Serier.";

            _logger.Info($"Playlist load completed. Channels={loadedChannels.Count}, {DescribeSectionCounts(loadedChannels)}");
            CompleteLoadProgress();
            return true;
        }
        catch (OperationCanceledException)
        {
            _pendingBrowseSectionAfterLoad = null;
            _logger.Warning("Playlist load was canceled.");
            RefreshDiscoveryData(resetCategorySelection: false);
            RefreshChannelsView();
            CacheStatusText = _catalogChannels.Count > 0
                ? "Cache: den lokala katalogen ligger kvar."
                : "Cache: ingen lokal lista anvandes i den avbrutna laddningen.";
            StatusMessage = _catalogChannels.Count > 0
                ? "Laddningen avbröts. Den lokala katalogen visas fortfarande."
                : "Laddningen avbröts. Ingen ny lista lades in.";

            InterruptLoadProgress("Avbruten");
            return false;
        }
        catch (Exception exception)
        {
            _pendingBrowseSectionAfterLoad = null;
            _logger.Error($"Playlist load failed. Source={_logger.DescribeSource(source)}", exception);
            RefreshDiscoveryData(resetCategorySelection: false);
            RefreshChannelsView();
            CacheStatusText = _catalogChannels.Count > 0
                ? "Cache: lokal katalog finns kvar trots fel."
                : "Cache: ingen lokal lista kunde användas fÖr den har kÄllan.";
            StatusMessage = _catalogChannels.Count > 0
                ? $"Kunde inte uppdatera kÄllan. Cachade kategorier visas fortfarande: {exception.Message}"
                : $"Kunde inte ladda spellistan: {exception.Message}";

            InterruptLoadProgress("Fel vid laddning");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_loadPlaylistCancellationTokenSource, loadCancellationTokenSource))
            {
                _loadPlaylistCancellationTokenSource = null;
            }

            IsBusy = false;
            _logger.Info($"Playlist load finalized. SuccessCatalogCount={_catalogChannels.Count}");
        }
    }

    private void ApplyPendingBrowseSectionAfterLoad()
    {
        if (_pendingBrowseSectionAfterLoad is not { } pendingSection)
        {
            return;
        }

        _pendingBrowseSectionAfterLoad = null;
        SelectedBrowseSectionOption = _browseSections.FirstOrDefault(option => option.Section == pendingSection);
        _logger.Info($"Pending browse section applied after load: {pendingSection}");
    }

    private bool FilterChannel(object item)
    {
        if (item is not Channel channel)
        {
            return false;
        }

        if (FavoritesOnly && !channel.IsFavorite)
        {
            return false;
        }

        if (IsSearchResultsActive || string.IsNullOrWhiteSpace(_appliedSearchText))
        {
            return true;
        }

        return channel.Name.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase)
            || channel.CategoryName.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase)
            || channel.Group.Contains(_appliedSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void PlayChannel(Channel channel)
    {
        try
        {
            _logger.Info($"Starting playback. Name={channel.Name}, Type={channel.ContentType}, Source={_logger.DescribeSource(channel.Url)}");
            using var media = CreateMedia(channel);

            foreach (var option in channel.MediaOptions)
            {
                media.AddOption($":{option}");
            }

            var didStart = _mediaPlayer.Play(media);
            _hasPlaybackSession = didStart;
            ResetPlaybackProgress();
            NotifyPlaybackProperties();

            if (didStart)
            {
                _ = RememberRecentPlaybackAsync(channel);
                _ = ApplyPlaybackAudioDefaultsAsync(channel);
                EnterFullscreenMode();
                _logger.Info($"Playback started successfully. Name={channel.Name}");
            }
            else
            {
                _logger.Warning($"Playback did not start. Name={channel.Name}");
            }

            StatusMessage = didStart
                ? $"Spelar {channel.Name} i fullscreen. Tryck Escape för att gå tillbaka."
                : $"Försökte starta {channel.Name}, men spelaren returnerade inget startbesked.";
        }
        catch (Exception exception)
        {
            _logger.Error($"Playback failed. Name={channel.Name}", exception);
            ResetPlaybackState();
            StatusMessage = $"Kunde inte spela upp {channel.Name}: {exception.Message}";
        }
    }

    private Media CreateMedia(Channel channel)
    {
        if (File.Exists(channel.Url))
        {
            return new Media(_libVlc, channel.Url, FromType.FromPath);
        }

        return new Media(_libVlc, channel.Url, FromType.FromLocation);
    }

    private async Task ApplyPlaybackAudioDefaultsAsync(Channel channel)
    {
        if (channel.ContentType == ChannelContentType.Live)
        {
            return;
        }

        for (var attempt = 0; attempt < AudioDefaultsMaxAttempts; attempt++)
        {
            await Task.Delay(AudioDefaultsRetryDelay);

            if (!string.Equals(SelectedChannel?.Url, channel.Url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_hasPlaybackSession && !_mediaPlayer.IsPlaying)
            {
                continue;
            }

            var appliedChannelMode = _mediaPlayer.SetChannel(AudioOutputChannel.Stereo);
            var ensuredAudioTrack = EnsureActiveAudioTrack();

            if (appliedChannelMode || ensuredAudioTrack || _mediaPlayer.AudioTrack >= 0)
            {
                return;
            }
        }
    }

    private bool EnsureActiveAudioTrack()
    {
        if (_mediaPlayer.AudioTrack >= 0)
        {
            return false;
        }

        var trackDescriptions = _mediaPlayer.AudioTrackDescription?.ToArray() ?? [];
        var firstPlayableTrack = trackDescriptions
            .Where(track => track.Id >= 0)
            .Cast<LibVLCSharp.Shared.Structures.TrackDescription?>()
            .FirstOrDefault();

        if (!firstPlayableTrack.HasValue)
        {
            return false;
        }

        return _mediaPlayer.SetAudioTrack(firstPlayableTrack.Value.Id);
    }

    private void RefreshPlaybackTrackOptions()
    {
        if (SelectedChannel?.IsVod != true)
        {
            if (AudioTrackOptions.Count > 0 || SubtitleTrackOptions.Count > 0)
            {
                ResetPlaybackTrackOptions();
            }

            return;
        }

        var audioOptions = BuildAudioTrackOptions(_mediaPlayer.AudioTrackDescription?.ToArray() ?? []);
        var subtitleOptions = BuildSubtitleTrackOptions(_mediaPlayer.SpuDescription?.ToArray() ?? []);

        var currentAudioId = _mediaPlayer.AudioTrack;
        var currentSubtitleId = _mediaPlayer.Spu;

        var selectedAudio = audioOptions.FirstOrDefault(option => option.Id == currentAudioId);
        if (selectedAudio is null && currentAudioId < 0)
        {
            selectedAudio = audioOptions.FirstOrDefault();
        }

        var selectedSubtitle = subtitleOptions.FirstOrDefault(option => option.Id == currentSubtitleId)
            ?? subtitleOptions.FirstOrDefault(option => option.Id == -1);

        if (AreTrackOptionsEqual(AudioTrackOptions, audioOptions)
            && AreTrackOptionsEqual(SubtitleTrackOptions, subtitleOptions)
            && SelectedAudioTrackOption?.Id == selectedAudio?.Id
            && SelectedSubtitleTrackOption?.Id == selectedSubtitle?.Id)
        {
            return;
        }

        _suppressTrackSelectionChanged = true;
        try
        {
            AudioTrackOptions = audioOptions;
            SubtitleTrackOptions = subtitleOptions;
            SelectedAudioTrackOption = selectedAudio;
            SelectedSubtitleTrackOption = selectedSubtitle;
        }
        finally
        {
            _suppressTrackSelectionChanged = false;
        }
    }

    private void ResetPlaybackTrackOptions()
    {
        _suppressTrackSelectionChanged = true;
        try
        {
            AudioTrackOptions = [];
            SubtitleTrackOptions = [];
            SelectedAudioTrackOption = null;
            SelectedSubtitleTrackOption = null;
        }
        finally
        {
            _suppressTrackSelectionChanged = false;
        }
    }

    private static IReadOnlyList<PlaybackTrackOption> BuildAudioTrackOptions(IReadOnlyList<LibVLCSharp.Shared.Structures.TrackDescription> tracks)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        var options = new List<PlaybackTrackOption>(tracks.Count);
        var displayIndex = 1;
        foreach (var track in tracks)
        {
            if (track.Id < 0)
            {
                continue;
            }

            options.Add(new PlaybackTrackOption(track.Id, NormalizePlaybackTrackLabel(track.Name, $"Ljudspår {displayIndex}")));
            displayIndex++;
        }

        return options;
    }

    private static IReadOnlyList<PlaybackTrackOption> BuildSubtitleTrackOptions(IReadOnlyList<LibVLCSharp.Shared.Structures.TrackDescription> tracks)
    {
        var options = new List<PlaybackTrackOption>
        {
            new(-1, "Av"),
        };

        var displayIndex = 1;
        foreach (var track in tracks)
        {
            if (track.Id < 0)
            {
                continue;
            }

            options.Add(new PlaybackTrackOption(track.Id, NormalizePlaybackTrackLabel(track.Name, $"Undertext {displayIndex}")));
            displayIndex++;
        }

        return options;
    }

    private static string NormalizePlaybackTrackLabel(string? rawLabel, string fallbackLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel))
        {
            return fallbackLabel;
        }

        var label = rawLabel.Trim();
        return string.Equals(label, "Disable", StringComparison.OrdinalIgnoreCase)
            ? "Av"
            : label;
    }

    private static bool AreTrackOptionsEqual(IReadOnlyList<PlaybackTrackOption> left, IReadOnlyList<PlaybackTrackOption> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Id != right[index].Id
                || !string.Equals(left[index].Label, right[index].Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SaveFavoritesAsync()
    {
        try
        {
            await _favoritesStore.SaveAsync(_favoriteUrls);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte spara favoriter: {exception.Message}";
        }
    }

    private async Task RememberRecentPlaybackAsync(Channel channel)
    {
        if (channel is null)
        {
            return;
        }

        var section = channel.ContentType switch
        {
            ChannelContentType.Live => BrowseSection.Live,
            ChannelContentType.Movie => BrowseSection.Movies,
            ChannelContentType.Series => BrowseSection.Series,
            _ => BrowseSection.Live,
        };

        _recentPlaybackEntries.RemoveAll(entry =>
            entry.Section == section
            && string.Equals(entry.ChannelUrl, channel.Url, StringComparison.OrdinalIgnoreCase));
        _recentPlaybackEntries.Insert(0, new RecentPlaybackEntry(section, channel.Url, DateTimeOffset.UtcNow));
        _recentPlaybackEntries = _recentPlaybackEntries
            .OrderByDescending(entry => entry.PlayedAtUtc)
            .Take(RecentPlaybackLimit)
            .ToList();

        await SaveCurrentSettingsAsync();
        RefreshDiscoveryData(resetCategorySelection: false);

        // Do not rebuild "Senast spelade" while a title is starting.
        // Rebinding the visible list can cause WPF to clear SelectedChannel,
        // which immediately stops playback and shows the loading spinner again.
    }

    private void CancelPlaylistLoad()
    {
        if (!IsBusy)
        {
            return;
        }

        StatusMessage = "Avbryter laddning...";
        _loadPlaylistCancellationTokenSource?.Cancel();
    }

    private ICollectionView CreateChannelsView(RangeObservableCollection<Channel> channels)
    {
        var view = CollectionViewSource.GetDefaultView(channels);
        view.Filter = FilterChannel;
        return view;
    }

    private void ResetChannels()
    {
        _posterLoadCancellationTokenSource?.Cancel();
        _posterLoadCancellationTokenSource?.Dispose();
        _posterLoadCancellationTokenSource = null;
        _scrollChannelGridToTopOnAttach = true;
        ScrollChannelGridToTop();
        DetachChannelGridScrollViewer();
        _selectedCategoryChannels = [];
        _selectedCategorySeriesGroups = [];
        _visibleChannelRenderCount = 0;
        _visibleSeriesGroupRenderCount = 0;
        _isAppendingVisibleChannels = false;
        _isSeriesAppending = false;
        SelectedSeriesGroup = null;
        SeriesGroups = [];

        Channels = [];
        ChannelsView = CreateChannelsView(Channels);
        RefreshChannelsView();
    }

    private void AttachChannelGridScrollViewer()
    {
        DependencyObject? channelListRoot = null;

        if (IsLiveSection && LiveChannelListBox.Visibility == Visibility.Visible)
        {
            channelListRoot = LiveChannelListBox;
        }
        else if (IsSeriesSection && SeriesGridListBox.Visibility == Visibility.Visible)
        {
            channelListRoot = SeriesGridListBox;
        }
        else if (ChannelGridListBox.Visibility == Visibility.Visible)
        {
            channelListRoot = ChannelGridListBox;
        }

        if (channelListRoot is null)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(channelListRoot);
        if (ReferenceEquals(_channelGridScrollViewer, scrollViewer))
        {
            ScrollChannelGridToTopIfRequested();
            return;
        }

        DetachChannelGridScrollViewer();
        _channelGridScrollViewer = scrollViewer;

        if (_channelGridScrollViewer is not null)
        {
            _channelGridScrollViewer.ScrollChanged += ChannelGridScrollViewer_ScrollChanged;
        }

        ScrollChannelGridToTopIfRequested();
    }

    private void DetachChannelGridScrollViewer()
    {
        if (_channelGridScrollViewer is null)
        {
            return;
        }

        _channelGridScrollViewer.ScrollChanged -= ChannelGridScrollViewer_ScrollChanged;
        _channelGridScrollViewer = null;
    }

    private void ScrollChannelGridToTop()
    {
        if (_channelGridScrollViewer is null)
        {
            return;
        }

        _channelGridScrollViewer.ScrollToHorizontalOffset(0);
        _channelGridScrollViewer.ScrollToVerticalOffset(0);
    }

    private void ScrollChannelGridToTopIfRequested()
    {
        if (!_scrollChannelGridToTopOnAttach)
        {
            return;
        }

        _scrollChannelGridToTopOnAttach = false;
        Dispatcher.InvokeAsync(ScrollChannelGridToTop, DispatcherPriority.Loaded);
    }

    private async Task AddChannelsInBatchesAsync(
        IReadOnlyList<Channel> channels,
        CancellationToken cancellationToken,
        string progressStage)
    {
        UpdateProgressStage(progressStage);
        _parsedChannelCount = channels.Count;
        _totalBytes = null;
        _bytesRead = 0;
        ResetChannels();
        _selectedCategoryChannels = channels;
        _posterLoadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var posterLoadToken = _posterLoadCancellationTokenSource.Token;

        await AppendVisibleChannelBatchAsync(InitialVisibleChannelBatchSize, posterLoadToken);
        UpdateProgressDisplay();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await EnsureVisibleChannelBufferAsync(posterLoadToken);
        RefreshChannelsView();
    }

    private async Task AddSeriesGroupsInBatchesAsync(
        IReadOnlyList<SeriesGroupItem> seriesGroups,
        CancellationToken cancellationToken,
        string progressStage)
    {
        UpdateProgressStage(progressStage);
        _parsedChannelCount = seriesGroups.Sum(group => group.EpisodeCount);
        _totalBytes = null;
        _bytesRead = 0;
        ResetChannels();
        _selectedCategorySeriesGroups = seriesGroups;
        ApplySeriesSeasonFavorites(_selectedCategorySeriesGroups);
        _posterLoadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var posterLoadToken = _posterLoadCancellationTokenSource.Token;

        await AppendVisibleSeriesGroupBatchAsync(InitialVisibleSeriesBatchSize, posterLoadToken);
        UpdateProgressDisplay();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await EnsureVisibleSeriesGroupBufferAsync(posterLoadToken);
        RefreshChannelsView();
    }

    private async Task<bool> AppendVisibleChannelBatchAsync(int requestedCount, CancellationToken cancellationToken)
    {
        if (_isAppendingVisibleChannels || _visibleChannelRenderCount >= _selectedCategoryChannels.Count)
        {
            return false;
        }

        _isAppendingVisibleChannels = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = _selectedCategoryChannels.Count - _visibleChannelRenderCount;
            var takeCount = Math.Min(requestedCount, remaining);
            if (takeCount <= 0)
            {
                return false;
            }

            var batch = _selectedCategoryChannels
                .Skip(_visibleChannelRenderCount)
                .Take(takeCount)
                .ToArray();

            _visibleChannelRenderCount += batch.Length;
            ApplyFavorites(batch);
            using (SuspendChannelCollectionNotifications())
            {
                Channels.AddRange(batch);
            }

            if (!IsVisibleChannelListReady && Channels.Count > 0)
            {
                IsVisibleChannelListReady = true;
            }

            _ = LoadPosterImagesAsync(batch, cancellationToken);
            RefreshChannelsView();
            NotifyDiscoveryProperties();
            await Dispatcher.Yield(DispatcherPriority.Background);
            return true;
        }
        finally
        {
            _isAppendingVisibleChannels = false;
        }
    }

    private async Task<bool> AppendVisibleSeriesGroupBatchAsync(int requestedCount, CancellationToken cancellationToken)
    {
        if (_isSeriesAppending || _visibleSeriesGroupRenderCount >= _selectedCategorySeriesGroups.Count)
        {
            return false;
        }

        _isSeriesAppending = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = _selectedCategorySeriesGroups.Count - _visibleSeriesGroupRenderCount;
            var takeCount = Math.Min(requestedCount, remaining);
            if (takeCount <= 0)
            {
                return false;
            }

            var batch = _selectedCategorySeriesGroups
                .Skip(_visibleSeriesGroupRenderCount)
                .Take(takeCount)
                .ToArray();

            _visibleSeriesGroupRenderCount += batch.Length;
            SeriesGroups.AddRange(batch);

            if (!IsVisibleChannelListReady && SeriesGroups.Count > 0)
            {
                IsVisibleChannelListReady = true;
            }

            _ = LoadPosterImagesAsync(
                batch.Select(group => group.RepresentativeChannel).DistinctBy(channel => channel.Url),
                cancellationToken);

            RefreshChannelsView();
            NotifyDiscoveryProperties();
            await Dispatcher.Yield(DispatcherPriority.Background);
            return true;
        }
        finally
        {
            _isSeriesAppending = false;
        }
    }

    private async Task EnsureVisibleChannelBufferAsync(CancellationToken cancellationToken)
    {
        AttachChannelGridScrollViewer();
        if (_channelGridScrollViewer is null)
        {
            return;
        }

        while (HasMoreVisibleChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var needsMoreChannels = _channelGridScrollViewer.ExtentHeight <= _channelGridScrollViewer.ViewportHeight + IncrementalScrollLoadThreshold;
            if (!needsMoreChannels)
            {
                break;
            }

            var appended = await AppendVisibleChannelBatchAsync(IncrementalVisibleChannelBatchSize, cancellationToken);
            if (!appended)
            {
                break;
            }

            AttachChannelGridScrollViewer();
        }
    }

    private async Task EnsureVisibleSeriesGroupBufferAsync(CancellationToken cancellationToken)
    {
        AttachChannelGridScrollViewer();
        if (_channelGridScrollViewer is null)
        {
            return;
        }

        while (HasMoreVisibleChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var needsMoreGroups = _channelGridScrollViewer.ExtentHeight <= _channelGridScrollViewer.ViewportHeight + IncrementalScrollLoadThreshold;
            if (!needsMoreGroups)
            {
                break;
            }

            var appended = await AppendVisibleSeriesGroupBatchAsync(IncrementalVisibleSeriesBatchSize, cancellationToken);
            if (!appended)
            {
                break;
            }

            AttachChannelGridScrollViewer();
        }
    }

    private async Task LoadPosterImagesAsync(IEnumerable<Channel> channels, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (channel.PosterImageSource is not null)
                {
                    continue;
                }

                MediaImageSource? posterImage = null;
                foreach (var imageSource in GetChannelImageSources(channel))
                {
                    posterImage = await _posterImageService.LoadAsync(imageSource, cancellationToken);
                    if (posterImage is not null)
                    {
                        break;
                    }
                }

                if (posterImage is null)
                {
                    continue;
                }

                await Dispatcher.InvokeAsync(
                    () => channel.PosterImageSource = posterImage,
                    DispatcherPriority.Background,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static IEnumerable<string> GetChannelImageSources(Channel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
        {
            yield return channel.LogoUrl;
        }

        if (!string.IsNullOrWhiteSpace(channel.GuideInfo?.IconUrl)
            && !string.Equals(channel.GuideInfo.IconUrl, channel.LogoUrl, StringComparison.OrdinalIgnoreCase))
        {
            yield return channel.GuideInfo.IconUrl;
        }
    }

    private async Task<IReadOnlyList<Channel>> LoadCatalogFromSourceAsync(
        string source,
        int? maxChannels,
        string progressStage,
        CancellationToken cancellationToken)
    {
        _logger.Info($"Loading catalog from source. Stage={progressStage}, Source={_logger.DescribeSource(source)}, MaxChannels={(maxChannels?.ToString() ?? "all")}");
        var loadProgressGate = new object();
        M3uPlaylistService.LoadProgress? latestPendingProgress = null;
        var loadProgressPumpQueued = 0;

        void QueueLoadProgressPump()
        {
            if (Interlocked.CompareExchange(ref loadProgressPumpQueued, 1, 0) != 0)
            {
                return;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    M3uPlaylistService.LoadProgress? progressToApply;
                    lock (loadProgressGate)
                    {
                        progressToApply = latestPendingProgress;
                        latestPendingProgress = null;
                    }

                    if (progressToApply is null)
                    {
                        return;
                    }

                    _parsedChannelCount = progressToApply.ChannelsParsed;
                    _bytesRead = progressToApply.BytesRead;
                    _totalBytes = progressToApply.TotalBytes;
                    UpdateProgressStage(progressStage);
                    UpdateProgressDisplay();
                }
                finally
                {
                    Interlocked.Exchange(ref loadProgressPumpQueued, 0);

                    lock (loadProgressGate)
                    {
                        if (latestPendingProgress is not null)
                        {
                            QueueLoadProgressPump();
                        }
                    }
                }
            }, DispatcherPriority.Background);
        }

        var sourceLoadProgress = new InlineProgress<M3uPlaylistService.LoadProgress>(progress =>
        {
            lock (loadProgressGate)
            {
                latestPendingProgress = progress;
            }

            QueueLoadProgressPump();
        });

        var loadedChannels = await _playlistService.LoadAsync(
            source,
            maxChannels: maxChannels,
            batchSize: UiBatchSize,
            loadProgress: sourceLoadProgress,
            cancellationToken: cancellationToken);
        _logger.Info($"Catalog source load completed. Stage={progressStage}, Channels={loadedChannels.Count}");
        return loadedChannels;
    }

    private async Task SaveCurrentSettingsAsync()
    {
        try
        {
            await _appSettingsStore.SaveAsync(new AppSettingsStore.AppSettings
            {
                PlaylistSource = PlaylistSource.Trim(),
                XmlTvSource = XmlTvSource.Trim(),
                LoadFirstHundredOnly = LoadFirstHundredOnly,
                FavoriteSeasonKeys = _favoriteSeasonKeys
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                RecentPlaybackEntries = _recentPlaybackEntries
                    .OrderByDescending(entry => entry.PlayedAtUtc)
                    .Take(RecentPlaybackLimit)
                    .ToList(),
                CategoryDisplayPreferences = _categoryDisplayPreferences
                    .OrderBy(preference => preference.Section)
                    .ThenBy(preference => preference.SortOrder)
                    .ThenBy(preference => preference.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            });
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte spara playlistinstallningarna: {exception.Message}";
            _logger.Error("Saving app settings failed.", exception);
        }
    }

    private async Task RestoreXmlTvGuideAsync(CancellationToken cancellationToken)
    {
        CancelXmlTvGuideRefresh();

        if (!HasXmlTvSource)
        {
            _logger.Info("Skipping XMLTV restore because no XMLTV source is configured.");
            _guideByChannelUrl.Clear();
            ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
            XmlTvStatusText = "XMLTV: ingen EPG-källa sparad. Live visas utan Nu/Nästa.";
            return;
        }

        if (!_catalogChannels.Any(channel => channel.ContentType == ChannelContentType.Live))
        {
            _logger.Info("Skipping XMLTV restore because current catalog has no live channels.");
            _guideByChannelUrl.Clear();
            ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
            XmlTvStatusText = "XMLTV: inga livekanaler finns i den aktuella katalogen.";
            return;
        }

        try
        {
            _logger.Info($"Trying to restore XMLTV cache. Source={_logger.DescribeSource(XmlTvSource)}");
            var cachedGuide = await _xmlTvCacheStore.TryLoadAsync(
                PlaylistSource.Trim(),
                XmlTvSource.Trim(),
                cancellationToken).ConfigureAwait(false);

            _guideByChannelUrl = cachedGuide?.GuidesByChannelUrl is { Count: > 0 }
                ? new Dictionary<string, ChannelGuideInfo>(cachedGuide.GuidesByChannelUrl, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ChannelGuideInfo>(StringComparer.OrdinalIgnoreCase);

            ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
            QueueLiveGuideIconLoads(cancellationToken);
            _logger.Info(cachedGuide is { GuidesByChannelUrl.Count: > 0 }
                ? $"XMLTV cache restored. Guides={cachedGuide.GuidesByChannelUrl.Count}"
                : "XMLTV cache not found.");
            XmlTvStatusText = cachedGuide is { GuidesByChannelUrl.Count: > 0 }
                ? $"XMLTV: visar lokal guide-cache frÅn {cachedGuide.CachedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}."
                : "XMLTV: ingen lokal guide-cache hittades an.";
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to restore XMLTV cache.", exception);
            _guideByChannelUrl.Clear();
            ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
            XmlTvStatusText = $"XMLTV: kunde inte lasa lokal guide-cache ({exception.Message}).";
        }
    }

    private void StartXmlTvGuideRefresh()
    {
        CancelXmlTvGuideRefresh();

        if (!HasXmlTvSource || !_catalogChannels.Any(channel => channel.ContentType == ChannelContentType.Live))
        {
            _logger.Info("XMLTV background refresh not started because source or live channels are missing.");
            return;
        }

        var refreshCancellationTokenSource = new CancellationTokenSource();
        _xmlTvRefreshCancellationTokenSource = refreshCancellationTokenSource;
        _ = RefreshXmlTvGuideAsync(
            PlaylistSource.Trim(),
            XmlTvSource.Trim(),
            refreshCancellationTokenSource);
    }

    private async Task RefreshXmlTvGuideAsync(
        string playlistSource,
        string xmlTvSource,
        CancellationTokenSource refreshCancellationTokenSource)
    {
        var cancellationToken = refreshCancellationTokenSource.Token;

        try
        {
            _logger.Info($"Starting XMLTV background refresh. Source={_logger.DescribeSource(xmlTvSource)}");
            await Dispatcher.InvokeAsync(
                () => XmlTvStatusText = "XMLTV: uppdaterar liveguide i bakgrunden...",
                DispatcherPriority.Background,
                cancellationToken);

            var liveChannels = _catalogChannels
                .Where(channel => channel.ContentType == ChannelContentType.Live)
                .ToArray();

            var guideResult = await LoadXmlTvGuideWithRetriesAsync(
                xmlTvSource,
                liveChannels,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            _guideByChannelUrl = new Dictionary<string, ChannelGuideInfo>(
                guideResult.GuidesByChannelUrl,
                StringComparer.OrdinalIgnoreCase);

            await _xmlTvCacheStore.SaveAsync(
                playlistSource,
                xmlTvSource,
                guideResult.GuidesByChannelUrl,
                cancellationToken).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
                XmlTvStatusText = guideResult.MatchedChannelCount > 0
                    ? $"XMLTV: liveguide uppdaterad för {guideResult.MatchedChannelCount} kanaler."
                    : "XMLTV: ingen matchande liveguide hittades i flodet.";
            }, DispatcherPriority.Background, cancellationToken);

            QueueLiveGuideIconLoads(cancellationToken);
            _logger.Info($"XMLTV background refresh completed. MatchedChannels={guideResult.MatchedChannelCount}");
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("XMLTV background refresh canceled.");
        }
        catch (Exception exception)
        {
            _logger.Error("XMLTV background refresh failed.", exception);
            if (!refreshCancellationTokenSource.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(
                    () => XmlTvStatusText = $"XMLTV: kunde inte uppdatera guide ({exception.Message}).",
                    DispatcherPriority.Background);
            }
        }
        finally
        {
            if (ReferenceEquals(_xmlTvRefreshCancellationTokenSource, refreshCancellationTokenSource))
            {
                _xmlTvRefreshCancellationTokenSource = null;
            }

            refreshCancellationTokenSource.Dispose();
        }
    }

    private void CancelXmlTvGuideRefresh()
    {
        if (_xmlTvRefreshCancellationTokenSource is null)
        {
            return;
        }

        var refreshCancellationTokenSource = _xmlTvRefreshCancellationTokenSource;
        _xmlTvRefreshCancellationTokenSource = null;
        refreshCancellationTokenSource.Cancel();
        refreshCancellationTokenSource.Dispose();
    }

    private async Task<XmlTvService.GuideLoadResult> LoadXmlTvGuideWithRetriesAsync(
        string xmlTvSource,
        IReadOnlyCollection<Channel> liveChannels,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= XmlTvRefreshMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.Info($"XMLTV load attempt {attempt}/{XmlTvRefreshMaxAttempts} started.");
                return await _xmlTvService.LoadGuideAsync(
                    xmlTvSource,
                    liveChannels,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                _logger.Warning($"XMLTV load attempt {attempt}/{XmlTvRefreshMaxAttempts} failed: {exception.Message}");
                if (attempt >= XmlTvRefreshMaxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("XMLTV kunde inte laddas.");
    }

    private static void ApplyGuideMapToChannels(
        IEnumerable<Channel> channels,
        IReadOnlyDictionary<string, ChannelGuideInfo> guideByChannelUrl)
    {
        foreach (var channel in channels)
        {
            if (channel.ContentType != ChannelContentType.Live)
            {
                channel.GuideInfo = null;
                continue;
            }

            channel.GuideInfo = guideByChannelUrl.TryGetValue(channel.Url, out var guideInfo)
                ? guideInfo
                : null;
        }
    }

    private void QueueLiveGuideIconLoads(CancellationToken cancellationToken)
    {
        var liveChannelsNeedingIcons = _catalogChannels
            .Where(channel => channel.ContentType == ChannelContentType.Live
                && channel.PosterImageSource is null
                && (channel.LogoUrl is not null || channel.GuideInfo?.IconUrl is not null))
            .DistinctBy(channel => channel.Url)
            .ToArray();

        if (liveChannelsNeedingIcons.Length == 0)
        {
            return;
        }

        _ = LoadPosterImagesAsync(liveChannelsNeedingIcons, cancellationToken);
    }

    private void SetCatalogChannels(IReadOnlyList<Channel> channels)
    {
        _catalogChannels = channels;
        ApplyGuideMapToChannels(_catalogChannels, _guideByChannelUrl);
        RefreshDiscoveryData(resetCategorySelection: false);
        OnPropertyChanged(nameof(DashboardHintText));
        OnPropertyChanged(nameof(LiveCardSummary));
        OnPropertyChanged(nameof(MoviesCardSummary));
        OnPropertyChanged(nameof(SeriesCardSummary));
    }

    private async Task LoadSelectedCategoryFromCacheAsync(CategoryOption selectedCategory, bool clearCurrentPlaybackSelection = true)
    {
        if (CurrentSection is null)
        {
            return;
        }

        try
        {
            _logger.Info($"Loading category from cache. Section={CurrentSection}, Category={selectedCategory.Label}, ClearPlayback={clearCurrentPlaybackSelection}");
            if (clearCurrentPlaybackSelection)
            {
                SelectedChannel = null;
            }
            IsBrowserContentLoading = true;
            IsLoadProgressVisible = false;
            LoadProgressText = string.Empty;
            StatusMessage = $"Bygger {selectedCategory.Label} frÅn lokal data...";
            await Dispatcher.Yield(DispatcherPriority.Render);

            var selectedChannels = GetSelectedCategoryChannels();
            ApplyFavorites(selectedChannels);

            if (CurrentSection == BrowseSection.Series)
            {
                var seriesGroups = _seriesCatalogService.BuildGroups(selectedChannels);
                ApplySeriesSeasonFavorites(seriesGroups);
                await AddSeriesGroupsInBatchesAsync(seriesGroups, CancellationToken.None, $"Visar {selectedCategory.Label}");
                StatusMessage = seriesGroups.Count == 0
                    ? $"Inga serier hittades i {selectedCategory.Label}."
                    : $"Visar {seriesGroups.Count} serier i {selectedCategory.Label}.";
                _logger.Info($"Category cache load completed for series. Category={selectedCategory.Label}, Groups={seriesGroups.Count}");
            }
            else
            {
                await AddChannelsInBatchesAsync(selectedChannels, CancellationToken.None, $"Visar {selectedCategory.Label}");
                StatusMessage = selectedChannels.Count == 0
                    ? $"Inga objekt hittades i {selectedCategory.Label}."
                    : $"Visar {selectedChannels.Count} objekt i {selectedCategory.Label}.";
                _logger.Info($"Category cache load completed. Category={selectedCategory.Label}, Channels={selectedChannels.Count}");
            }

            CacheStatusText = IsFavoritesCategory(selectedCategory)
                ? "Cache: visar lokala favoriter ovanpå den sparade katalogen."
                : "Cache: visar lokal spelhistorik ovanpå den sparade katalogen.";
            IsLoadProgressVisible = false;
            IsVisibleChannelListReady = true;
            RefreshChannelsView();
        }
        catch (OperationCanceledException)
        {
            _logger.Warning($"Category cache load canceled. Category={selectedCategory.Label}");
        }
        finally
        {
            IsBrowserContentLoading = false;
        }
    }

    private async Task RefreshSelectedCategoryAsync(CategoryOption selectedCategory)
    {
        if (IsBusy)
        {
            _logger.Warning($"RefreshSelectedCategoryAsync ignored because the app is busy. Category={selectedCategory.Label}");
            return;
        }

        var source = PlaylistSource.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            StatusMessage = "Ange en M3U-länk eller välj en lokal fil för att fortsätta.";
            return;
        }

        CancelXmlTvGuideRefresh();
        using var loadCancellationTokenSource = new CancellationTokenSource();
        _loadPlaylistCancellationTokenSource = loadCancellationTokenSource;

        try
        {
            _logger.Info($"Refreshing selected category. Section={CurrentSection}, Category={selectedCategory.Label}, Source={_logger.DescribeSource(source)}");
            IsBusy = true;
            IsBrowserContentLoading = true;
            BeginLoadProgress($"Uppdaterar {selectedCategory.Label}...");
            StatusMessage = $"Uppdaterar {selectedCategory.Label} från källan...";
            CacheStatusText = "Cache: visar cachade kategorier medan den valda kategorin uppdateras.";
            SelectedChannel = null;
            await Dispatcher.Yield(DispatcherPriority.Render);

            int? maxChannels = LoadFirstHundredOnly ? TestChannelLimit : null;
            IReadOnlyList<Channel> loadedChannels;
            var apiResult = CurrentSection is BrowseSection section
                ? await _xtreamApiService.TryLoadCategoryAsync(
                    source,
                    section,
                    selectedCategory.Key,
                    _catalogChannels,
                    maxChannels,
                    loadCancellationTokenSource.Token)
                : null;

            if (apiResult is not null)
            {
                loadedChannels = MergeUpdatedCategoryIntoCatalog(selectedCategory.Key, apiResult.Channels);
                _logger.Info($"Category refresh using provider API. Category={selectedCategory.Label}, Provider={apiResult.ProviderName}, UpdatedChannels={apiResult.Channels.Count}");
                CacheStatusText = $"Cache: uppdaterar vald kategori direkt via {apiResult.ProviderName}.";
                UpdateProgressStage($"Bygger {selectedCategory.Label}");
                UpdateProgressDisplay();
            }
            else
            {
                CacheStatusText = "Cache: inget direkt API hittades, läser hela källan för att uppdatera vald kategori.";
                _logger.Info($"Category refresh falling back to full catalog load. Category={selectedCategory.Label}");
                loadedChannels = await LoadCatalogFromSourceAsync(
                    source,
                    maxChannels,
                    $"Uppdaterar {selectedCategory.Label}",
                    loadCancellationTokenSource.Token);
            }

            loadCancellationTokenSource.Token.ThrowIfCancellationRequested();

            ApplyFavorites(loadedChannels);
            var previousCategoryChannels = GetSelectedCategoryChannels();
            var updatedCategoryChannels = GetSelectedCategoryChannels(loadedChannels);
            var updatedSeriesGroups = CurrentSection == BrowseSection.Series
                ? _seriesCatalogService.BuildGroups(updatedCategoryChannels)
                : [];
            ApplySeriesSeasonFavorites(updatedSeriesGroups);
            var hadChanges = !PlaylistLooksUnchanged(previousCategoryChannels, updatedCategoryChannels);
            SetCatalogChannels(loadedChannels);
            if (CurrentSection == BrowseSection.Live)
            {
                await RestoreXmlTvGuideAsync(loadCancellationTokenSource.Token);
                StartXmlTvGuideRefresh();
            }

            if (CurrentSection == BrowseSection.Series)
            {
                await AddSeriesGroupsInBatchesAsync(
                    updatedSeriesGroups,
                    loadCancellationTokenSource.Token,
                    $"Bygger {selectedCategory.Label}");
            }
            else
            {
                await AddChannelsInBatchesAsync(
                    updatedCategoryChannels,
                    loadCancellationTokenSource.Token,
                    $"Bygger {selectedCategory.Label}");
            }

            IsVisibleChannelListReady = true;

            if (!LoadFirstHundredOnly)
            {
                await _playlistCacheStore.SaveAsync(source, loadedChannels, loadCancellationTokenSource.Token);
                CacheStatusText = hadChanges
                    ? apiResult is not null
                        ? "Cache: den valda kategorin uppdaterades direkt via API och sparades lokalt."
                        : "Cache: lokal lista uppdaterad efter senaste kontrollen."
                    : "Cache: inga ändringar hittades. Den lokala listan är fortfarande aktuell.";
            }
            else
            {
                CacheStatusText = "Cache: testläget aktivt, ingen ny cache sparades.";
            }

            StatusMessage = CurrentSection == BrowseSection.Series
                ? SeriesGroups.Count == 0
                    ? $"Inga serier hittades i {selectedCategory.Label} efter uppdateringen."
                    : $"Visar {SeriesGroups.Count} serier i {selectedCategory.Label}."
                : Channels.Count == 0
                    ? $"Inga objekt hittades i {selectedCategory.Label} efter uppdateringen."
                    : $"Visar {Channels.Count} objekt i {selectedCategory.Label}.";
            _logger.Info(CurrentSection == BrowseSection.Series
                ? $"Category refresh completed for series. Category={selectedCategory.Label}, Groups={SeriesGroups.Count}, Changed={hadChanges}"
                : $"Category refresh completed. Category={selectedCategory.Label}, Channels={Channels.Count}, Changed={hadChanges}");
            CompleteLoadProgress();
        }
        catch (OperationCanceledException)
        {
            _logger.Warning($"Category refresh canceled. Category={selectedCategory.Label}");
            CacheStatusText = "Cache: cachade kategorier ligger kvar efter avbruten uppdatering.";
            StatusMessage = "Uppdateringen avbröts. Välj en kategori igen för ett nytt försök.";
            InterruptLoadProgress("Avbruten");
        }
        catch (Exception exception)
        {
            _logger.Error($"Category refresh failed. Category={selectedCategory.Label}", exception);
            CacheStatusText = "Cache: cachade kategorier ligger kvar trots fel vid uppdateringen.";
            StatusMessage = $"Kunde inte uppdatera vald kategori: {exception.Message}";
            InterruptLoadProgress("Fel vid laddning");
        }
        finally
        {
            if (ReferenceEquals(_loadPlaylistCancellationTokenSource, loadCancellationTokenSource))
            {
                _loadPlaylistCancellationTokenSource = null;
            }

            IsBrowserContentLoading = false;
            IsBusy = false;
            OnPropertyChanged(nameof(ChannelListPlaceholderText));
            _logger.Info($"Category refresh finalized. Category={selectedCategory.Label}");
        }
    }

    private void ApplyFavorites(IEnumerable<Channel> channels)
    {
        foreach (var channel in channels)
        {
            channel.IsFavorite = _favoriteUrls.Contains(channel.Url);
        }
    }

    private void ApplySeriesSeasonFavorites(IEnumerable<SeriesGroupItem> groups)
    {
        foreach (var group in groups)
        {
            foreach (var season in group.Seasons)
            {
                season.IsFavorite = _favoriteSeasonKeys.Contains(season.FavoriteKey);
            }
        }
    }

    private void BeginLoadProgress(string progressStage)
    {
        _parsedChannelCount = 0;
        _bytesRead = 0;
        _totalBytes = null;
        UpdateProgressStage(progressStage);
        IsLoadProgressVisible = true;
        IsLoadProgressIndeterminate = true;
        LoadProgressValue = 0;
        LoadProgressText = $"{progressStage} - förbereder...";
    }

    private void ScheduleSearchRefresh()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        ApplySearchFilterNow();
    }

    private void ApplySearchFilterNow()
    {
        _searchDebounceTimer.Stop();
        _appliedSearchText = SearchText.Trim();
        RefreshChannelsView();
        NotifyDiscoveryProperties();
    }

    private void RefreshChannelsView()
    {
        if (HasActiveFilter())
        {
            ChannelsView.Refresh();
        }

        UpdateVisibleChannelCount();
        OnPropertyChanged(nameof(ChannelCountText));
        OnPropertyChanged(nameof(ChannelListSubtitle));
    }

    private void RefreshDiscoveryData(bool resetCategorySelection)
    {
        RebuildCategoryOptions(resetCategorySelection);
        RebuildPlaylistCategoryItems();
        RefreshLatestItems();
        NotifyDiscoveryProperties();
    }

    private void RebuildCategoryOptions(bool resetSelection)
    {
        if (!HasSelectedBrowseSection)
        {
            CategoryOptions = [];

            if (SelectedCategoryOption is not null)
            {
                _suppressCategorySelectionChanged = true;
                SelectedCategoryOption = null;
                _suppressCategorySelectionChanged = false;
            }

            return;
        }

        var categories = GetCategoryOptionsForSection(CurrentSection!.Value, includeHidden: false);

        var previousKey = !resetSelection && SelectedCategoryOption is not null
            ? SelectedCategoryOption.Key
            : null;

        CategoryOptions = new RangeObservableCollection<CategoryOption>(categories);

        var replacementSelection = previousKey is null
            ? null
            : categories.FirstOrDefault(option => string.Equals(option.Key, previousKey, StringComparison.OrdinalIgnoreCase));

        if (!Equals(SelectedCategoryOption, replacementSelection))
        {
            _suppressCategorySelectionChanged = true;
            SelectedCategoryOption = replacementSelection;
            _suppressCategorySelectionChanged = false;
        }
    }

    private void RefreshLatestItems()
    {
        if (!HasSelectedBrowseSection)
        {
            LatestItems = [];
            return;
        }

        var latest = GetChannelsForCurrentSection()
            .Take(LatestItemLimit)
            .ToList();

        LatestItems = new RangeObservableCollection<Channel>(latest);
    }

    private void RebuildPlaylistCategoryItems()
    {
        if (SelectedCategoryManagerSectionOption is null)
        {
            PlaylistCategoryItems = [];
            return;
        }

        var section = SelectedCategoryManagerSectionOption.Section;
        var preferenceMap = _categoryDisplayPreferences
            .Where(item => item.Section == section)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.SortOrder).First(),
                StringComparer.OrdinalIgnoreCase);

        var items = GetCategoryOptionsForSection(section, includeHidden: true)
            .Select(category =>
            {
                preferenceMap.TryGetValue(category.Key, out var preference);

                return new PlaylistCategoryItem
                {
                    Section = section,
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

        PlaylistCategoryItems = new RangeObservableCollection<PlaylistCategoryItem>(items);
    }

    private bool MovePlaylistCategoryItem(PlaylistCategoryItem item, int direction)
    {
        var currentIndex = PlaylistCategoryItems.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        var items = PlaylistCategoryItems.ToList();
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
        PlaylistCategoryItems = new RangeObservableCollection<PlaylistCategoryItem>(items);
        return true;
    }

    private async Task PersistPlaylistCategoryPreferencesAsync(BrowseSection section)
    {
        if (SelectedCategoryManagerSectionOption?.Section == section && PlaylistCategoryItems.Count > 0)
        {
            _categoryDisplayPreferences.RemoveAll(preference => preference.Section == section);
            _categoryDisplayPreferences.AddRange(
                PlaylistCategoryItems.Select((item, index) => new CategoryDisplayPreference(
                    section,
                    item.Key,
                    item.IsVisible,
                    index)));
        }

        await SaveCurrentSettingsAsync();
        RefreshDiscoveryData(resetCategorySelection: false);
        RefreshChannelsView();
        RebuildPlaylistCategoryItems();
        OnPropertyChanged(nameof(PlaylistCategoryManagerHintText));
    }

    private async Task SetAllPlaylistCategoryVisibilityAsync(bool isVisible)
    {
        if (SelectedCategoryManagerSectionOption is null || PlaylistCategoryItems.Count == 0)
        {
            return;
        }

        foreach (var item in PlaylistCategoryItems)
        {
            item.IsVisible = isVisible;
        }

        await PersistPlaylistCategoryPreferencesAsync(SelectedCategoryManagerSectionOption.Section);
    }

    private IEnumerable<Channel> GetChannelsForCurrentSection()
    {
        return !HasSelectedBrowseSection || CurrentSection is null
            ? Enumerable.Empty<Channel>()
            : GetChannelsForSection(CurrentSection.Value);
    }

    private bool MatchesCurrentSection(Channel channel)
    {
        return CurrentSection is not null
            && MatchesSection(channel, CurrentSection.Value);
    }

    private bool MatchesSection(Channel channel, BrowseSection section)
    {
        return section switch
        {
            BrowseSection.Live => channel.ContentType == ChannelContentType.Live,
            BrowseSection.Movies => channel.ContentType == ChannelContentType.Movie,
            BrowseSection.Series => channel.ContentType == ChannelContentType.Series,
            _ => false,
        };
    }

    private IEnumerable<Channel> GetChannelsForSection(BrowseSection section, bool includeHiddenCategories = false)
    {
        return _catalogChannels.Where(channel =>
            MatchesSection(channel, section)
            && (includeHiddenCategories || IsCategoryVisible(section, channel.CategoryName)));
    }

    private List<CategoryOption> GetCategoryOptionsForSection(BrowseSection section, bool includeHidden)
    {
        var categories = _catalogChannels
            .Where(channel => MatchesSection(channel, section))
            .GroupBy(channel => channel.CategoryName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CategoryOption(group.Key, group.Key, group.Count()))
            .ToList();

        var visibleCategories = ApplyCategoryDisplayPreferences(section, categories, includeHidden);
        if (includeHidden)
        {
            return visibleCategories;
        }

        return [.. BuildSpecialCategoryOptions(section), .. visibleCategories];
    }

    private IReadOnlyList<Channel> GetChannelsForSelectedCategory(
        BrowseSection section,
        CategoryOption category,
        IReadOnlyList<Channel> sourceChannels)
    {
        if (IsFavoritesCategory(category))
        {
            return GetFavoriteChannelsForSection(section, sourceChannels);
        }

        if (IsRecentCategory(category))
        {
            return GetRecentChannelsForSection(section, sourceChannels);
        }

        return sourceChannels
            .Where(channel => MatchesSection(channel, section)
                && string.Equals(channel.CategoryName, category.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<CategoryOption> BuildSpecialCategoryOptions(BrowseSection section)
    {
        var favoritesCount = section == BrowseSection.Series
            ? _seriesCatalogService.BuildGroups(GetFavoriteChannelsForSection(section, _catalogChannels)).Count
            : GetFavoriteChannelsForSection(section, _catalogChannels).Count;
        var specialCategories = new List<CategoryOption>
        {
            new(FavoritesCategoryKey, "Favoriter", favoritesCount),
        };

        if (section != BrowseSection.Live)
        {
            var recentCount = section == BrowseSection.Series
                ? _seriesCatalogService.BuildGroups(GetRecentChannelsForSection(section, _catalogChannels)).Count
                : GetRecentChannelsForSection(section, _catalogChannels).Count;

            specialCategories.Add(new CategoryOption(RecentCategoryKey, "Senast spelade", recentCount));
        }

        return specialCategories;
    }

    private IReadOnlyList<Channel> GetFavoriteChannelsForSection(BrowseSection section, IReadOnlyList<Channel> sourceChannels)
    {
        var sectionChannels = sourceChannels.Where(channel => MatchesSection(channel, section));

        if (section == BrowseSection.Series)
        {
            return sectionChannels
                .Where(channel =>
                {
                    var seasonKey = _seriesCatalogService.TryGetSeasonFavoriteKey(channel);
                    return seasonKey is not null && _favoriteSeasonKeys.Contains(seasonKey);
                })
                .ToList();
        }

        return sectionChannels
            .Where(channel => _favoriteUrls.Contains(channel.Url))
            .ToList();
    }

    private IReadOnlyList<Channel> GetRecentChannelsForSection(BrowseSection section, IReadOnlyList<Channel> sourceChannels)
    {
        var recentLookup = _recentPlaybackEntries
            .Where(entry => entry.Section == section)
            .GroupBy(entry => entry.ChannelUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(entry => entry.PlayedAtUtc),
                StringComparer.OrdinalIgnoreCase);

        return sourceChannels
            .Where(channel => MatchesSection(channel, section) && recentLookup.ContainsKey(channel.Url))
            .OrderByDescending(channel => recentLookup[channel.Url])
            .ToList();
    }

    private static bool IsSpecialCategory(CategoryOption? category)
    {
        return category is not null
            && (string.Equals(category.Key, FavoritesCategoryKey, StringComparison.Ordinal)
                || string.Equals(category.Key, RecentCategoryKey, StringComparison.Ordinal));
    }

    private static bool IsFavoritesCategory(CategoryOption? category)
    {
        return category is not null
            && string.Equals(category.Key, FavoritesCategoryKey, StringComparison.Ordinal);
    }

    private static bool IsRecentCategory(CategoryOption? category)
    {
        return category is not null
            && string.Equals(category.Key, RecentCategoryKey, StringComparison.Ordinal);
    }

    private List<CategoryOption> ApplyCategoryDisplayPreferences(
        BrowseSection section,
        IEnumerable<CategoryOption> categories,
        bool includeHidden)
    {
        var preferenceMap = _categoryDisplayPreferences
            .Where(preference => preference.Section == section)
            .GroupBy(preference => preference.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(preference => preference.SortOrder).First(),
                StringComparer.OrdinalIgnoreCase);

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

    private bool IsCategoryVisible(BrowseSection section, string categoryKey)
    {
        var preference = _categoryDisplayPreferences
            .Where(item => item.Section == section)
            .OrderBy(item => item.SortOrder)
            .FirstOrDefault(item => string.Equals(item.Key, categoryKey, StringComparison.OrdinalIgnoreCase));

        return preference?.IsVisible ?? true;
    }

    private bool MatchesSelectedCategory(Channel channel)
    {
        return SelectedCategoryOption is not null
            && CurrentSection is BrowseSection section
            && GetChannelsForSelectedCategory(section, SelectedCategoryOption, _catalogChannels)
                .Any(candidate => string.Equals(candidate.Url, channel.Url, StringComparison.OrdinalIgnoreCase));
    }

    private string GetCurrentSectionLabel()
    {
        return CurrentSection switch
        {
            BrowseSection.Live => "Live",
            BrowseSection.Movies => "Film",
            BrowseSection.Series => "Serier",
            _ => "Välj sektion",
        };
    }

    private string GetDashboardSectionSummary(BrowseSection section, string emptyLabel)
    {
        if (_catalogChannels.Count == 0)
        {
            return HasPlaylistSource
                ? $"Klar att lasa {emptyLabel.ToLowerInvariant()}"
                : $"Öppna Playlists för {emptyLabel.ToLowerInvariant()}";
        }

        var count = GetChannelsForSection(section).Count();

        return count == 0
            ? $"Inga objekt i {emptyLabel.ToLowerInvariant()}"
            : $"{count} objekt i cache";
    }

    private string GetShortPlaylistDisplayName()
    {
        if (!HasPlaylistSource)
        {
            return "ingen";
        }

        if (Uri.TryCreate(PlaylistSource, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.Host) ? PlaylistSource : uri.Host;
        }

        return Path.GetFileName(PlaylistSource);
    }

    private static string DescribeSectionCounts(IReadOnlyCollection<Channel> channels)
    {
        var liveCount = channels.Count(channel => channel.ContentType == ChannelContentType.Live);
        var movieCount = channels.Count(channel => channel.ContentType == ChannelContentType.Movie);
        var seriesCount = channels.Count(channel => channel.ContentType == ChannelContentType.Series);
        return $"live={liveCount}, film={movieCount}, serier={seriesCount}, total={channels.Count}";
    }

    private void UpdateVisibleChannelCount()
    {
        if (IsSeriesSection)
        {
            _visibleChannelCount = HasSelectedSeriesGroup
                ? CurrentSeriesEpisodes.Count
                : SeriesGroups.Count;
            return;
        }

        _visibleChannelCount = HasActiveFilter()
            ? ChannelsView.Cast<object>().Count()
            : Channels.Count;
    }

    private bool HasActiveFilter()
    {
        return FavoritesOnly;
    }

    private void CompleteLoadProgress()
    {
        UpdateProgressStage("Klar");
        IsLoadProgressVisible = true;
        IsLoadProgressIndeterminate = false;
        LoadProgressValue = 100;
        var visibleObjectCount = HasSelectedCategory ? Channels.Count : _catalogChannels.Count;
        LoadProgressText = LoadFirstHundredOnly
            ? $"Klar - {visibleObjectCount} objekt behandlade i testläget"
            : $"Klar - {visibleObjectCount} objekt klara";
    }

    private void InterruptLoadProgress(string stage)
    {
        if (!IsLoadProgressVisible && Channels.Count == 0)
        {
            return;
        }

        UpdateProgressStage(stage);
        UpdateProgressDisplay();
    }

    private void UpdateProgressStage(string progressStage)
    {
        _loadProgressStage = progressStage;
    }

    private void UpdateProgressDisplay()
    {
        if (string.IsNullOrWhiteSpace(_loadProgressStage))
        {
            return;
        }

        IsLoadProgressVisible = true;

        if (LoadFirstHundredOnly)
        {
            IsLoadProgressIndeterminate = false;
            LoadProgressValue = Math.Clamp((double)Math.Min(_parsedChannelCount, TestChannelLimit) / TestChannelLimit * 100d, 0d, 100d);
        }
        else if (_totalBytes is > 0)
        {
            IsLoadProgressIndeterminate = false;
            LoadProgressValue = Math.Clamp((double)_bytesRead / _totalBytes.Value * 100d, 0d, 100d);
        }
        else
        {
            IsLoadProgressIndeterminate = true;
            LoadProgressValue = 0;
        }

        var bytesText = _bytesRead > 0
            ? _totalBytes is > 0
                ? $"{FormatBytes(_bytesRead)} / {FormatBytes(_totalBytes.Value)}"
                : FormatBytes(_bytesRead)
            : "0 B";
        var visibleObjectCount = HasSelectedCategory ? Channels.Count : _catalogChannels.Count;

        LoadProgressText = LoadFirstHundredOnly
            ? $"{_loadProgressStage} - hittat {_parsedChannelCount}/{TestChannelLimit} objekt - visar {visibleObjectCount} - läst {bytesText}"
            : $"{_loadProgressStage} - hittat {_parsedChannelCount} objekt - visar {visibleObjectCount} - läst {bytesText}";
    }

    private IReadOnlyList<Channel> GetSelectedCategoryChannels(IReadOnlyList<Channel>? sourceChannels = null)
    {
        if (!HasSelectedBrowseSection || SelectedCategoryOption is null || CurrentSection is null)
        {
            return [];
        }

        return GetChannelsForSelectedCategory(CurrentSection.Value, SelectedCategoryOption, sourceChannels ?? _catalogChannels);
    }

    private IReadOnlyList<Channel> MergeUpdatedCategoryIntoCatalog(string categoryKey, IReadOnlyList<Channel> updatedCategoryChannels)
    {
        if (CurrentSection is null)
        {
            return _catalogChannels;
        }

        var mergedChannels = new List<Channel>(_catalogChannels.Count + updatedCategoryChannels.Count);
        var insertedUpdatedCategory = false;

        foreach (var channel in _catalogChannels)
        {
            var belongsToSelectedCategory = MatchesCurrentSection(channel)
                && string.Equals(channel.CategoryName, categoryKey, StringComparison.OrdinalIgnoreCase);

            if (belongsToSelectedCategory)
            {
                if (!insertedUpdatedCategory)
                {
                    mergedChannels.AddRange(updatedCategoryChannels);
                    insertedUpdatedCategory = true;
                }

                continue;
            }

            mergedChannels.Add(channel);
        }

        if (!insertedUpdatedCategory)
        {
            mergedChannels.AddRange(updatedCategoryChannels);
        }

        return mergedChannels;
    }

    private static bool PlaylistLooksUnchanged(IReadOnlyList<Channel> current, IReadOnlyList<Channel> updated)
    {
        if (ReferenceEquals(current, updated))
        {
            return true;
        }

        if (current.Count != updated.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = updated[index];

            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Url, right.Url, StringComparison.Ordinal)
                || !string.Equals(left.Group, right.Group, StringComparison.Ordinal)
                || !string.Equals(left.LogoUrl, right.LogoUrl, StringComparison.Ordinal)
                || !string.Equals(left.TvgId, right.TvgId, StringComparison.Ordinal)
                || !string.Equals(left.TvgName, right.TvgName, StringComparison.Ordinal)
                || left.MediaOptions.Count != right.MediaOptions.Count)
            {
                return false;
            }

            for (var optionIndex = 0; optionIndex < left.MediaOptions.Count; optionIndex++)
            {
                if (!string.Equals(left.MediaOptions[optionIndex], right.MediaOptions[optionIndex], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.0} {units[unitIndex]}";
    }

    private static string FormatPlaybackTime(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "00:00";
        }

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        UpdatePlaybackProgressFromPlayer();
        NotifyPlaybackProperties();
    }

    private void UpdatePlaybackProgressFromPlayer()
    {
        if (!_hasPlaybackSession && !_mediaPlayer.IsPlaying)
        {
            PlaybackPositionMilliseconds = 0;
            PlaybackDurationMilliseconds = 0;
            ResetPlaybackTrackOptions();
            return;
        }

        if (!_isSeekInteractionActive)
        {
            PlaybackPositionMilliseconds = Math.Max(0, _mediaPlayer.Time);
        }

        PlaybackDurationMilliseconds = Math.Max(0, _mediaPlayer.Length);

        var muted = _mediaPlayer.Mute;
        if (muted != IsMuted)
        {
            IsMuted = muted;
        }

        var currentVolume = Math.Clamp(_mediaPlayer.Volume, 0, 100);
        if (currentVolume != _volume)
        {
            _volume = currentVolume;
            OnPropertyChanged(nameof(Volume));
        }

        RefreshPlaybackTrackOptions();
    }

    private void NotifyPlaybackProperties()
    {
        OnPropertyChanged(nameof(CanControlPlayback));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanSelectAudioTrack));
        OnPropertyChanged(nameof(CanSelectSubtitleTrack));
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(FullscreenButtonText));
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlaybackModeText));
        OnPropertyChanged(nameof(PlayerControlHint));
        OnPropertyChanged(nameof(ShowAudioTrackSelector));
        OnPropertyChanged(nameof(ShowSubtitleTrackSelector));
        OnPropertyChanged(nameof(ShowPlaybackTrackSelectors));
    }

    private void ResetPlaybackProgress()
    {
        PlaybackPositionMilliseconds = 0;
        PlaybackDurationMilliseconds = 0;
        NotifyPlaybackProperties();
    }

    private void ResetPlaybackState()
    {
        _hasPlaybackSession = false;
        ResetPlaybackTrackOptions();
        ResetPlaybackProgress();
        NotifyPlaybackProperties();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        RevealFullscreenChrome();

        if (SelectedChannel is null)
        {
            return;
        }

        try
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                StatusMessage = $"Pausad: {SelectedChannel.Name}";
            }
            else if (_hasPlaybackSession)
            {
                _mediaPlayer.Play();
                StatusMessage = $"Fortsatter spela: {SelectedChannel.Name}";
            }
            else
            {
                PlayChannel(SelectedChannel);
                return;
            }

            NotifyPlaybackProperties();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte toggla uppspelning: {exception.Message}";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        RevealFullscreenChrome();

        if (SelectedChannel is null && !_hasPlaybackSession)
        {
            return;
        }

        try
        {
            _mediaPlayer.Stop();
            ResetPlaybackState();

            if (IsFullscreen)
            {
                SelectedChannel = null;
                return;
            }

            StatusMessage = "Uppspelningen stoppades.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte stoppa uppspelningen: {exception.Message}";
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        RevealFullscreenChrome();

        try
        {
            var newMuteState = !IsMuted;
            _mediaPlayer.Mute = newMuteState;
            IsMuted = newMuteState;
            NotifyPlaybackProperties();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte ändra ljudläget: {exception.Message}";
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RevealFullscreenChrome();

        if (!IsLoaded)
        {
            return;
        }

        Volume = (int)Math.Round(e.NewValue);
        NotifyPlaybackProperties();
    }

    private void AudioTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RevealFullscreenChrome();

        if (_suppressTrackSelectionChanged
            || SelectedChannel?.IsVod != true
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not PlaybackTrackOption selectedOption)
        {
            return;
        }

        if (_mediaPlayer.AudioTrack == selectedOption.Id)
        {
            return;
        }

        try
        {
            var changed = _mediaPlayer.SetAudioTrack(selectedOption.Id);
            StatusMessage = changed
                ? $"Ljudspår: {selectedOption.Label}"
                : $"Kunde inte byta till ljudspår {selectedOption.Label}.";
            RefreshPlaybackTrackOptions();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte byta ljudspår: {exception.Message}";
        }
    }

    private void SubtitleTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RevealFullscreenChrome();

        if (_suppressTrackSelectionChanged
            || SelectedChannel?.IsVod != true
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not PlaybackTrackOption selectedOption)
        {
            return;
        }

        if (_mediaPlayer.Spu == selectedOption.Id)
        {
            return;
        }

        try
        {
            var changed = _mediaPlayer.SetSpu(selectedOption.Id);
            StatusMessage = changed
                ? selectedOption.Id < 0
                    ? "Undertexter avstängda."
                    : $"Undertext: {selectedOption.Label}"
                : $"Kunde inte byta undertext till {selectedOption.Label}.";
            RefreshPlaybackTrackOptions();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte byta undertext: {exception.Message}";
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RevealFullscreenChrome();

        if (!CanSeek)
        {
            return;
        }

        _isSeekInteractionActive = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RevealFullscreenChrome();

        if (!CanSeek)
        {
            _isSeekInteractionActive = false;
            return;
        }

        try
        {
            if (sender is not System.Windows.Controls.Slider slider)
            {
                return;
            }

            var seekTarget = (long)slider.Value;
            _mediaPlayer.Time = seekTarget;
            PlaybackPositionMilliseconds = seekTarget;
            StatusMessage = $"Hoppade till {FormatPlaybackTime(seekTarget)}.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Kunde inte hoppa i videon: {exception.Message}";
        }
        finally
        {
            _isSeekInteractionActive = false;
            NotifyPlaybackProperties();
        }
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        RevealFullscreenChrome();
        ToggleFullscreen();
    }

    private void EnterFullscreenMode()
    {
        if (IsFullscreen)
        {
            RevealFullscreenChrome();
            return;
        }

        _restoreBounds = new Rect(Left, Top, Width, Height);
        _restoreWindowState = WindowState;
        _restoreWindowStyle = WindowStyle;
        _restoreResizeMode = ResizeMode;
        _restoreTopmost = Topmost;

        var screenBounds = GetCurrentScreenBounds();

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;
        Topmost = true;
        IsFullscreen = true;
        _lastFullscreenCursorPosition = GetCursorScreenPosition();
        _fullscreenCursorTrackerTimer.Start();
        Dispatcher.InvokeAsync(UpdateFullscreenPopupLayout, DispatcherPriority.Loaded);
        RevealFullscreenChrome();
    }

    private void RestoreFromFullscreen()
    {
        if (!IsFullscreen)
        {
            return;
        }

        Topmost = _restoreTopmost;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        _fullscreenChromeTimer.Stop();
        _fullscreenCursorTrackerTimer.Stop();
        Mouse.OverrideCursor = null;
        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        Width = _restoreBounds.Width;
        Height = _restoreBounds.Height;
        WindowState = _restoreWindowState;
        IsFullscreen = false;
    }

    private void RevealFullscreenChrome()
    {
        if (!ShowFullscreenPlayer)
        {
            return;
        }

        SetFullscreenChromeVisible(true);
        _fullscreenChromeTimer.Stop();
        _fullscreenChromeTimer.Start();
    }

    private void UpdateFullscreenPopupLayout()
    {
        var popupWidth = Math.Max(320, FullscreenHost.ActualWidth - 48);
        var popupLeft = Left + 24;
        var popupTop = Top + Math.Max(24, FullscreenHost.ActualHeight - _fullscreenControlsPopupHeight - 24);

        if (Math.Abs(_fullscreenChromeWidth - popupWidth) > 0.5)
        {
            _fullscreenChromeWidth = popupWidth;
            OnPropertyChanged(nameof(FullscreenChromeWidth));
        }

        if (Math.Abs(_fullscreenControlsPopupLeft - popupLeft) > 0.5)
        {
            _fullscreenControlsPopupLeft = popupLeft;
            OnPropertyChanged(nameof(FullscreenControlsPopupLeft));
        }

        if (Math.Abs(_fullscreenControlsPopupTop - popupTop) > 0.5)
        {
            _fullscreenControlsPopupTop = popupTop;
            OnPropertyChanged(nameof(FullscreenControlsPopupTop));
        }
    }

    private void SetFullscreenChromeVisible(bool isVisible)
    {
        ShowFullscreenChrome = isVisible;
        OnPropertyChanged(nameof(ShowFullscreenChromePopup));
        Mouse.OverrideCursor = isVisible ? null : Cursors.None;
    }

    private void ToggleFullscreen()
    {
        if (!IsFullscreen)
        {
            EnterFullscreenMode();
            StatusMessage = "Fullscreen aktiverad. Tryck Escape för att gå ur.";
            return;
        }

        if (SelectedChannel is not null)
        {
            SelectedChannel = null;
            StatusMessage = "Fullscreen avslutad.";
            return;
        }

        RestoreFromFullscreen();
        StatusMessage = "Fullscreen avslutad.";
    }

    private Rect GetCurrentScreenBounds()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
        }

        var monitorInfo = new MonitorInfoEx();
        monitorInfo.Size = Marshal.SizeOf<MonitorInfoEx>();

        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
        }

        var bounds = monitorInfo.Monitor;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top);
        }

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new System.Windows.Point(bounds.Left, bounds.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(bounds.Right, bounds.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static System.Drawing.Point GetCursorScreenPosition()
    {
        return GetCursorPos(out var point)
            ? new System.Drawing.Point(point.X, point.Y)
            : new System.Drawing.Point(int.MinValue, int.MinValue);
    }

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void NotifyDiscoveryProperties()
    {
        OnPropertyChanged(nameof(CurrentSectionSummary));
        OnPropertyChanged(nameof(BrowseHeaderTitle));
        OnPropertyChanged(nameof(LatestSectionTitle));
        OnPropertyChanged(nameof(LatestSectionHint));
        OnPropertyChanged(nameof(CategorySectionTitle));
        OnPropertyChanged(nameof(CategorySectionSubtitle));
        OnPropertyChanged(nameof(DashboardHintText));
        OnPropertyChanged(nameof(PlaylistCardText));
        OnPropertyChanged(nameof(LiveCardSummary));
        OnPropertyChanged(nameof(MoviesCardSummary));
        OnPropertyChanged(nameof(SeriesCardSummary));
        OnPropertyChanged(nameof(ChannelListTitle));
        OnPropertyChanged(nameof(ChannelListSubtitle));
        OnPropertyChanged(nameof(HasLatestItems));
        OnPropertyChanged(nameof(HasSelectedBrowseSection));
        OnPropertyChanged(nameof(HasSelectedCategory));
        OnPropertyChanged(nameof(HasSelectedSeriesGroup));
        OnPropertyChanged(nameof(CanBrowseCategories));
        OnPropertyChanged(nameof(CanSearchCurrentSection));
        OnPropertyChanged(nameof(SearchPanelTitle));
        OnPropertyChanged(nameof(IsLiveSection));
        OnPropertyChanged(nameof(IsSeriesSection));
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowBrowserShell));
        OnPropertyChanged(nameof(ShowDashboardBusyOverlay));
        OnPropertyChanged(nameof(ShowLatestSection));
        OnPropertyChanged(nameof(ShowChannelList));
        OnPropertyChanged(nameof(ShowLiveChannelList));
        OnPropertyChanged(nameof(ShowPosterChannelGrid));
        OnPropertyChanged(nameof(ShowSeriesOverviewGrid));
        OnPropertyChanged(nameof(ShowSeriesDetailView));
        OnPropertyChanged(nameof(ShowChannelListPlaceholder));
        OnPropertyChanged(nameof(ShowGridLoadingOverlay));
        OnPropertyChanged(nameof(ShowChannelListPlaceholderText));
        OnPropertyChanged(nameof(ChannelListPlaceholderText));
        OnPropertyChanged(nameof(ChannelSelectionHintText));
        OnPropertyChanged(nameof(HasMoreVisibleChannels));
        OnPropertyChanged(nameof(ChannelCountText));
        OnPropertyChanged(nameof(SeriesDetailTitle));
        OnPropertyChanged(nameof(SeriesDetailSubtitle));
        OnPropertyChanged(nameof(CurrentSeriesEpisodes));
        OnPropertyChanged(nameof(CurrentBrowserSelectionText));
    }

    private void Channels_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_suspendChannelCollectionNotifications > 0)
        {
            return;
        }

        UpdateVisibleChannelCount();
        NotifyDiscoveryProperties();
    }

    private IDisposable SuspendChannelCollectionNotifications()
    {
        return new ChannelCollectionNotificationScope(this);
    }

    private sealed class ChannelCollectionNotificationScope : IDisposable
    {
        private MainWindow? _owner;

        public ChannelCollectionNotificationScope(MainWindow owner)
        {
            _owner = owner;
            _owner._suspendChannelCollectionNotifications++;
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner._suspendChannelCollectionNotifications--;
            _owner = null;
        }
    }

    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value)
        {
            onReport(value);
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
