import { pipelinesList, type Pipeline } from '@olve/api';
import type { Client } from '@olve/api/client';
import { call } from './api';
import { CliError, UsageError } from './errors';

/** `<pipeline>#<run>[/<step>[@<attempt>][/<path>]]`; a CI run is `<pipeline>#ci<run>…`. */
export type RunAddress = {
  pipeline: string;
  run: number;
  step?: string;
  attempt?: number;
  path?: string;
};

/** Deploy runs (`#412`) or CI runs (`#ci12`): numbered in sequences of their own. */
export type RunKind = 'cd' | 'ci';

const prefix = (kind: RunKind) => (kind === 'ci' ? 'ci' : '');

// The pipeline part is lazy: the first "#<digits>" followed by "/" or the end splits it, so a
// file path containing '#' stays in the path. Step names take anything but '/' and '@'.
const patterns: Record<RunKind, RegExp> = {
  cd: /^(?<pipeline>.+?)#(?<run>\d+)(?:\/(?<step>[^/@]+)(?:@(?<attempt>[^/]*))?(?:\/(?<path>.+))?)?$/s,
  ci: /^(?<pipeline>.+?)#ci(?<run>\d+)(?:\/(?<step>[^/@]+)(?:@(?<attempt>[^/]*))?(?:\/(?<path>.+))?)?$/s,
};

function mismatch(text: string, kind: RunKind): CliError {
  if (kind === 'cd') {
    const hint = patterns.ci.test(text)
      ? ` '${text}' is a CI run: use \`pl ci …\` (\`pl ci get\`, \`pl ci logs\`, \`pl wait ci\`).`
      : '';
    return new CliError(
      `Expected an address like <pipeline>#<run>[/<step>[@<attempt>]] (e.g. olve-pipelines#412/deploy-beta@1), got '${text}'.${hint}`,
    );
  }
  const run = patterns.cd.exec(text)?.groups?.run;
  const hint = run ? ` '#${run}' is a deploy run; CI run ${run} is '#ci${run}'.` : '';
  return new CliError(
    `Expected a CI run address like <pipeline>#ci<run>[/<step>[@<attempt>]] (e.g. olve-pipelines#ci12/test@1), got '${text}'.${hint}`,
  );
}

export function parseAddress(text: string, kind: RunKind = 'cd'): RunAddress {
  const match = patterns[kind].exec(text);
  if (!match?.groups) throw mismatch(text, kind);
  const run = Number(match.groups.run);
  if (!Number.isInteger(run) || run < 1)
    throw new CliError(`Run number in '${text}' must be a positive integer.`);
  let attempt: number | undefined;
  if (match.groups.attempt !== undefined) {
    attempt = Number(match.groups.attempt);
    if (!/^\d+$/.test(match.groups.attempt) || attempt < 1)
      throw new CliError(`Attempt in '${text}' must be a positive integer (e.g. @1).`);
  }
  const pipeline = match.groups.pipeline!.trim();
  if (!pipeline) throw new CliError(`Address '${text}' has no pipeline.`);
  return { pipeline, run, step: match.groups.step, attempt, path: match.groups.path };
}

export function parseRunAddress(text: string, kind: RunKind = 'cd'): RunAddress {
  const address = parseAddress(text, kind);
  if (address.step !== undefined)
    throw new CliError(
      `Expected a run address <pipeline>#${prefix(kind)}<run>, got the step address '${text}'.`,
    );
  return address;
}

export function parseStepAddress(
  text: string,
  allowPath = false,
  kind: RunKind = 'cd',
): RunAddress & { step: string } {
  const address = parseAddress(text, kind);
  if (address.step === undefined)
    throw new CliError(
      `Expected a step address <pipeline>#${prefix(kind)}<run>/<step>[@<attempt>], got '${text}'.`,
    );
  if (address.path !== undefined && !allowPath)
    throw new CliError(`Unexpected file path in step address '${text}'.`);
  return address as RunAddress & { step: string };
}

function format(
  kind: RunKind,
  pipeline: string,
  run: number,
  step?: string,
  attempt?: number,
  path?: string,
): string {
  let text = `${pipeline}#${prefix(kind)}${run}`;
  if (step === undefined) return text;
  text += `/${step}`;
  if (attempt !== undefined) text += `@${attempt}`;
  if (path !== undefined) text += `/${path}`;
  return text;
}

export const formatAddress = (
  pipeline: string,
  run: number,
  step?: string,
  attempt?: number,
  path?: string,
): string => format('cd', pipeline, run, step, attempt, path);

export const formatCiAddress = (
  pipeline: string,
  run: number,
  step?: string,
  attempt?: number,
  path?: string,
): string => format('ci', pipeline, run, step, attempt, path);

/** `<pipeline>/<step>`, for the pipeline-scoped step commands. */
export function parseStepRef(text: string): { pipeline: string; step: string } {
  const slash = text.lastIndexOf('/');
  if (slash <= 0 || slash === text.length - 1)
    throw new CliError(
      `Expected <pipeline>/<step> (e.g. olve-pipelines/deploy-prod), got '${text}'.`,
    );
  const pipeline = text.slice(0, slash).trim();
  if (!pipeline) throw new CliError(`'${text}' has no pipeline.`);
  if (pipeline.includes('#'))
    throw new CliError(`Expected <pipeline>/<step> without a run number, got '${text}'.`);
  return { pipeline, step: text.slice(slash + 1) };
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export type ResolvedPipeline = { id: string; label: string };

/** A pipeline by id or by name (exact, else case-insensitive); the label is what the user typed. */
export async function resolvePipeline(
  api: Client,
  text: string,
): Promise<ResolvedPipeline> {
  if (guid.test(text)) return { id: text.toLowerCase(), label: text };
  const all = await call(pipelinesList({ client: api }));
  return { id: matchPipeline(all, text).id, label: text };
}

export function matchPipeline(pipelines: readonly Pipeline[], name: string): Pipeline {
  const exact = pipelines.filter((p) => p.name === name);
  if (exact.length === 1) return exact[0]!;
  const candidates =
    exact.length > 1
      ? exact
      : pipelines.filter((p) => p.name.toLowerCase() === name.toLowerCase());
  if (candidates.length === 1) return candidates[0]!;
  if (candidates.length === 0)
    throw new CliError(
      `No pipeline named '${name}'. Run \`pl pipeline list\`, or pass the pipeline id.`,
    );
  throw new CliError(
    `Pipeline name '${name}' is ambiguous (${candidates.map((p) => p.id).join(', ')}); pass the pipeline id.`,
  );
}

export type ResolvedAddress = ResolvedPipeline & { address: RunAddress };

export async function resolveRun(
  api: Client,
  text: string,
  kind: RunKind = 'cd',
): Promise<ResolvedAddress> {
  const address = parseRunAddress(text, kind);
  return { ...(await resolvePipeline(api, address.pipeline)), address };
}

export async function resolveStep(
  api: Client,
  text: string,
  allowPath = false,
  kind: RunKind = 'cd',
): Promise<ResolvedAddress & { address: RunAddress & { step: string } }> {
  const address = parseStepAddress(text, allowPath, kind);
  return { ...(await resolvePipeline(api, address.pipeline)), address };
}

/** A pull request number: `12` or `#12`. */
export function parsePull(text: string): number {
  const match = /^#?(\d+)$/.exec(text.trim());
  const n = match ? Number(match[1]) : NaN;
  if (!(n >= 1))
    throw new UsageError(
      `Expected a pull request number (e.g. 12 or #12), got '${text}'.`,
    );
  return n;
}
