import { implement, Source, step } from '@olivervea/pipelines';
import * as M from '../models';
import testSh from '../scripts/test.sh' with { type: 'file' };

export const test = implement(
  step({
    name: 'test',
    description: 'Runs the unit tests.',
    image: 'mcr.microsoft.com/dotnet/sdk:10.0-noble',
    input: { source: Source },
    output: M.TestReport,
  }),
  async (_input, ctx) => JSON.parse(await ctx.sh.text({ script: testSh })),
);
