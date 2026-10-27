import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { test } from '../pipeline';

const TESTS = 'test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj';
const CLIENT = 'clients/olve-pipelines-client-ts/src';

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const opts = { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } };
  const committedApi = await Bun.file(join(tree, 'api.json')).text();

  const tools = ctx.exec(['dotnet', 'tool', 'restore'], opts);
  tools.catch(() => {}); // awaited below; a failed build must not leave it unhandled
  await ctx.exec(['dotnet', 'build', TESTS, '-c', 'Release', '-m:1'], opts);

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

  // The build regenerates api.json; the committed one and the TS client generated from it must match.
  const client = (async () => {
    if ((await Bun.file(join(tree, 'api.json')).text()) !== committedApi)
      throw new Error(
        'api.json is out of date with the API: build and commit it, then regenerate the TS client',
      );
    await tools;
    const generated = join(ctx.tmpDir, 'client');
    await ctx.exec(
      [
        'dotnet',
        'kiota',
        'generate',
        '-l',
        'typescript',
        '-d',
        'api.json',
        '-o',
        generated,
        '-n',
        'OlvePipelinesClient',
        '--clean-output',
      ],
      opts,
    );
    // kiota-lock.json's description hash follows every api.json change without the code changing.
    await ctx
      .exec([
        'diff',
        '-r',
        '-x',
        'kiota-lock.json',
        '-x',
        '.kiota.log',
        generated,
        join(tree, CLIENT),
      ])
      .catch(() => {
        throw new Error(
          `${CLIENT} is out of date with api.json: regenerate it (see CLAUDE.md) and commit`,
        );
      });
  })();

  const reference = ctx
    .exec(['dotnet', 'run', 'tools/api-reference.cs', '--', '--check'], opts)
    .catch(() => {
      throw new Error(
        'docs/setup/api-reference.md is out of date with api.json: run `dotnet run tools/api-reference.cs` and commit',
      );
    });

  const results = await Promise.allSettled([unitTests, format, client, reference]);
  const failed = results.flatMap((r) => (r.status === 'rejected' ? [r.reason] : []));
  if (failed.length > 0)
    throw failed.length === 1
      ? failed[0]
      : new AggregateError(failed, failed.map(String).join('; '));
  ctx.log(
    'tests passed; formatting, api.json, the TS client and the API reference are current',
  );
});
