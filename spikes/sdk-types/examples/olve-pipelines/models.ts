import { model, z, fileRef } from "@olve/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.Pipelines"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
  publishCli: z.boolean().default(true),
}));

export const BuildArtifact = model("BuildArtifact", z.object({
  version: z.string(),
  commit: z.object({ repo: z.string(), sha: z.string() }),
  image: fileRef(),
  chart: fileRef(),
}));

export const TestReport = model("TestReport", z.object({ passed: z.number(), failed: z.number() }));

export const BetaDeployment = model("BetaDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));
export const ProdDeployment = model("ProdDeployment", z.object({ release: z.string(), version: z.string(), url: z.string().url() }));

export const ProdApproval = model("ProdApproval", z.object({ approved: z.boolean(), by: z.string(), note: z.string().optional() }));

export const CliRelease = model("CliRelease", z.object({ tag: z.string(), assets: z.array(z.string()) }));
