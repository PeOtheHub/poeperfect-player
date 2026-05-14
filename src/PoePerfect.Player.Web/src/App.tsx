import {
  ArrowLeft,
  Captions,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Eye,
  EyeOff,
  Film,
  Heart,
  Home,
  ListFilter,
  Play,
  RefreshCw,
  Search,
  Settings,
  Tv,
  Upload,
} from "lucide-react";
import { useEffect, useMemo, useState, type SyntheticEvent } from "react";
import { LoadingInline, LoadingOverlay, LoadingSpinner } from "./components/LoadingSpinner";
import { VideoPlayer } from "./components/VideoPlayer";
import type {
  AppView,
  BrowseSection,
  CategoryManagerItem,
  CategoryOption,
  CategoryPreferences,
  Channel,
  ExternalSubtitleTrack,
  MovieDetail,
  SeriesDetail,
  SeriesEpisode,
  SeriesSeason,
  XtreamConnection,
} from "./domain";
import { isSpecialCategory, sectionLabels } from "./domain";
import { loadCatalogFromFile, loadCatalogFromSource } from "./services/catalog";
import {
  loadCategoryPreferences,
  loadFavorites,
  loadRecent,
  loadStoredSource,
  loadWatchProgress,
  rememberRecent,
  saveCategoryPreferences,
  saveFavorites,
  saveStoredSource,
  saveWatchProgress,
  clearWatchProgress,
  type RecentEntry,
} from "./services/storage";
import { shouldUseGatewayStream, startGatewayStream, stopGatewayStream } from "./services/gateway";
import { probeEmbeddedSubtitleTracks } from "./services/subtitles";
import { loadMovieDetail, loadSeriesDetail } from "./services/xtream";

const latestLimit = 20;
const minSearchLength = 2;

type ResumePlaybackRequest = {
  channel: Channel;
  mode: "normal" | "gateway";
  resumeSeconds: number;
};

