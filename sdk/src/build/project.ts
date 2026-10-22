// Layout of a pipeline directory and the identity of the SDK building it.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { isImplementation } from "../core/handlers.ts";
import { sha256Hex } from "../util/hash.ts";
import type { HarnessInfo } from "../util/handler-hash.ts";

export const SDK_ROOT = resolve(import.meta.dir, "../..");

export interface Project {
  dir: string;
  pipelineFile: string;
  /** Contract files (excluded from handler bundles): pipeline.ts, models.ts. */
  contractFiles: string[];
  /** step name → absolute handler file, sorted by step name. The step is the one the file's `implement(step, …)` names. */
  handlers: [string, string][];
}

export class ProjectError extends Error {
  override name = "ProjectError";
}

export async function loadProject(dirArg: string): Promise<Project> {
  const dir = resolve(dirArg);
  if (!existsSync(dir) || !statSync(dir).isDirectory()) throw new ProjectError(`${dirArg} is not a directory`);
  const pipelineFile = join(dir, "pipeline.ts");
  if (!existsSync(pipelineFile)) throw new ProjectError(`${join(dirArg, "pipeline.ts")} not found (it must default-export pipeline({...}))`);
  const contractFiles = ["pipeline.ts", "models.ts"].map((f) => join(dir, f)).filter((f) => existsSync(f));
  const hdir = join(dir, "handlers");
  const files = existsSync(hdir)
    ? readdirSync(hdir)
        .filter((f) => f.endsWith(".ts") && !f.endsWith(".test.ts") && !f.endsWith(".d.ts") && statSync(join(hdir, f)).isFile())
        .sort()
    : [];
  return { dir, pipelineFile, contractFiles, handlers: await resolveHandlers(hdir, files) };
}

/** Pairs each handler file with the step its default export implements (file names don't matter). */
async function resolveHandlers(hdir: string, files: string[]): Promise<[string, string][]> {
  const byStep = new Map<string, string>();
  for (const f of files) {
    const file = join(hdir, f);
    let impl: unknown;
    try {
      impl = ((await import(file)) as { default?: unknown }).default;
    } catch (e) {
      throw new ProjectError(`handlers/${f} failed to load: ${(e as Error)?.message ?? String(e)}`);
    }
    if (!isImplementation(impl))
      throw new ProjectError(`handlers/${f} must default-export implement(<step>, …); put shared code in a subfolder such as handlers/lib/`);
    const step = (impl.step as { name: string }).name;
    const other = byStep.get(step);
    if (other) throw new ProjectError(`handlers/${other.slice(hdir.length + 1)} and handlers/${f} both implement step '${step}'`);
    byStep.set(step, file);
  }
  return [...byStep].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
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
