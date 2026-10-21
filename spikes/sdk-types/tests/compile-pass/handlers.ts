import { implement, type HandlerResult } from "@olve/pipelines";
import { deployBeta, deploy } from "../fixtures";
import type { Equal, Expect } from "../type-assert";

// Plain object literals satisfy nominal outputs (the brand is optional on values).
implement(deployBeta, ({ artifact }) => ({ release: "r", version: artifact.version, url: "https://x" }));
// Skipping is always allowed.
implement(deploy, async (_input, ctx) => ctx.skip("not today"));
// Secrets typed to exactly the declared set.
implement(deploy, async ({ artifact }, ctx) => {
  const key: string = ctx.secrets.SSH_PRIVATE_KEY;
  type _ = Expect<Equal<keyof typeof ctx.secrets, "SSH_PRIVATE_KEY">>;
  return { release: key, version: artifact.version, url: "u" };
});
type _r = Expect<Equal<HandlerResult<typeof deployBeta> extends infer R ? (R extends { url: string } ? true : false) : never, boolean>>;
