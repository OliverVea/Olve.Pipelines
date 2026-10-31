import { cpSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import * as M from '../models';
import { layerTar } from '../layers';

export const buildFrontend = implement(
  step({
    name: 'build-frontend',
    description:
      'Lints the frontend, checks its formatting and builds it as an image layer.',
    image: 'node:22-slim',
    input: { source: Source, spec: M.SpecBuild },
    output: M.FrontendBuild,
    timeoutSeconds: 900,
  }),
  async ({ source, spec }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const env = {
      npm_config_cache: join(ctx.tmpDir, 'npm-cache'),
      npm_config_update_notifier: 'false',
    };
    const frontend = join(tree, 'src/frontend');
    await ctx.exec(['npm', 'install', '--no-audit', '--no-fund'], { cwd: frontend, env });
    await ctx.exec(['npm', 'run', 'lint'], { cwd: frontend, env });
    await ctx.exec(['npm', 'run', 'format:check'], { cwd: frontend, env });
    await ctx.exec(['npm', 'run', 'build'], { cwd: frontend, env });

    const root = join(ctx.tmpDir, 'frontend');
    mkdirSync(join(root, 'app'), { recursive: true });
    cpSync(join(frontend, 'dist'), join(root, 'app/wwwroot'), { recursive: true });
    const layer = await ctx.output.file(await layerTar(ctx, root, 'frontend.tar.gz'), {
      name: 'frontend.tar.gz',
    });
    ctx.log('frontend lint, formatting and build OK');
    return { layer };
  },
);
