import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { configCheck, manifestsValidate } from '@olve/api';
import { resolvePipeline } from '../address';
import { call } from '../api';
import { assemble } from '../dev/assemble';
import { type DevManifest, type Json, type JsonObject } from '../dev/manifest';
import { flatten, placeStep, select, whenMismatch } from '../dev/plan';
import { cellName, pickCells } from '../dev/matrix';
import { Pool, Workspace } from '../dev/pool';
import { fetchInputFiles, seedFromRun } from '../dev/remote';
import {
  DevSession,
  status,
  type DevConsole,
  type RunOptions,
  type StepOutcome,
} from '../dev/session';
import {
  exitMeaning,
  renderEnvelope,
  renderManifest,
  renderSummary,
  type StepResult,
} from '../dev/render';
import { resolveToolchain } from '../dev/toolchain';
import { CliError, describe } from '../errors';
import { coerce } from '../input';
import type { Command, CommandContext } from '../registry';

const DEFAULT_DIR = '.pipelines';
/** `pl dev validate` when the service's reconcile checks find errors. */
export const INVALID_EXIT_CODE = 78;

type Dev = { session: DevSession; manifest: DevManifest; pool: Pool };

const devConsole = (ctx: CommandContext): DevConsole => ({
  json: ctx.json,
  out: (line) => ctx.out.line(line),
  err: (line) => ctx.stderr(line),
});

function resolveDir(ctx: CommandContext): string {
  const dir = resolve(ctx.args.optionOr('dir', DEFAULT_DIR));
  if (!existsSync(join(dir, 'package.json')))
    throw new CliError(
      `No package.json in '${dir}'. Run \`pl dev\` from the repo root, or pass --dir <pipeline directory>.`,
    );
  return dir;
}

/** Describes the pipeline; undefined (with the exit code set) when describe fails. */
async function open(ctx: CommandContext, console: DevConsole): Promise<Dev | undefined> {
  const dir = resolveDir(ctx);
  const tools = await resolveToolchain(ctx.run, dir, ctx.env);
  const workspace = new Workspace(join(dir, '.olve'));
  const session = new DevSession(
    ctx.run,
    tools,
    dir,
    workspace,
    console,
    ctx.env,
    ctx.signal,
  );
  const described = await session.describe();
  if (!described.manifest) {
    console.err(`error: describe failed (exit ${described.exitCode}):`);
    for (const line of described.problems.split('\n').filter(Boolean))
      console.err(`  ${line}`);
    ctx.exitCode = described.exitCode;
    return undefined;
  }
  return { session, manifest: described.manifest, pool: Pool.load(workspace.poolJson) };
}

export function parseSecrets(values: readonly string[]): Record<string, string> {
  const secrets: Record<string, string> = {};
  for (const value of values) {
    const eq = value.indexOf('=');
    if (eq <= 0)
      throw new CliError('--secret expects NAME=value (the value is never echoed).');
    secrets[value.slice(0, eq)] = value.slice(eq + 1);
  }
  return secrets;
}

export function parseList(values: readonly string[]): string[] | undefined {
  const list = values.flatMap((v) =>
    v
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
  );
  return list.length ? list : undefined;
}

const fieldPairs = (ctx: CommandContext) => [
  ...ctx.args.options('field'),
  ...ctx.args.options('input'),
];

type Schema = Record<string, Json>;

/** The pipeline input: the pool's previous input with `--field` pairs over it (typed by the model), then its defaults. */
export function buildPipelineInput(
  schema: JsonObject | undefined,
  base: JsonObject | undefined,
  pairs: readonly string[],
): JsonObject {
  const input: JsonObject = structuredClone(base ?? {});
  const properties = (schema?.properties ?? undefined) as
    | Record<string, Schema>
    | undefined;
  const problems: string[] = [];
  for (const pair of pairs) {
    const eq = pair.indexOf('=');
    if (eq <= 0) {
      problems.push(`--field expects key=value, got '${pair}'.`);
      continue;
    }
    const key = pair.slice(0, eq);
    if (properties && !(key in properties)) {
      problems.push(
        `Unknown pipeline input field '${key}' (known: ${Object.keys(properties).join(', ')}).`,
      );
      continue;
    }
    try {
      input[key] = coerce(key, pair.slice(eq + 1), properties?.[key]) as Json;
    } catch (error) {
      if (error instanceof CliError) problems.push(...error.problems);
      else throw error;
    }
  }
  if (problems.length) throw new CliError(problems);
  for (const [key, property] of Object.entries(properties ?? {}))
    if (
      !(key in input) &&
      property &&
      typeof property === 'object' &&
      'default' in property
    )
      input[key] = structuredClone(property.default ?? null);
  return input;
}

