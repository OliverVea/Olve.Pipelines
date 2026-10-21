import { implement } from "@olve/pipelines";
import { summary } from "../fixtures";

implement(summary, (_input, ctx) => {
  // @ts-expect-error -- expect: Property 'SSH_PRIVATE_KEY' does not exist on type 'DeclaredSecrets<never>'
  ctx.log(ctx.secrets.SSH_PRIVATE_KEY);
});
