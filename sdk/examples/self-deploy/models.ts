import { model, z, fileRef } from "@olivervea/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
  publishCli: z.boolean().default(true),
}));

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  commit: z.object({ repo: z.string(), sha: z.string() }),
  image: fileRef(),          // a file (image tarball)
  chart: fileRef(),          // a directory (helm chart)
}));

export const TestReport = model("TestReport", z.object({ passed: z.number().int(), failed: z.number().int() }));

export const BetaDeployment = model("BetaDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));
export const ProdDeployment = model("ProdDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));

export const CliRelease = model("CliRelease", z.object({ tag: z.string(), assets: z.array(z.string()) }));
