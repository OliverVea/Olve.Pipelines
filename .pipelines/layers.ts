import { join } from 'node:path';
import type { Ctx } from '@olivervea/pipelines';

/** Archives `root`'s `app/` as a gzipped image layer: root-owned, sorted, mtime 0, so equal content gives an equal layer. */
export async function layerTar(
  ctx: Pick<Ctx, 'exec' | 'tmpDir'>,
  root: string,
  name: string,
): Promise<string> {
  const path = join(ctx.tmpDir, name);
  await ctx.exec([
    'tar',
    '--sort=name',
    '--owner=0',
    '--group=0',
    '--numeric-owner',
    '--mtime=@0',
    '-czf',
    path,
    '-C',
    root,
    'app',
  ]);
  return path;
}
