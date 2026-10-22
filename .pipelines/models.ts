// Data models of the Olve.Pipelines self-deploy (contract file: contracts only).
// Source and BuiltImage come from the SDK's library vocabulary.
import { fileRef, model, z } from "@olve/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  /** Where to deploy. Beta only while v2 is under development; prod joins when v2 replaces v1. */
  deployTo: z.enum(["beta"]).default("beta"),
}));

/**
 * The `pl` operator CLI built from the deployed commit. The instance serves the latest successful
 * publish-cli attempt's files at GET /download/<file name> (`Distribution__CliPipeline`).
 */
export const CliRelease = model("CliRelease", z.object({
  /** The run's version stamp, also the binaries' InformationalVersion (`pl --version`). */
  version: z.string(),
  /** Native-AOT linux binary, stored as `pl-linux-x64`. */
  linuxX64: fileRef(),
  /** Self-contained single-file Windows build, stored as `pl-win-x64.exe`. */
  winX64: fileRef(),
}));
