import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { randomUUID } from "node:crypto";
import { createReadStream, existsSync, mkdirSync, rmSync } from "node:fs";
import { access } from "node:fs/promises";
import type { IncomingMessage, ServerResponse } from "node:http";
import { createRequire } from "node:module";
import { basename, join } from "node:path";
import { tmpdir } from "node:os";

type GatewaySession = {
  id: string;
  dir: string;
  process: ChildProcessWithoutNullStreams;
  sourceUrl: string;
  startedAt: number;
  stderr: string[];
  ended: boolean;
  startAtSeconds: number;
};

const requireOptional = createRequire(import.meta.url);
const ffmpegCommand = optionalPackageBinary("ffmpeg-static") ?? "ffmpeg";
const mediaUserAgent = "PoePerfectPlayerWeb/0.1";
const gatewayRoot = join(tmpdir(), "poeperfect-web-gateway");
const sessions = new Map<string, GatewaySession>();

export function configureGatewayApi(use: (route: string, handler: (request: IncomingMessage, response: ServerResponse) => void) => void) {
  mkdirSync(gatewayRoot, { recursive: true });
  use("/api/gateway/start", (request, response) => {
    void handleStartRequest(request, response);
  });
  use("/api/gateway/stop", (request, response) => {
    void handleStopRequest(request, response);
  });
  use("/api/gateway/media", (request, response) => {
    void handleMediaRequest(request, response);
  });
}

async function handleStartRequest(request: IncomingMessage, response: ServerResponse) {
  try {
    const body = await readJsonBody(request);
    const targetUrl = String(body.url ?? "");
    const subtitleLanguage = String(body.subtitleLanguage ?? "swe");
    const startAtSeconds = Math.max(0, Number(body.startAtSeconds ?? 0) || 0);
    if (!isAllowedHttpUrl(targetUrl)) {
      writeJson(response, 400, { error: "url must use http or https" });
      return;
    }

    const session = startGatewaySession(targetUrl, subtitleLanguage, startAtSeconds);
    writeJson(response, 200, {
      sessionId: session.id,
      playlistUrl: `/api/gateway/media/${session.id}/master.m3u8`,
      mediaPlaylistUrl: `/api/gateway/media/${session.id}/index.m3u8`,
      startAtSeconds: session.startAtSeconds,
    });
  } catch (error) {
    writeJson(response, 500, {
      error: error instanceof Error ? error.message : "Could not start gateway",
    });
  }
}

async function handleStopRequest(request: IncomingMessage, response: ServerResponse) {
  const requestUrl = new URL(request.url ?? "", "http://localhost");
  const sessionId = requestUrl.searchParams.get("id") ?? "";
  const didStop = stopGatewaySession(sessionId);
  writeJson(response, 200, { stopped: didStop });
}

async function handleMediaRequest(request: IncomingMessage, response: ServerResponse) {
  const parts = (request.url ?? "").split("?")[0].split("/").filter(Boolean);
  const isFullPath = parts[0] === "api";
  const sessionId = isFullPath ? parts[3] ?? "" : parts[0] ?? "";
  const fileName = basename((isFullPath ? parts.slice(4) : parts.slice(1)).join("/") || "");
  console.info(`[gateway] media request ${sessionId || "(no-session)"}/${fileName || "(no-file)"}`);
  const session = sessions.get(sessionId);
  if (!session || !fileName) {
    console.warn(`[gateway] media session not found for ${sessionId}/${fileName}`);
    writeText(response, 404, "Gateway session not found");
    return;
  }

  if (fileName === "master.m3u8") {
    await waitForFile(join(session.dir, "index.m3u8"), 20_000);
    const hasSubtitlePlaylist = existsSync(join(session.dir, "index_vtt.m3u8"));
    const hasVideoPlaylist = existsSync(join(session.dir, "index.m3u8"));
    console.info(`[gateway] master ready video=${hasVideoPlaylist} subtitles=${hasSubtitlePlaylist} ended=${session.ended}`);
    if (!hasVideoPlaylist) {
      writeText(response, 503, `Gateway playlist not ready.\n${session.stderr.join("\n")}`);
      return;
    }
    writeText(response, 200, makeMasterPlaylist(hasSubtitlePlaylist), "application/vnd.apple.mpegurl");
    return;
  }

  const filePath = join(session.dir, fileName);
  if (fileName === "index.m3u8" || fileName === "index_vtt.m3u8") {
    await waitForFile(filePath, 20_000);
  }

  if (!filePath.startsWith(session.dir) || !existsSync(filePath)) {
    console.warn(`[gateway] file not found ${sessionId}/${fileName} ended=${session.ended}`);
    writeText(response, 404, "Gateway file not found");
    return;
  }

  response.writeHead(200, {
    "access-control-allow-origin": "*",
    "cache-control": "no-store",
    "content-type": contentTypeForFile(fileName),
  });
  createReadStream(filePath).pipe(response);
}

