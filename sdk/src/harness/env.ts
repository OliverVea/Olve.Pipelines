// The environment contract between the service and the harness. Documented in README.md.
import { join } from "node:path";

export const ENV = {
  /** Root of the harness IO directory (default /olve). */
  dir: "OLVE_DIR",
  pipeline: "OLVE_PIPELINE",
  runId: "OLVE_RUN_ID",
  run: "OLVE_RUN",
  attempt: "OLVE_ATTEMPT",
  versionStamp: "OLVE_VERSION_STAMP",
  /** Pre-signed PUT for result.tar.gz (run / map-trigger). Optional. */
  resultUrl: "OLVE_RESULT_URL",
  /** JSON object: file key (`<run>/<step>/<attempt>/<path>`) → pre-signed GET URL. Optional. */
  fileUrls: "OLVE_FILE_URLS",
  /** Base URL for file downloads: GET `<base>/<run>/<step>/<attempt>/<object path>`. Optional. */
  filesBaseUrl: "OLVE_FILES_BASE_URL",
  /** Bearer token sent with OLVE_FILES_BASE_URL requests. Optional. */
  filesToken: "OLVE_FILES_TOKEN",
  /** Local directory laid out as `<run>/<step>/<attempt>/files/<object path>` (dev). Optional. */
  filesDir: "OLVE_FILES_DIR",
} as const;

/** Env vars never passed to child processes (they carry credentials for the harness only). */
export const PRIVATE_ENV: readonly string[] = [ENV.resultUrl, ENV.fileUrls, ENV.filesToken];

export const olveDir = () => process.env[ENV.dir] || "/olve";
export const paths = () => {
  const root = olveDir();
  return {
    root,
    input: join(root, "input.json"),
    work: join(root, "work"),
    output: join(root, "work", "output.json"),
    files: join(root, "work", "files"),
    tmp: join(root, "tmp"),
    fileUrls: join(root, "files.json"),
  };
};

function int(name: string, fallback: number): number {
  const v = process.env[name];
  if (v === undefined || v === "") return fallback;
  const n = Number(v);
  if (!Number.isInteger(n) || n < 0) throw new Error(`${name} must be a non-negative integer, got '${v}'`);
  return n;
}

export function runInfoFromEnv(step: string) {
  return {
    pipeline: process.env[ENV.pipeline] ?? "local",
    id: process.env[ENV.runId] ?? "local",
    number: int(ENV.run, 0),
    attempt: Math.max(1, int(ENV.attempt, 1)),
    step,
    version: process.env[ENV.versionStamp] ?? "0.0.0-local",
  };
}
