import { cpSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { implement } from '@olivervea/pipelines';
import { buildFrontend } from '../pipeline';
import { layerTar } from '../layers';

export default implement(buildFrontend, async ({ source, spec }, ctx) => {
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
});
