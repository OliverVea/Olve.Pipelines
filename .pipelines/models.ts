// Data models of the Olve.Pipelines self-deploy (contract file: contracts only).
// Source and BuiltImage come from the SDK's library vocabulary.
import { model, z } from "@olve/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  /** Where to deploy. Beta only while v2 is under development; prod joins when v2 replaces v1. */
  deployTo: z.enum(["beta"]).default("beta"),
}));
