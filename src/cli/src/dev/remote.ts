import { createReadStream, existsSync, mkdirSync, renameSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { runsGet, runStepsOutput } from '@olve/api';
import type { Client } from '@olve/api/client';
import { formatAddress, resolveRun } from '../address';
import { call, downloadStepFile } from '../api';
import { CliError, describe } from '../errors';
import type { Json, JsonObject } from './manifest';
import { RUN, type Pool, type Workspace } from './pool';

type FileRef = {
  run: number;
  step: string;
  attempt: number;
  path: string;
  sha256: string;
};

/** Replaces the pool with a real run's input, its steps' outcomes and its config. */
export async function seedFromRun(
  api: Client,
  text: string,
  pool: Pool,
  workspace: Workspace,
): Promise<void> {
  const { id, label, address } = await resolveRun(api, text);
  const run = await call(
    runsGet({ client: api, path: { pipelineId: id, number: address.run } }),
  );
  const where = formatAddress(label, address.run);
  const steps = run.stages.flatMap((s) => s.steps);
  if (!steps.length)
    throw new CliError(
      `${where} has no stages (its version's manifest is unavailable), so its outputs can't be used.`,
    );
  let config: JsonObject | undefined;
  if (run.config) {
    const parsed = Bun.YAML.parse(run.config) as Json;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) config = parsed;
  }

  pool.input = structuredClone(run.input as JsonObject);
  pool.steps.clear();
  pool.source = { pipeline: label, pipelineId: id, run: address.run, config };
  rmSync(workspace.poolFiles, { recursive: true, force: true });
  for (const step of steps) {
    if (step.succeededAttempt != null) {
      const output = await call(
        runStepsOutput({
          client: api,
          path: { pipelineId: id, number: address.run, step: step.name },
          query: { attempt: step.succeededAttempt },
        }),
      );
      pool.steps.set(step.name, {
        status: 'succeeded',
        output: (output.output ?? null) as Json,
      });
    } else if (step.status === 'skipped' || step.status === 'excluded')
      pool.steps.set(step.name, {
        status: 'skipped',
        reason: `${step.status} in ${where}${step.reason ? `: ${step.reason}` : ''}`,
      });
    else if (step.status === 'failed' || step.status === 'cancelled')
      pool.steps.set(step.name, {
        status: 'failed',
        reason: `${step.status} in ${where}${step.reason ? `: ${step.reason}` : ''}`,
      });
  }
}

function isFileRef(value: Json): value is FileRef {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const keys = Object.keys(value).sort().join(',');
  return (
    keys === 'attempt,path,run,sha256,step' &&
    typeof value.run === 'number' &&
    typeof value.attempt === 'number' &&
    typeof value.step === 'string' &&
    typeof value.path === 'string' &&
    typeof value.sha256 === 'string'
  );
}

function fileRefs(value: Json, found: FileRef[] = []): FileRef[] {
  if (isFileRef(value)) found.push(value);
  else if (Array.isArray(value)) for (const v of value) fileRefs(v, found);
  else if (value && typeof value === 'object')
    for (const v of Object.values(value)) fileRefs(v, found);
  return found;
}

const objectPath = (path: string) =>
  path.endsWith('/') ? path.slice(0, -1) + '.tar' : path;

/** Downloads the files of the source run that the input references and the pool lacks. */
export async function fetchInputFiles(
  api: Client,
  pool: Pool,
  input: JsonObject,
  workspace: Workspace,
  log: (line: string) => void,
  signal?: AbortSignal,
): Promise<void> {
  const seen = new Set<string>();
  for (const ref of fileRefs(input)) {
    if (ref.run === RUN) continue;
    const target = join(
      workspace.poolFiles,
      String(ref.run),
      ref.step,
      String(ref.attempt),
      'files',
      objectPath(ref.path),
    );
    if (seen.has(target) || existsSync(target)) continue;
    seen.add(target);
    const address = formatAddress(
      pool.source?.pipeline ?? '?',
      ref.run,
      ref.step,
      ref.attempt,
    );
    if (pool.source?.run !== ref.run)
      throw new CliError(
        `The input references ${address}/${ref.path}, which is not from a run in the local pool; pass --from <pipeline>#${ref.run}.`,
      );
    log(`fetching ${address}/${ref.path}`);
    const response = await downloadStepFile(
      api,
      {
        pipelineId: pool.source.pipelineId,
        number: ref.run,
        step: ref.step,
        path: ref.path,
        attempt: ref.attempt,
      },
      signal,
    );
    mkdirSync(dirname(target), { recursive: true });
    const temp = `${target}.part`;
    try {
      await Bun.write(temp, response);
      const actual = await sha256(temp);
      if (actual !== ref.sha256)
        throw new CliError(
          `${address}/${ref.path} has sha256 ${actual}, expected ${ref.sha256}.`,
        );
      renameSync(temp, target);
    } catch (error) {
      rmSync(temp, { force: true });
      if (error instanceof CliError) throw error;
      throw new CliError(`Could not write '${target}': ${describe(error)}`);
    }
  }
}

async function sha256(path: string): Promise<string> {
  const hasher = new Bun.CryptoHasher('sha256');
  for await (const chunk of createReadStream(path)) hasher.update(chunk as Buffer);
  return hasher.digest('hex');
}