export function App() {
  const [view, setView] = useState<AppView>("dashboard");
  const [source, setSource] = useState(loadStoredSource);
  const [catalog, setCatalog] = useState<Channel[]>([]);
  const [connection, setConnection] = useState<XtreamConnection | undefined>();
  const [categoryPreferences, setCategoryPreferences] = useState<CategoryPreferences>(() => loadCategoryPreferences());
  const [favorites, setFavorites] = useState(() => loadFavorites());
  const [recent, setRecent] = useState<RecentEntry[]>(() => loadRecent());
  const [section, setSection] = useState<BrowseSection>("movies");
  const [managerSection, setManagerSection] = useState<BrowseSection>("movies");
  const [selectedCategoryKey, setSelectedCategoryKey] = useState("__latest");
  const [query, setQuery] = useState("");
  const [deferredQuery, setDeferredQuery] = useState("");
  const [status, setStatus] = useState("Ange playlist och ladda katalogen.");
  const [isLoading, setIsLoading] = useState(false);
  const [selectedMovie, setSelectedMovie] = useState<Channel | undefined>();
  const [movieDetail, setMovieDetail] = useState<MovieDetail | undefined>();
  const [isMovieDetailLoading, setIsMovieDetailLoading] = useState(false);
  const [subtitlePrepareStatus, setSubtitlePrepareStatus] = useState("");
  const [isPreparingSubtitles, setIsPreparingSubtitles] = useState(false);
  const [selectedSeries, setSelectedSeries] = useState<Channel | undefined>();
  const [seriesDetail, setSeriesDetail] = useState<SeriesDetail | undefined>();
  const [selectedSeasonKey, setSelectedSeasonKey] = useState<string | undefined>();
  const [isSeriesDetailLoading, setIsSeriesDetailLoading] = useState(false);
  const [playerChannel, setPlayerChannel] = useState<Channel | undefined>();
  const [resumePrompt, setResumePrompt] = useState<ResumePlaybackRequest | undefined>();

  const sectionCounts = useMemo(() => getSectionCounts(catalog, categoryPreferences), [catalog, categoryPreferences]);
  const categories = useMemo(
    () => buildCategoryOptions(catalog, section, favorites, recent, categoryPreferences),
    [catalog, categoryPreferences, favorites, recent, section],
  );
  const selectedCategory = categories.find((category) => category.key === selectedCategoryKey) ?? categories[0];
  const visibleChannels = useMemo(
    () => getVisibleChannels(catalog, section, selectedCategory, favorites, recent, deferredQuery, categoryPreferences),
    [catalog, categoryPreferences, deferredQuery, favorites, recent, section, selectedCategory],
  );
  const managerItems = useMemo(
    () => buildCategoryManagerItems(catalog, managerSection, categoryPreferences),
    [catalog, categoryPreferences, managerSection],
  );
  const selectedSeason = seriesDetail?.seasons.find((season) => season.key === selectedSeasonKey)
    ?? seriesDetail?.seasons[0];
  const preparedMovieSubtitles: ExternalSubtitleTrack[] = [];

  useEffect(() => {
    const timeoutId = window.setTimeout(() => {
      setDeferredQuery(query.trim().length >= minSearchLength ? query : "");
    }, 220);
    return () => window.clearTimeout(timeoutId);
  }, [query]);

  useEffect(() => {
    if (categories.length > 0 && !categories.some((category) => category.key === selectedCategoryKey)) {
      setSelectedCategoryKey(categories[0].key);
    }
  }, [categories, selectedCategoryKey]);

  useEffect(() => {
    if (seriesDetail?.seasons.length && !seriesDetail.seasons.some((season) => season.key === selectedSeasonKey)) {
      setSelectedSeasonKey(seriesDetail.seasons[0].key);
    }
  }, [selectedSeasonKey, seriesDetail]);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (isEditableKeyTarget(event.target)) {
        return;
      }

      if (event.key !== "Escape" && event.key !== "Backspace") {
        return;
      }

      if (playerChannel) {
        closePlayer();
        event.preventDefault();
      } else if (selectedMovie || selectedSeries) {
        closeDetails();
        event.preventDefault();
      } else if (view === "browser" || view === "playlists") {
        setView("dashboard");
        event.preventDefault();
      }
    }

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [playerChannel, selectedMovie, selectedSeries, view]);

  async function loadFromUrl() {
    if (!source.trim()) {
      setStatus("Ange en M3U- eller Xtream-länk.");
      return;
    }

    setIsLoading(true);
    closeDetails();
    setStatus("Laddar katalog...");
    try {
      saveStoredSource(source);
      const result = await loadCatalogFromSource(source, new AbortController().signal);
      setCatalog(result.channels);
      setConnection(result.connection);
      openBestSection(result.channels);
      setStatus(`${result.channels.length.toLocaleString("sv-SE")} objekt laddade.`);
    } catch (error) {
      setStatus(error instanceof Error ? `Kunde inte ladda katalog: ${error.message}` : "Kunde inte ladda katalog.");
    } finally {
      setIsLoading(false);
    }
  }

  async function loadFromSelectedFile(file: File | undefined) {
    if (!file) {
      return;
    }

    setIsLoading(true);
    closeDetails();
    setStatus("Läser fil...");
    try {
      const result = await loadCatalogFromFile(file);
      setCatalog(result.channels);
      setConnection(undefined);
      openBestSection(result.channels);
      setStatus(`${result.channels.length.toLocaleString("sv-SE")} objekt laddade från ${file.name}.`);
    } catch (error) {
      setStatus(error instanceof Error ? `Kunde inte läsa fil: ${error.message}` : "Kunde inte läsa fil.");
    } finally {
      setIsLoading(false);
    }
  }

  function openBestSection(channels: Channel[]) {
    const nextSection: BrowseSection = channels.some((channel) => channel.contentType === "movies")
      ? "movies"
      : channels.some((channel) => channel.contentType === "series")
        ? "series"
        : "live";
    openSection(nextSection, "browser");
  }

  function openSection(nextSection: BrowseSection, nextView: AppView = "browser") {
    setSection(nextSection);
    setSelectedCategoryKey(nextSection === "live" ? "__favorites" : "__latest");
    setQuery("");
    setDeferredQuery("");
    closeDetails();
    setView(nextView);
  }

  async function openChannel(channel: Channel) {
    if (channel.contentType === "movies") {
      abortSubtitlePreparation();
      setSelectedMovie(channel);
      setSelectedSeries(undefined);
      setMovieDetail(undefined);
      setSubtitlePrepareStatus(
        shouldUseGatewayStream(channel.url)
          ? "Valj Spela med undertexter om du vill prova inbaddade undertexter via gateway."
          : "",
      );
      setIsMovieDetailLoading(true);
      setStatus(`Visar ${cleanCardTitle(channel.name)}.`);
      try {
        const detail = await loadMovieDetail(connection, channel);
        setMovieDetail(detail);
      } catch {
        setMovieDetail(undefined);
      } finally {
        setIsMovieDetailLoading(false);
      }
      return;
    }

    if (channel.contentType === "series") {
      await openSeries(channel);
      return;
    }

    void startPlayback(channel);
  }

  async function openSeries(channel: Channel) {
    setSelectedSeries(channel);
    setSelectedMovie(undefined);
    setSeriesDetail(undefined);
    setSelectedSeasonKey(undefined);
    setIsSeriesDetailLoading(true);
    setStatus(`Hämtar avsnitt för ${channel.name}...`);
    try {
      const detail = await loadSeriesDetail(connection, channel)
        ?? buildLocalSeriesDetail(channel, catalog);
      setSeriesDetail(detail);
      setSelectedSeasonKey(detail.seasons[0]?.key);
      setStatus(`Visar ${detail.title}.`);
    } catch {
      const detail = buildLocalSeriesDetail(channel, catalog);
      setSeriesDetail(detail);
      setSelectedSeasonKey(detail.seasons[0]?.key);
      setStatus(`Visar lokala avsnitt för ${channel.name}.`);
    } finally {
      setIsSeriesDetailLoading(false);
    }
  }

  function requestPlayback(channel: Channel, mode: ResumePlaybackRequest["mode"]) {
    const progress = getResumableProgress(channel);
    if (progress) {
      setResumePrompt({
        channel,
        mode,
        resumeSeconds: progress.positionSeconds,
      });
      return;
    }

    if (mode === "gateway") {
      void startGatewayPlayback(channel, 0);
    } else {
      startPlayback(channel, 0);
    }
  }

  function continueResumePlayback() {
    if (!resumePrompt) {
      return;
    }

    const { channel, mode, resumeSeconds } = resumePrompt;
    setResumePrompt(undefined);
    if (mode === "gateway") {
      void startGatewayPlayback(channel, resumeSeconds);
    } else {
      startPlayback(channel, resumeSeconds);
    }
  }

  function startResumePlaybackFromBeginning() {
    if (!resumePrompt) {
      return;
    }

    const { channel, mode } = resumePrompt;
    clearWatchProgress(channel.url);
    setResumePrompt(undefined);
    if (mode === "gateway") {
      void startGatewayPlayback(channel, 0);
    } else {
      startPlayback(channel, 0);
    }
  }

  function startPlayback(channel: Channel, resumePositionSeconds = 0) {
    abortSubtitlePreparation();
    setPlayerChannel({
      ...channel,
      resumePositionSeconds,
    });
    setRecent(rememberRecent({ section: channel.contentType, url: channel.url, playedAt: Date.now() }));
  }

  async function startGatewayPlayback(channel: Channel, startAtSeconds = 0) {
    abortSubtitlePreparation();
    setStatus("Startar lokal gateway...");
    try {
      const durationSeconds = selectedMovie?.url === channel.url
        ? movieDetail?.durationSeconds ?? parseDurationSeconds(movieDetail?.duration ?? "")
        : channel.durationSeconds;
      const [gateway, embeddedSubtitleTracks] = await Promise.all([
        startGatewayStream(channel.url, "swe", startAtSeconds),
        probeEmbeddedSubtitleTracks(channel.url).catch(() => []),
      ]);
      const detailSubtitleTracks = selectedMovie?.url === channel.url
        ? movieDetail?.subtitleTracks ?? []
        : [];
      setPlayerChannel({
        ...channel,
        url: gateway.playlistUrl,
        originalUrl: channel.url,
        gatewaySessionId: gateway.sessionId,
        gatewayStartOffsetSeconds: gateway.startAtSeconds ?? 0,
        gatewaySubtitleLanguage: "swe",
        gatewaySubtitleTrackIndex: -1,
        resumePositionSeconds: startAtSeconds,
        durationSeconds,
        subtitleTracks: mergeSubtitleTracks([
          ...(channel.subtitleTracks ?? []),
          ...detailSubtitleTracks,
          ...embeddedSubtitleTracks,
        ]),
      });
      setRecent(rememberRecent({ section: channel.contentType, url: channel.url, playedAt: Date.now() }));
      setStatus(embeddedSubtitleTracks.length > 0
        ? `Spelar via lokal gateway med ${embeddedSubtitleTracks.length} inbaddade undertextspar.`
        : "Spelar via lokal gateway.");
    } catch (error) {
      setStatus(error instanceof Error ? `Kunde inte starta gateway: ${error.message}` : "Kunde inte starta gateway.");
    }
  }

  function closePlayer() {
    if (playerChannel?.gatewaySessionId) {
      void stopGatewayStream(playerChannel.gatewaySessionId);
    }
    setPlayerChannel(undefined);
  }

  function savePlayerProgress(positionSeconds: number, durationSeconds: number) {
    if (!playerChannel || playerChannel.contentType === "live") {
      return;
    }

    const sourceUrl = playerChannel.originalUrl ?? playerChannel.url;
    if (!Number.isFinite(positionSeconds) || positionSeconds < 5) {
      return;
    }

    const knownDuration = durationSeconds || playerChannel.durationSeconds || 0;
    if (knownDuration > 0 && positionSeconds >= knownDuration - 60) {
      clearWatchProgress(sourceUrl);
      return;
    }

    saveWatchProgress({
      url: sourceUrl,
      positionSeconds,
      durationSeconds: knownDuration,
      updatedAt: Date.now(),
    });
  }

  function getResumableProgress(channel: Channel) {
    if (channel.contentType === "live") {
      return undefined;
    }

    const progress = loadWatchProgress(channel.url);
    if (!progress || progress.positionSeconds < 30) {
      return undefined;
    }

    const durationSeconds = channel.durationSeconds || progress.durationSeconds || 0;
    if (durationSeconds > 0 && progress.positionSeconds >= durationSeconds - 60) {
      clearWatchProgress(channel.url);
      return undefined;
    }

    return progress;
  }

  function toggleFavorite(channel: Channel) {
    const next = new Set(favorites);
    if (next.has(channel.url)) {
      next.delete(channel.url);
    } else {
      next.add(channel.url);
    }

    setFavorites(next);
    saveFavorites(next);
  }

  function toggleCategoryVisibility(label: string) {
    const next = cloneCategoryPreferences(categoryPreferences);
    const sectionPreferences = next[managerSection] ?? {};
    const item = managerItems.find((candidate) => candidate.label === label);
    sectionPreferences[label] = {
      visible: !(item?.visible ?? true),
      order: item?.order ?? managerItems.findIndex((candidate) => candidate.label === label),
    };
    next[managerSection] = sectionPreferences;
    setCategoryPreferences(next);
    saveCategoryPreferences(next);
  }

  function moveCategory(label: string, direction: -1 | 1) {
    const index = managerItems.findIndex((item) => item.label === label);
    const swapIndex = index + direction;
    if (index < 0 || swapIndex < 0 || swapIndex >= managerItems.length) {
      return;
    }

    const next = cloneCategoryPreferences(categoryPreferences);
    const sectionPreferences = next[managerSection] ?? {};
    managerItems.forEach((item, itemIndex) => {
      sectionPreferences[item.label] = {
        visible: item.visible,
        order: itemIndex,
      };
    });

    sectionPreferences[managerItems[index].label].order = swapIndex;
    sectionPreferences[managerItems[swapIndex].label].order = index;
    next[managerSection] = sectionPreferences;
    setCategoryPreferences(next);
    saveCategoryPreferences(next);
  }

  function closeDetails() {
    abortSubtitlePreparation();
    setSubtitlePrepareStatus("");
    setSelectedMovie(undefined);
    setMovieDetail(undefined);
    setIsMovieDetailLoading(false);
    setSelectedSeries(undefined);
    setSeriesDetail(undefined);
    setSelectedSeasonKey(undefined);
    setIsSeriesDetailLoading(false);
  }

  function abortSubtitlePreparation() {
    setIsPreparingSubtitles(false);
  }

  if (playerChannel) {
    return (
      <VideoPlayer
        channel={playerChannel}
        onClose={closePlayer}
        onProgress={savePlayerProgress}
        onGatewaySessionChange={(gateway) => {
          setPlayerChannel((current) => current
            ? {
                ...current,
                url: gateway.playlistUrl,
                gatewaySessionId: gateway.sessionId,
                gatewayStartOffsetSeconds: gateway.startAtSeconds,
                gatewaySubtitleTrackIndex: gateway.subtitleTrackIndex,
              }
            : current);
        }}
      />
    );
  }

  return (
    <main className={`app-shell ${view === "dashboard" ? "dashboard-mode" : ""}`}>
      {view !== "dashboard" && (
        <header className="topbar">
          <button className="brand-lockup brand-button" onClick={() => setView("dashboard")}>
            <div className="brand-mark">P</div>
            <div>
              <strong>PoePerfect Player</strong>
              <span>{catalog.length > 0 ? `${catalog.length.toLocaleString("sv-SE")} objekt` : "Web"}</span>
            </div>
          </button>

          <nav className="top-actions" aria-label="Huvudnavigering">
            <button className="icon-text-button" onClick={() => setView("dashboard")}>
              <Home size={18} />
              Start
            </button>
            <button className={`icon-text-button ${view === "playlists" ? "active" : ""}`} onClick={() => setView("playlists")}>
              <Settings size={18} />
              Playlists
            </button>
          </nav>
        </header>
      )}

      {view === "dashboard" ? (
        <Dashboard
          counts={sectionCounts}
          source={source}
          onOpenSection={openSection}
          onOpenPlaylists={() => setView("playlists")}
        />
      ) : view === "playlists" ? (
        <PlaylistManager
          source={source}
          setSource={setSource}
          isLoading={isLoading}
          status={status}
          managerSection={managerSection}
          setManagerSection={setManagerSection}
          managerItems={managerItems}
          onLoadFromUrl={loadFromUrl}
          onLoadFromFile={loadFromSelectedFile}
          onToggleCategory={toggleCategoryVisibility}
          onMoveCategory={moveCategory}
        />
      ) : (
        <BrowserView
          section={section}
          status={status}
          categories={categories}
          selectedCategory={selectedCategory}
          selectedMovie={selectedMovie}
          movieDetail={movieDetail}
          isMovieDetailLoading={isMovieDetailLoading}
          subtitlePrepareStatus={subtitlePrepareStatus}
          isPreparingSubtitles={isPreparingSubtitles}
          preparedMovieSubtitles={preparedMovieSubtitles}
          selectedSeries={selectedSeries}
          seriesDetail={seriesDetail}
          selectedSeason={selectedSeason}
          isSeriesDetailLoading={isSeriesDetailLoading}
          query={query}
          setQuery={setQuery}
          visibleChannels={visibleChannels}
          favorites={favorites}
          onSelectCategory={(category) => {
            setSelectedCategoryKey(category.key);
            closeDetails();
          }}
          onOpenChannel={openChannel}
          onMovieBack={closeDetails}
          onMoviePlay={(channel) => requestPlayback(channel, "normal")}
          onMovieGatewayPlay={(channel) => requestPlayback(channel, "gateway")}
          onFavorite={toggleFavorite}
          onSeriesBack={closeDetails}
          onSeasonSelect={setSelectedSeasonKey}
          onEpisodePlay={(episode) => requestPlayback(episode.channel, "normal")}
          onBackToDashboard={() => setView("dashboard")}
        />
      )}
      {resumePrompt ? (
        <ResumePromptDialog
          title={cleanCardTitle(resumePrompt.channel.name)}
          resumeSeconds={resumePrompt.resumeSeconds}
          onContinue={continueResumePlayback}
          onStartOver={startResumePlaybackFromBeginning}
          onCancel={() => setResumePrompt(undefined)}
        />
      ) : null}
    </main>
  );
}

