import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { build } from '../pipeline';
import { versionFor } from './lib/release';

export default implement(build, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const files = readdirSync(tree, { recursive: true }).length;
  const version = versionFor(ctx.run.version, source.sha);
  const image = await ctx.output.write(
    'image.tar',
    `fake image ${version} (${files} entries)\n`,
  );
  const chartDir = join(ctx.tmpDir, 'chart');
  await ctx.sh(
    `mkdir -p "${chartDir}/templates" && echo "version: ${version}" > "${chartDir}/Chart.yaml"`,
  );
  const chart = await ctx.output.dir(chartDir, { name: 'chart' });
  return { version, commit: { repo: source.repo, sha: source.sha }, image, chart };
});
