const KEY = 'olve-favorite-pipelines';
const CHANGED = 'olve-favorites-changed';

/** Pipeline ids starred in this browser. */
export function favorites(): Set<string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? '[]');
    return new Set(
      Array.isArray(parsed) ? parsed.filter((x) => typeof x === 'string') : [],
    );
  } catch {
    return new Set();
  }
}

export function setFavorite(pipelineId: string, on: boolean): void {
  const ids = favorites();
  if (on) ids.add(pipelineId);
  else ids.delete(pipelineId);
  try {
    localStorage.setItem(KEY, JSON.stringify([...ids]));
  } catch {
    /* storage unavailable */
  }
  window.dispatchEvent(new Event(CHANGED));
}

/** Calls `listener` when favorites change, in this tab or another; returns the unsubscribe. */
export function onFavoritesChanged(listener: () => void): () => void {
  const storage = (e: StorageEvent) => {
    if (e.key === KEY) listener();
  };
  window.addEventListener(CHANGED, listener);
  window.addEventListener('storage', storage);
  return () => {
    window.removeEventListener(CHANGED, listener);
    window.removeEventListener('storage', storage);
  };
}
