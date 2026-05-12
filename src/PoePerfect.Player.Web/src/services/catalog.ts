import type { LoadResult } from "../domain";
import { fetchText } from "./http";
import { parseM3uPlaylist } from "./m3u";
import { loadXtreamCatalog, tryCreateXtreamConnection } from "./xtream";

export async function loadCatalogFromSource(source: string, signal?: AbortSignal): Promise<LoadResult> {
  const trimmedSource = source.trim();
  if (!trimmedSource) {
    return { channels: [], sourceKind: "m3u" };
  }

  const connection = tryCreateXtreamConnection(trimmedSource);
  if (connection) {
    try {
      const channels = await loadXtreamCatalog(connection, signal);
      if (channels.length > 0) {
        return { channels, sourceKind: "xtream", connection };
      }
    } catch {
      // Fall back to raw M3U loading below. Some providers support get.php but not player_api.php.
    }
  }

  const playlist = await fetchText(trimmedSource, signal);
  return {
    channels: parseM3uPlaylist(playlist),
    sourceKind: "m3u",
    connection,
  };
}

export async function loadCatalogFromFile(file: File): Promise<LoadResult> {
  const playlist = await file.text();
  return {
    channels: parseM3uPlaylist(playlist),
    sourceKind: "m3u",
  };
}
