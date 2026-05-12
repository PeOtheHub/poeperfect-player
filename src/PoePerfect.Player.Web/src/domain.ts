export type BrowseSection = "live" | "movies" | "series";

export type AppView = "dashboard" | "browser" | "playlists";

export type Channel = {
  id: string;
  name: string;
  url: string;
  group: string;
  categoryName: string;
  logoUrl?: string;
  tvgId?: string;
  tvgName?: string;
  addedAt?: number;
  contentType: BrowseSection;
  playlistIndex: number;
};

export type CategoryOption = {
  key: string;
  label: string;
  count: number;
  special?: "latest" | "favorites" | "recent";
};

export type MovieDetail = {
  title: string;
  posterUrl?: string;
  plot: string;
  genre: string;
  cast: string;
  director: string;
  rating: string;
  duration: string;
  releaseDate: string;
};

export type SeriesEpisode = {
  id: string;
  title: string;
  subtitle: string;
  seasonNumber: number;
  episodeNumber?: number;
  channel: Channel;
};

export type SeriesSeason = {
  key: string;
  label: string;
  seasonNumber: number;
  episodes: SeriesEpisode[];
};

export type SeriesDetail = {
  id: string;
  title: string;
  posterUrl?: string;
  plot: string;
  genre: string;
  cast: string;
  rating: string;
  seasons: SeriesSeason[];
};

export type CategoryPreference = {
  visible: boolean;
  order: number;
};

export type CategoryPreferences = Partial<
  Record<BrowseSection, Record<string, CategoryPreference>>
>;

export type CategoryManagerItem = {
  label: string;
  count: number;
  visible: boolean;
  order: number;
};

export type XtreamConnection = {
  playerApiUrl: string;
  streamBaseUrl: string;
  username: string;
  password: string;
  output: string;
  displayName: string;
};

export type LoadResult = {
  channels: Channel[];
  sourceKind: "m3u" | "xtream";
  connection?: XtreamConnection;
};

export const sectionLabels: Record<BrowseSection, string> = {
  live: "Live",
  movies: "Film",
  series: "Serier",
};

export function isSpecialCategory(category: CategoryOption | undefined) {
  return Boolean(category?.special);
}