export const missingRequired = (schema: JsonObject | undefined, input: JsonObject) =>
  (Array.isArray(schema?.required) ? schema.required : []).filter(
    (f): f is string => typeof f === 'string' && !(f in input),
  );

async function resolveConfig(ctx: CommandContext): Promise<JsonObject | undefined> {
  const file = ctx.args.option('config');
  const from = ctx.args.option('config-from');
  if (file !== undefined && from !== undefined)
    throw new CliError('Use either --config or --config-from, not both.');
  let text: string | undefined;
  if (file !== undefined) {
    if (!existsSync(file)) throw new CliError(`Config file '${file}' not found.`);
    text = await Bun.file(file).text();
  } else if (from !== undefined) {
    const pipeline = await resolvePipeline(ctx.api, from);
    const check = await call(
      configCheck({ client: ctx.api, path: { pipelineId: pipeline.id } }),
    );
    if (check.effective == null)
      throw new CliError(
        `${pipeline.label} has no valid config (${check.problems.join('; ')}); see \`pl config check ${pipeline.label}\`.`,
      );
    text = check.effective;
  }
  if (text === undefined) return undefined;
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new CliError(
      `--config is not valid JSON (${describe(error)}); \`pl config check <pipeline> --json\` prints the effective config as JSON.`,
    );
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
    throw new CliError('--config must be a JSON object of config entries.');
  return parsed as JsonObject;
}

/** `--from <pipeline>#<run>`: seeds the pool from that run. The config is --config/--config-from, else the run's. */
async function seed(
  ctx: CommandContext,
  dev: Dev,
  console: DevConsole,
  explicitConfig: JsonObject | undefined,
): Promise<JsonObject | undefined> {
  const from = ctx.args.option('from');
  if (from !== undefined) {
    await seedFromRun(ctx.api, from, dev.pool, dev.session.workspace);
    dev.pool.save(dev.session.workspace.poolJson);
    const outputs = [...dev.pool.steps.values()].filter(
      (e) => e.status === 'succeeded',
    ).length;
    status(
      console,
      `pool seeded from ${from}: its input, ${outputs} step output(s)${dev.pool.source?.config ? ', its config' : ''}`,
    );
  }
  return explicitConfig ?? dev.pool.source?.config;
}

const fetchFiles = (ctx: CommandContext, dev: Dev, input: JsonObject) =>
  fetchInputFiles(
    ctx.api,
    dev.pool,
    input,
    dev.session.workspace,
    (line) => status(devConsole(ctx), line),
    ctx.signal,
  );

const dirOption = '  --dir <path>   Pipeline directory (default: .pipelines)';

type NamedOutcome = { name: string; outcome: StepOutcome };

async function runOne(
  ctx: CommandContext,
  dev: Dev,
  stepName: string,
  inputFile: string | undefined,
  pairs: string[],
  options: RunOptions,
): Promise<NamedOutcome[]> {
  const { session, manifest, pool } = dev;
  const placed = placeStep(manifest, stepName, options.config);
  const cells = pickCells(placed.step, ctx.args.option('cell'));
  pool.input = buildPipelineInput(manifest.inputSchema, pool.input, pairs);
  if (inputFile !== undefined) session.copyInput(inputFile);
  else {
    if (placed.step.isFailureHandler)
      throw new CliError(
        `'${placed.step.name}' is a failure handler: pass its FailureContext with --input-file.`,
      );
    const assembly = assemble(placed.step, pool, manifest);
    if (assembly.problems.length)
      throw new CliError(assembly.problems.map((p) => `${placed.step.name}: ${p}`));
    if (assembly.skippedBecause.length)
      throw new CliError(
        `${placed.step.name} ${assembly.skippedBecause.join('; ')}; it would be skipped in a run (rerun that step, or pass --input-file).`,
      );
    await fetchFiles(ctx, dev, assembly.input);
    session.writeInput(assembly.input);
  }
  const outcomes: NamedOutcome[] = [];
  if (cells) pool.steps.delete(placed.step.name);
  for (const cell of cells ?? [undefined]) {
    const outcome = await session.runStep(
      manifest,
      placed.step,
      pool,
      options,
      placed.cases,
      cell,
    );
    pool.save(session.workspace.poolJson);
    outcomes.push({
      name: cell ? cellName(placed.step.name, cell) : placed.step.name,
      outcome,
    });
  }
  return outcomes;
}

