import type { ExternalSubtitleTrack } from "../domain";
import { fetchText } from "./http";

type SubtitleProbeResponse = {
  available: boolean;
  error?: string;
  tracks?: Array<{
    subtitleTrackIndex?: number;
    streamIndex: number;
    label: string;
    language?: string;
  }>;
};

export type EmbeddedSubtitleProbeResult = {
  status: "skipped" | "ok" | "none" | "error";
  tracks: ExternalSubtitleTrack[];
  message?: string;
};

const probeCache = new Map<string, Promise<EmbeddedSubtitleProbeResult>>();

export async function probeEmbeddedSubtitleTracksDetailed(mediaUrl: string): Promise<EmbeddedSubtitleProbeResult> {
  if (!import.meta.env.DEV || !mediaUrl || mediaUrl.startsWith("xtream-series://")) {
    return { status: "skipped", tracks: [] };
  }

  const cached = probeCache.get(mediaUrl);
  if (cached) {
    return cached;
  }

  const probePromise = probeEmbeddedSubtitleTracksUncached(mediaUrl);
  probeCache.set(mediaUrl, probePromise);
  return probePromise;
}

async function probeEmbeddedSubtitleTracksUncached(mediaUrl: string): Promise<EmbeddedSubtitleProbeResult> {
  try {
    const response = await fetch(`/api/subtitles/probe?url=${encodeURIComponent(mediaUrl)}`);
    const result = await readProbeResponse(response);
    if (!response.ok) {
      const message = result?.error || `Subtitle probe failed (${response.status})`;
      console.warn("[subtitles]", message);
      return { status: "error", tracks: [], message };
    }

    if (!result?.available || !result.tracks?.length) {
      return { status: "none", tracks: [], message: "Inga inbaddade undertexter hittades." };
    }

    const tracks = result.tracks.map((track, index) => {
      const subtitleTrackIndex = track.subtitleTrackIndex ?? index;
      return {
        id: `embedded:${subtitleTrackIndex}`,
        label: track.label || `Inbaddad undertext ${subtitleTrackIndex + 1}`,
        language: track.language,
        url: `/api/subtitles/extract?url=${encodeURIComponent(mediaUrl)}&stream=${subtitleTrackIndex}`,
      };
    });

    return { status: "ok", tracks, message: `Hittade ${tracks.length} inbaddade undertextspar.` };
  } catch (error) {
    const message = error instanceof Error ? error.message : "Subtitle probe failed";
    console.warn("[subtitles]", message);
    return { status: "error", tracks: [], message };
  }
}

export async function probeEmbeddedSubtitleTracks(mediaUrl: string): Promise<ExternalSubtitleTrack[]> {
  return (await probeEmbeddedSubtitleTracksDetailed(mediaUrl)).tracks;
}

export async function preparePreferredEmbeddedSubtitles(mediaUrl: string, signal?: AbortSignal) {
  const probe = await probeEmbeddedSubtitleTracksDetailed(mediaUrl);
  if (probe.status !== "ok") {
    return probe;
  }

  const preferredTracks = probe.tracks.filter(isPreferredSubtitleTrack);
  if (preferredTracks.length === 0) {
    return {
      status: "none" as const,
      tracks: [],
      message: `Hittade ${probe.tracks.length} undertextspar, men inga svenska eller engelska.`,
    };
  }

  const preparedTracks: ExternalSubtitleTrack[] = [];
  try {
    for (const track of preferredTracks) {
      if (signal?.aborted) {
        throw new DOMException("Subtitle preparation aborted", "AbortError");
      }

      const webVtt = await fetchText(track.url, signal);
      const objectUrl = URL.createObjectURL(new Blob([webVtt], { type: "text/vtt" }));
      preparedTracks.push({
        ...track,
        id: `prepared:${track.id}`,
        url: objectUrl,
      });
    }
  } catch (error) {
    releasePreparedSubtitleTracks(preparedTracks);
    throw error;
  }

  return {
    status: "ok" as const,
    tracks: preparedTracks,
    message: `Forberedde ${preparedTracks.length} svenska/engelska undertextspar.`,
  };
}

export function releasePreparedSubtitleTracks(tracks: ExternalSubtitleTrack[]) {
  tracks.forEach((track) => {
    if (track.url.startsWith("blob:")) {
      URL.revokeObjectURL(track.url);
    }
  });
}

export function isPreferredSubtitleTrack(track: { label?: string; language?: string }) {
  const normalizedText = `${track.language ?? ""} ${track.label ?? ""}`.toLowerCase();
  const tokens = normalizedText.split(/[^a-z0-9]+/).filter(Boolean);
  const preferredTokens = new Set([
    "sv",
    "swe",
    "se",
    "swedish",
    "svenska",
    "en",
    "eng",
    "english",
    "engelska",
  ]);
  return tokens.some((token) => preferredTokens.has(token));
}

async function readProbeResponse(response: Response) {
  try {
    return (await response.json()) as SubtitleProbeResponse;
  } catch {
    return undefined;
  }
}
