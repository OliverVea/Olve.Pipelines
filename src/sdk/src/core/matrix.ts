import { isModel, type AnyModel, type Infer } from './models.ts';
import type { Simplify } from './types.ts';

/** Axis name → its values, in order. Object key order is the axis order. */
export type Axes = { readonly [axis: string]: readonly string[] };
/** One combination of axis values. */
export type Cell = Readonly<Record<string, string>>;
/** The cell type of a step whose axes are `Ax`. */
export type CellOfAxes<Ax extends Axes> = Simplify<{
  readonly [K in keyof Ax as string extends K ? never : K]: Ax[K][number];
}>;

/** What the step's other cells do when one fails. */
export type FailureStrategy =
  { readonly kind: 'keepRunning' } | { readonly kind: 'cascadeFailure' };

/** Every cell runs to the end (the default). */
export function keepRunning(): FailureStrategy {
  return Object.freeze({ kind: 'keepRunning' as const });
}
/** The first failed cell cancels the cells still running or waiting. */
export function cascadeFailure(): FailureStrategy {
  return Object.freeze({ kind: 'cascadeFailure' as const });
}

export interface MatrixDef<Ax extends Axes> {
  /** The cells are the cross product of the axes, first axis outermost. */
  axes: Ax;
  /** Each entry names axes with one of their values; a cell matching all of them is left out. */
  exclude?: readonly NoInfer<Partial<CellOfAxes<Ax>>>[];
  /** Default `keepRunning()`. */
  failure?: FailureStrategy;
  /** At most this many of the step's cells running at once in a run. */
  maxParallel?: number;
}

/** A matrix step's matrix, as the step carries it. */
export interface Matrix<Ax extends Axes = Axes> {
  readonly axes: Ax;
  readonly exclude: readonly Cell[];
  readonly failure: FailureStrategy;
  readonly maxParallel?: number;
}

export const AXIS_NAME = /^[a-z][a-zA-Z0-9]{0,30}$/;
export const AXIS_VALUE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$/;
export const MAX_AXES = 4;
export const MAX_CELLS = 64;

export function makeMatrix(def: MatrixDef<Axes>): Matrix {
  return Object.freeze({
    axes: Object.freeze(
      Object.fromEntries(
        Object.entries(def.axes ?? {}).map(([k, v]) => [
          k,
          Object.freeze(Array.isArray(v) ? [...v] : v),
        ]),
      ),
    ),
    exclude: Object.freeze((def.exclude ?? []).map((e) => Object.freeze({ ...e }))),
    failure: def.failure ?? keepRunning(),
    ...(def.maxParallel !== undefined ? { maxParallel: def.maxParallel } : {}),
  }) as Matrix;
}

const matches = (cell: Cell, x: Cell) =>
  Object.entries(x).every(([k, v]) => cell[k] === v);

/** The cells in order: the cross product in axis order (first axis outermost) minus the excludes. */
export function matrixCells(m: Matrix): Cell[] {
  let cells: Record<string, string>[] = [{}];
  for (const [axis, values] of Object.entries(m.axes))
    cells = cells.flatMap((c) =>
      (Array.isArray(values) ? values : []).map((v) => ({ ...c, [axis]: v })),
    );
  return cells.filter((c) => !m.exclude.some((x) => matches(c, x)));
}

/** `build-cli[os=linux,arch=x64]`: the name a cell's attempts, files and address use. */
export function cellName(step: string, cell: Cell): string {
  return `${step}[${Object.entries(cell)
    .map(([k, v]) => `${k}=${v}`)
    .join(',')}]`;
}

/** The cell `raw` (JSON text, as in `OLVE_CELL`) names, in axis order, or what is wrong. */
export function parseCell(
  step: string,
  m: Matrix,
  raw: string | undefined,
): { ok: true; cell: Cell } | { ok: false; message: string } {
  const axes = Object.keys(m.axes);
  const example = JSON.stringify(matrixCells(m)[0] ?? {});
  if (raw === undefined || raw === '')
    return {
      ok: false,
      message: `step '${step}' is a matrix step: OLVE_CELL must name the cell to run, e.g. ${example}`,
    };
  let v: unknown;
  try {
    v = JSON.parse(raw);
  } catch (e) {
    return { ok: false, message: `OLVE_CELL is not JSON: ${(e as Error).message}` };
  }
  if (typeof v !== 'object' || v === null || Array.isArray(v))
    return {
      ok: false,
      message: `OLVE_CELL must be a JSON object of axis values, e.g. ${example}`,
    };
  const given = v as Record<string, unknown>;
  const ordered: Record<string, string> = {};
  for (const a of axes) {
    if (typeof given[a] !== 'string') break;
    ordered[a] = given[a] as string;
  }
  const exact =
    Object.keys(given).length === axes.length &&
    Object.keys(ordered).length === axes.length;
  const all = matrixCells(m);
  if (exact && all.some((c) => cellName(step, c) === cellName(step, ordered)))
    return { ok: true, cell: Object.freeze(ordered) };
  return {
    ok: false,
    message: `OLVE_CELL ${raw} is not a cell of step '${step}' (cells: ${all.map((c) => cellName(step, c)).join(', ')})`,
  };
}

// ─── cells(): reading a matrix step ──────────────────────────────────────────────────────────

/** A step input that reads every cell of a matrix step whose output is `model`. */
export interface Cells<M extends AnyModel = AnyModel> {
  readonly kind: 'cells';
  readonly model: M;
}
export function cells<M extends AnyModel>(model: M): Cells<M> {
  return Object.freeze({ kind: 'cells' as const, model });
}
export function isCells(v: unknown): v is Cells {
  return (
    typeof v === 'object' &&
    v !== null &&
    (v as { kind?: unknown }).kind === 'cells' &&
    isModel((v as { model?: unknown }).model)
  );
}
/** One entry of a `cells` input, in cell order. */
export interface CellOutput<M extends AnyModel, C extends Cell = Cell> {
  readonly cell: C;
  readonly output: Infer<M>;
}
