import { implement, npmDistTag } from "@olivervea/pipelines";
import { promote } from "../pipeline";
import { releaseImage, tagImage } from "../registry";

export default implement(promote, async ({ server, harness, sdk }, ctx) => {
  const { publish } = ctx.config;
  const tag = publish.imageTags.release;
  await tagImage(ctx, server, tag, publish.server);
  for (const to of publish.server.release) await releaseImage(ctx, server, publish.server, to, [tag]);
  if (harness) {
    await tagImage(ctx, harness, tag, publish.harness);
    for (const to of publish.harness.release) await releaseImage(ctx, harness, publish.harness, to, [tag]);
  }

  if (!publish.npm.enabled) ctx.log("npm publishing is turned off (publish.npm.enabled): no dist-tag moved");
  else if (!sdk) ctx.log("no SDK version was published by this run: no dist-tag moved");
  else {
    const token = process.env.NPM_TOKEN?.trim();
    if (!token) throw new Error("NPM_TOKEN is not set");
    await npmDistTag(ctx, { name: sdk.name, version: sdk.version, tag: publish.npm.releaseTag, token });
  }
});
