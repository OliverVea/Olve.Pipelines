import { pipeline, step, steps, secret, z } from "../sdk";
import * as M from "./models";

export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for image import + helm on the host.");

// ---- steps (contracts) ----
export const checkout = step({
  name: "checkout", image: "alpine/git:latest",
  input: { repo: z.string(), sha: z.string() }, // inline schemas, like the gitCheckout library step
  output: M.Source,
});
export const build = step({
  name: "build", image: "oven/bun:1-alpine",
  input: { source: M.Source }, output: M.BuildArtifact,
});
export const test = step({
  name: "test", image: "alpine:latest",
  input: { source: M.Source }, output: M.TestReport,
});
export const deployBeta = step({
  name: "deploy-beta", image: "alpine:latest",
  input: { artifact: M.BuildArtifact }, output: M.BetaDeployment,
  secrets: [SSH_PRIVATE_KEY], timeoutSeconds: 900,
});
export const approveProd = steps.wait({
  name: "approve-prod",
  input: { beta: M.BetaDeployment }, output: M.ReviewDecision,
  resolveBy: {
    manual: true,
    check: { every: "5m", async run({ beta }, ctx) {
      // Spike: "approved" once the beta URL's health file says so (local stand-in for an HTTP check).
      const ok = await ctx.readText("approved.txt").then((t) => t.trim() === beta.version, () => false);
      return ok ? { approved: true, note: "auto" } : ctx.notYet();
    } },
  },
  timeoutHours: 72,
});
export const deployProd = step({
  name: "deploy-prod", image: "alpine:latest",
  input: { artifact: M.BuildArtifact, decision: M.ReviewDecision }, output: M.ProdDeployment,
  secrets: [SSH_PRIVATE_KEY],
});

// ---- pipeline ----
export default pipeline({
  name: "Olve.Pipelines",
  description: "Build, deploy beta then prod.",
  input: M.DeployRequest,
})
  .onPush((e) => ({ repo: e.repository.full_name, sha: e.after,
                    deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" }))
  .stage(checkout, (r) => ({ repo: r.input.repo, sha: r.input.sha }))
  .stage([build, (r) => ({ source: r.checkout })], [test, (r) => ({ source: r.checkout })])
  .stage(deployBeta)                                   // no mapper: match inputs by model
  .when({ deployTo: "prod" }, (p) => p
    .stage(approveProd)
    .stage(deployProd));
