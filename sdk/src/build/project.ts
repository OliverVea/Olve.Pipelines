// Layout of a pipeline directory and the identity of the SDK building it.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { sha256Hex } from "../util/hash.ts";
import type { HarnessInfo } from "../util/handler-hash.ts";

export const SDK_ROOT = resolve(import.meta.dir, "../..");

export interface Project {
  dir: string;
  pipelineFile: string;
  /** Contract files (excluded from handler bundles): pipeline.ts, models.ts. */
  contractFiles: string[];
  /** step name → absolute handler file, sorted by name. */
  handlers: [string, string][];
}

export class ProjectError extends Error {
  override name = "ProjectError";
}

export function loadProject(dirArg: string): Project {
  const dir = resolve(dirArg);
  if (!existsSync(dir) || !statSync(dir).isDirectory()) throw new ProjectError(`${dirArg} is not a directory`);
  const pipelineFile = join(dir, "pipeline.ts");
  if (!existsSync(pipelineFile)) throw new ProjectError(`${join(dirArg, "pipeline.ts")} not found (it must default-export pipeline({...}))`);
  const contractFiles = ["pipeline.ts", "models.ts"].map((f) => join(dir, f)).filter((f) => existsSync(f));
  const hdir = join(dir, "handlers");
  const handlers: [string, string][] = existsSync(hdir)
    ? readdirSync(hdir)
        .filter((f) => f.endsWith(".ts") && !f.endsWith(".test.ts") && !f.endsWith(".d.ts") && statSync(join(hdir, f)).isFile())
        .sort()
        .map((f) => [f.slice(0, -3), join(hdir, f)])
    : [];
  return { dir, pipelineFile, contractFiles, handlers };
}

function listFiles(dir: string): string[] {
  const out: string[] = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...listFiles(p));
    else if (e.isFile()) out.push(p);
  }
  return out;
}

let cached: HarnessInfo | undefined;
/**
 * Harness identity: Bun version, SDK version, and a hash over the SDK's runtime sources (src/,
 * without the build and dev tools) plus the zod version the SDK resolves.
 */
export function harnessInfo(): HarnessInfo {
  if (cached) return cached;
  const pkg = JSON.parse(readFileSync(join(SDK_ROOT, "package.json"), "utf8")) as { version: string };
  const src = join(SDK_ROOT, "src");
  const files = listFiles(src)
    .map((f) => relative(src, f).split("\\").join("/"))
    .filter((f) => !f.startsWith("build/") && !f.startsWith("dev/") && !f.startsWith("testing/") && f.endsWith(".ts"))
    .sort();
  const h = new Bun.CryptoHasher("sha256");
  for (const f of files) h.update(`${f}\0${sha256Hex(readFileSync(join(src, f)))}\n`);
  let zodVersion = "unknown";
  try {
    const zodPkg = Bun.resolveSync("zod/package.json", SDK_ROOT);
    zodVersion = (JSON.parse(readFileSync(zodPkg, "utf8")) as { version: string }).version;
  } catch {
    /* keep unknown */
  }
  h.update(`zod@${zodVersion}\n`);
  cached = { bun: Bun.version, sdk: pkg.version, sdkHash: "sha256:" + h.digest("hex") };
  return cached;
}

export const relPosix = (from: string, to: string) => {
  const r = relative(from, to).split("\\").join("/");
  return r.startsWith(".") ? r : `./${r}`;
};
export { dirname };
