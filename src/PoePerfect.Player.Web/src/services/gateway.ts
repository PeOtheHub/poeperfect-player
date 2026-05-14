type GatewayStartResponse = {
  sessionId: string;
  playlistUrl: string;
  mediaPlaylistUrl?: string;
  startAtSeconds?: number;
};

export async function startGatewayStream(
  sourceUrl: string,
  subtitleLanguage = "swe",
  startAtSeconds = 0,
  audioTrackIndex = 0,
  subtitleTrackIndex = -1,
) {
  const response = await fetch("/api/gateway/start", {
    method: "POST",
    headers: {
      "content-type": "application/json",
    },
    body: JSON.stringify({
      url: sourceUrl,
      subtitleLanguage,
      startAtSeconds,
      audioTrackIndex,
      subtitleTrackIndex,
    }),
  });
  if (!response.ok) {
    throw new Error(`Gateway HTTP ${response.status}`);
  }

  return (await response.json()) as GatewayStartResponse;
}

export async function stopGatewayStream(sessionId: string) {
  await fetch(`/api/gateway/stop?id=${encodeURIComponent(sessionId)}`).catch(() => undefined);
}

export function shouldUseGatewayStream(sourceUrl: string) {
  return import.meta.env.DEV && /^https?:/i.test(sourceUrl) && /\.mkv(?:$|\?)/i.test(sourceUrl);
}
