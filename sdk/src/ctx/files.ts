// File references: producing them (`ctx.output.*`) and fetching them (`ctx.files.fetch`).
//
// Storage rule (shared with the service): a ref's *object path* is `path` for files and
// `path` minus the trailing "/" plus ".tar" for directories (a deterministic tar). The step writes
// objects to `$OLVE_DIR/work/files/<object path>`; the service stores them under
// `runs/<p>/<run>/<step>/<attempt>/files/<object path>`.
import { copyFileSync, existsSync, mkdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileRefSchema, FILE_PATH, type FileRef } from "../core/models.ts";
import { ENV } from "../harness/env.ts";
import { sha256File } from "../util/hash.ts";
import { dirEntries, extractTar, writeTar } from "../util/tar.ts";
import type { OutputFileOptions, RunInfo } from "./types.ts";

export const objectPath = (refPath: string): string => (refPath.endsWith("/") ? refPath.slice(0, -1) + ".tar" : refPath);
export const fileKey = (ref: Pick<FileRef, "run" | "step" | "attempt" | "path">): string => `${ref.run}/${ref.step}/${ref.attempt}/${ref.path}`;

/** Strips everything from `?` on (pre-signed credentials). */
export const redactUrl = (url: string): string => url.replace(/[?#].*$/, "?…");

export interface FilesDeps {
  run: RunInfo;
  workFiles: string;
  tmpDir: string;
  root: string;
  signal: AbortSignal;
  log: (message: string, fields?: Record<string, unknown>) => void;
}

function checkName(name: string, what: string): void {
  if (!FILE_PATH.test(name) || name.endsWith("/")) throw new Error(`${what}: '${name}' is not a valid relative file name (no leading '/', no '..', no trailing '/')`);
}

export function createOutput(deps: FilesDeps) {
  const used = new Set<string>();
  const claim = (obj: string) => {
    if (used.has(obj) || existsSync(join(deps.workFiles, obj))) throw new Error(`output file '${obj}' already exists`);
    used.add(obj);
    const dest = join(deps.workFiles, obj);
    mkdirSync(dirname(dest), { recursive: true });
    return dest;
  };
  const ref = (path: string, dest: string): FileRef =>
    ({ run: deps.run.number, step: deps.run.step, attempt: deps.run.attempt, path, sha256: sha256File(dest) }) as FileRef;

  return {
    async file(path: string, opts: OutputFileOptions = {}): Promise<FileRef> {
      const src = resolve(path);
      if (!statSync(src).isFile()) throw new Error(`ctx.output.file: '${path}' is not a file`);
      const name = opts.name ?? basename(src);
      checkName(name, "ctx.output.file");
      const dest = claim(name);
      copyFileSync(src, dest);
      return ref(name, dest);
    },
    async dir(path: string, opts: OutputFileOptions = {}): Promise<FileRef> {
      const src = resolve(path);
      if (!statSync(src).isDirectory()) throw new Error(`ctx.output.dir: '${path}' is not a directory`);
      const name = opts.name ?? basename(src);
      checkName(name, "ctx.output.dir");
      const dest = claim(objectPath(name + "/"));
      writeTar(dest, dirEntries(src));
      return ref(name + "/", dest);
    },
    async write(name: string, content: string | Uint8Array): Promise<FileRef> {
      checkName(name, "ctx.output.write");
      const dest = claim(name);
      writeFileSync(dest, content);
      return ref(name, dest);
    },
  };
}

function urlMap(root: string): Record<string, string> {
  const raw = process.env[ENV.fileUrls] ?? (existsSync(join(root, "files.json")) ? readFileSync(join(root, "files.json"), "utf8") : undefined);
  if (!raw) return {};
  try {
    const m = JSON.parse(raw) as unknown;
    if (typeof m !== "object" || m === null || Array.isArray(m)) throw new Error("not an object");
    return m as Record<string, string>;
  } catch (e) {
    throw new Error(`${ENV.fileUrls} / files.json is not a JSON object of file key → URL (${(e as Error).message})`);
  }
}

const TRANSIENT = (status: number) => status >= 500 || status === 408 || status === 429;

async function download(url: string, dest: string, headers: Record<string, string>, signal: AbortSignal, log: FilesDeps["log"]): Promise<void> {
  const attempts = 4;
  for (let i = 1; ; i++) {
    let status = 0;
    try {
      const res = await fetch(url, { headers, signal });
      status = res.status;
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await Bun.write(dest + ".partial", res);
      renameSync(dest + ".partial", dest);
      return;
    } catch (e) {
      rmSync(dest + ".partial", { force: true });
      const transient = status === 0 || TRANSIENT(status);
      if (signal.aborted || !transient || i >= attempts) throw new Error(`download of ${redactUrl(url)} failed: ${(e as Error).message}`);
      log("download failed, retrying", { url: redactUrl(url), attempt: i, error: (e as Error).message });
      await Bun.sleep(1000 * 2 ** (i - 1));
    }
  }
}

export function createFetch(deps: FilesDeps) {
  const cache = new Map<string, string>();
  return async (input: FileRef): Promise<string> => {
    const parsed = fileRefSchema.safeParse(input);
    if (!parsed.success) throw new Error(`ctx.files.fetch: not a valid file reference: ${JSON.stringify(input)}`);
    const ref = parsed.data;
    const key = fileKey(ref);
    const hit = cache.get(key);
    if (hit) return hit;

    const obj = objectPath(ref.path);
    const base = join(deps.tmpDir, "files", String(ref.run), ref.step, String(ref.attempt));
    const objFile = join(base, ".objects", obj);
    mkdirSync(dirname(objFile), { recursive: true });

    const own = ref.run === deps.run.number && ref.step === deps.run.step && ref.attempt === deps.run.attempt;
    const urls = urlMap(deps.root);
    const localDir = process.env[ENV.filesDir];
    const baseUrl = process.env[ENV.filesBaseUrl];
    if (own && existsSync(join(deps.workFiles, obj))) copyFileSync(join(deps.workFiles, obj), objFile);
    else if (urls[key]) await download(urls[key], objFile, {}, deps.signal, deps.log);
    else if (localDir && existsSync(join(localDir, String(ref.run), ref.step, String(ref.attempt), "files", obj)))
      copyFileSync(join(localDir, String(ref.run), ref.step, String(ref.attempt), "files", obj), objFile);
    else if (baseUrl) {
      const token = process.env[ENV.filesToken];
      const url = `${baseUrl.replace(/\/+$/, "")}/${ref.run}/${encodeURIComponent(ref.step)}/${ref.attempt}/${obj.split("/").map(encodeURIComponent).join("/")}`;
      await download(url, objFile, token ? { authorization: `Bearer ${token}` } : {}, deps.signal, deps.log);
    } else {
      throw new Error(
        `ctx.files.fetch: no source for ${key} (set ${ENV.fileUrls} / $OLVE_DIR/files.json, ${ENV.filesBaseUrl} or ${ENV.filesDir})`,
      );
    }

    const actual = sha256File(objFile);
    if (actual !== ref.sha256) {
      rmSync(objFile, { force: true });
      throw new Error(`ctx.files.fetch: ${key} has sha256 ${actual}, expected ${ref.sha256}`);
    }
    let result = objFile;
    if (ref.path.endsWith("/")) {
      result = join(base, ref.path.slice(0, -1));
      rmSync(result, { recursive: true, force: true });
      mkdirSync(result, { recursive: true });
      await extractTar(objFile, result);
    } else {
      result = join(base, ref.path);
      mkdirSync(dirname(result), { recursive: true });
      renameSync(objFile, result);
    }
    cache.set(key, result);
    return result;
  };
}
