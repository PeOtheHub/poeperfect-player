import type {
  Channel,
  ExternalSubtitleTrack,
  MovieDetail,
  SeriesDetail,
  SeriesEpisode,
  SeriesSeason,
  XtreamConnection,
} from "../domain";
import { fetchJson } from "./http";
import { normalizeCategory, parseAddedAt } from "./m3u";

type ApiCategory = {
  category_id?: string;
  category_name?: string;
};

type ApiMovieStream = {
  name?: string;
  title?: string;
  stream_id?: string | number;
  stream_icon?: string;
  container_extension?: string;
  category_id?: string | number;
  category_name?: string;
  added?: string | number;
};

type ApiLiveStream = {
  name?: string;
  title?: string;
  stream_id?: string | number;
  stream_icon?: string;
  epg_channel_id?: string;
  tvg_id?: string;
  tvg_name?: string;
  category_id?: string | number;
  added?: string | number;
};

type ApiSeries = {
  name?: string;
  title?: string;
  series_id?: string | number;
  cover?: string;
  category_id?: string | number;
  category_name?: string;
  last_modified?: string | number;
};

type MovieInfoResponse = {
  info?: Record<string, unknown>;
  movie_data?: Record<string, unknown>;
};

type SeriesInfoResponse = {
  info?: Record<string, unknown>;
  episodes?: Record<string, ApiSeriesEpisode[]>;
};

type ApiSeriesEpisode = {
  id?: string | number;
  episode_num?: string | number;
  title?: string;
  container_extension?: string;
  info?: Record<string, unknown>;
};

export function tryCreateXtreamConnection(source: string): XtreamConnection | undefined {
  let sourceUrl: URL;
  try {
    sourceUrl = new URL(source);
  } catch {
    return undefined;
  }

  const username = sourceUrl.searchParams.get("username");
  const password = sourceUrl.searchParams.get("password");
  if (!username || !password) {
    return undefined;
  }

  const directory = sourceUrl.pathname.includes("/")
    ? sourceUrl.pathname.slice(0, sourceUrl.pathname.lastIndexOf("/") + 1)
    : "/";
  const playerApiUrl = `${sourceUrl.origin}${directory}player_api.php`;
  const pathPrefix = directory.replace(/^\/|\/$/g, "");
  const streamBaseUrl = pathPrefix ? `${sourceUrl.origin}/${pathPrefix}` : sourceUrl.origin;

  return {
    playerApiUrl,
    streamBaseUrl,
    username,
    password,
    output: sourceUrl.searchParams.get("output") || "ts",
    displayName: sourceUrl.host,
  };
}

export async function loadXtreamCatalog(connection: XtreamConnection, signal?: AbortSignal) {
  const [liveCategories, movieCategories, seriesCategories] = await Promise.all([
    getCategories(connection, "get_live_categories", signal),
    getCategories(connection, "get_vod_categories", signal),
    getCategories(connection, "get_series_categories", signal),
  ]);

  const [live, movies, series] = await Promise.all([
    getLiveStreams(connection, liveCategories, signal),
    getMovieStreams(connection, movieCategories, signal),
    getSeries(connection, seriesCategories, signal),
  ]);

  return [...live, ...movies, ...series].map((channel, index) => ({
    ...channel,
    playlistIndex: index,
    id: channel.id || `${index}:${channel.url}`,
  }));
}

export async function loadMovieDetail(
  connection: XtreamConnection | undefined,
  channel: Channel,
  signal?: AbortSignal,
): Promise<MovieDetail | undefined> {
  if (!connection || channel.contentType !== "movies") {
    return undefined;
  }

  const streamId = getStreamId(channel.url);
  if (!streamId) {
    return undefined;
  }

  const response = await fetchJson<MovieInfoResponse>(apiUrl(connection, "get_vod_info", { vod_id: streamId }), signal);
  const info = response.info ?? {};
  const movieData = response.movie_data ?? {};
  const subtitleTracks = extractSubtitleTracks(connection, info, movieData, response);
  const durationSeconds = normalizeDurationSeconds(
    stringValue(info.duration_secs),
    stringValue(info.duration),
    stringValue(movieData.duration),
  );

  return {
    title: cleanText(stringValue(movieData.name) || stringValue(info.name) || channel.name),
    posterUrl:
      stringValue(info.movie_image) ||
      stringValue(info.cover_big) ||
      stringValue(info.poster_path) ||
      channel.logoUrl,
    plot: cleanText(stringValue(info.plot) || stringValue(info.description)),
    genre: cleanText(stringValue(info.genre)),
    cast: cleanText(stringValue(info.cast) || stringValue(info.actors)),
    director: cleanText(stringValue(info.director)),
    rating: cleanText(stringValue(info.rating) || stringValue(info.rating_5based)),
    duration: normalizeDuration(stringValue(info.duration_secs), stringValue(info.duration), stringValue(movieData.duration)),
    durationSeconds,
    releaseDate: cleanText(stringValue(info.releasedate) || stringValue(info.release_date)),
    subtitleTracks,
  };
}

