import { implement } from '@olivervea/pipelines';
import { npmDistTag } from '../npm';
import { promote } from '../pipeline';
import { githubRelease } from '../github-release';
import { releaseImage, tagImage } from '../registry';

export default implement(promote, async ({ source, server, harness, sdk, cli }, ctx) => {
  const { publish } = ctx.config;
  const tag = publish.imageTags.release;
  await tagImage(ctx, server, tag, publish.server);
  for (const to of publish.server.release)
    await releaseImage(ctx, server, publish.server, to, [tag]);
  if (harness) {
    await tagImage(ctx, harness, tag, publish.harness);
    for (const to of publish.harness.release)
      await releaseImage(ctx, harness, publish.harness, to, [tag]);
  }

  if (!publish.npm.enabled)
    ctx.log('npm publishing is turned off (publish.npm.enabled): no dist-tag moved');
  else if (!sdk) ctx.log('no SDK version was published by this run: no dist-tag moved');
  else {
    const token = process.env.NPM_TOKEN?.trim();
    if (!token) throw new Error('NPM_TOKEN is not set');
    await npmDistTag(ctx, {
      name: sdk.name,
      version: sdk.version,
      tag: publish.npm.releaseTag,
      token,
    });
  }

  const github = publish.cli.github;
  if (!github.enabled)
    return ctx.log('GitHub releases are turned off (publish.cli.github.enabled)');
  if (!cli)
    return ctx.log(
      'no pl was released by this run (publish.cli.enabled): no GitHub release',
    );
  const token = process.env.GHCR_TOKEN?.trim();
  if (!token) throw new Error('GHCR_TOKEN is not set');
  const image = publish.server.release[0]?.repository ?? server.repository;
  const lines = [
    `\`pl\` ${cli.version}: \`pl-linux-x64\` and \`pl-win-x64.exe\` below.`,
    '',
    `- Server image: \`${image}:${server.tag}\` (\`${server.digest}\`)`,
    ...(sdk ? [`- SDK: \`${sdk.name}@${sdk.version}\` on npm`] : []),
  ];
  await githubRelease(ctx, {
    apiUrl: ctx.config.source.apiUrl,
    repo: source.repo,
    token,
    tag: `v${cli.version}`,
    sha: source.sha,
    name: `Olve.Pipelines ${cli.version}`,
    body: lines.join('\n'),
    prerelease: github.prerelease,
    assets: [await ctx.files.fetch(cli.linuxX64), await ctx.files.fetch(cli.winX64)],
  });
});
