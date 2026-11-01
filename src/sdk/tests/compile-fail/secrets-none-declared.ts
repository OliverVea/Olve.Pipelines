import { implement } from "@olivervea/pipelines";
import { summary } from "../fixtures";

implement(summary.step, (_input, ctx) => {
  // @ts-expect-error -- expect: Property 'SSH_PRIVATE_KEY' does not exist on type 'DeclaredSecrets<never>'
  ctx.log(ctx.secrets.SSH_PRIVATE_KEY);
});