function Dashboard({
  counts,
  source,
  onOpenSection,
  onOpenPlaylists,
}: {
  counts: Record<BrowseSection, number>;
  source: string;
  onOpenSection: (section: BrowseSection) => void;
  onOpenPlaylists: () => void;
}) {
  const totalCount = counts.live + counts.movies + counts.series;
  const playlistLabel = getSourceLabel(source);
  const catalogStatus = totalCount > 0
    ? `${totalCount.toLocaleString("sv-SE")} objekt laddade`
    : "Ingen katalog laddad";

  return (
    <section className="dashboard">
      <div className="dashboard-home-shell">
        <div className="dashboard-home-top">
          <div>
            <div className="dashboard-brand-pill">PoePerfect Player</div>
            <p>Välj Live, Film eller Serier för att fortsätta.</p>
          </div>
          <button className="icon-text-button dashboard-playlist-button" onClick={onOpenPlaylists}>
            <ListFilter size={18} />
            Playlists
          </button>
        </div>

        <div className="dashboard-center">
          <div className="dashboard-title">
            <h1>PoePerfect Player</h1>
            <p>Live, Film eller Serier</p>
          </div>

          <div className="dashboard-grid">
            <DashboardCard
              icon={<Tv size={58} />}
              label="Live"
              description="Direkt och kanaler"
              count={counts.live}
              variant="live"
              onClick={() => onOpenSection("live")}
            />
            <DashboardCard
              icon={<Film size={58} />}
              label="Film"
              description="Filmer och premiärer"
              count={counts.movies}
              variant="movies"
              onClick={() => onOpenSection("movies")}
            />
            <DashboardCard
              icon={<Play size={58} />}
              label="Serier"
              description="Säsonger och avsnitt"
              count={counts.series}
              variant="series"
              onClick={() => onOpenSection("series")}
            />
          </div>
        </div>

        <div className="dashboard-footer">
          <div>
            <span>Playlist</span>
            <strong>{playlistLabel}</strong>
          </div>
          <div>
            <span>Katalog</span>
            <strong>{catalogStatus}</strong>
          </div>
        </div>
      </div>
    </section>
  );
}

