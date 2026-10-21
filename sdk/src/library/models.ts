import { z } from "zod";
import { fileRef, model } from "../core/models.ts";

/** A source tree at a commit. `tree` references a directory (`path` ends with `/`). */
export const Source = model("Source", z.object({ repo: z.string(), sha: z.string(), tree: fileRef() }));

/**
 * A container image built by a step, as an image tarball (`docker save` format) in the step's files.
 * `repository` is fully qualified the way a container runtime names it after loading the tar
 * (`olve-pipelines-v2` → `docker.io/library/olve-pipelines-v2`).
 */
export const BuiltImage = model(
  "BuiltImage",
  z.object({ repository: z.string(), tag: z.string(), digest: z.string(), tar: fileRef() }),
);

/** A Helm release that was installed or upgraded. */
export const HelmRelease = model(
  "HelmRelease",
  z.object({ release: z.string(), namespace: z.string(), image: z.string() }),
);

/** What a failure handler receives (`.onFailure(handler, …)`). */
export const FailureContext = model(
  "FailureContext",
  z.object({
    pipeline: z.string(),
    run: z.number().int(),
    step: z.string(),
    attempt: z.number().int(),
    reason: z.string(),
    logUrl: z.string(),
  }),
);
