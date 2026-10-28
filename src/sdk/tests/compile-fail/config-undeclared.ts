import { config, implement, step, z } from "@olivervea/pipelines";
import { M } from "../fixtures";

const TARGET = config("target", "Where to deploy.", z.object({ host: z.string() }));
const AUTH = config("auth", "Authentication.", z.object({ mode: z.literal("none") }));
const deploy = step({ name: "deploy", description: "", image: "alpine", input: { req: M.DeployRequest }, config: [TARGET] });

void AUTH;
implement(deploy, (_input, ctx) => {
  // @ts-expect-error -- expect: Property 'auth' does not exist on type 'ConfigValues<ConfigEntry<"target"
  ctx.log(ctx.config.auth.mode);
});
