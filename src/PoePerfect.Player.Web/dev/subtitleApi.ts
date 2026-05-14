import { spawn } from "node:child_process";
import type { IncomingMessage, ServerResponse } from "node:http";
import { createRequire } from "node:module";

type ProbeStream = {
  index?: number;
  codec_name?: string;
  codec_type?: string;
  channels?: number;
  channel_layout?: string;
  tags?: Record<string, string>;
  disposition?: Record<string, number>;
};

type ProbeResult = {
  streams?: ProbeStream[];
};

type SubtitleTrackInfo = {
  subtitleTrackIndex: number;
  streamIndex: number;
  containerStreamIndex: number;
  codec: string;
  language: string;
  title: string;
  label: string;
  isDefault: boolean;
};

type AudioTrackInfo = {
  audioTrackIndex: number;
  streamIndex: number;
  containerStreamIndex: number;
  codec: string;
  language: string;
  title: string;
  label: string;
  isDefault: boolean;
  channels?: number;
  channelLayout?: string;
};

const probeTimeoutMs = 30_000;
const extractTimeoutMs = 180_000;
const maxBufferedBytes = 8 * 1024 * 1024;
const requireOptional = createRequire(import.meta.url);
const ffprobeCommand = optionalPackageBinary("ffprobe-static") ?? "ffprobe";
const ffmpegCommand = optionalPackageBinary("ffmpeg-static") ?? "ffmpeg";
const mediaUserAgent = "PoePerfectPlayerWeb/0.1";

export function configureSubtitleApi(use: (route: string, handler: (request: IncomingMessage, response: ServerResponse) => void) => void) {
  use("/api/subtitles/probe", (request, response) => {
    void handleProbeRequest(request, response);
  });
  use("/api/subtitles/extract", (request, response) => {
    void handleExtractRequest(request, response);
  });
}

async function handleProbeRequest(request: IncomingMessage, response: ServerResponse) {
  const targetUrl = readTargetUrl(request, response);
  if (!targetUrl) {
    return;
  }

  try {
    const abortController = new AbortController();
    const abortCommand = () => abortController.abort();
    response.once("close", abortCommand);
    console.info(`[subtitles] probing ${redactMediaUrl(targetUrl)}`);
    const result = await runBufferedCommand(
      ffprobeCommand,
      [
        "-v",
        "error",
        "-user_agent",
        mediaUserAgent,
        "-rw_timeout",
        "30000000",
        "-print_format",
        "json",
        "-show_streams",
        targetUrl,
      ],
      probeTimeoutMs,
      abortController.signal,
    );
    response.off("close", abortCommand);
    const probe = JSON.parse(result.stdout || "{}") as ProbeResult;
    const tracks = normalizeProbeStreams(probe.streams ?? []);
    const audioTracks = normalizeAudioProbeStreams(probe.streams ?? []);
    console.info(`[subtitles] probe found ${tracks.length} subtitle track(s), ${audioTracks.length} audio track(s)`);
    writeJson(response, 200, {
      available: tracks.length > 0,
      extractor: "ffprobe",
      tracks,
      audioTracks,
    });
  } catch (error) {
    if (isCommandCancelled(error) || response.destroyed) {
      console.info("[subtitles] probe cancelled");
      return;
    }

    console.warn(`[subtitles] probe failed: ${errorMessage(error)}`);
    writeJson(response, commandStatus(error), {
      available: false,
      extractor: "ffprobe",
      error: errorMessage(error),
    });
  }
}

async function handleExtractRequest(request: IncomingMessage, response: ServerResponse) {
  const targetUrl = readTargetUrl(request, response);
  if (!targetUrl) {
    return;
  }

  const requestUrl = new URL(request.url ?? "", "http://localhost");
  const subtitleTrackIndex = Number(requestUrl.searchParams.get("stream") ?? "0");
  if (!Number.isInteger(subtitleTrackIndex) || subtitleTrackIndex < 0) {
    writeText(response, 400, "stream must be a zero-based subtitle track index");
    return;
  }

  try {
    const abortController = new AbortController();
    const abortCommand = () => abortController.abort();
    response.once("close", abortCommand);
    console.info(`[subtitles] extracting subtitle track ${subtitleTrackIndex} from ${redactMediaUrl(targetUrl)}`);
    const result = await runBufferedCommand(
      ffmpegCommand,
      [
        "-nostdin",
        "-v",
        "error",
        "-user_agent",
        mediaUserAgent,
        "-rw_timeout",
        "30000000",
        "-i",
        targetUrl,
        "-map",
        `0:s:${subtitleTrackIndex}`,
        "-vn",
        "-an",
        "-f",
        "webvtt",
        "pipe:1",
      ],
      extractTimeoutMs,
      abortController.signal,
    );
    response.off("close", abortCommand);
    console.info(`[subtitles] extracted ${result.stdout.length} byte(s) of WebVTT`);
    writeText(response, 200, normalizeWebVtt(result.stdout), "text/vtt; charset=utf-8");
  } catch (error) {
    if (isCommandCancelled(error) || response.destroyed) {
      console.info(`[subtitles] extraction cancelled for track ${subtitleTrackIndex}`);
      return;
    }

    console.warn(`[subtitles] extraction failed: ${errorMessage(error)}`);
    writeText(response, commandStatus(error), errorMessage(error));
  }
}

