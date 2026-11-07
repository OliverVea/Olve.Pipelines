/**
 * A matrix step runs once per cell: one combination of its axes' values, minus the excludes.
 * The step's own status is rolled up from its cells.
 */
import type { RunStepStatus } from '@olve/api';
import type { ManifestStep } from './manifest.js';

/** One combination, e.g. `{ os: 'linux', arch: 'x64' }`, in axis order. */
export type Cell = Record<string, string>;

export type Matrix = {
  axes: Record<string, string[]>;
  /** Every cell that runs, in run order. */
  cells: Cell[];
};

export type CellState = {
  cell: Cell;
  status: RunStepStatus;
  reason?: string | null;
  attemptCount?: number;
  latestAttempt?: number | null;
  startedAt?: Date | string | null;
  finishedAt?: Date | string | null;
  logsHref?: string;
};

/** A step's `matrix` as the manifest writes it. */
export type ManifestMatrix = {
  axes: { name: string; values: string[] }[];
  exclude?: Cell[];
  failure: { kind: string };
  maxParallel?: number;
};

/** The step's matrix with its cells: the cross product in axis order, minus the excludes. */
export function matrixOf(step: ManifestStep): Matrix | undefined {
  const m = step.matrix as ManifestMatrix | undefined;
  if (!m?.axes?.length) return undefined;
  let cells: Cell[] = [{}];
  for (const { name, values } of m.axes)
    cells = cells.flatMap((c) => values.map((v) => ({ ...c, [name]: v })));
  const excluded = (c: Cell) =>
    (m.exclude ?? []).some((x) => Object.entries(x).every(([k, v]) => c[k] === v));
  return {
    axes: Object.fromEntries(m.axes.map((a) => [a.name, a.values])),
    cells: cells.filter((c) => !excluded(c)),
  };
}

export const cellLabel = (cell: Cell): string => Object.values(cell).join(' · ');

/** How `pl` and the API address a cell: `build-cli[os=linux,arch=x64]`. */
export const cellAddress = (step: string, cell: Cell): string =>
  `${step}[${Object.entries(cell)
    .map(([k, v]) => `${k}=${v}`)
    .join(',')}]`;

export const sameCell = (a: Cell, b: Cell): boolean =>
  Object.keys(a).length === Object.keys(b).length &&
  Object.entries(a).every(([k, v]) => b[k] === v);

/** Rows by the first axis and columns by the second, for a two-axis matrix; holes are excluded cells. */
export function gridOf(m: Matrix): { rowAxis: string; colAxis: string } | null {
  const axes = Object.keys(m.axes);
  return axes.length === 2 ? { rowAxis: axes[0]!, colAxis: axes[1]! } : null;
}

const ORDER: RunStepStatus[] = [
  'failed',
  'in-progress',
  'waiting',
  'held',
  'scheduled',
  'pending',
  'cancelled',
  'obsolete',
  'succeeded',
  'skipped',
  'not-run',
  'excluded',
];

/**
 * The step's status from its cells: any failure fails it, else anything still going keeps it
 * going, and it succeeds only when every cell did (skipped cells count as done).
 */
export function rollUp(cells: readonly Pick<CellState, 'status'>[]): RunStepStatus {
  if (cells.length === 0) return 'not-run';
  const statuses = new Set(cells.map((c) => c.status));
  if (statuses.size === 1) return cells[0]!.status;
  if ([...statuses].every((s) => s === 'succeeded' || s === 'skipped'))
    return 'succeeded';
  return ORDER.find((s) => statuses.has(s)) ?? 'not-run';
}

/** "4/6": cells finished well out of all of them. */
export function progressOf(cells: readonly Pick<CellState, 'status'>[]): string {
  const done = cells.filter((c) => c.status === 'succeeded' || c.status === 'skipped');
  return `${done.length}/${cells.length}`;
}
