import { describe, expect, test } from 'vitest';
import type { RunStepStatus } from '@olve/api';
import {
  cellAddress,
  cellLabel,
  gridOf,
  matrixOf,
  progressOf,
  rollUp,
} from '../src/model/matrix.js';

const cells = (...statuses: RunStepStatus[]) => statuses.map((status) => ({ status }));

describe('rollUp', () => {
  test.each([
    ['no cells', [], 'not-run'],
    ['one status throughout', ['skipped', 'skipped'], 'skipped'],
    ['succeeded and skipped', ['succeeded', 'skipped'], 'succeeded'],
    ['a failure beats a running cell', ['succeeded', 'in-progress', 'failed'], 'failed'],
    ['a running cell beats waiting ones', ['scheduled', 'in-progress'], 'in-progress'],
    ['a cell waiting for input', ['succeeded', 'waiting'], 'waiting'],
    ['a cancelled cell is not a success', ['succeeded', 'cancelled'], 'cancelled'],
  ] as [string, RunStepStatus[], RunStepStatus][])('%s', (_, statuses, expected) =>
    expect(rollUp(cells(...statuses))).toBe(expected),
  );

  test('progress counts the cells finished well', () =>
    expect(progressOf(cells('succeeded', 'skipped', 'failed', 'in-progress'))).toBe(
      '2/4',
    ));
});

describe('cells', () => {
  const cell = { os: 'linux', arch: 'x64' };

  test('label and address keep the axis order', () => {
    expect(cellLabel(cell)).toBe('linux · x64');
    expect(cellAddress('build-cli', cell)).toBe('build-cli[os=linux,arch=x64]');
  });

  test('only a two-axis matrix is laid out as a grid', () => {
    expect(gridOf({ axes: { os: ['linux'], arch: ['x64'] }, cells: [cell] })).toEqual({
      rowAxis: 'os',
      colAxis: 'arch',
    });
    expect(gridOf({ axes: { os: ['linux'] }, cells: [{ os: 'linux' }] })).toBeNull();
  });
});

describe('matrixOf', () => {
  const step = (matrix?: unknown) =>
    ({ name: 'build-cli', matrix }) as unknown as Parameters<typeof matrixOf>[0];

  test('the cells are the cross product in axis order, minus the excludes', () =>
    expect(
      matrixOf(
        step({
          axes: [
            { name: 'os', values: ['linux', 'windows'] },
            { name: 'arch', values: ['x64', 'arm64'] },
          ],
          exclude: [{ os: 'windows', arch: 'arm64' }],
          failure: { kind: 'keepRunning' },
        }),
      ),
    ).toEqual({
      axes: { os: ['linux', 'windows'], arch: ['x64', 'arm64'] },
      cells: [
        { os: 'linux', arch: 'x64' },
        { os: 'linux', arch: 'arm64' },
        { os: 'windows', arch: 'x64' },
      ],
    }));

  test('an exclude naming one axis drops every cell with that value', () =>
    expect(
      matrixOf(
        step({
          axes: [
            { name: 'os', values: ['linux', 'windows'] },
            { name: 'arch', values: ['x64', 'arm64'] },
          ],
          exclude: [{ os: 'windows' }],
          failure: { kind: 'keepRunning' },
        }),
      )?.cells,
    ).toEqual([
      { os: 'linux', arch: 'x64' },
      { os: 'linux', arch: 'arm64' },
    ]));

  test('a plain step has no matrix', () => expect(matrixOf(step())).toBeUndefined());
});
