import { cpSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';

const TESTS = 'src/backend/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj';

export const test = implement(
  step({
    name: 'test',
    description: 'Runs the .NET unit tests and checks the C# formatting.',
    image: 'mcr.microsoft.com/dotnet/sdk:10.0-noble',
    input: { source: Source, spec: M.SpecBuild },
    timeoutSeconds: 1800,
  }),
  async ({ source, spec }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const opts = { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } };

    await ctx.exec(
      ['dotnet', 'build', TESTS, '-c', 'Release', '-m:1', '-p:SkipSpecGen=true'],
      opts,
    );

    const unitTests = ctx.exec(
      ['dotnet', 'test', '--project', TESTS, '-c', 'Release', '--no-build'],
      opts,
    );
    const format = ctx
      .exec(
        [
          'dotnet',
          'format',
          'Olve.Pipelines.slnx',
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

    const results = await Promise.allSettled([unitTests, format]);
    const failed = results.flatMap((r) => (r.status === 'rejected' ? [r.reason] : []));
    if (failed.length > 0)
      throw failed.length === 1
        ? failed[0]
        : new AggregateError(failed, failed.map(String).join('; '));
    ctx.log('tests passed; formatting is current');
  },
);