export async function loadSeriesDetail(
  connection: XtreamConnection | undefined,
  channel: Channel,
  signal?: AbortSignal,
): Promise<SeriesDetail | undefined> {
  if (!connection || channel.contentType !== "series") {
    return undefined;
  }

  const seriesId = getSeriesId(channel.url);
  if (!seriesId) {
    return undefined;
  }

  const response = await fetchJson<SeriesInfoResponse>(
    apiUrl(connection, "get_series_info", { series_id: seriesId }),
    signal,
  );
  const info = response.info ?? {};
  const seasons = Object.entries(response.episodes ?? {})
    .map(([seasonKey, episodes]) => buildSeriesSeason(connection, channel, Number(seasonKey), episodes))
    .filter((season): season is SeriesSeason => season.episodes.length > 0)
    .sort((left, right) => left.seasonNumber - right.seasonNumber);

  return {
    id: seriesId,
    title: cleanText(stringValue(info.name) || channel.name),
    posterUrl: stringValue(info.cover) || stringValue(info.movie_image) || channel.logoUrl,
    plot: cleanText(stringValue(info.plot)),
    genre: cleanText(stringValue(info.genre)),
    cast: cleanText(stringValue(info.cast) || stringValue(info.actors)),
    rating: cleanText(stringValue(info.rating)),
    seasons,
  };
}

async function getCategories(connection: XtreamConnection, action: string, signal?: AbortSignal) {
  try {
    const categories = await fetchJson<ApiCategory[]>(apiUrl(connection, action), signal);
    return new Map(
      categories
        .filter((category) => category.category_id && category.category_name)
        .map((category) => [String(category.category_id), category.category_name ?? ""]),
    );
  } catch {
    return new Map<string, string>();
  }
}

async function getLiveStreams(connection: XtreamConnection, categories: Map<string, string>, signal?: AbortSignal) {
  const streams = await fetchJson<ApiLiveStream[]>(apiUrl(connection, "get_live_streams"), signal);
  return streams.flatMap((stream, index): Channel[] => {
    const streamId = stringValue(stream.stream_id);
    const name = stringValue(stream.name) || stringValue(stream.title);
    if (!streamId || !name) {
      return [];
    }

    const categoryName = normalizeCategory(categories.get(stringValue(stream.category_id)) || "Kanaler", "live");

    return [
      {
        id: `live:${streamId}`,
        name,
        url: `${connection.streamBaseUrl}/live/${encodeURIComponent(connection.username)}/${encodeURIComponent(connection.password)}/${streamId}.${connection.output}`,
        group: categoryName,
        categoryName,
        logoUrl: stringValue(stream.stream_icon),
        tvgId: stringValue(stream.epg_channel_id) || stringValue(stream.tvg_id),
        tvgName: stringValue(stream.tvg_name),
        addedAt: parseAddedAt(stringValue(stream.added)),
        contentType: "live",
        playlistIndex: index,
      },
    ];
  });
}

async function getMovieStreams(connection: XtreamConnection, categories: Map<string, string>, signal?: AbortSignal) {
  const streams = await fetchJson<ApiMovieStream[]>(apiUrl(connection, "get_vod_streams"), signal);
  return streams.flatMap((stream, index): Channel[] => {
    const streamId = stringValue(stream.stream_id);
    const name = stringValue(stream.name) || stringValue(stream.title);
    if (!streamId || !name) {
      return [];
    }

    const categoryName = normalizeCategory(
      categories.get(stringValue(stream.category_id)) || stringValue(stream.category_name) || "Filmer",
      "movies",
    );
    const extension = stringValue(stream.container_extension) || "mp4";

    return [
      {
        id: `movie:${streamId}`,
        name,
        url: `${connection.streamBaseUrl}/movie/${encodeURIComponent(connection.username)}/${encodeURIComponent(connection.password)}/${streamId}.${extension}`,
        group: categoryName,
        categoryName,
        logoUrl: stringValue(stream.stream_icon),
        addedAt: parseAddedAt(stringValue(stream.added)),
        contentType: "movies",
        playlistIndex: index,
      },
    ];
  });
}

