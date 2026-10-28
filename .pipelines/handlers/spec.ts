import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { spec } from '../pipeline';

export default implement(spec, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const dir = join(tree, 'spec');
  await ctx.exec(['bun', 'install', '--frozen-lockfile'], { cwd: dir });
  await ctx.exec(['bun', 'run', 'compile'], { cwd: dir });
  const artifacts = await ctx.output.dir(join(tree, 'artifacts'), {
    name: 'artifacts.tar',
  });
  ctx.log('contract compiled');
  return { artifacts };
});
