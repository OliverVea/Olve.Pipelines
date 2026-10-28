import {
  attemptsList,
  runsGet,
  runStepsAnswer,
  runStepsDecline,
  versionsGet,
  type Attempt,
} from '@olve/api';
import { formatAddress, resolveStep, type ResolvedAddress } from '../address';
import { call } from '../api';
import { CliError } from '../errors';
import { time } from '../format';
import { buildInput, type Schema } from '../input';
import { table } from '../output';
import type { Command, CommandContext } from '../registry';

const attemptLine = (label: string, a: Attempt) =>
  `${formatAddress(label, a.runNumber, a.stepName, a.number)}  ${a.status.type}`;

type ManifestNode = {
  name?: string;
  steps?: ManifestNode[];
  stages?: ManifestNode[];
  cases?: Record<string, ManifestNode[]>;
  externalInputs?: Record<string, { model?: string }>;
};

function findStep(
  stages: ManifestNode[] | undefined,
  step: string,
): ManifestNode | undefined {
  for (const stage of stages ?? []) {
    for (const s of stage.steps ?? []) if (s.name === step) return s;
    const nested = findStep(stage.stages, step);
    if (nested) return nested;
    for (const branch of Object.values(stage.cases ?? {})) {
      const inCase = findStep(branch, step);
      if (inCase) return inCase;
    }
  }
  return undefined;
}

/** The external input's model schema in the run's pinned manifest, to type --field values. Best-effort. */
async function modelSchema(
  ctx: CommandContext,
  addr: ResolvedAddress & { address: { step: string } },
  name: string | undefined,
): Promise<Schema | undefined> {
  try {
    const run = await call(
      runsGet({
        client: ctx.api,
        path: { pipelineId: addr.id, number: addr.address.run },
      }),
    );
    const version = await call(
      versionsGet({
        client: ctx.api,
        path: { pipelineId: addr.id, number: run.versionNumber },
      }),
    );
    const manifest = JSON.parse(version.manifest) as {
      stages?: ManifestNode[];
      models?: Record<string, { input?: Schema; output?: Schema }>;
    };
    const inputs = findStep(manifest.stages, addr.address.step)?.externalInputs ?? {};
    const input =
      name !== undefined
        ? inputs[name]
        : Object.keys(inputs).length === 1
          ? Object.values(inputs)[0]
          : undefined;
    const model = input?.model === undefined ? undefined : manifest.models?.[input.model];
    return model?.input ?? model?.output;
  } catch {
    return undefined;
  }
}

export const attemptCommands: Command[] = [
  {
    noun: 'attempts',
    summary: "What's running: scheduled/in-progress attempts across all pipelines",
    detail: `pl attempts

  Lists every scheduled or in-progress step attempt on the instance, oldest first, by address
  (<pipeline>#<run>/<step>@<attempt>). Cancel one with \`pl step cancel <pipeline>#<run>/<step>\`.`,
    async run(ctx) {
      const attempts = await call(
        attemptsList({ client: ctx.api, query: { status: 'active' } }),
      );
      ctx.out.emit(attempts, () => {
        if (attempts.length === 0) {
          ctx.out.line('Nothing is running.');
          return;
        }
        ctx.out.lines(
          table(
            ['ATTEMPT', 'STATUS', 'CREATED', 'STARTED'],
            attempts.map((a) => [
              formatAddress(a.pipelineName, a.run, a.step, a.attempt),
              a.status,
              time(a.createdAt),
              time(a.startedAt),
            ]),
          ),
        );
      });
    },
  },
  {
    noun: 'input',
    verb: 'list',
    summary: 'Steps waiting for external input, across all pipelines',
    detail: `pl input list

  Every step that waits for an answer, oldest first, by address. Answer one with
  \`pl input answer <address>\`, or decline it with \`pl input decline <address>\`.`,
    async run(ctx) {
      const waiting = await call(
        attemptsList({ client: ctx.api, query: { status: 'waiting' } }),
      );
      ctx.out.emit(waiting, () => {
        if (waiting.length === 0) {
          ctx.out.line('Nothing waits for input.');
          return;
        }
        ctx.out.lines(
          table(
            ['STEP', 'WAITING SINCE'],
            waiting.map((a) => [
              formatAddress(a.pipelineName, a.run, a.step),
              time(a.createdAt),
            ]),
          ),
        );
      });
    },
  },
  {
    noun: 'input',
    verb: 'answer',
    operands: 1,
    options: ['name', 'field', 'value'],
    summary: "Answer a step's external input",
    detail: `pl input answer <pipeline>#<run>/<step> [--name <input>] [--value <json|@file|@->] [--field k=v ...]

  Gives the value of an external input of a step that waits for it. --value is the whole JSON
  object (inline, @file, or @- for stdin) and each --field sets one field, typed by the input's
  model. --name picks the input when the step has several.

  The step runs once every external input is answered.
    pl input answer myapp#12/approve --field approved=true --field comment="looks good"`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      if (addr.address.attempt !== undefined)
        throw new CliError(
          `Answer targets the step's waiting attempt; drop the @${addr.address.attempt} from the address.`,
        );
      const name = ctx.args.option('name');
      const fields = ctx.args.options('field');
      const schema = fields.length ? await modelSchema(ctx, addr, name) : undefined;
      const value = await buildInput(
        ctx.args.option('value'),
        fields,
        schema,
        ctx.stdin,
        '--value',
      );
      const attempt = await call(
        runStepsAnswer({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          body: { name: name ?? null, value },
        }),
      );
      ctx.out.emit(attempt, () => ctx.out.line(attemptLine(addr.label, attempt)));
    },
  },
  {
    noun: 'input',
    verb: 'decline',
    operands: 1,
    options: ['comment'],
    summary: 'Decline a step that waits for input (it fails without running)',
    detail: `pl input decline <pipeline>#<run>/<step> [--comment <text>]

  Fails the waiting step without running it; the reason names you and the comment. The run
  fails at the stage barrier, like after any failed step.`,
    async run(ctx) {
      const addr = await resolveStep(ctx.api, ctx.args.operand(0)!);
      if (addr.address.attempt !== undefined)
        throw new CliError(
          `Decline targets the step's waiting attempt; drop the @${addr.address.attempt} from the address.`,
        );
      const attempt = await call(
        runStepsDecline({
          client: ctx.api,
          path: {
            pipelineId: addr.id,
            number: addr.address.run,
            step: addr.address.step,
          },
          body: { comment: ctx.args.option('comment') ?? null },
        }),
      );
      ctx.out.emit(attempt, () => ctx.out.line(attemptLine(addr.label, attempt)));
    },
  },
];
