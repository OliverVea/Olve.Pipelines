import { implement } from "@olivervea/pipelines";
import { publishImages } from "../pipeline";
import { releaseImage, tagImage } from "../registry";

export default implement(publishImages, async ({ server, harness }, ctx) => {
  const { publish } = ctx.config;
  const tag = publish.imageTags.prerelease;
  await tagImage(ctx, server, tag, publish.server);
  for (const to of publish.server.release) await releaseImage(ctx, server, publish.server, to, [tag]);
  if (!harness) return ctx.log("no harness image this run (publish.harness.push is off)");
  await tagImage(ctx, harness, tag, publish.harness);
  for (const to of publish.harness.release) await releaseImage(ctx, harness, publish.harness, to, [tag]);
});
