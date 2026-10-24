import { implement } from "@olivervea/pipelines";
import { publishImages } from "../pipeline";
import { tagImage } from "../registry";

export default implement(publishImages, async ({ server, harness }, ctx) => {
  const { publish } = ctx.config;
  const tag = publish.imageTags.prerelease;
  await tagImage(ctx, server, tag, publish.server);
  if (harness) await tagImage(ctx, harness, tag, publish.harness);
  else ctx.log("no harness image this run (publish.harness.push is off)");
});