async function confirm(
  ctx: CommandContext,
  question: string,
): Promise<'yes' | 'no' | 'not-interactive'> {
  if (!ctx.stdinIsTerminal) return 'not-interactive';
  const answer = (await ctx.prompt(`${question} [y/N] `)).trim().toLowerCase();
  return answer === 'y' || answer === 'yes' ? 'yes' : 'no';
}

async function runPipeline(
  ctx: CommandContext,
  dev: Dev,
  console: DevConsole,
  options: RunOptions,
  results: StepResult[],
): Promise<number> {
  const { session, manifest, pool } = dev;
  const workspace = session.workspace;
  const only = parseList(ctx.args.options('only'));
  const plan = select(flatten(manifest, options.config), ctx.args.option('until'), only);
  // A full run starts a fresh pool; --only and --from reuse its input and the other steps' outputs.
  if (!only && ctx.args.option('from') === undefined) {
    pool.input = undefined;
    pool.steps.clear();
    pool.source = undefined;
  }
  const input = buildPipelineInput(manifest.inputSchema, pool.input, fieldPairs(ctx));
  const missing = missingRequired(
    manifest.inputOutputSchema ?? manifest.inputSchema,
    input,
  );
  if (missing.length)
    throw new CliError(
      `Pipeline input is missing required field(s): ${missing.join(', ')}. Pass ${missing.map((m) => `--field ${m}=…`).join(' ')}.`,
    );
  pool.input = input;
  pool.save(workspace.poolJson);

  const skip = (name: string, reason: string) => {
    status(console, `skipping '${name}': ${reason}`);
    pool.recordSkipped(name, reason, workspace);
    pool.save(workspace.poolJson);
    results.push({ step: name, status: 'skipped', detail: reason });
  };

  for (const planned of plan) {
    const step = planned.step;
    const when = whenMismatch(planned.conditions, pool.input);
    if (when) {
      skip(step.name, when);
      continue;
    }
    const assembly = assemble(step, pool, manifest);
    if (assembly.problems.length)
      throw new CliError(assembly.problems.map((p) => `${step.name}: ${p}`));
    if (assembly.skippedBecause.length) {
      skip(step.name, assembly.skippedBecause.join('; '));
      continue;
    }
    if (step.secrets.length && !ctx.args.flag('yes')) {
      const answer = await confirm(
        ctx,
        `'${step.name}' declares secrets (${step.secrets.join(', ')}) and may do real things. Run it?`,
      );
      if (answer === 'not-interactive')
        throw new CliError(
          `'${step.name}' declares secrets and no terminal is available to confirm; pass --yes to run it.`,
        );
      if (answer === 'no') {
        results.push({
          step: step.name,
          status: 'declined',
          detail: 'not confirmed; stopping',
        });
        throw new CliError(`Stopped before '${step.name}' (not confirmed).`);
      }
    }
    await fetchFiles(ctx, dev, assembly.input);
    session.writeInput(assembly.input);
    // Every cell runs; a failed cell stops the run after the last one.
    const cells = pickCells(step, undefined);
    if (cells) pool.steps.delete(step.name);
    let stop: number | undefined;
    for (const cell of cells ?? [undefined]) {
      const name = cell ? cellName(step.name, cell) : step.name;
      const outcome = await session.runStep(
        manifest,
        step,
        pool,
        options,
        planned.cases,
        cell,
      );
      pool.save(workspace.poolJson);
      if (!console.json) renderEnvelope(console, name, outcome);
      const entry = pool.steps.get(name)!;
      results.push({ step: name, status: entry.status, detail: entry.reason ?? null });
      if (entry.status === 'failed') {
        status(
          console,
          `'${name}' failed (exit ${outcome.exitCode}${exitMeaning(outcome.exitCode)})${cells ? '' : '; stopping'}`,
        );
        stop ??= outcome.exitCode === 0 ? 1 : outcome.exitCode;
      } else if (outcome.exitCode !== 0) stop ??= outcome.exitCode;
    }
    if (stop !== undefined) {
      if (cells) status(console, `'${step.name}' failed; stopping`);
      return stop;
    }
  }
  return 0;
}

