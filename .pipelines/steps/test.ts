import { cpSync, existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import { DOTNET_SDK } from '../images';
import * as M from '../models';

const TESTS = 'src/backend/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj';
// `dotnet test` hides what the test process prints, so the run leaves its one-line report here.
const RECORDED_FIXTURES = 'artifacts/recorded-fixtures.txt';

export const test = implement(
  step({
    name: 'test',
    description: 'Checks the C# formatting and runs the .NET unit tests.',
    image: DOTNET_SDK,
    input: { source: Source, spec: M.SpecBuild },
    timeoutSeconds: 1800,
  }),
  async ({ source, spec }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    // Whitespace is all the repo's .editorconfig enforces; analyzer warnings already fail the build.
    await ctx
      .exec(
        [
          'dotnet',
          'format',
          'whitespace',
          '--folder',
          '--verify-no-changes',
          '-v',
          'quiet',
        ],
        {
          cwd: tree,
        },
      )
      .catch(() => {
        throw new Error(
          'C# formatting is off: run `dotnet format` (or tools/check.sh --fix) and commit',
        );
      });
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const opts = { cwd: tree, env: { MSBUILDDISABLENODEREUSE: '1' } };

    await ctx.exec(
      ['dotnet', 'build', TESTS, '-c', 'Release', '-m:1', '-p:SkipSpecGen=true'],
      opts,
    );

    await ctx
      .exec(['dotnet', 'test', '--project', TESTS, '-c', 'Release', '--no-build'], opts)
      .finally(() => {
        const summary = join(tree, RECORDED_FIXTURES);
        if (existsSync(summary)) ctx.log(readFileSync(summary, 'utf8').trim());
      });
    ctx.log('tests passed');
  },
);
