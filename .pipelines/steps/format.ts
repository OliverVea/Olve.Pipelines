import { cpSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import { DOTNET_SDK } from '../images';
import * as M from '../models';

const SOLUTION = 'Olve.Pipelines.slnx';

export const format = implement(
  step({
    name: 'format',
    description: 'Checks the C# formatting.',
    image: DOTNET_SDK,
    input: { source: Source, spec: M.SpecBuild },
    timeoutSeconds: 900,
  }),
  async ({ source, spec }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const opts = { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } };
    await ctx.exec(['dotnet', 'restore', SOLUTION, '-p:SkipSpecGen=true'], opts);
    await ctx
      .exec(
        [
          'dotnet',
          'format',
          SOLUTION,
          '--verify-no-changes',
          '--no-restore',
          '-v',
          'quiet',
        ],
        opts,
      )
      .catch(() => {
        throw new Error(
          'C# formatting is off: run `dotnet format` (or tools/check.sh --fix) and commit',
        );
      });
    ctx.log('formatting is current');
  },
);
