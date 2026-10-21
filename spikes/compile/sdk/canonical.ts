// Canonical JSON: object keys sorted recursively, arrays kept in order (order is meaningful:
// stages, steps, enum values). Stable for hashing.
export function canonicalize(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(canonicalize);
  if (v && typeof v === "object") {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(v).sort()) {
      const x = (v as any)[k];
      if (x !== undefined) out[k] = canonicalize(x);
    }
    return out;
  }
  if (typeof v === "number" && !Number.isFinite(v)) throw new Error(`non-finite number in manifest: ${v}`);
  return v;
}

export const canonicalJson = (v: unknown, indent = 2) => JSON.stringify(canonicalize(v), null, indent) + "\n";

export function sha256(data: string | Uint8Array): string {
  return new Bun.CryptoHasher("sha256").update(data).digest("hex");
}
