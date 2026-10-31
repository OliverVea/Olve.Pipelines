import { implement, type HandlerResult } from "@olivervea/pipelines";
import { deployBeta, deployProd } from "../fixtures";
import type { Equal, Expect } from "../type-assert";

// Plain object literals satisfy nominal outputs (the brand is optional on values).
implement(deployBeta.step, ({ artifact }) => ({ release: "r", version: artifact.version, url: "https://x" }));
// Skipping is always allowed.
implement(deployProd.step, async (_input, ctx) => ctx.skip("not today"));
// Secrets typed to exactly the declared set.
implement(deployProd.step, async ({ artifact }, ctx) => {
  const key: string = ctx.secrets.SSH_PRIVATE_KEY;
  type _ = Expect<Equal<keyof typeof ctx.secrets, "SSH_PRIVATE_KEY">>;
  return { release: key, version: artifact.version, url: "u" };
});
type _r = Expect<Equal<HandlerResult<typeof deployBeta> extends infer R ? (R extends { url: string } ? true : false) : never, boolean>>;