function DashboardCard({
  icon,
  label,
  description,
  count,
  variant,
  onClick,
}: {
  icon: React.ReactNode;
  label: string;
  description: string;
  count: number;
  variant: BrowseSection;
  onClick: () => void;
}) {
  return (
    <button className={`dashboard-card ${variant}`} onClick={onClick}>
      <div className="dashboard-card-icon">{icon}</div>
      <small>{description}</small>
      <strong>{label}</strong>
      <span>{count.toLocaleString("sv-SE")} objekt i katalog</span>
    </button>
  );
}

function getSourceLabel(source: string) {
  const trimmedSource = source.trim();
  if (!trimmedSource) {
    return "Ingen playlist vald";
  }

  try {
    return new URL(trimmedSource).hostname.replace(/^www\./, "");
  } catch {
    return trimmedSource.split(/[\\/]/).pop() || "Lokal fil";
  }
}

function PlaylistManager({
  source,
  setSource,
  isLoading,
  status,
  managerSection,
  setManagerSection,
  managerItems,
  onLoadFromUrl,
  onLoadFromFile,
  onToggleCategory,
  onMoveCategory,
}: {
  source: string;
  setSource: (source: string) => void;
  isLoading: boolean;
  status: string;
  managerSection: BrowseSection;
  setManagerSection: (section: BrowseSection) => void;
  managerItems: CategoryManagerItem[];
  onLoadFromUrl: () => void;
  onLoadFromFile: (file: File | undefined) => void;
  onToggleCategory: (label: string) => void;
  onMoveCategory: (label: string, direction: -1 | 1) => void;
}) {
  return (
    <section className="playlist-page">
      <div className="settings-panel source-settings">
        <div>
          <h1>Playlists</h1>
          <p>{status}</p>
        </div>
        <div className="source-controls playlist-source-controls">
          <label className="source-input-label">
            M3U/Xtream
            <input
              value={source}
              onChange={(event) => setSource(event.target.value)}
              placeholder="M3U eller Xtream URL"
              spellCheck={false}
            />
          </label>
          <button className="icon-text-button" onClick={onLoadFromUrl} disabled={isLoading}>
            {isLoading ? <LoadingSpinner size="small" /> : <RefreshCw size={18} />}
            {isLoading ? "Laddar" : "Ladda"}
          </button>
          <label className={`file-button ${isLoading ? "disabled" : ""}`}>
            <Upload size={18} />
            Fil
            <input
              type="file"
              accept=".m3u,.m3u8,text/*"
              disabled={isLoading}
              onChange={(event) => onLoadFromFile(event.target.files?.[0])}
            />
          </label>
        </div>
      </div>

      <div className="settings-panel category-manager">
        <div className="manager-header">
          <div>
            <h2>Underkategorier</h2>
            <p>Välj vilka kategorier som ska visas och sortera ordningen i sidomenyn.</p>
          </div>
          <div className="manager-tabs">
            {(["live", "movies", "series"] as BrowseSection[]).map((item) => (
              <button
                key={item}
                className={`section-chip ${managerSection === item ? "active" : ""}`}
                onClick={() => setManagerSection(item)}
              >
                {sectionLabels[item]}
              </button>
            ))}
          </div>
        </div>

        <div className="manager-list">
          {managerItems.map((item, index) => (
            <div className="manager-row" key={item.label}>
              <button className="visibility-button" onClick={() => onToggleCategory(item.label)} aria-pressed={item.visible}>
                {item.visible ? <Eye size={18} /> : <EyeOff size={18} />}
              </button>
              <div>
                <strong>{item.label}</strong>
                <span>{item.count.toLocaleString("sv-SE")} objekt</span>
              </div>
              <div className="row-move-buttons">
                <button className="mini-button" onClick={() => onMoveCategory(item.label, -1)} disabled={index === 0}>
                  <ChevronUp size={17} />
                </button>
                <button className="mini-button" onClick={() => onMoveCategory(item.label, 1)} disabled={index === managerItems.length - 1}>
                  <ChevronDown size={17} />
                </button>
              </div>
            </div>
          ))}
          {managerItems.length === 0 && <div className="empty-state">Ladda en katalog för att visa kategorier.</div>}
        </div>
      </div>

      {isLoading ? <LoadingOverlay message={status} /> : null}
    </section>
  );
}

function ResumePromptDialog({
  title,
  resumeSeconds,
  onContinue,
  onStartOver,
  onCancel,
}: {
  title: string;
  resumeSeconds: number;
  onContinue: () => void;
  onStartOver: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="resume-backdrop" role="presentation" onClick={onCancel}>
      <section className="resume-dialog" role="dialog" aria-modal="true" aria-labelledby="resume-title" onClick={(event) => event.stopPropagation()}>
        <div>
          <p>Fortsatt titta?</p>
          <h2 id="resume-title">{title}</h2>
          <span>Du slutade vid {formatClockTime(resumeSeconds)}.</span>
        </div>
        <div className="resume-actions">
          <button className="play-button" onClick={onContinue} autoFocus>
            <Play size={18} fill="currentColor" />
            Fortsätt
          </button>
          <button className="icon-text-button" onClick={onStartOver}>
            Från början
          </button>
          <button className="icon-text-button" onClick={onCancel}>
            Avbryt
          </button>
        </div>
      </section>
    </div>
  );
}

