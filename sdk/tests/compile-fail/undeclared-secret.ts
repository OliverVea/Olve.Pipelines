import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../fixtures";

// deploy-beta declares only SSH_PRIVATE_KEY.
implement(deployBeta, async ({ artifact }, ctx) => {
  // @ts-expect-error -- expect: Property 'GITHUB_TOKEN' does not exist on type 'DeclaredSecrets<"SSH_PRIVATE_KEY">'
  await ctx.sh("x", { env: { TOKEN: ctx.secrets.GITHUB_TOKEN } });
  return { release: "r", version: artifact.version, url: "u" };
});
