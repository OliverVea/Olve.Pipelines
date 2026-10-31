import { implement, step, optional, PushedImage } from '@olivervea/pipelines';
import { PUBLISH, REGISTRY_SECRETS } from '../config';
import { CRANE_IMAGE } from '../images';
import { releaseImage, tagImage } from '../registry';

export const publishImages = implement(
  step({
    name: 'publish-images',
    description:
      "Tags this run's server and harness images as the prerelease and copies them to the release repositories (same digests).",
    image: CRANE_IMAGE,
    input: { server: PushedImage, harness: optional(PushedImage) },
    config: [PUBLISH],
    secrets: REGISTRY_SECRETS,
    timeoutSeconds: 300,
  }),
  async ({ server, harness }, ctx) => {
    const { publish } = ctx.config;
    const tag = publish.imageTags.prerelease;
    await tagImage(ctx, server, tag, publish.server);
    for (const to of publish.server.release)
      await releaseImage(ctx, server, publish.server, to, [tag]);
    if (!harness)
      return ctx.log('no harness image this run (publish.harness.push is off)');
    await tagImage(ctx, harness, tag, publish.harness);
    for (const to of publish.harness.release)
      await releaseImage(ctx, harness, publish.harness, to, [tag]);
  },
);
