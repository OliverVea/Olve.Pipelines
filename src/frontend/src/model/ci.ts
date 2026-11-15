/** View logic for CI: pull requests, CI runs and the merge queue. */
import type {
  CiHealth,
  CiRun,
  CiRunSummary,
  CiStepState,
  Pull,
  QueueEntry,
  RunStatus,
} from '@olve/api';
import type { Tone } from '../styles/ui.js';
import { toneOf } from '../styles/ui.js';
import type { Manifest, ManifestStep, Stage } from './manifest.js';
import type { ManifestMatrix } from './matrix.js';

/**
 * What a pull request's row says: its latest run's status, or without one `conflict`, `pending` (open,
 * its first run not started) or `Cancelled` (closed or merged before a run started).
 */
export type PullStatus = RunStatus | 'conflict' | 'pending';

export const pullStatus = (p: Pull): PullStatus =>
  p.conflict
    ? 'conflict'
    : (p.latestRun?.status ?? (p.state === 'open' ? 'pending' : 'Cancelled'));

/** The run a pull request's row shows: none while it conflicts. */
export const pullRun = (p: Pull): CiRunSummary | null =>
  p.conflict ? null : p.latestRun;

const CI_TONES: Record<string, Tone> = {
  conflict: 'wait',
  queued: 'idle',
  testing: 'run',
  merged: 'ok',
  removed: 'fail',
};

export function ciTone(status: string): Tone {
  return CI_TONES[status] ?? toneOf(status as RunStatus);
}

export function ciLabel(status: string): string {
  return status.toLowerCase();
}

/** A run's steps by stage, for the step strip. */
export function stripOf(steps: readonly CiStepState[]): CiStepState[][] {
  const stages = new Map<number, CiStepState[]>();
  for (const s of steps) {
    const list = stages.get(s.stage) ?? [];
    list.push(s);
    stages.set(s.stage, list);
  }
  return [...stages.entries()].sort(([a], [b]) => a - b).map(([, list]) => list);
}

/** The tag after an open pull request's row: its place in the queue. */
export const queuePill = (p: Pull): string | null =>
  p.queue?.position != null ? `queue ${p.queue.position}` : null;

/** Hover text of the stale tag: the main the run was tested on, and main now. */
export const staleTitle = (run: CiRunSummary, head: string | null): string =>
  `tested on main ${run.mainSha.slice(0, 7)}${head ? `; main is now ${head.slice(0, 7)}` : ''}`;

/** Why a run ended without a result of its own: superseded, or its pull request closed, merged or left the queue. */
export function runWhy(
  r: Pick<CiRunSummary, 'supersededByNumber' | 'cancelReason'>,
): { supersededBy: number } | { text: string } | null {
  if (r.supersededByNumber != null) return { supersededBy: r.supersededByNumber };
  if (r.cancelReason === 'dequeued') return { text: 'dequeued' };
  if (r.cancelReason) return { text: `pull request ${r.cancelReason}` };
  return null;
}

/** The note on a queue entry: why it was removed, or the failed run whose split put it back in the queue. */
export function queueWhy(
  e: QueueEntry,
  runStatus: (run: number) => RunStatus | undefined,
): { text: string } | { split: number } | null {
  if (e.state === 'removed') return e.reason ? { text: e.reason } : null;
  if (e.state === 'queued' && e.runs.length && runStatus(e.runs[0]) === 'Failed')
    return { split: e.runs[0] };
  return null;
}

/** The overview's "Pull requests" cell: left out without open pull requests. */
export function pullsCell(
  ci: CiHealth | null | undefined,
): { tone: Tone; value: string; sub: string } | null {
  if (!ci || ci.open === 0) return null;
  const parts = [
    ...(ci.failing ? [`${ci.failing} failing`] : []),
    ...(ci.running ? [`${ci.running} running`] : []),
  ];
  return {
    tone: ci.failing ? 'fail' : ci.running ? 'run' : 'ok',
    value: `${ci.open} open`,
    sub: parts.length ? parts.join(' · ') : 'all passing',
  };
}

const prs = (n: number) => `${n} PR${n === 1 ? '' : 's'}`;

/** The homepage card's tags: only when a pull request fails or runs. */
export function pullTags(ci: CiHealth | null | undefined): {
  failing?: string;
  running?: string;
} {
  if (!ci) return {};
  return {
    ...(ci.failing ? { failing: `${prs(ci.failing)} failing` } : {}),
    ...(ci.running ? { running: `${prs(ci.running)} running` } : {}),
  };
}

/**
 * The CI run's stages as a manifest for the stage graph. The API serves a run's steps, not their
 * descriptors, so the steps carry names and a matrix from their cells, and no input mappings.
 */
export function ciManifest(run: CiRun): Manifest {
  const stages: Stage[] = run.stages.map((stage) => {
    const steps: ManifestStep[] = stage.steps.map((s) => ({
      name: s.name,
      kind: '',
      image: '',
      input: {},
      inputTypes: {},
      output: null,
      secrets: [],
      ...(s.cells?.length ? { matrix: matrixFromCells(s.cells.map((c) => c.cell)) } : {}),
    }));
    return stage.when.length
      ? {
          when: Object.fromEntries(stage.when.map((w) => [w.field, w.values])),
          stages: [{ steps }],
        }
      : { steps };
  });
  return {
    apiVersion: '2.0',
    name: '',
    input: 'CiInput',
    models: {},
    secrets: [],
    triggers: [],
    stages,
    failureHandlers: [],
  };
}

function matrixFromCells(cells: Record<string, string>[]): ManifestMatrix {
  const axes = new Map<string, string[]>();
  for (const cell of cells)
    for (const [k, v] of Object.entries(cell)) {
      const values = axes.get(k) ?? [];
      if (!values.includes(v)) values.push(v);
      axes.set(k, values);
    }
  let product: Record<string, string>[] = [{}];
  for (const [name, values] of axes)
    product = product.flatMap((c) => values.map((v) => ({ ...c, [name]: v })));
  const same = (a: Record<string, string>, b: Record<string, string>) =>
    Object.entries(a).every(([k, v]) => b[k] === v);
  const exclude = product.filter((c) => !cells.some((x) => same(c, x)));
  return {
    axes: [...axes.entries()].map(([name, values]) => ({ name, values })),
    ...(exclude.length ? { exclude } : {}),
    failure: { kind: 'keepRunning' },
  };
}
