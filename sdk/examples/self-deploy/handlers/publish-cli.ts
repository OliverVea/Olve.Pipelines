import { implement } from '@olivervea/pipelines';
import { publishCli } from '../pipeline';

export default implement(publishCli, async ({ artifact, prod }, ctx) => {
  ctx.log('publishing pl', { version: artifact.version, after: prod.url });
  return { tag: `v${artifact.version}`, assets: ['pl-linux-x64', 'pl-win-x64.exe'] };
});
