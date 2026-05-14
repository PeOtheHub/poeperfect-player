import type { BrowseSection, CategoryPreferences } from "../domain";

const sourceKey = "poeperfect.web.source";
const favoriteKey = "poeperfect.web.favorites";
const recentKey = "poeperfect.web.recent";
const categoryPreferencesKey = "poeperfect.web.category-preferences";
const watchProgressKey = "poeperfect.web.watch-progress";

export function loadStoredSource() {
  return localStorage.getItem(sourceKey) ?? "";
}

export function saveStoredSource(source: string) {
  localStorage.setItem(sourceKey, source);
}

export function loadFavorites() {
  return new Set(readJson<string[]>(favoriteKey, []));
}

export function saveFavorites(favorites: Set<string>) {
  localStorage.setItem(favoriteKey, JSON.stringify([...favorites].slice(0, 1000)));
}

export type RecentEntry = {
  section: BrowseSection;
  url: string;
  playedAt: number;
};

export function loadRecent() {
  return readJson<RecentEntry[]>(recentKey, []);
}

export function rememberRecent(entry: RecentEntry) {
  const next = [
    entry,
    ...loadRecent().filter((item) => item.section !== entry.section || item.url !== entry.url),
  ].slice(0, 120);
  localStorage.setItem(recentKey, JSON.stringify(next));
  return next;
}

export type WatchProgress = {
  url: string;
  positionSeconds: number;
  durationSeconds: number;
  updatedAt: number;
};

export function loadWatchProgress(url: string) {
  return readJson<Record<string, WatchProgress>>(watchProgressKey, {})[url];
}

export function saveWatchProgress(progress: WatchProgress) {
  const allProgress = readJson<Record<string, WatchProgress>>(watchProgressKey, {});
  allProgress[progress.url] = progress;
  localStorage.setItem(watchProgressKey, JSON.stringify(allProgress));
}

export function clearWatchProgress(url: string) {
  const allProgress = readJson<Record<string, WatchProgress>>(watchProgressKey, {});
  if (!(url in allProgress)) {
    return;
  }

  delete allProgress[url];
  localStorage.setItem(watchProgressKey, JSON.stringify(allProgress));
}

export function loadCategoryPreferences(): CategoryPreferences {
  return readJson<CategoryPreferences>(categoryPreferencesKey, {});
}

export function saveCategoryPreferences(preferences: CategoryPreferences) {
  localStorage.setItem(categoryPreferencesKey, JSON.stringify(preferences));
}

function readJson<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}
