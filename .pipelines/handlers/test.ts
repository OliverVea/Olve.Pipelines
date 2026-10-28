import { cpSync } from 'node:fs';
import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { test } from '../pipeline';

const TESTS = 'src/backend/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj';

export default implement(test, async ({ source, spec }, ctx) => {
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
});
