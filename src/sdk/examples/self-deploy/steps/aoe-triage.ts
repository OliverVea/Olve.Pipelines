import { FailureContext, implement, step } from '@olivervea/pipelines';

export const aoeTriage = implement(
  step({
    name: 'aoe-triage',
    description: 'Opens an AI session to triage a failed deploy.',
    image: 'alpine:latest',
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