async function getSeries(connection: XtreamConnection, categories: Map<string, string>, signal?: AbortSignal) {
  const streams = await fetchJson<ApiSeries[]>(apiUrl(connection, "get_series"), signal);
  return streams.flatMap((series, index): Channel[] => {
    const seriesId = stringValue(series.series_id);
    const name = stringValue(series.name) || stringValue(series.title);
    if (!seriesId || !name) {
      return [];
    }

    const categoryName = normalizeCategory(
      categories.get(stringValue(series.category_id)) || stringValue(series.category_name) || "Serier",
      "series",
    );

    return [
      {
        id: `series:${seriesId}`,
        name,
        url: `xtream-series://${seriesId}`,
        group: categoryName,
        categoryName,
        logoUrl: stringValue(series.cover),
        addedAt: parseAddedAt(stringValue(series.last_modified)),
        contentType: "series",
        playlistIndex: index,
      },
    ];
  });
}

function apiUrl(connection: XtreamConnection, action: string, extra: Record<string, string> = {}) {
  const url = new URL(connection.playerApiUrl);
  url.searchParams.set("username", connection.username);
  url.searchParams.set("password", connection.password);
  url.searchParams.set("action", action);
  for (const [key, value] of Object.entries(extra)) {
    url.searchParams.set(key, value);
  }

  return url.toString();
}

function getStreamId(url: string) {
  const match = /\/(?:movie|vod)\/[^/]+\/[^/]+\/([^/.?]+)/i.exec(url);
  return match?.[1] ? decodeURIComponent(match[1]) : undefined;
}

function getSeriesId(url: string) {
  if (url.startsWith("xtream-series://")) {
    return url.replace("xtream-series://", "");
  }

  const match = /\/series\/[^/]+\/[^/]+\/([^/.?]+)/i.exec(url);
  return match?.[1] ? decodeURIComponent(match[1]) : undefined;
}

function buildSeriesSeason(
  connection: XtreamConnection,
  seriesChannel: Channel,
  seasonNumber: number,
  episodes: ApiSeriesEpisode[],
): SeriesSeason {
  const normalizedSeasonNumber = Number.isFinite(seasonNumber) && seasonNumber > 0 ? seasonNumber : 1;
  const sortedEpisodes = [...episodes]
    .map((episode, index) => buildSeriesEpisode(connection, seriesChannel, normalizedSeasonNumber, episode, index))
    .filter((episode): episode is SeriesEpisode => Boolean(episode))
    .sort((left, right) => (left.episodeNumber ?? 0) - (right.episodeNumber ?? 0));

  return {
    key: `season:${normalizedSeasonNumber}`,
    label: `Säsong ${normalizedSeasonNumber}`,
    seasonNumber: normalizedSeasonNumber,
    episodes: sortedEpisodes,
  };
}

function buildSeriesEpisode(
  connection: XtreamConnection,
  seriesChannel: Channel,
  seasonNumber: number,
  episode: ApiSeriesEpisode,
  index: number,
): SeriesEpisode | undefined {
  const episodeId = stringValue(episode.id);
  if (!episodeId) {
    return undefined;
  }

  const episodeNumber = parseEpisodeNumber(episode.episode_num) ?? index + 1;
  const title = cleanText(stringValue(episode.title)) || `Avsnitt ${episodeNumber}`;
  const extension = stringValue(episode.container_extension) || "mp4";
  const url = `${connection.streamBaseUrl}/series/${encodeURIComponent(connection.username)}/${encodeURIComponent(connection.password)}/${episodeId}.${extension}`;

  return {
    id: `series:${seriesChannel.id}:${seasonNumber}:${episodeId}`,
    title,
    subtitle: `Säsong ${seasonNumber} - Avsnitt ${episodeNumber}`,
    seasonNumber,
    episodeNumber,
    channel: {
      id: `episode:${episodeId}`,
      name: `${seriesChannel.name} - ${title}`,
      url,
      group: seriesChannel.group,
      categoryName: seriesChannel.categoryName,
      logoUrl: stringValue(episode.info?.movie_image) || seriesChannel.logoUrl,
      addedAt: parseAddedAt(stringValue(episode.info?.releasedate)),
      contentType: "series",
      playlistIndex: seriesChannel.playlistIndex,
      subtitleTracks: extractSubtitleTracks(connection, episode, episode.info),
    },
  };
}