function readTargetUrl(request: IncomingMessage, response: ServerResponse) {
  const requestUrl = new URL(request.url ?? "", "http://localhost");
  const targetUrl = requestUrl.searchParams.get("url");
  if (!targetUrl) {
    writeText(response, 400, "Missing url");
    return "";
  }

  try {
    const parsed = new URL(targetUrl);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
      writeText(response, 400, "url must use http or https");
      return "";
    }
  } catch {
    writeText(response, 400, "Invalid url");
    return "";
  }

  return targetUrl;
}

function redactMediaUrl(value: string) {
  try {
    const parsed = new URL(value);
    if (parsed.username) {
      parsed.username = "***";
    }
    if (parsed.password) {
      parsed.password = "***";
    }

    ["username", "password", "user", "pass", "token"].forEach((key) => {
      if (parsed.searchParams.has(key)) {
        parsed.searchParams.set(key, "***");
      }
    });

    const pathSegments = parsed.pathname.split("/");
    const streamTypeIndex = pathSegments.findIndex((segment) => (
      segment === "live" || segment === "movie" || segment === "series"
    ));
    if (streamTypeIndex >= 0) {
      if (pathSegments[streamTypeIndex + 1]) {
        pathSegments[streamTypeIndex + 1] = "***";
      }
      if (pathSegments[streamTypeIndex + 2]) {
        pathSegments[streamTypeIndex + 2] = "***";
      }
      parsed.pathname = pathSegments.join("/");
    }

    return parsed.toString();
  } catch {
    return "(invalid media url)";
  }
}

function normalizeProbeStreams(streams: ProbeStream[]): SubtitleTrackInfo[] {
  return uniquifyTrackLabels(streams
    .filter((stream) => stream.codec_type === "subtitle")
    .map((stream, subtitleTrackIndex) => ({
      subtitleTrackIndex,
      streamIndex: subtitleTrackIndex,
      containerStreamIndex: stream.index ?? subtitleTrackIndex,
      codec: stream.codec_name ?? "",
      language: stream.tags?.language ?? "",
      title: stream.tags?.title ?? "",
      label: buildTrackLabel(stream, `Subtitle ${subtitleTrackIndex + 1}`),
      isDefault: stream.disposition?.default === 1,
    })));
}

function normalizeAudioProbeStreams(streams: ProbeStream[]): AudioTrackInfo[] {
  return uniquifyTrackLabels(streams
    .filter((stream) => stream.codec_type === "audio")
    .map((stream, audioTrackIndex) => ({
      audioTrackIndex,
      streamIndex: audioTrackIndex,
      containerStreamIndex: stream.index ?? audioTrackIndex,
      codec: stream.codec_name ?? "",
      language: stream.tags?.language ?? "",
      title: stream.tags?.title ?? "",
      label: buildTrackLabel(stream, `Audio ${audioTrackIndex + 1}`),
      isDefault: stream.disposition?.default === 1,
      channels: stream.channels,
      channelLayout: stream.channel_layout,
    })));
}

function buildTrackLabel(stream: ProbeStream, fallback: string) {
  const language = normalizeLanguageLabel(stream.tags?.language ?? "");
  const title = stream.tags?.title?.trim() ?? "";
  if (language && title && title.toLowerCase() !== language.toLowerCase()) {
    return `${language} - ${title}`;
  }

  return title || language || fallback;
}

function normalizeLanguageLabel(value: string) {
  const normalized = value.trim().toLowerCase();
  const labels: Record<string, string> = {
    da: "Danish",
    dan: "Danish",
    de: "German",
    deu: "German",
    ger: "German",
    en: "English",
    eng: "English",
    es: "Spanish",
    spa: "Spanish",
    fi: "Finnish",
    fin: "Finnish",
    fr: "French",
    fra: "French",
    fre: "French",
    it: "Italian",
    ita: "Italian",
    ja: "Japanese",
    jpn: "Japanese",
    nb: "Norwegian",
    no: "Norwegian",
    nor: "Norwegian",
    pt: "Portuguese",
    por: "Portuguese",
    sv: "Swedish",
    swe: "Swedish",
  };
  return labels[normalized] ?? value.trim();
}

