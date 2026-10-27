// Canonical JSON: object keys sorted recursively (by UTF-16 code units), arrays kept in order
// (order is meaningful: stages, steps, enum values), `undefined` members dropped. Stable for hashing.
export function canonicalize(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(canonicalize);
  if (v !== null && typeof v === 'object') {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(v).sort()) {
      const x = (v as Record<string, unknown>)[k];
      if (x !== undefined) out[k] = canonicalize(x);
    }
    return out;
  }
  if (typeof v === 'number' && !Number.isFinite(v))
    throw new Error(`non-finite number in canonical JSON: ${v}`);
  if (typeof v === 'bigint' || typeof v === 'function' || typeof v === 'symbol')
    throw new Error(`${typeof v} is not JSON`);
  return v;
}

/** Canonical JSON text, 2-space indented, with a trailing newline. */
export const canonicalJson = (v: unknown): string =>
  JSON.stringify(canonicalize(v), null, 2) + '\n';