function BrowserView({
  section,
  status,
  categories,
  selectedCategory,
  selectedMovie,
  movieDetail,
  isMovieDetailLoading,
  subtitlePrepareStatus,
  isPreparingSubtitles,
  preparedMovieSubtitles,
  selectedSeries,
  seriesDetail,
  selectedSeason,
  isSeriesDetailLoading,
  query,
  setQuery,
  visibleChannels,
  favorites,
  onSelectCategory,
  onOpenChannel,
  onMovieBack,
  onMoviePlay,
  onMovieGatewayPlay,
  onFavorite,
  onSeriesBack,
  onSeasonSelect,
  onEpisodePlay,
  onBackToDashboard,
}: {
  section: BrowseSection;
  status: string;
  categories: CategoryOption[];
  selectedCategory: CategoryOption | undefined;
  selectedMovie: Channel | undefined;
  movieDetail: MovieDetail | undefined;
  isMovieDetailLoading: boolean;
  subtitlePrepareStatus: string;
  isPreparingSubtitles: boolean;
  preparedMovieSubtitles: ExternalSubtitleTrack[];
  selectedSeries: Channel | undefined;
  seriesDetail: SeriesDetail | undefined;
  selectedSeason: SeriesSeason | undefined;
  isSeriesDetailLoading: boolean;
  query: string;
  setQuery: (query: string) => void;
  visibleChannels: Channel[];
  favorites: Set<string>;
  onSelectCategory: (category: CategoryOption) => void;
  onOpenChannel: (channel: Channel) => void;
  onMovieBack: () => void;
  onMoviePlay: (channel: Channel) => void;
  onMovieGatewayPlay: (channel: Channel) => void;
  onFavorite: (channel: Channel) => void;
  onSeriesBack: () => void;
  onSeasonSelect: (seasonKey: string) => void;
  onEpisodePlay: (episode: SeriesEpisode) => void;
  onBackToDashboard: () => void;
}) {
  const hasActiveSearch = query.trim().length >= minSearchLength;
  const title = selectedMovie
    ? cleanCardTitle(selectedMovie.name)
    : selectedSeries
      ? seriesDetail?.title ?? selectedSeries.name
      : hasActiveSearch
        ? `Sökresultat: ${query}`
      : selectedCategory?.label ?? sectionLabels[section];
  const selectedMovieSubtitleTracks = selectedMovie
    ? mergeSubtitleTracks([
        ...(selectedMovie.subtitleTracks ?? []),
        ...(movieDetail?.subtitleTracks ?? []),
        ...preparedMovieSubtitles,
      ])
    : [];
  const movieSubtitleCount = selectedMovieSubtitleTracks.length;

  return (
    <section className="browser">
      <aside className="rail">
        <button className="icon-text-button back-dashboard" onClick={onBackToDashboard}>
          <ArrowLeft size={18} />
          Start
        </button>
        <div className="category-panel">
          <h2>Kategorier i {sectionLabels[section]}</h2>
          <div className="category-list">
            {categories.map((category) => (
              <button
                key={category.key}
                className={`category-button ${category.key === selectedCategory?.key ? "active" : ""}`}
                onClick={() => onSelectCategory(category)}
              >
                <span>{category.label}</span>
                <small>{category.count.toLocaleString("sv-SE")}</small>
              </button>
            ))}
          </div>
        </div>
      </aside>

      <section className="content-surface">
        {!selectedMovie && !selectedSeries ? (
          <div className="content-header">
            <div>
              <h1>{title}</h1>
              <p>{status}</p>
            </div>
            <label className="search-box">
              <Search size={18} />
              <input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    event.preventDefault();
                    event.stopPropagation();
                  }
                }}
                placeholder="Sök minst 2 tecken"
              />
            </label>
          </div>
        ) : null}

        {selectedMovie ? (
          <MovieDetailView
            channel={selectedMovie}
            detail={movieDetail}
            isLoading={isMovieDetailLoading}
            isFavorite={favorites.has(selectedMovie.url)}
            subtitlePrepareStatus={subtitlePrepareStatus}
            isPreparingSubtitles={isPreparingSubtitles}
            preparedSubtitleCount={movieSubtitleCount}
            onBack={onMovieBack}
            onPlay={() => onMoviePlay({
              ...selectedMovie,
              subtitleTracks: [],
            })}
            onSubtitlePlay={
              shouldUseGatewayStream(selectedMovie.url)
                ? () => onMovieGatewayPlay(selectedMovie)
                : movieSubtitleCount > 0
                  ? () => onMoviePlay({
                      ...selectedMovie,
                      subtitleTracks: selectedMovieSubtitleTracks,
                    })
                  : undefined
            }
            onFavorite={() => onFavorite(selectedMovie)}
          />
        ) : selectedSeries ? (
          <SeriesDetailView
            channel={selectedSeries}
            detail={seriesDetail}
            selectedSeason={selectedSeason}
            isLoading={isSeriesDetailLoading}
            onBack={onSeriesBack}
            onSeasonSelect={onSeasonSelect}
            onEpisodePlay={onEpisodePlay}
          />
        ) : (
          <div className="poster-grid">
            {visibleChannels.map((channel) => (
              <PosterCard
                key={channel.id}
                channel={channel}
                isFavorite={favorites.has(channel.url)}
                showCategory={isSpecialCategory(selectedCategory)}
                onOpen={() => void onOpenChannel(channel)}
              />
            ))}
            {visibleChannels.length === 0 && <div className="empty-state">Inget att visa.</div>}
          </div>
        )}
      </section>
    </section>
  );
}

function PosterCard({
  channel,
  isFavorite,
  showCategory,
  onOpen,
}: {
  channel: Channel;
  isFavorite: boolean;
  showCategory: boolean;
  onOpen: () => void;
}) {
  const displayTitle = cleanCardTitle(channel.name);
  const metadata = extractCardMetadata(channel);
  const [hasPosterError, setHasPosterError] = useState(false);
  const hasPosterImage = Boolean(channel.logoUrl && !hasPosterError);
  return (
    <button className="poster-card" onClick={onOpen}>
      <div className="poster-art">
        {hasPosterImage ? (
          <img
            src={channel.logoUrl}
            alt=""
            loading="lazy"
            onError={(event) => hideBrokenPoster(event, setHasPosterError)}
          />
        ) : (
          <div className="poster-placeholder">
            <span>{channel.contentType === "live" ? "LIVE" : channel.contentType === "series" ? "SERIE" : "FILM"}</span>
            <strong>{displayTitle}</strong>
          </div>
        )}
        {isFavorite ? <Heart className="card-heart" size={18} fill="#EF4444" color="#EF4444" /> : null}
      </div>
      <strong>{displayTitle}</strong>
      {metadata.length > 0 ? (
        <div className="poster-meta">
          {metadata.map((item) => <span key={item}>{item}</span>)}
        </div>
      ) : showCategory ? (
        <small>{channel.categoryName}</small>
      ) : null}
    </button>
  );
}

