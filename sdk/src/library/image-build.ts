// steps.imageBuild — build a container image from a `Source` with kaniko: to an image tarball that
// travels as a file ref (a deploy imports it on the host), or pushed to a registry (`push: true`).
// Ported from v1's olve-lib.sh (olve_kaniko_build + build.sh's layer cache); footguns kept below.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync } from "node:fs";
import { join } from "node:path";
import type { Ctx, RunInfo } from "../ctx/types.ts";
import type { FileRef } from "../core/models.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import { sha256File } from "../util/hash.ts";
import { dirEntries, writeTar } from "../util/tar.ts";
import { BuiltImage, PushedImage, Source } from "./models.ts";

/** kaniko's debug image (it has a busybox shell for debugging; the executor itself needs none). */
export const KANIKO_IMAGE = "gcr.io/kaniko-project/executor:debug";

/**
 * Footgun: the build context must live under /kaniko, NOT /workspace (or another plain directory in
 * the container): kaniko wipes the filesystem root between the stages of a multi-stage build, which
 * breaks a later stage's COPY from the context. /kaniko survives stage transitions.
 */
export const KANIKO_CONTEXT = "/kaniko/build-context";

const input = { source: Source };
export type ImageBuildInput = typeof input;

export interface ImageBuildDef<N extends string> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /**
   * Image name, e.g. `olve-pipelines-v2`; with `push`, the registry repository to push to
   * (`registry.ovea.pro/app/api`). The tag is the run's version (see `imageTag`) unless `contentTag`.
   */
  repository: string;
  /** Build context: a directory of the source tree (default: the root). */
  context?: string;
  /** Dockerfile path, relative to the context (default `Dockerfile`). */
  dockerfile?: string;
  /**
   * Registry repository for kaniko's layer cache (`--cache=true --cache-repo=…`), so unchanged
   * layers (restore, apt, npm, …) are reused. kaniko pushes cache layers even with `--no-push`.
   * A registry without GC grows with every changed layer — prune it when needed.
   */
  cacheRepo?: string;
  /**
   * kaniko `--registry-map` (registry → mirror prefix); kaniko falls back to the original registry if
   * the mirror lacks the image. Defaults to the homelab pull-through caches (`DEFAULT_REGISTRY_MAP`);
   * a map replaces the default entirely, `{}` turns mapping off.
   */
  registryMap?: Record<string, string>;
  timeoutSeconds?: number;
  /**
   * Kill kaniko when it writes no output for this long (default 300 s; 0 disables), failing the
   * attempt so `retries` can start a fresh one. Catches base-image pulls that stall on an idle
   * connection (seen with mcr.microsoft.com), which kaniko itself never times out. Keep it well above
   * the longest silent build phase (an AOT "Generating native code" runs ~2 min with no output).
   */
  stallTimeoutSeconds?: number;
  /**
   * Automatic retries of a failed attempt (a fresh pod: kaniko unpacks the base image over the
   * container root and does not clean up when killed, so retrying inside the same pod is unsafe).
   * A `--no-push` build is idempotent, so the default is 2 (three tries).
   */
  retries?: number;
}

/** Tarball mode (default): output `BuiltImage` with the image tar as a file. */
export interface ImageBuildTarballDef<N extends string> extends ImageBuildDef<N> {
  push?: false;
  contentTag?: never;
}

/**
 * Registry mode: push to `repository` and output `PushedImage`, for images too big to travel as a
 * tarball (the step's scratch space is ~1 GiB).
 */
export interface ImageBuildPushDef<N extends string> extends ImageBuildDef<N> {
  push: true;
  /**
   * Tag the image `src-<hash>`, the hash of the build context's content, and skip the build when the
   * registry already has that tag: the image is rebuilt only when its context changes.
   */
  contentTag?: boolean;
}

/**
 * Default `registryMap`: pull-through caches for mcr.microsoft.com and Docker Hub. kaniko under gVisor
 * can stall forever pulling from mcr.microsoft.com directly.
 */
export const DEFAULT_REGISTRY_MAP: Readonly<Record<string, string>> = {
  "mcr.microsoft.com": "mcr-cache.ovea.pro",
  "index.docker.io": "hub-cache.ovea.pro",
};

/** How long a cache registry gets to answer `GET /v2/` before the build goes without it. */
export const CACHE_PROBE_TIMEOUT_MS = 5000;

/** The registry host of an image reference or mirror prefix (`registry.ovea.pro/a/b` → `registry.ovea.pro`). */
export function registryHost(ref: string): string {
  return ref.split("/")[0]!;
}

/** Whether a registry answers its API root: 200, or 401 (it wants a token). Never throws. */
export async function registryUp(http: Ctx["http"], host: string, signal?: AbortSignal, timeoutMs = CACHE_PROBE_TIMEOUT_MS): Promise<boolean> {
  try {
    const timeout = AbortSignal.timeout(timeoutMs);
    const res = await http.fetch(`https://${host}/v2/`, { signal: signal ? AbortSignal.any([signal, timeout]) : timeout });
    await res.body?.cancel().catch(() => {});
    return res.status === 200 || res.status === 401;
  } catch {
    return false;
  }
}

