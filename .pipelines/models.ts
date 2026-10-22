import { fileRef, model, z } from "@olve/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
}));

export const CliRelease = model("CliRelease", z.object({
  version: z.string(),
  linuxX64: fileRef(),
  winX64: fileRef(),
}));

export const SdkRelease = model("SdkRelease", z.object({
  name: z.string(),
  version: z.string(),
}));
