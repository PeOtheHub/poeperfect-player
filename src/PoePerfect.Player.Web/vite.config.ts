import react from "@vitejs/plugin-react";
import type { IncomingMessage, ServerResponse } from "node:http";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    react(),
    {
      name: "poeperfect-dev-proxy",
      configureServer(server) {
        server.middlewares.use("/api/proxy", async (request, response) => {
          await proxyRemoteRequest(request, response);
        });
      },
    },
  ],
});

async function proxyRemoteRequest(request: IncomingMessage, response: ServerResponse) {
  try {
    const requestUrl = new URL(request.url ?? "", "http://localhost");
    const targetUrl = requestUrl.searchParams.get("url");
    if (!targetUrl) {
      response.writeHead(400);
      response.end("Missing url");
      return;
    }

    const upstream = await fetch(targetUrl, {
      headers: {
        "user-agent": "PoePerfectPlayerWeb/0.1",
      },
    });
    const body = Buffer.from(await upstream.arrayBuffer());
    response.writeHead(upstream.status, {
      "access-control-allow-origin": "*",
      "cache-control": "no-store",
      "content-type": upstream.headers.get("content-type") ?? "application/octet-stream",
    });
    response.end(body);
  } catch (error) {
    response.writeHead(502, {
      "access-control-allow-origin": "*",
      "content-type": "text/plain; charset=utf-8",
    });
    response.end(error instanceof Error ? error.message : "Proxy request failed");
  }
}
