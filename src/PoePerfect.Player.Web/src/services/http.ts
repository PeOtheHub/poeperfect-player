export async function fetchText(sourceUrl: string, signal?: AbortSignal) {
  const response = await fetch(proxiedUrl(sourceUrl), { signal });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return response.text();
}

export async function fetchJson<T>(sourceUrl: string, signal?: AbortSignal) {
  const response = await fetch(proxiedUrl(sourceUrl), { signal });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return (await response.json()) as T;
}

export function proxiedUrl(sourceUrl: string) {
  return import.meta.env.DEV ? `/api/proxy?url=${encodeURIComponent(sourceUrl)}` : sourceUrl;
}
