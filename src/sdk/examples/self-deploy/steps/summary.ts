import { implement, optional, step } from '@olivervea/pipelines';
import * as M from '../models';

export const summary = implement(
  step({
    name: 'summary',
    description: 'Reports what was deployed.',
    image: 'alpine:latest',
    input: {
      beta: M.BetaDeployment,
      prod: optional(M.ProdDeployment),
      cli: optional(M.CliRelease),
    },
  }),
  ({ beta, prod, cli }, ctx) => {
    ctx.log('deployed', {
      beta: beta.url,
      prod: prod?.url ?? '(skipped)',
      cli: cli?.tag ?? '(skipped)',
    });
  },
);
