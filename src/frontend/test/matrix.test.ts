import { describe, expect, test } from 'vitest';
import type { RunStepStatus } from '@olve/api';
import {
  cellAddress,
  cellLabel,
  gridOf,
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
