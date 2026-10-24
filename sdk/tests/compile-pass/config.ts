import { config, implement, pipeline, secret, secretRef, step, z } from "@olivervea/pipelines";
import { M } from "../fixtures";
import type { Equal, Expect } from "../type-assert";

const DEX = secret("DEX_CLIENT_SECRET", "Dex client secret.");
const AUTH = config("auth", "Authentication.", z.discriminatedUnion("mode", [
  z.object({ mode: z.literal("none") }),
  z.object({ mode: z.literal("dex"), issuer: z.string(), clientSecret: secretRef(DEX) }),
]));
const TARGET = config("target", "Where to deploy.", z.object({ host: z.string(), namespace: z.string().default("olve") }));

const deploy = step({ name: "deploy", description: "", image: "alpine", input: { req: M.DeployRequest }, config: [AUTH, TARGET], secrets: [DEX] });

// ctx.config is typed to exactly the declared entries (output view: defaults applied), and unions narrow.
implement(deploy, (_input, ctx) => {
  type _keys = Expect<Equal<keyof typeof ctx.config, "auth" | "target">>;
  const ns: string = ctx.config.target.namespace;
  if (ctx.config.auth.mode === "dex") {
    const ref: { secret: "DEX_CLIENT_SECRET" } = ctx.config.auth.clientSecret;
    ctx.log(ref.secret + ctx.config.auth.issuer + ns);
  }
});

// Steps with config compose in pipelines like any other step.
export default pipeline({ name: "cfg", input: M.DeployRequest }).stage(deploy);