function MovieDetailView({
  channel,
  detail,
  isLoading,
  isFavorite,
  subtitlePrepareStatus,
  isPreparingSubtitles,
  preparedSubtitleCount,
  onBack,
  onPlay,
  onSubtitlePlay,
  onFavorite,
}: {
  channel: Channel;
  detail: MovieDetail | undefined;
  isLoading: boolean;
  isFavorite: boolean;
  subtitlePrepareStatus: string;
  isPreparingSubtitles: boolean;
  preparedSubtitleCount: number;
  onBack: () => void;
  onPlay: () => void;
  onSubtitlePlay?: () => void;
  onFavorite: () => void;
}) {
  const posterUrl = detail?.posterUrl || channel.logoUrl;
  const displayTitle = cleanCardTitle(detail?.title || channel.name);
  const metadata = extractCardMetadata(channel);

  return (
    <div className="movie-detail">
      <button className="icon-text-button detail-back" onClick={onBack}>
        <ArrowLeft size={20} />
        Tillbaka
      </button>
      <div className="movie-poster-large">
        {posterUrl ? <img src={posterUrl} alt="" /> : null}
        <span>FILM</span>
      </div>
      <div className="movie-copy">
        <div className="movie-title-row">
          <div>
            <h2>{displayTitle}</h2>
            {metadata.length > 0 ? (
              <div className="detail-meta-chips">
                {metadata.map((item) => <span key={item}>{item}</span>)}
              </div>
            ) : null}
            <p>{metadataText(channel, detail, isLoading)}</p>
          </div>
          <button className="heart-toggle" aria-pressed={isFavorite} onClick={onFavorite}>
            <Heart size={30} color={isFavorite ? "#EF4444" : "#F8FAFC"} fill={isFavorite ? "#EF4444" : "none"} />
          </button>
        </div>

        <div className="detail-actions">
          <button className="play-button" onClick={onPlay} autoFocus>
            <Play size={20} fill="currentColor" />
            Spela
          </button>
          {onSubtitlePlay ? (
            <button className="icon-text-button" onClick={onSubtitlePlay}>
              <Captions size={18} />
              Spela med undertexter
            </button>
          ) : null}
          <div className={`subtitle-prep-status ${isPreparingSubtitles ? "working" : ""} ${preparedSubtitleCount > 0 && !isPreparingSubtitles ? "ready" : ""}`}>
            {preparedSubtitleCount > 0 && !isPreparingSubtitles ? <CheckCircle2 size={18} /> : <Captions size={18} />}
            <span>
              {isPreparingSubtitles
                ? "Kontrollerar undertexter..."
                : preparedSubtitleCount > 0
                  ? `Externa undertexter klara: ${preparedSubtitleCount} spar`
                  : subtitlePrepareStatus || "Inga externa undertexter klara."}
            </span>
          </div>
        </div>

        <p className="plot">{detail?.plot || (isLoading ? "Hämtar metadata..." : "Ingen beskrivning hittades ännu.")}</p>

        <div className="detail-grid">
          <DetailItem label="Genre" value={detail?.genre || channel.categoryName} />
          <DetailItem label="Längd" value={detail?.duration || "Saknas"} />
          <DetailItem label="Betyg" value={detail?.rating || "Saknas"} />
          <DetailItem label="Premiär" value={detail?.releaseDate || "Saknas"} />
          <DetailItem label="Regi" value={detail?.director || "Saknas"} />
          <DetailItem label="Cast" value={detail?.cast || "Saknas"} wide />
        </div>
      </div>
      {isLoading ? <LoadingInline message="Hämtar metadata..." /> : null}
    </div>
  );
}

function SeriesDetailView({
  channel,
  detail,
  selectedSeason,
  isLoading,
  onBack,
  onSeasonSelect,
  onEpisodePlay,
}: {
  channel: Channel;
  detail: SeriesDetail | undefined;
  selectedSeason: SeriesSeason | undefined;
  isLoading: boolean;
  onBack: () => void;
  onSeasonSelect: (seasonKey: string) => void;
  onEpisodePlay: (episode: SeriesEpisode) => void;
}) {
  return (
    <div className="series-detail">
      <button className="icon-text-button detail-back" onClick={onBack}>
        <ArrowLeft size={20} />
        Tillbaka
      </button>
      <div className="series-hero">
        <div className="series-poster">
          {detail?.posterUrl || channel.logoUrl ? <img src={detail?.posterUrl || channel.logoUrl} alt="" /> : null}
          <span>SERIE</span>
        </div>
        <div className="series-copy">
          <h2>{detail?.title || channel.name}</h2>
          <p>{isLoading ? "Hämtar säsonger och avsnitt..." : detail?.plot || "Ingen beskrivning hittades ännu."}</p>
          <div className="series-meta">
            <span>{detail?.genre || channel.categoryName}</span>
            <span>{detail?.rating ? `Betyg ${detail.rating}` : "Betyg saknas"}</span>
            <span>{detail?.seasons.reduce((sum, season) => sum + season.episodes.length, 0) ?? 0} avsnitt</span>
          </div>
        </div>
      </div>

      <div className="series-browser">
        <div className="season-list">
          {(detail?.seasons ?? []).map((season) => (
            <button
              key={season.key}
              className={`season-button ${selectedSeason?.key === season.key ? "active" : ""}`}
              onClick={() => onSeasonSelect(season.key)}
            >
              <span>{season.label}</span>
              <small>{season.episodes.length}</small>
            </button>
          ))}
        </div>
        <div className="episode-list">
          {(selectedSeason?.episodes ?? []).map((episode) => (
            <div className="episode-row" key={episode.id}>
              <div>
                <strong>{episode.title}</strong>
                <span>{episode.subtitle}</span>
              </div>
              <button className="icon-text-button" onClick={() => onEpisodePlay(episode)}>
                <Play size={17} fill="currentColor" />
                Spela
              </button>
            </div>
          ))}
          {!isLoading && (selectedSeason?.episodes.length ?? 0) === 0 && <div className="empty-state">Inga avsnitt hittades.</div>}
        </div>
      </div>
      {isLoading ? <LoadingInline message="Hämtar säsonger och avsnitt..." /> : null}
    </div>
  );
}