/**
 * Drops the caches that don't answer, so a cache outage (GC, maintenance) never blocks a build: an
 * unreachable mirror is left out of the registry map, an unreachable `cacheRepo` means no layer cache.
 */
export async function reachableCaches(
  ctx: Pick<Ctx, "http" | "log" | "signal">,
  caches: { registryMap: Record<string, string>; cacheRepo?: string },
): Promise<{ registryMap: Record<string, string>; cacheRepo?: string }> {
  const hosts = new Set([...Object.values(caches.registryMap), ...(caches.cacheRepo ? [caches.cacheRepo] : [])].map(registryHost));
  const up = new Map(await Promise.all([...hosts].map(async (h) => [h, await registryUp(ctx.http, h, ctx.signal)] as const)));
  const registryMap: Record<string, string> = {};
  for (const [from, to] of Object.entries(caches.registryMap)) {
    if (up.get(registryHost(to))) registryMap[from] = to;
    else ctx.log(`${registryHost(to)} unreachable; pulling from ${from} directly`);
  }
  if (caches.cacheRepo && !up.get(registryHost(caches.cacheRepo))) {
    ctx.log(`${registryHost(caches.cacheRepo)} unreachable; building without the layer cache`);
    return { registryMap };
  }
  return { registryMap, ...(caches.cacheRepo ? { cacheRepo: caches.cacheRepo } : {}) };
}

/** Default `retries`: a stalled or flaky build gets two fresh attempts before the run fails. */
export const DEFAULT_RETRIES = 2;

/** Default `stallTimeoutSeconds`: well above the longest silent phase of a normal build. */
export const DEFAULT_STALL_TIMEOUT_SECONDS = 300;

/**
 * The image tag for this run: the run's version stamp, plus `-attempt<n>` on reruns. A rebuilt image
 * must not reuse the tag: deploys import it with `pullPolicy=Never`, so an unchanged tag would leave
 * the old pods running.
 */
export function imageTag(run: Pick<RunInfo, "version" | "attempt">): string {
  const tag = run.attempt > 1 ? `${run.version}-attempt${run.attempt}` : run.version;
  if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(tag)) throw new Error(`'${tag}' (from the run's version stamp) is not a valid image tag`);
  return tag;
}

/** The name a container runtime gives an image after loading it (`x` → `docker.io/library/x`). */
export function qualifyRepository(repository: string): string {
  const first = repository.split("/")[0]!;
  if (!repository.includes("/")) return `docker.io/library/${repository}`;
  if (first.includes(".") || first.includes(":") || first === "localhost") return repository;
  return `docker.io/${repository}`;
}

/** A relative path inside the source tree (no `..`, not absolute). */
function checkRelative(what: string, path: string): string {
  const clean = path.replace(/^\.\/+/, "").replace(/\/+$/, "");
  if (clean.startsWith("/") || clean.split("/").includes("..")) throw new Error(`imageBuild ${what} '${path}' must be a relative path inside the source tree`);
  return clean === "." ? "" : clean;
}

