import { implement } from '@olivervea/pipelines';
import { summary } from '../pipeline';

export default implement(summary, ({ beta, prod, cli }, ctx) => {
  ctx.log('deployed', {
    beta: beta.url,
    prod: prod?.url ?? '(skipped)',
    cli: cli?.tag ?? '(skipped)',
  });
});
