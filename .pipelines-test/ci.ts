import { checks, implement, step, z } from "@olivervea/pipelines";

const deployPreview = implement(
  step({
    name: "deploy-preview",
    description: "Pretends to deploy a preview and links its hosts.",
    image: "alpine:3.22",
    input: { pull: z.number() },
    reuse: false,
  }),
  ({ pull }, ctx) => {
    ctx.link(`https://pr-${pull}.fixture-preview.invalid`, "web");
    ctx.link(`https://api.pr-${pull}.fixture-preview.invalid`, "api");
  },
);

const removePreview = implement(
  step({
    name: "remove-preview",
    description: "Pretends to remove the preview.",
    image: "alpine:3.22",
    input: { pull: z.number() },
  }),
  ({ pull }, ctx) => ctx.log(`removed the preview of #${pull}`),
);

export const ci = checks({ description: "A fake preview per pull request." })
  .when({ kind: "pull" }, c => c.stage([deployPreview, from => ({ pull: from.input.pull })]))
  .onClose([removePreview, from => ({ pull: from.input.pull })]);
