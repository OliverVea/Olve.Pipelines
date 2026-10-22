// steps.imageBuild — build a container image from a `Source` with kaniko, to an image tarball.
//
// No registry push: the tarball travels as a file ref and a deploy step imports it on the host.
// Ported from v1's olve-lib.sh (olve_kaniko_build + build.sh's layer cache); footguns kept below.
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import type { Ctx, RunInfo } from "../ctx/types.ts";
import type { FileRef } from "../core/models.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import { BuiltImage, Source } from "./models.ts";

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
  /** Image name, e.g. `olve-pipelines-v2`. The tag is the run's version (see `imageTag`). */
  repository: string;
  /**
   * Registry repository for kaniko's layer cache (`--cache=true --cache-repo=…`), so unchanged
   * layers (restore, apt, npm, …) are reused. kaniko pushes cache layers even with `--no-push`.
   * A registry without GC grows with every changed layer — prune it when needed.
   */
  cacheRepo?: string;
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

/**
 * Builds `<source>/Dockerfile` with the source root as context, using kaniko, into an image tarball.
 * Output: `BuiltImage { repository, tag, digest, tar }`.
 */
export function imageBuild<const N extends string>(def: ImageBuildDef<N>): Step<N, ImageBuildInput, typeof BuiltImage, never> & LibraryStep {
  return makeStep(
    "imageBuild",
    { name: def.name, image: KANIKO_IMAGE, input, output: BuiltImage, secrets: [], timeoutSeconds: def.timeoutSeconds, retries: def.retries ?? DEFAULT_RETRIES },
    {
      manifestConfig: () => ({ repository: def.repository, ...(def.cacheRepo ? { cacheRepo: def.cacheRepo } : {}) }),
      async run({ source }: { source: { tree: FileRef } }, ctx: Ctx) {
        const tag = imageTag(ctx.run);
        const tree = await ctx.files.fetch(source.tree);
        await ctx.exec(["mkdir", "-p", KANIKO_CONTEXT]);
        await ctx.exec(["cp", "-r", `${tree}/.`, KANIKO_CONTEXT]);

        // Outputs go to the harness's scratch dir (/olve/tmp, an emptyDir mount kaniko leaves alone;
        // kaniko images have no /tmp), then into the step's files.
        mkdirSync(ctx.tmpDir, { recursive: true });
        const out = mkdtempSync(join(ctx.tmpDir, "image-build-"));
        const tarPath = join(out, "image.tar");
        const digestPath = join(out, "digest");
        // No --single-snapshot: it is incompatible with multi-stage Dockerfiles whose later stages need
        // the build context intact.
        await ctx.exec([
          "/kaniko/executor",
          `--context=${KANIKO_CONTEXT}`,
          `--dockerfile=${KANIKO_CONTEXT}/Dockerfile`,
          "--no-push",
          `--tar-path=${tarPath}`,
          `--digest-file=${digestPath}`,
          `--destination=${def.repository}:${tag}`,
          ...(def.cacheRepo ? ["--cache=true", `--cache-repo=${def.cacheRepo}`] : []),
        ], { idleTimeoutSeconds: def.stallTimeoutSeconds ?? DEFAULT_STALL_TIMEOUT_SECONDS });

        let digest: string;
        try {
          digest = readFileSync(digestPath, "utf8").trim();
        } catch {
          throw new Error(`kaniko did not write the image digest to ${digestPath}`);
        }
        if (!/^sha256:[0-9a-f]{64}$/.test(digest)) throw new Error(`kaniko wrote an unexpected digest: '${digest}'`);
        const tar = await ctx.output.file(tarPath, { name: "image.tar" });
        rmSync(tarPath); // the copy in the step's files is the one that counts; don't hold the image twice in /olve
        ctx.log("image built", { image: `${def.repository}:${tag}`, digest });
        return { repository: qualifyRepository(def.repository), tag, digest, tar };
      },
    },
  ) as never;
}
