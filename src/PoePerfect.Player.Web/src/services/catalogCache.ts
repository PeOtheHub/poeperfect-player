import type { Channel, LoadResult, XtreamConnection } from "../domain";

const databaseName = "poeperfect-player";
const storeName = "catalog-cache";
const cacheKey = "latest";

type CachedCatalog = {
  id: string;
  source: string;
  sourceKind: LoadResult["sourceKind"];
  channels: Channel[];
  connection?: XtreamConnection;
  savedAt: number;
};

export async function loadCachedCatalog(source: string): Promise<LoadResult | undefined> {
  const trimmedSource = source.trim();
  if (!trimmedSource || !("indexedDB" in window)) {
    return undefined;
  }

  try {
    const database = await openDatabase();
    const cached = await readFromStore<CachedCatalog>(database, cacheKey);
    database.close();
    if (!cached || cached.source !== trimmedSource || cached.channels.length === 0) {
      return undefined;
    }

    return {
      channels: cached.channels,
      connection: cached.connection,
      sourceKind: cached.sourceKind,
    };
  } catch {
    return undefined;
  }
}

export async function saveCachedCatalog(source: string, result: LoadResult) {
  const trimmedSource = source.trim();
  if (!trimmedSource || result.channels.length === 0 || !("indexedDB" in window)) {
    return;
  }

  try {
    const database = await openDatabase();
    await writeToStore(database, {
      id: cacheKey,
      source: trimmedSource,
      sourceKind: result.sourceKind,
      channels: result.channels,
      connection: result.connection,
      savedAt: Date.now(),
    });
    database.close();
  } catch {
    // Cache writes should never block playback or playlist loading.
  }
}

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(databaseName, 1);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(storeName)) {
        database.createObjectStore(storeName, { keyPath: "id" });
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function readFromStore<T>(database: IDBDatabase, key: string): Promise<T | undefined> {
  return new Promise((resolve, reject) => {
    const transaction = database.transaction(storeName, "readonly");
    const request = transaction.objectStore(storeName).get(key);
    request.onsuccess = () => resolve(request.result as T | undefined);
    request.onerror = () => reject(request.error);
  });
}

function writeToStore(database: IDBDatabase, value: CachedCatalog): Promise<void> {
  return new Promise((resolve, reject) => {
    const transaction = database.transaction(storeName, "readwrite");
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.objectStore(storeName).put(value);
  });
}