/** The `src-<12 hex>` tag of a directory: the sha256 of its deterministic tar (as `ctx.output.dir` writes it). */
export function contentTagOf(dir: string, tmpDir: string): string {
  mkdirSync(tmpDir, { recursive: true });
  const work = mkdtempSync(join(tmpDir, "content-tag-"));
  try {
    const tar = join(work, "context.tar");
    writeTar(tar, dirEntries(dir));
    return `src-${sha256File(tar).slice(0, 12)}`;
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

const MANIFEST_TYPES = [
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
  "application/vnd.docker.distribution.manifest.v2+json",
].join(", ");

/** The digest of `repository:tag` in its registry (anonymous `HEAD …/manifests/<tag>`), or undefined if it has none. */
export async function registryDigest(http: Ctx["http"], repository: string, tag: string): Promise<string | undefined> {
  const [host, ...path] = repository.split("/");
  const res = await http.fetch(`https://${host}/v2/${path.join("/")}/manifests/${tag}`, { method: "HEAD", headers: { Accept: MANIFEST_TYPES } });
  await res.body?.cancel().catch(() => {});
  if (res.status === 404) return undefined;
  if (!res.ok) throw new Error(`registry lookup of ${repository}:${tag} answered HTTP ${res.status}`);
  const digest = res.headers.get("docker-content-digest") ?? "";
  return /^sha256:[0-9a-f]{64}$/.test(digest) ? digest : undefined;
}

/**
 * Builds an image with kaniko from `<context>/<dockerfile>` in the source tree (default: `Dockerfile`
 * at the root). Default: into an image tarball, output `BuiltImage { repository, tag, digest, tar }`.
 * With `push: true`: pushed to `repository`, output `PushedImage { repository, tag, digest }`.
 */
export function imageBuild<const N extends string>(def: ImageBuildTarballDef<N>): Step<N, ImageBuildInput, typeof BuiltImage, never> & LibraryStep;
export function imageBuild<const N extends string>(def: ImageBuildPushDef<N>): Step<N, ImageBuildInput, typeof PushedImage, never> & LibraryStep;
export function imageBuild<const N extends string>(def: ImageBuildTarballDef<N> | ImageBuildPushDef<N>): AnyImageBuild {
  const registryMap = def.registryMap ?? DEFAULT_REGISTRY_MAP;
  const mapped = Object.keys(registryMap).length > 0;
  const push = def.push === true;
  const contentTag = push && def.contentTag === true;
  const context = checkRelative("context", def.context ?? "");
  const dockerfile = checkRelative("dockerfile", def.dockerfile ?? "Dockerfile");
  if (push && !/[.:]|^localhost$/.test(def.repository.split("/")[0]!)) throw new Error(`imageBuild '${def.name}': push needs a repository with a registry host, got '${def.repository}'`);
  return makeStep(
    "imageBuild",
    { name: def.name, description: def.description, image: KANIKO_IMAGE, input, output: push ? PushedImage : BuiltImage, secrets: [], timeoutSeconds: def.timeoutSeconds, retries: def.retries ?? DEFAULT_RETRIES },
    {
      manifestConfig: () => ({
        repository: def.repository,
        ...(context ? { context } : {}),
        ...(dockerfile !== "Dockerfile" ? { dockerfile } : {}),
        ...(push ? { push: true } : {}),
        ...(contentTag ? { contentTag: true } : {}),
        ...(def.cacheRepo ? { cacheRepo: def.cacheRepo } : {}),
        ...(mapped ? { registryMap: { ...registryMap } } : {}),
      }),
      async run({ source }: { source: { tree: FileRef } }, ctx: Ctx) {
        const tree = await ctx.files.fetch(source.tree);
        const contextDir = context ? join(tree, context) : tree;
        if (!existsSync(contextDir) || !statSync(contextDir).isDirectory()) throw new Error(`build context '${context}' not found in the source tree`);
        if (!existsSync(join(contextDir, dockerfile))) throw new Error(`${dockerfile} not found in the build context${context ? ` '${context}'` : ""}`);

        const tag = contentTag ? contentTagOf(contextDir, ctx.tmpDir) : imageTag(ctx.run);
        if (contentTag) {
          const digest = await registryDigest(ctx.http, def.repository, tag);
          if (digest) {
            ctx.log("image already in the registry", { image: `${def.repository}:${tag}`, digest });
            return { repository: def.repository, tag, digest };
          }
        }

        const caches = await reachableCaches(ctx, { registryMap, cacheRepo: def.cacheRepo });
        await ctx.exec(["mkdir", "-p", KANIKO_CONTEXT]);
        await ctx.exec(["cp", "-r", `${tree}/.`, KANIKO_CONTEXT]);
        const kanikoContext = context ? `${KANIKO_CONTEXT}/${context}` : KANIKO_CONTEXT;

        // Outputs go to the harness's scratch dir (/olve/tmp, an emptyDir mount kaniko leaves alone;
        // kaniko images have no /tmp), then into the step's files.
        mkdirSync(ctx.tmpDir, { recursive: true });
        const out = mkdtempSync(join(ctx.tmpDir, "image-build-"));
        const tarPath = join(out, "image.tar");
        const digestPath = join(out, "digest");
        const output = push ? [] : ["--no-push", `--tar-path=${tarPath}`];
        // No --single-snapshot: it is incompatible with multi-stage Dockerfiles whose later stages need
        // the build context intact. --skip-push-permission-check: with --no-push kaniko otherwise checks
        // the cache repo at startup and exits if it is down; later cache lookup and push errors are
        // only warnings.
        await ctx.exec([
          "/kaniko/executor",
          `--context=${kanikoContext}`,
          `--dockerfile=${kanikoContext}/${dockerfile}`,
          ...output,
          `--digest-file=${digestPath}`,
          `--destination=${def.repository}:${tag}`,
          ...(caches.cacheRepo ? ["--cache=true", `--cache-repo=${caches.cacheRepo}`, "--skip-push-permission-check"] : []),
          ...Object.entries(caches.registryMap).map(([from, to]) => `--registry-map=${from}=${to}`),
        ], { idleTimeoutSeconds: def.stallTimeoutSeconds ?? DEFAULT_STALL_TIMEOUT_SECONDS });

        let digest: string;
        try {
          digest = readFileSync(digestPath, "utf8").trim();
        } catch {
          throw new Error(`kaniko did not write the image digest to ${digestPath}`);
        }
        if (!/^sha256:[0-9a-f]{64}$/.test(digest)) throw new Error(`kaniko wrote an unexpected digest: '${digest}'`);
        if (push) {
          ctx.log("image pushed", { image: `${def.repository}:${tag}`, digest });
          return { repository: def.repository, tag, digest };
        }
        const tar = await ctx.output.file(tarPath, { name: "image.tar" });
        rmSync(tarPath); // the copy in the step's files is the one that counts; don't hold the image twice in /olve
        ctx.log("image built", { image: `${def.repository}:${tag}`, digest });
        return { repository: qualifyRepository(def.repository), tag, digest, tar };
      },
    },
  ) as never;
}

type AnyImageBuild = Step<string, ImageBuildInput, typeof BuiltImage | typeof PushedImage, never> & LibraryStep;