function startGatewaySession(sourceUrl: string, subtitleLanguage: string, startAtSeconds: number): GatewaySession {
  const id = randomUUID();
  const dir = join(gatewayRoot, id);
  mkdirSync(dir, { recursive: true });

  const languageMap = subtitleLanguage === "eng" || subtitleLanguage === "en"
    ? "0:s:m:language:eng?"
    : "0:s:m:language:swe?";
  const args = [
    "-hide_banner",
    "-nostdin",
    "-user_agent",
    mediaUserAgent,
    "-rw_timeout",
    "30000000",
    "-readrate",
    "1.15",
    "-ss",
    startAtSeconds.toFixed(3),
    "-fflags",
    "+genpts",
    "-i",
    sourceUrl,
    "-map",
    "0:v:0",
    "-map",
    "0:a:0?",
    "-map",
    languageMap,
    "-c:v",
    "libx264",
    "-preset",
    "veryfast",
    "-tune",
    "zerolatency",
    "-crf",
    "23",
    "-pix_fmt",
    "yuv420p",
    "-profile:v",
    "high",
    "-level",
    "4.1",
    "-g",
    "48",
    "-keyint_min",
    "48",
    "-sc_threshold",
    "0",
    "-force_key_frames",
    "expr:gte(t,n_forced*2)",
    "-avoid_negative_ts",
    "make_zero",
    "-c:a",
    "aac",
    "-ac",
    "2",
    "-b:a",
    "160k",
    "-c:s",
    "webvtt",
    "-f",
    "hls",
    "-hls_time",
    "2",
    "-hls_list_size",
    "0",
    "-hls_playlist_type",
    "event",
    "-hls_flags",
    "temp_file+independent_segments",
    "-hls_segment_filename",
    join(dir, "segment_%05d.ts"),
    join(dir, "index.m3u8"),
  ];

  console.info(`[gateway] starting ${id} from ${redactMediaUrl(sourceUrl)} (${subtitleLanguage}, ${startAtSeconds.toFixed(1)}s)`);
  const process = spawn(ffmpegCommand, args, {
    shell: false,
    windowsHide: true,
  });
  const session: GatewaySession = {
    id,
    dir,
    process,
    sourceUrl,
    startedAt: Date.now(),
    stderr: [],
    ended: false,
    startAtSeconds,
  };
  sessions.set(id, session);

  process.stderr.on("data", (chunk: Buffer) => {
    const message = chunk.toString("utf8").trim();
    if (message) {
      session.stderr.push(message);
      session.stderr = session.stderr.slice(-20);
    }
  });
  process.on("close", (code) => {
    console.info(`[gateway] ${id} stopped with code ${code}`);
    if (session.stderr.length) {
      console.info(`[gateway] ${id} stderr tail:\n${session.stderr.join("\n")}`);
    }
    session.ended = true;
  });
  process.on("error", (error) => {
    console.warn(`[gateway] ${id} failed: ${error.message}`);
    session.ended = true;
  });

  return session;
}

function stopGatewaySession(sessionId: string) {
  const session = sessions.get(sessionId);
  if (!session) {
    return false;
  }

  console.info(`[gateway] stopping ${sessionId}`);
  session.process.kill();
  sessions.delete(sessionId);
  windowlessTimeout(() => {
    try {
      rmSync(session.dir, { recursive: true, force: true });
    } catch {
      // Best-effort temp cleanup.
    }
  }, 2_000);
  return true;
}

async function readJsonBody(request: IncomingMessage) {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }

  const rawBody = Buffer.concat(chunks).toString("utf8");
  return rawBody ? JSON.parse(rawBody) as Record<string, unknown> : {};
}

function makeMasterPlaylist(hasSubtitlePlaylist: boolean) {
  const lines = [
    "#EXTM3U",
    "#EXT-X-VERSION:3",
  ];

  if (hasSubtitlePlaylist) {
    lines.push(
      '#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="Svenska",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,LANGUAGE="sv",URI="index_vtt.m3u8"',
      '#EXT-X-STREAM-INF:BANDWIDTH=5000000,CODECS="avc1.640029,mp4a.40.2",SUBTITLES="subs"',
    );
  } else {
    lines.push('#EXT-X-STREAM-INF:BANDWIDTH=5000000,CODECS="avc1.640029,mp4a.40.2"');
  }

  lines.push("index.m3u8", "");
  return lines.join("\n");
}

async function waitForFile(filePath: string, timeoutMs: number) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    try {
      await access(filePath);
      return;
    } catch {
      await delay(250);
    }
  }
}

function delay(ms: number) {
  return new Promise((resolve) => windowlessTimeout(() => resolve(undefined), ms));
}

function isAllowedHttpUrl(value: string) {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:";
  } catch {
    return false;
  }
}

function redactMediaUrl(value: string) {
  try {
    const parsed = new URL(value);
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

function contentTypeForFile(fileName: string) {
  if (fileName.endsWith(".m3u8")) {
    return "application/vnd.apple.mpegurl";
  }
  if (fileName.endsWith(".ts")) {
    return "video/mp2t";
  }
  if (fileName.endsWith(".vtt")) {
    return "text/vtt; charset=utf-8";
  }
  return "application/octet-stream";
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
