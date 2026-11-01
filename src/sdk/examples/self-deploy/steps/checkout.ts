import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { implement, Source, step, z } from '@olivervea/pipelines';
import { GITHUB_TOKEN } from '../secrets';

// Fake checkout: materialises a tiny tree instead of downloading the repo tarball.
export const checkout = implement(
  step({
    name: 'checkout',
    description: 'Fetches the repository at the pushed commit.',
    image: 'alpine/git:latest',
    input: { repo: z.string(), sha: z.string() }, // plain schemas → a mapper is required
    output: Source,
    secrets: [GITHUB_TOKEN],
  }),
  async ({ repo, sha }, ctx) => {
    const dir = join(ctx.tmpDir, `tree-${sha.slice(0, 7)}`);
    mkdirSync(join(dir, 'src'), { recursive: true });
    writeFileSync(join(dir, 'README.md'), `# ${repo}@${sha}\n`);
    writeFileSync(join(dir, 'src', 'main.ts'), "console.log('hi');\n");
    ctx.log('checked out', { repo, sha });
    return { repo, sha, tree: await ctx.output.dir(dir, { name: 'tree' }) };
  },
);