export const devCommands: Command[] = [
  {
    noun: 'dev',
    verb: 'describe',
    usesApi: false,
    options: ['dir'],
    summary: 'Describe the local pipeline from source (manifest + checks)',
    detail: `pl dev describe [--dir <path>] [--json]

  Runs the SDK's describe on the pipeline source (Bun, no compile) and prints a summary of
  the manifest: input model, secrets, triggers, stages, \`when\` blocks, failure handlers.
  It uses the SDK the pipeline's package.json pins (run \`bun install\` in the pipeline
  directory first). Describe-time problems are printed and the command exits 78.

${dirOption}
  --json         Print the raw manifest (canonical JSON) instead`,
    async run(ctx) {
      const console = devConsole(ctx);
      const dev = await open(ctx, console);
      if (!dev) return;
      if (ctx.json) ctx.out.write(dev.manifest.text);
      else renderManifest(console.out, dev.manifest);
    },
  },
  {
    noun: 'dev',
    verb: 'validate',
    options: ['dir'],
    summary: "Describe locally, then run the service's reconcile checks on the manifest",
    detail: `pl dev validate [--dir <path>] [--json]

  Runs describe on the pipeline source (like \`pl dev describe\`) and posts the manifest to
  the service's validation endpoint, which runs the same checks reconcile does — without
  pushing or persisting anything. Errors exit 78; warnings alone exit 0. Needs a session
  (\`pl login\`) against the instance whose checks you want.

${dirOption}
  --json         Print the validation response as JSON`,
    async run(ctx) {
      const dev = await open(ctx, devConsole(ctx));
      if (!dev) return;
      const report = await call(
        manifestsValidate({ client: ctx.api, body: JSON.parse(dev.manifest.text) }),
      );
      ctx.out.emit(report, () => {
        ctx.out.line(
          report.valid
            ? `${dev.manifest.name}: valid (${report.manifestHash})`
            : `${dev.manifest.name}: invalid — ${report.errors.length} error(s)`,
        );
        const format = (i: (typeof report.errors)[number]) =>
          `[${i.code}] ${i.step ? `${i.step}: ` : ''}${i.message}${i.path ? ` (${i.path})` : ''}`;
        for (const issue of report.errors) ctx.out.line(`  error   ${format(issue)}`);
        for (const issue of report.warnings) ctx.out.line(`  warning ${format(issue)}`);
      });
      if (!report.valid) ctx.exitCode = INVALID_EXIT_CODE;
    },
  },
  {
    noun: 'dev',
    verb: 'run',
    operands: 1,
    usesApi: false,
    options: [
      'cell',
      'config',
      'config-from',
      'dir',
      'field',
      'from',
      'input',
      'input-file',
      'secret',
    ],
    flags: ['docker'],
    summary: 'Run one step locally from source (or --docker in its image)',
    detail: `pl dev run <step> [options]

  Runs one step through the harness path (validate input -> handler -> validate output),
  from source with Bun, writing to <dir>/.olve/work/. Prints the envelope; the exit code is
  the harness's (0 ok/skipped, 1 handler threw, 65 input invalid, 66 output invalid, 78
  pipeline invalid).

  input.json is built from the step's manifest mappings against the local pool
  (<dir>/.olve/pool.json): \`input.*\` refs read the pipeline input (--field values over the
  pool's previous input, then the model's defaults); step refs read earlier steps' outputs
  from previous \`pl dev run\`/\`pl dev pipeline\` runs, so running steps in order chains them.
  Output files go to <dir>/.olve/files/ for later steps' ctx.files.fetch.

  A matrix step runs every cell in turn (each with OLVE_CELL set), or the cells --cell picks;
  each cell's result is kept in the pool under its name (build-cli[os=linux,arch=x64]), and a
  later step's cells(...) input reads them in cell order.

  --from <pipeline>#<run> replaces the pool with a real run's input, the outputs of its
  steps' latest successful attempts and its config, so the step runs on that run's data.
  The files a step's input references are downloaded the first time a step needs them
  (sha256-checked). Later \`pl dev run\`s keep using that pool. Needs a session (\`pl login\`).

${dirOption}
  --from <pipeline>#<run>  Seed the pool from that run (input, outputs, config, files)
  --field k=v              Set a pipeline-input field (repeatable; typed by the input model)
  --input-file <file>      Use this file as input.json verbatim (skips assembly)
  --cell axis=value,...    A matrix step: run only the cells with these values
                           (os=linux,arch=x64, or os=linux for every linux cell)
  --config <file.json>     The config the step gets (a JSON object of config entries)
  --config-from <pipeline> Use that pipeline's effective config, as its runs get it; also picks
                           the \`select\` case of a step several cases share (default: the
                           config of the --from run, if the pool has one)
  --secret NAME=value      Secret value (repeatable). Default: the step's declared secrets
                           from the environment; other pipeline secrets are not passed on
  --docker                 Compile with olve-build and run the binary in the step's image
                           like the pod (/olve layout, bundled loader, same env)
  --json                   Print the raw envelope on stdout, one line per cell for a matrix
                           step (step output goes to stderr)`,
    async run(ctx) {
      const stepName = ctx.args.operand(0)!;
      const inputFile = ctx.args.option('input-file');
      const pairs = fieldPairs(ctx);
      if (inputFile !== undefined && pairs.length)
        throw new CliError('Use either --input-file or --field, not both.');
      if (inputFile !== undefined && ctx.args.option('from') !== undefined)
        throw new CliError('Use either --input-file or --from, not both.');
      if (inputFile !== undefined && !existsSync(inputFile))
        throw new CliError(`Input file '${inputFile}' not found.`);
      const secretOverrides = parseSecrets(ctx.args.options('secret'));
      const explicitConfig = await resolveConfig(ctx);
      const console = devConsole(ctx);
      const dev = await open(ctx, console);
      if (!dev) return;
      const config = await seed(ctx, dev, console, explicitConfig);
      const outcomes = await runOne(ctx, dev, stepName, inputFile, pairs, {
        secretOverrides,
        docker: ctx.args.flag('docker'),
        warnUndeclaredSecrets: true,
        config,
      });
      for (const { name, outcome } of outcomes) renderEnvelope(console, name, outcome);
      ctx.exitCode =
        outcomes.find((o) => o.outcome.exitCode !== 0)?.outcome.exitCode ?? 0;
    },
  },
  {
    noun: 'dev',
    verb: 'pipeline',
    usesApi: false,
    options: [
      'config',
      'config-from',
      'dir',
      'field',
      'from',
      'input',
      'only',
      'secret',
      'until',
    ],
    flags: ['yes', 'docker'],
    aliases: { y: 'yes' },
    summary: "Run the pipeline's stages locally from source",
    detail: `pl dev pipeline [options]

  Runs the stages in order from source (steps of a stage, and a matrix step's cells, run one
  after another locally), chaining outputs through the local pool and evaluating \`when\`
  blocks against the pipeline input. A step whose \`when\` does not match is skipped, and so is any later step
  that requires its output. A failed step stops the run (exit = the harness exit code).
  Before each step that declares secrets (they do real things) it asks for confirmation;
  without a terminal it refuses unless --yes. Failure handlers are not run.

${dirOption}
  --from <pipeline>#<run>   Seed the pool from that run (input, outputs, config, files), e.g.
                      with --only to run some steps locally on that run's data
  --field k=v         Pipeline-input field (repeatable); the model's defaults fill the rest
  --config <file.json>      The config every step gets; it also picks each \`select\` case
  --config-from <pipeline>  Use that pipeline's effective config, as its runs get it
                      (default: the --from run's config; without any config, every case
                      of a \`select\` runs)
  --until <step>      Stop after the stage containing <step>
  --only <a,b,...>    Run only these steps, reusing the pool's input and outputs for the rest
  --secret NAME=value Secret value (repeatable); default: from the environment
  --docker            Run every step compiled, in its image (see \`pl dev run --help\`)
  --yes, -y           Don't ask before steps that declare secrets
  --json              Print a JSON summary [{step, status, detail}] on stdout`,
    async run(ctx) {
      const secretOverrides = parseSecrets(ctx.args.options('secret'));
      const explicitConfig = await resolveConfig(ctx);
      const console = devConsole(ctx);
      const dev = await open(ctx, console);
      if (!dev) return;
      // A full run resets the pool, so it doesn't inherit an earlier --from run's config.
      const fresh =
        !ctx.args.options('only').length && ctx.args.option('from') === undefined;
      const config = fresh
        ? explicitConfig
        : await seed(ctx, dev, console, explicitConfig);
      for (const name of Object.keys(secretOverrides))
        if (!dev.manifest.secrets.some((s) => s.name === name))
          status(
            console,
            `warning: --secret ${name} ignored: no step of the pipeline declares it`,
          );
      const results: StepResult[] = [];
      try {
        ctx.exitCode = await runPipeline(
          ctx,
          dev,
          console,
          {
            secretOverrides,
            docker: ctx.args.flag('docker'),
            warnUndeclaredSecrets: false,
            config,
          },
          results,
        );
      } finally {
        renderSummary(console, results);
      }
    },
  },
];
