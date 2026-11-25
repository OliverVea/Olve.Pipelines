import { FailureContext, implement, step } from '@olivervea/pipelines';

export const aoeTriage = implement(
  step({
    name: 'aoe-triage',
    description: 'Opens an AI session to triage a failed deploy.',
    image:
      'alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf',
    input: { failure: FailureContext },
  }),
  ({ failure }, ctx) => {
    ctx.log('would open an agent session', {
      run: failure.run.address,
      steps: failure.failedSteps.map((f) => f.step),
      reason: failure.reason,
    });
  },
);
