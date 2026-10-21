import { model, z, fileRef } from "../sdk";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
  publishCli: z.boolean().default(true),
}));

export const Source = model("Source", z.object({
  repo: z.string(),
  sha: z.string(),
  tree: fileRef(),
}));

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  commit: z.object({ repo: z.string(), sha: z.string() }),
  image: fileRef(),
}));

export const TestReport = model("TestReport", z.object({ passed: z.number().int(), failed: z.number().int() }));

export const BetaDeployment = model("BetaDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));
export const ProdDeployment = model("ProdDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));

export const ReviewDecision = model("ReviewDecision", z.object({ approved: z.boolean(), note: z.string().optional() }));