function uniquifyTrackLabels<T extends { label: string }>(tracks: T[]) {
  const totals = new Map<string, number>();
  tracks.forEach((track) => totals.set(track.label, (totals.get(track.label) ?? 0) + 1));
  const seen = new Map<string, number>();
  return tracks.map((track) => {
    if ((totals.get(track.label) ?? 0) <= 1) {
      return track;
    }

    const count = (seen.get(track.label) ?? 0) + 1;
    seen.set(track.label, count);
    return {
      ...track,
      label: `${track.label} ${count}`,
    };
  });
}

function normalizeWebVtt(content: string) {
  const normalized = content.replace(/^\uFEFF/, "").replace(/\r\n/g, "\n").replace(/\r/g, "\n").trimStart();
  return /^WEBVTT/i.test(normalized) ? normalized : `WEBVTT\n\n${normalized}`;
}

function runBufferedCommand(command: string, args: string[], timeoutMs: number, signal?: AbortSignal) {
  return new Promise<{ stdout: string; stderr: string }>((resolve, reject) => {
    const child = spawn(command, args, {
      shell: false,
      windowsHide: true,
    });
    const stdoutChunks: Buffer[] = [];
    const stderrChunks: Buffer[] = [];
    let stdoutLength = 0;
    let stderrLength = 0;
    let didTimeout = false;
    let didCancel = false;

    const timeout = windowlessTimeout(() => {
      didTimeout = true;
      child.kill();
    }, timeoutMs);
    const abortCommand = () => {
      didCancel = true;
      child.kill();
    };

    if (signal?.aborted) {
      abortCommand();
    } else {
      signal?.addEventListener("abort", abortCommand, { once: true });
    }

    child.stdout.on("data", (chunk: Buffer) => {
      stdoutLength += chunk.length;
      if (stdoutLength > maxBufferedBytes) {
        child.kill();
        reject(new Error("Subtitle extraction output exceeded the dev prototype buffer limit"));
        return;
      }
      stdoutChunks.push(chunk);
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderrLength += chunk.length;
      if (stderrLength <= maxBufferedBytes) {
        stderrChunks.push(chunk);
      }
    });
    child.on("error", (error) => {
      windowlessClearTimeout(timeout);
      signal?.removeEventListener("abort", abortCommand);
      reject(error);
    });
    child.on("close", (code) => {
      windowlessClearTimeout(timeout);
      signal?.removeEventListener("abort", abortCommand);
      const stdout = Buffer.concat(stdoutChunks).toString("utf8");
      const stderr = Buffer.concat(stderrChunks).toString("utf8");
      if (didCancel) {
        reject(new Error("Subtitle command cancelled"));
        return;
      }
      if (didTimeout) {
        reject(new Error(`${command} timed out after ${timeoutMs}ms`));
        return;
      }
      if (code !== 0) {
        reject(new Error(stderr.trim() || `${command} exited with code ${code}`));
        return;
      }
      resolve({ stdout, stderr });
    });
  });
}

function isCommandCancelled(error: unknown) {
  return error instanceof Error && error.message === "Subtitle command cancelled";
}

function commandStatus(error: unknown) {
  return (error as NodeJS.ErrnoException)?.code === "ENOENT" ? 503 : 502;
}

function errorMessage(error: unknown) {
  if ((error as NodeJS.ErrnoException)?.code === "ENOENT") {
    return "ffprobe/ffmpeg is not installed or not available on PATH";
  }

  return error instanceof Error ? error.message : "Subtitle command failed";
}

function writeJson(response: ServerResponse, status: number, body: unknown) {
  response.writeHead(status, {
    "access-control-allow-origin": "*",
    "cache-control": "no-store",
    "content-type": "application/json; charset=utf-8",
  });
  response.end(JSON.stringify(body, null, 2));
}

function writeText(response: ServerResponse, status: number, body: string, contentType = "text/plain; charset=utf-8") {
  response.writeHead(status, {
    "access-control-allow-origin": "*",
    "cache-control": "no-store",
    "content-type": contentType,
  });
  response.end(body);
}

function windowlessTimeout(callback: () => void, ms: number) {
  return setTimeout(callback, ms);
}

function windowlessClearTimeout(timeout: NodeJS.Timeout) {
  clearTimeout(timeout);
}

function optionalPackageBinary(packageName: string) {
  try {
    const packageExport = requireOptional(packageName) as string | { path?: string } | null;
    if (typeof packageExport === "string") {
      return packageExport;
    }

    return packageExport?.path;
  } catch {
    return undefined;
  }
}
