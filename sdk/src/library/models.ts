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

/**
 * A container image pushed to a registry by a step (`imageBuild` with `push: true`), pulled from there
 * by the deploy. `repository` includes the registry host.
 */
export const PushedImage = model(
  "PushedImage",
  z.object({ repository: z.string(), tag: z.string(), digest: z.string() }),
);

/** A Helm release that was installed or upgraded. */
export const HelmRelease = model(
  "HelmRelease",
  z.object({
    release: z.string(),
    namespace: z.string(),
    image: z.string(),
    /** Where step pods reach the release (the target's `url`); absent = its in-cluster Service URL (`serviceUrl`). */
    url: z.string().optional(),
  }),
);

/** One failed step of a run, as seen by a failure handler. */
export const FailedStep = z.object({
  step: z.string(),
  attempt: z.number().int(),
  /** `<pipeline id>#<run>/<step>@<attempt>` (spec §5.5). */
  address: z.string(),
  /** The attempt's failure reason (handler error, exit code, timeout, …). */
  reason: z.string(),
  /** The attempt's log (`GET …/runs/<n>/steps/<step>/logs?attempt=<a>`). */
  logUrl: z.string(),
});

/**
 * What a failure handler receives (`.onFailure(handler, …)`). The service builds it when a run fails and
 * runs the handler once per failure, untracked and best-effort (not a run attempt). `failedSteps` lists
 * the run's failed steps the handler is scoped to (all of them for an unscoped handler); never empty.
 */
export const FailureContext = model(
  "FailureContext",
  z.object({
    pipeline: z.object({ id: z.string(), name: z.string() }),
    run: z.object({
      id: z.string(),
      number: z.number().int(),
      /** `<pipeline id>#<run>` (spec §5.5). */
      address: z.string(),
      /** The run's derived version, `yyyy.MM.dd.<run>`. */
      versionStamp: z.string(),
      trigger: z.enum(["push", "poll", "manual"]),
      /** The run (`GET …/api/pipelines/<id>/runs/<n>`). */
      url: z.string(),
    }),
    /** The pipeline version the run is pinned to, and the commit it was built from. */
    version: z.object({ number: z.number().int(), commit: z.string().nullable() }),
    /** The run's pipeline input (the pipeline input model's output view). */
    input: z.unknown(),
    /** One line summarising why the run failed. */
    reason: z.string(),
    failedSteps: z.array(FailedStep),
  }),
);
