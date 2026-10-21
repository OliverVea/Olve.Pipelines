import { z } from "zod";
import { fileRef, model } from "../core/models.ts";

/** A source tree at a commit. `tree` references a directory (`path` ends with `/`). */
export const Source = model("Source", z.object({ repo: z.string(), sha: z.string(), tree: fileRef() }));

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
