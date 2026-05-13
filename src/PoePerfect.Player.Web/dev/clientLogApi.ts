import type { IncomingMessage, ServerResponse } from "node:http";

export function configureClientLogApi(
  use: (route: string, handler: (request: IncomingMessage, response: ServerResponse) => void) => void,
) {
  use("/api/client-log", (request, response) => {
    void handleClientLog(request, response);
  });
}

async function handleClientLog(request: IncomingMessage, response: ServerResponse) {
  if (request.method === "OPTIONS") {
    writeEmpty(response, 204);
    return;
  }

  if (request.method !== "POST") {
    writeEmpty(response, 405);
    return;
  }

  try {
    const body = await readJsonBody(request);
    const scope = safeLogValue(body.scope, "client");
    const message = safeLogValue(body.message, "event");
    const data = body.data === undefined ? "" : ` ${JSON.stringify(body.data).slice(0, 1800)}`;
    console.info(`[client:${scope}] ${message}${data}`);
    writeEmpty(response, 204);
  } catch (error) {
    console.warn("[client-log] failed to read client log", error);
    writeEmpty(response, 400);
  }
}

async function readJsonBody(request: IncomingMessage) {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }

  const rawBody = Buffer.concat(chunks).toString("utf8");
  return rawBody ? JSON.parse(rawBody) as Record<string, unknown> : {};
}

function safeLogValue(value: unknown, fallback: string) {
  if (typeof value !== "string") {
    return fallback;
  }

  return value.replace(/[^\w:.-]/g, "_").slice(0, 80) || fallback;
}

function writeEmpty(response: ServerResponse, status: number) {
  response.writeHead(status, {
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "POST, OPTIONS",
    "access-control-allow-headers": "content-type",
    "cache-control": "no-store",
  });
  response.end();
}
