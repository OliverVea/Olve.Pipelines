import { implement, optional, step } from '@olivervea/pipelines';
import * as M from '../models';

export const summary = implement(
  step({
    name: 'summary',
    description: 'Reports what was deployed.',
    image:
      'alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf',
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
