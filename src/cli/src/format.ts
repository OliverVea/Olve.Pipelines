const pad = (n: number) => String(n).padStart(2, '0');

/** Local time as `yyyy-MM-dd HH:mm:ss`, or `-`. */
export function time(iso: string | null | undefined): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** `1h05m`, `3m07s` or `42s` between two timestamps; `-` when either is missing. */
export function duration(
  start: string | null | undefined,
  end: string | null | undefined,
): string {
  if (!start || !end) return '-';
  const ms = Date.parse(end) - Date.parse(start);
  if (Number.isNaN(ms) || ms < 0) return '-';
  const seconds = Math.round(ms / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (hours >= 1) return `${hours}h${pad(minutes)}m`;
  if (minutes >= 1) return `${minutes}m${pad(seconds % 60)}s`;
  return `${seconds}s`;
}

/** The first seven characters of a commit sha; `-` for none. */
export function short(sha: string | null | undefined): string {
  return sha ? (sha.length > 7 ? sha.slice(0, 7) : sha) : '-';
}

/** Compact JSON; `null` for an absent value. */
export function json(value: unknown): string {
  return value === undefined ? 'null' : JSON.stringify(value);
}

export function indented(value: unknown): string {
  return value === undefined ? 'null' : JSON.stringify(value, null, 2);
}