function extractSubtitleTracks(connection: XtreamConnection, ...sources: unknown[]): ExternalSubtitleTrack[] {
  const tracks = new Map<string, ExternalSubtitleTrack>();

  function addTrack(rawUrl: string, labelHint?: string, languageHint?: string) {
    const url = normalizeSubtitleUrl(rawUrl, connection);
    if (!url || !isLikelySubtitleUrl(url, labelHint)) {
      return;
    }

    const language = cleanText(languageHint || "");
    const label = cleanText(labelHint || language || subtitleLabelFromUrl(url) || `Undertext ${tracks.size + 1}`);
    if (!tracks.has(url)) {
      tracks.set(url, {
        id: `external:${tracks.size}:${url}`,
        label,
        url,
        language: language || undefined,
      });
    }
  }

  function visit(value: unknown, keyHint = "", labelHint = "", languageHint = "") {
    if (value === null || value === undefined) {
      return;
    }

    if (typeof value === "string") {
      const keyLooksRelevant = /sub|caption|vtt|srt/i.test(keyHint);
      extractUrls(value).forEach((url) => addTrack(url, labelHint || keyHint, languageHint));
      if (keyLooksRelevant && extractUrls(value).length === 0) {
        addTrack(value, labelHint || keyHint, languageHint);
      }
      return;
    }

    if (Array.isArray(value)) {
      value.forEach((item, index) => visit(item, keyHint, labelHint || `${keyHint} ${index + 1}`, languageHint));
      return;
    }

    if (typeof value !== "object") {
      return;
    }

    const record = value as Record<string, unknown>;
    const label =
      stringValue(record.label) ||
      stringValue(record.name) ||
      stringValue(record.title) ||
      stringValue(record.language) ||
      stringValue(record.lang) ||
      labelHint;
    const language = stringValue(record.language) || stringValue(record.lang) || languageHint;

    for (const urlKey of ["url", "file", "src", "source", "link", "href"]) {
      if (typeof record[urlKey] === "string") {
        addTrack(record[urlKey] as string, label, language);
      }
    }

    const isSubtitleScope = /sub|caption|vtt|srt/i.test(keyHint);
    for (const [key, item] of Object.entries(record)) {
      if (isSubtitleScope || /sub|caption|vtt|srt/i.test(key)) {
        visit(item, key, label || key, language);
      }
    }
  }

  sources.forEach((source) => visit(source));
  return [...tracks.values()];
}

function extractUrls(value: string) {
  return [...value.matchAll(/https?:\/\/[^\s"'<>]+/gi)].map((match) => match[0]);
}

function normalizeSubtitleUrl(value: string, connection: XtreamConnection) {
  const trimmed = value.trim();
  if (!trimmed) {
    return "";
  }

  try {
    return new URL(trimmed).toString();
  } catch {
    try {
      return new URL(trimmed, `${connection.streamBaseUrl}/`).toString();
    } catch {
      return "";
    }
  }
}

function isLikelySubtitleUrl(url: string, labelHint = "") {
  const withoutQuery = url.split("?")[0].toLowerCase();
  return /\.(srt|vtt)$/.test(withoutQuery) || /sub|caption|vtt|srt/i.test(labelHint);
}

function subtitleLabelFromUrl(url: string) {
  try {
    const pathname = new URL(url).pathname;
    const fileName = pathname.split("/").pop() ?? "";
    return decodeURIComponent(fileName)
      .replace(/\.(srt|vtt)$/i, "")
      .replace(/[._-]+/g, " ")
      .trim();
  } catch {
    return "";
  }
}

function parseEpisodeNumber(value: unknown) {
  const parsed = Number(stringValue(value));
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
}

function stringValue(value: unknown) {
  if (value === null || value === undefined) {
    return "";
  }

  return String(value).trim();
}

function cleanText(value: string) {
  const parser = new DOMParser();
  const decoded = parser.parseFromString(value || "", "text/html").documentElement.textContent || "";
  return decoded.replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim();
}

function normalizeDuration(...values: string[]) {
  for (const value of values) {
    if (!value) {
      continue;
    }

    if (/^\d+$/.test(value)) {
      const seconds = Number(value);
      if (seconds > 0) {
        const hours = Math.floor(seconds / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        return hours > 0 ? `${hours} h ${minutes.toString().padStart(2, "0")} min` : `${minutes} min`;
      }
    }

    return value;
  }

  return "";
}

function normalizeDurationSeconds(...values: string[]) {
  for (const value of values) {
    const seconds = parseDurationSeconds(value);
    if (seconds > 0) {
      return seconds;
    }
  }

  return undefined;
}

function parseDurationSeconds(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return 0;
  }

  if (/^\d+$/.test(trimmed)) {
    return Number(trimmed);
  }

  const clockParts = trimmed.split(":").map((part) => Number(part));
  if (clockParts.length >= 2 && clockParts.length <= 3 && clockParts.every((part) => Number.isFinite(part))) {
    const [hours, minutes, seconds] = clockParts.length === 3
      ? clockParts
      : [0, clockParts[0], clockParts[1]];
    return (hours * 3600) + (minutes * 60) + seconds;
  }

  const hourMatch = trimmed.match(/(\d+)\s*h/i);
  const minuteMatch = trimmed.match(/(\d+)\s*min/i);
  const hours = hourMatch ? Number(hourMatch[1]) : 0;
  const minutes = minuteMatch ? Number(minuteMatch[1]) : 0;
  return (hours * 3600) + (minutes * 60);
}
