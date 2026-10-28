import { implement } from '@olivervea/pipelines';
import { test } from '../pipeline';
import testSh from '../scripts/test.sh' with { type: 'file' };

export default implement(test, async (_input, ctx) =>
  JSON.parse(await ctx.sh.text({ script: testSh })),
);