function DetailItem({ label, value, wide }: { label: string; value: string; wide?: boolean }) {
  return (
    <div className={`detail-item ${wide ? "wide" : ""}`}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function mergeSubtitleTracks(tracks: ExternalSubtitleTrack[]) {
  const seenUrls = new Set<string>();
  return tracks.filter((track) => {
    if (seenUrls.has(track.url)) {
      return false;
    }

    seenUrls.add(track.url);
    return true;
  });
}

function extractCardMetadata(channel: Channel) {
  const bracketItems = Array.from(channel.name.matchAll(/\[([^\]]+)\]/g))
    .map((match) => match[1].trim())
    .filter(Boolean);
  const text = `${channel.name} ${channel.categoryName}`.toLowerCase();
  const items: string[] = [];
  const yearMatch = channel.name.match(/\b(19\d{2}|20\d{2})\b/);
  if (yearMatch) {
    items.push(yearMatch[1]);
  }
  bracketItems.forEach((item) => {
    const normalized = normalizeMetadataChip(item);
    if (normalized && !/^(19\d{2}|20\d{2})$/.test(normalized)) {
      items.push(normalized);
    }
  });
  if (/\b(?:4k|uhd|2160p)\b/i.test(text)) {
    items.push("4K");
  } else if (/\b1080p\b/i.test(text)) {
    items.push("1080p");
  }
  if (/dolby\s*vision|\bdv\b/i.test(text)) {
    items.push("Dolby Vision");
  }
  if (/multi[-\s]*sub|multi\s*subtitle|multi-sub/i.test(text)) {
    items.push("Multi-Sub");
  }
  if (/multi[-\s]*audio|multi\s*audio/i.test(text)) {
    items.push("Multi-Audio");
  }
  if (/\b(?:hdr10?|hdr)\b/i.test(text)) {
    items.push("HDR");
  }

  return [...new Set(items)].slice(0, 4);
}

function hideBrokenPoster(
  event: SyntheticEvent<HTMLImageElement>,
  setHasPosterError: (value: boolean) => void,
) {
  event.currentTarget.style.display = "none";
  setHasPosterError(true);
}

function cleanCardTitle(value: string) {
  return value
    .replace(/\[[^\]]+\]/g, "")
    .replace(/\s{2,}/g, " ")
    .trim();
}

function normalizeMetadataChip(value: string) {
  const normalized = value.trim().replace(/\s+/g, " ");
  const lower = normalized.toLowerCase();
  if (!normalized) {
    return "";
  }
  if (/^(multi[-\s]*sub|multi\s*subtitle)s?$/i.test(normalized)) {
    return "Multi-Sub";
  }
  if (/^multi[-\s]*audio$/i.test(normalized)) {
    return "Multi-Audio";
  }
  if (/^dolby\s*vision$/i.test(normalized)) {
    return "Dolby Vision";
  }
  if (lower === "pre") {
    return "PRE";
  }
  if (/^(4k|uhd|2160p)$/i.test(normalized)) {
    return "4K";
  }
  if (/^(1080p|720p|hdr|hdr10)$/i.test(normalized)) {
    return normalized.toUpperCase();
  }

  return normalized.length <= 18 ? normalized : "";
}

function formatClockTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return "00:00";
  }

  const totalSeconds = Math.floor(seconds);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainingSeconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, "0")}:${remainingSeconds.toString().padStart(2, "0")}`
    : `${minutes.toString().padStart(2, "0")}:${remainingSeconds.toString().padStart(2, "0")}`;
}

function isEditableKeyTarget(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  return target instanceof HTMLInputElement
    || target instanceof HTMLTextAreaElement
    || target instanceof HTMLSelectElement
    || target.isContentEditable;
}

function parseDurationSeconds(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return undefined;
  }

  if (/^\d+$/.test(trimmed)) {
    const seconds = Number(trimmed);
    return seconds > 0 ? seconds : undefined;
  }

  const clockParts = trimmed.split(":").map((part) => Number(part));
  if (clockParts.length >= 2 && clockParts.length <= 3 && clockParts.every((part) => Number.isFinite(part))) {
    const [hours, minutes, seconds] = clockParts.length === 3
      ? clockParts
      : [0, clockParts[0], clockParts[1]];
    const total = (hours * 3600) + (minutes * 60) + seconds;
    return total > 0 ? total : undefined;
  }

  const normalized = trimmed.toLowerCase();
  const hourMatch = normalized.match(/(\d+)\s*(?:h|tim|hour)/);
  const minuteMatch = normalized.match(/(\d+)\s*(?:min|m\b)/);
  const hours = hourMatch ? Number(hourMatch[1]) : 0;
  const minutes = minuteMatch ? Number(minuteMatch[1]) : 0;
  const total = (hours * 3600) + (minutes * 60);
  return total > 0 ? total : undefined;
}

function getSectionCounts(channels: Channel[], preferences: CategoryPreferences) {
  return {
    live: getSectionChannels(channels, "live", preferences).length,
    movies: getSectionChannels(channels, "movies", preferences).length,
    series: getSectionChannels(channels, "series", preferences).length,
  };
}

function buildCategoryOptions(
  catalog: Channel[],
  section: BrowseSection,
  favorites: Set<string>,
  recent: RecentEntry[],
  preferences: CategoryPreferences,
): CategoryOption[] {
  const sectionChannels = getSectionChannels(catalog, section, preferences);
  const favoriteCount = sectionChannels.filter((channel) => favorites.has(channel.url)).length;
  const recentCount = recent.filter((entry) => entry.section === section).length;
  const regularCategories = buildCategoryManagerItems(catalog, section, preferences)
    .filter((item) => item.visible)
    .map((item) => ({
      key: `category:${item.label}`,
      label: item.label,
      count: item.count,
    }));

  const specialCategories: CategoryOption[] =
    section === "live"
      ? [
          { key: "__favorites", label: "Favoriter", count: favoriteCount, special: "favorites" },
          { key: "__recent", label: "Senast spelade", count: recentCount, special: "recent" },
        ]
      : [
          { key: "__latest", label: "Senast tillagda", count: Math.min(latestLimit, sectionChannels.length), special: "latest" },
          { key: "__favorites", label: "Favoriter", count: favoriteCount, special: "favorites" },
          { key: "__recent", label: "Senast spelade", count: recentCount, special: "recent" },
        ];

  return [...specialCategories, ...regularCategories];
}

function getVisibleChannels(
  catalog: Channel[],
  section: BrowseSection,
  selectedCategory: CategoryOption | undefined,
  favorites: Set<string>,
  recent: RecentEntry[],
  query: string,
  preferences: CategoryPreferences,
) {
  const sectionChannels = getSectionChannels(catalog, section, preferences);
  const normalizedQuery = query.trim().length >= minSearchLength
    ? query.trim().toLowerCase()
    : "";
  let channels: Channel[];

  if (normalizedQuery) {
    channels = sectionChannels.filter((channel) =>
      [channel.name, channel.categoryName, channel.group].some((value) => value.toLowerCase().includes(normalizedQuery)),
    );
  } else if (!selectedCategory) {
    channels = sectionChannels;
  } else if (isSpecialCategory(selectedCategory)) {
    if (selectedCategory.special === "favorites") {
      channels = sectionChannels.filter((channel) => favorites.has(channel.url));
    } else if (selectedCategory.special === "recent") {
      const byUrl = new Map(sectionChannels.map((channel) => [channel.url, channel]));
      channels = recent
        .filter((entry) => entry.section === section)
        .map((entry) => byUrl.get(entry.url))
        .filter((channel): channel is Channel => Boolean(channel));
    } else {
      channels = sortForSection(section, sectionChannels).slice(0, latestLimit);
    }
  } else {
    const label = selectedCategory.key.replace(/^category:/, "");
    channels = sectionChannels.filter((channel) => channel.categoryName === label);
  }

  const sortedChannels = isSpecialCategory(selectedCategory) && selectedCategory?.special === "recent"
    ? channels
    : sortForSection(section, channels);

  return section === "series" ? groupSeriesCards(sortedChannels) : sortedChannels;
}

function buildCategoryManagerItems(
  catalog: Channel[],
  section: BrowseSection,
  preferences: CategoryPreferences,
): CategoryManagerItem[] {
  const counts = new Map<string, number>();
  catalog
    .filter((channel) => channel.contentType === section)
    .forEach((channel) => counts.set(channel.categoryName, (counts.get(channel.categoryName) ?? 0) + 1));

  const sectionPreferences = preferences[section] ?? {};
  return [...counts.entries()]
    .map(([label, count], index) => ({
      label,
      count,
      visible: sectionPreferences[label]?.visible ?? true,
      order: sectionPreferences[label]?.order ?? index + 10000,
    }))
    .sort((left, right) => left.order - right.order || left.label.localeCompare(right.label, "sv"));
}

function getSectionChannels(catalog: Channel[], section: BrowseSection, preferences: CategoryPreferences) {
  const sectionPreferences = preferences[section] ?? {};
  return catalog.filter((channel) => channel.contentType === section && (sectionPreferences[channel.categoryName]?.visible ?? true));
}

function sortForSection(section: BrowseSection, channels: Channel[]) {
  if (section === "live") {
    return [...channels].sort((left, right) => left.name.localeCompare(right.name, "sv"));
  }

  const hasAddedMetadata = channels.some((channel) => channel.addedAt);
  return [...channels].sort((left, right) => {
    if (hasAddedMetadata) {
      return (right.addedAt ?? 0) - (left.addedAt ?? 0) || left.playlistIndex - right.playlistIndex;
    }

    return left.playlistIndex - right.playlistIndex;
  });
}

function groupSeriesCards(channels: Channel[]) {
  const groups = new Map<string, Channel>();
  for (const channel of channels) {
    if (channel.url.startsWith("xtream-series://")) {
      groups.set(channel.url, channel);
      continue;
    }

    const title = getLocalSeriesTitle(channel.name);
    const key = `${channel.categoryName}|${title}`;
    if (!groups.has(key)) {
      groups.set(key, {
        ...channel,
        id: `series-group:${key}`,
        name: title,
      });
    }
  }

  return [...groups.values()];
}

function buildLocalSeriesDetail(channel: Channel, catalog: Channel[]): SeriesDetail {
  const title = channel.url.startsWith("xtream-series://") ? channel.name : getLocalSeriesTitle(channel.name);
  const episodes = catalog
    .filter((candidate) => candidate.contentType === "series")
    .filter((candidate) => candidate.url === channel.url || getLocalSeriesTitle(candidate.name) === title)
    .map((candidate, index) => buildLocalSeriesEpisode(candidate, index));
  const seasonMap = new Map<number, SeriesEpisode[]>();

  for (const episode of episodes) {
    const list = seasonMap.get(episode.seasonNumber) ?? [];
    list.push(episode);
    seasonMap.set(episode.seasonNumber, list);
  }

  const seasons = [...seasonMap.entries()]
    .map(([seasonNumber, seasonEpisodes]) => ({
      key: `season:${seasonNumber}`,
      label: seasonNumber === 1 ? "Avsnitt" : `Säsong ${seasonNumber}`,
      seasonNumber,
      episodes: seasonEpisodes.sort((left, right) => (left.episodeNumber ?? 0) - (right.episodeNumber ?? 0)),
    }))
    .sort((left, right) => left.seasonNumber - right.seasonNumber);

  return {
    id: channel.id,
    title,
    posterUrl: channel.logoUrl,
    plot: "",
    genre: channel.categoryName,
    cast: "",
    rating: "",
    seasons: seasons.length > 0 ? seasons : [{
      key: "season:1",
      label: "Avsnitt",
      seasonNumber: 1,
      episodes: [buildLocalSeriesEpisode(channel, 0)],
    }],
  };
}

function buildLocalSeriesEpisode(channel: Channel, index: number): SeriesEpisode {
  const seasonNumber = parseSeasonNumber(channel.name) ?? 1;
  const episodeNumber = parseEpisodeNumber(channel.name) ?? index + 1;
  return {
    id: `local:${channel.url}`,
    title: getLocalEpisodeTitle(channel.name, episodeNumber),
    subtitle: `Säsong ${seasonNumber} - Avsnitt ${episodeNumber}`,
    seasonNumber,
    episodeNumber,
    channel,
  };
}

function getLocalSeriesTitle(name: string) {
  return name
    .replace(/\bS\d{1,2}E\d{1,3}\b/gi, "")
    .replace(/\b\d{1,2}x\d{1,3}\b/g, "")
    .replace(/\bseason\s*\d+\b/gi, "")
    .replace(/\bsäsong\s*\d+\b/gi, "")
    .replace(/\bepisode\s*\d+\b/gi, "")
    .replace(/\bavsnitt\s*\d+\b/gi, "")
    .replace(/\s*[-:|]\s*$/, "")
    .replace(/\s+/g, " ")
    .trim() || name;
}

function getLocalEpisodeTitle(name: string, episodeNumber: number) {
  return name === getLocalSeriesTitle(name) ? `Avsnitt ${episodeNumber}` : name;
}

function parseSeasonNumber(name: string) {
  const match = /\bS(\d{1,2})E\d{1,3}\b/i.exec(name)
    ?? /\bseason\s*(\d+)\b/i.exec(name)
    ?? /\bsäsong\s*(\d+)\b/i.exec(name)
    ?? /\b(\d{1,2})x\d{1,3}\b/i.exec(name);
  const parsed = Number(match?.[1]);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
}

function parseEpisodeNumber(name: string) {
  const match = /\bS\d{1,2}E(\d{1,3})\b/i.exec(name)
    ?? /\bepisode\s*(\d+)\b/i.exec(name)
    ?? /\bavsnitt\s*(\d+)\b/i.exec(name)
    ?? /\b\d{1,2}x(\d{1,3})\b/i.exec(name);
  const parsed = Number(match?.[1]);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
}

function metadataText(channel: Channel, detail: MovieDetail | undefined, isLoading: boolean) {
  if (isLoading) {
    return "Hämtar metadata...";
  }

  const parts = [detail?.releaseDate, detail?.duration, detail?.rating ? `Betyg ${detail.rating}` : undefined, channel.categoryName]
    .filter(Boolean)
    .map(String);
  return parts.join(" - ");
}

function cloneCategoryPreferences(preferences: CategoryPreferences): CategoryPreferences {
  return {
    live: { ...(preferences.live ?? {}) },
    movies: { ...(preferences.movies ?? {}) },
    series: { ...(preferences.series ?? {}) },
  };
}
