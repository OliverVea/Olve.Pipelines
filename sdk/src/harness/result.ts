// Writing, printing and uploading the result of `run` / `map-trigger`.
import { existsSync, mkdirSync, readdirSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { canonicalJson } from "../util/canonical.ts";
import { dirEntries, writeTarGz, type TarEntry } from "../util/tar.ts";
import { redactUrl } from "../ctx/files.ts";
import type { Envelope } from "./execute.ts";

export const RESULT_BEGIN = "---OLVE-RESULT-BEGIN---";
export const RESULT_END = "---OLVE-RESULT-END---";
export const MANIFEST_BEGIN = "---OLVE-MANIFEST-BEGIN---";
export const MANIFEST_END = "---OLVE-MANIFEST-END---";

/** Atomically writes the envelope to `<work>/output.json`. */
export function writeEnvelope(work: string, envelope: Envelope): string {
  mkdirSync(work, { recursive: true });
  const path = join(work, "output.json");
  writeFileSync(path + ".partial", canonicalJson(envelope));
  renameSync(path + ".partial", path);
  return path;
}

/** The envelope as one compact line between sentinels (fallback channel via pod logs). */
export function sentinelBlock(envelope: Envelope): string {
  return `${RESULT_BEGIN}\n${JSON.stringify(JSON.parse(canonicalJson(envelope)))}\n${RESULT_END}\n`;
}

/** Packs output.json + files/ into result.tar.gz (deterministic). */
export async function packResult(work: string, out: string): Promise<void> {
  const entries: TarEntry[] = [{ path: "output.json", type: "file", mode: 0o644, source: join(work, "output.json") }];
  const files = join(work, "files");
  if (existsSync(files)) entries.push({ path: "files", type: "dir", mode: 0o755 }, ...dirEntries(files, "files"));
  await writeTarGz(out, entries);
}

const TRANSIENT = (status: number) => status >= 500 || status === 408 || status === 429;

/** PUTs `file` to a pre-signed URL; retries network errors / 5xx / 408 / 429 with backoff, fails fast on other 4xx. */
export async function uploadFile(
  url: string,
  file: string,
  log: (line: string) => void,
  opts: { attempts?: number; baseDelayMs?: number } = {},
): Promise<void> {
  const attempts = opts.attempts ?? 5;
  const base = opts.baseDelayMs ?? 1000;
  for (let i = 1; ; i++) {
    let status = 0;
    let detail: string;
    try {
      const res = await fetch(url, { method: "PUT", body: Bun.file(file) });
      status = res.status;
      if (res.ok) return;
      detail = `HTTP ${res.status} ${res.statusText}`;
      await res.body?.cancel();
    } catch (e) {
      detail = (e as Error).message;
    }
    const transient = status === 0 || TRANSIENT(status);
    if (!transient || i >= attempts) throw new Error(`upload to ${redactUrl(url)} failed: ${detail}`);
    log(`upload to ${redactUrl(url)} failed (${detail}); retry ${i}/${attempts - 1}`);
    await Bun.sleep(base * 2 ** (i - 1));
  }
}

/**
 * Clears results of a previous run in the same $OLVE_DIR (dev loops reuse it): output.json and the
 * *contents* of files/. The directories themselves are kept (olve-init creates them 1777).
 */
export function removeStale(work: string): void {
  rmSync(join(work, "output.json"), { force: true });
  rmSync(join(work, "output.json.partial"), { force: true });
  const files = join(work, "files");
  if (existsSync(files)) for (const e of readdirSync(files)) rmSync(join(files, e), { recursive: true, force: true });
}
