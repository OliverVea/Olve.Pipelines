import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';

export const spec = implement(
  step({
    name: 'spec',
    description:
      "Compiles the API contract (src/spec/): the OpenAPI document, the server's generated C#, the TypeScript client and the API reference page, for the build steps.",
    image: 'oven/bun:1.4.3-slim',
    input: { source: Source },
    output: M.SpecBuild,
    timeoutSeconds: 600,
  }),
  async ({ source }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    const dir = join(tree, 'src/spec');
    await ctx.exec(['bun', 'install', '--frozen-lockfile'], { cwd: dir });
    await ctx.exec(['bun', 'run', 'build'], { cwd: dir });
    const artifacts = await ctx.output.dir(join(tree, 'artifacts'), {
      name: 'artifacts.tar',
    });
    ctx.log('contract compiled');
    return { artifacts };
  },
);
