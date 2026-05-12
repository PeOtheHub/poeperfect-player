import type { BrowseSection, Channel } from "../domain";

const attributePattern = /([a-zA-Z0-9_-]+)\s*=\s*"([^"]*)"/g;
const latestAttributeNames = [
  "added",
  "added-at",
  "added_at",
  "date-added",
  "date_added",
  "created",
  "created-at",
  "created_at",
];

export function parseM3uPlaylist(content: string): Channel[] {
  const lines = content.replace(/^\uFEFF/, "").split(/\r?\n/);
  const channels: Channel[] = [];
  let pending: PendingEntry | undefined;

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line) {
      continue;
    }

    if (line.startsWith("#EXTINF", 0)) {
      pending = parseExtInf(line);
      continue;
    }

    if (line.startsWith("#") || !pending) {
      continue;
    }

    const name = pending.displayName || pending.tvgName || "Namnlös titel";
    const group = pending.group || "Okänd grupp";
    const contentType = detectContentType(name, line, group);
    channels.push({
      id: `${channels.length}:${line}`,
      name,
      url: line,
      group,
      categoryName: normalizeCategory(group, contentType),
      logoUrl: pending.logoUrl,
      tvgId: pending.tvgId,
      tvgName: pending.tvgName,
      addedAt: pending.addedAt,
      contentType,
      playlistIndex: channels.length,
    });
    pending = undefined;
  }

  return channels;
}

type PendingEntry = {
  displayName?: string;
  group?: string;
  logoUrl?: string;
  tvgId?: string;
  tvgName?: string;
  addedAt?: number;
};

function parseExtInf(line: string): PendingEntry {
  const separatorIndex = findDisplayNameSeparator(line);
  const metadata = separatorIndex >= 0 ? line.slice(0, separatorIndex) : line;
  const displayName = separatorIndex >= 0 ? line.slice(separatorIndex + 1).trim() : "";
  const attributes = new Map<string, string>();

  for (const match of metadata.matchAll(attributePattern)) {
    attributes.set(match[1].toLowerCase(), match[2].trim());
  }

  return {
    displayName,
    group: attributes.get("group-title"),
    logoUrl: attributes.get("tvg-logo"),
    tvgId: attributes.get("tvg-id"),
    tvgName: attributes.get("tvg-name"),
    addedAt: parseAddedAt(latestAttributeNames.map((name) => attributes.get(name)).find(Boolean)),
  };
}

function findDisplayNameSeparator(line: string) {
  let inQuote = false;
  for (let index = 0; index < line.length; index += 1) {
    const current = line[index];
    if (current === '"') {
      inQuote = !inQuote;
    } else if (current === "," && !inQuote) {
      return index;
    }
  }

  return -1;
}

export function parseAddedAt(value: string | undefined) {
  if (!value?.trim()) {
    return undefined;
  }

  const trimmed = value.trim();
  if (/^\d+$/.test(trimmed)) {
    const numeric = Number(trimmed);
    if (!Number.isFinite(numeric) || numeric <= 0) {
      return undefined;
    }

    return numeric > 9_999_999_999 ? numeric : numeric * 1000;
  }

  const parsed = Date.parse(trimmed);
  return Number.isFinite(parsed) ? parsed : undefined;
}

export function detectContentType(name: string, url: string, group: string): BrowseSection {
  const lowerUrl = url.toLowerCase();
  if (lowerUrl.includes("/series/") || lowerUrl.includes("series_id") || lowerUrl.includes("type=series")) {
    return "series";
  }

  if (
    lowerUrl.includes("/movie/") ||
    lowerUrl.includes("/vod/") ||
    lowerUrl.includes("movie_id") ||
    lowerUrl.includes("type=movie")
  ) {
    return "movies";
  }

  if (lowerUrl.includes("/live/") || lowerUrl.includes("type=live")) {
    return "live";
  }

  const lowerName = name.toLowerCase();
  const lowerGroup = group.toLowerCase();
  if (
    includesAny(lowerName, ["season", "säsong", "episode", "avsnitt"]) ||
    startsWithAny(lowerGroup, ["series", "serier", "serie", "tv shows", "tv-shows"])
  ) {
    return "series";
  }

  if (
    startsWithAny(lowerGroup, ["vod", "movie", "movies", "film", "filmer"]) ||
    /\.(mp4|mkv|avi|mov|m3u8)(\?|$)/i.test(lowerUrl)
  ) {
    return "movies";
  }

  return "live";
}

export function normalizeCategory(group: string, section: BrowseSection) {
  const prefixes: Record<BrowseSection, string[]> = {
    live: ["live", "tv", "channels", "channel", "kanaler"],
    movies: ["vod", "movie", "movies", "film", "filmer"],
    series: ["series", "serier", "serie", "tv shows", "tv-shows"],
  };
  let normalized = group.trim() || fallbackCategory(section);
  const lower = normalized.toLowerCase();
  const prefix = prefixes[section].find((candidate) => lower.startsWith(candidate));

  if (prefix) {
    normalized = normalized.slice(prefix.length).replace(/^[|/\\:>\-\s]+/, "").trim();
  }

  return normalized || fallbackCategory(section);
}

function fallbackCategory(section: BrowseSection) {
  if (section === "movies") {
    return "Filmer";
  }

  if (section === "series") {
    return "Serier";
  }

  return "Kanaler";
}

function includesAny(value: string, candidates: string[]) {
  return candidates.some((candidate) => value.includes(candidate));
}

function startsWithAny(value: string, candidates: string[]) {
  return candidates.some((candidate) => value.startsWith(candidate));
}
