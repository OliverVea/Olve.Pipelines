import { CliError } from '../errors';
import type { DevMatrix, DevStep } from './manifest';

export type Cell = Record<string, string>;

/** The cross product in axis order (first axis outermost) minus the excludes. */
export function cellsOf(matrix: DevMatrix): Cell[] {
  let cells: Cell[] = [{}];
  for (const { name, values } of matrix.axes)
    cells = cells.flatMap((c) => values.map((v) => ({ ...c, [name]: v })));
  return cells.filter(
    (c) => !matrix.exclude.some((x) => Object.entries(x).every(([k, v]) => c[k] === v)),
  );
}

/** `build-cli[os=linux,arch=x64]`. */
export const cellName = (step: string, cell: Cell) =>
  `${step}[${Object.entries(cell)
    .map(([k, v]) => `${k}=${v}`)
    .join(',')}]`;

/** The cells `--cell os=linux,arch=x64` picks (every cell without one); a partial value picks every cell it matches. */
export function pickCells(step: DevStep, arg: string | undefined): Cell[] | undefined {
  if (!step.matrix) {
    if (arg !== undefined)
      throw new CliError(`--cell: '${step.name}' is not a matrix step.`);
    return undefined;
  }
  const all = cellsOf(step.matrix);
  if (arg === undefined) return all;
  const wanted: Cell = {};
  for (const pair of arg
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean)) {
    const eq = pair.indexOf('=');
    if (eq <= 0) throw new CliError(`--cell expects axis=value,…, got '${pair}'.`);
    wanted[pair.slice(0, eq)] = pair.slice(eq + 1);
  }
  const picked = all.filter((c) => Object.entries(wanted).every(([k, v]) => c[k] === v));
  if (!picked.length)
    throw new CliError(
      `--cell ${arg} matches no cell of '${step.name}' (cells: ${all.map((c) => cellName(step.name, c)).join(', ')}).`,
    );
  return picked;
}
