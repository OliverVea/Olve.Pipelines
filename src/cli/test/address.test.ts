import { describe, expect, test } from 'bun:test';
import {
  formatAddress,
  matchPipeline,
  parseAddress,
  parseRunAddress,
  parseStepAddress,
  parseStepRef,
} from '../src/address';

describe('addresses', () => {
  test('parses pipeline#run, the step, the attempt and a file path', () => {
    expect(parseAddress('olve-pipelines#412')).toEqual({
      pipeline: 'olve-pipelines',
      run: 412,
      step: undefined,
      attempt: undefined,
      path: undefined,
    });
    expect(parseAddress('p#4/deploy-beta@2')).toEqual({
      pipeline: 'p',
      run: 4,
      step: 'deploy-beta',
      attempt: 2,
      path: undefined,
    });
    expect(parseAddress('Olve.Pipelines v2#147/build@1/dist/a#b.txt')).toEqual({
      pipeline: 'Olve.Pipelines v2',
      run: 147,
      step: 'build',
      attempt: 1,
      path: 'dist/a#b.txt',
    });
  });

  test('rejects what is not an address', () => {
    expect(() => parseAddress('p')).toThrow('Expected an address');
    expect(() => parseAddress('p#0')).toThrow('positive integer');
    expect(() => parseAddress('p#1/s@x')).toThrow('Attempt');
    expect(() => parseRunAddress('p#1/s')).toThrow('got the step address');
    expect(() => parseStepAddress('p#1')).toThrow('Expected a step address');
    expect(() => parseStepAddress('p#1/s/f')).toThrow('Unexpected file path');
  });

  test('formats back and parses pipeline/step refs', () => {
    expect(formatAddress('p', 3, 'build', 2, 'x/y')).toBe('p#3/build@2/x/y');
    expect(parseStepRef('olve-pipelines/deploy-prod')).toEqual({
      pipeline: 'olve-pipelines',
      step: 'deploy-prod',
    });
    expect(() => parseStepRef('p#1/s')).toThrow('without a run number');
  });

  test('names match exactly first, then case-insensitively, else fail', () => {
    const pipelines = [
      { id: '1', name: 'Alpha' },
      { id: '2', name: 'alpha' },
      { id: '3', name: 'beta' },
    ] as unknown as Parameters<typeof matchPipeline>[0];
    expect(matchPipeline(pipelines, 'alpha').id).toBe('2');
    expect(matchPipeline(pipelines, 'BETA').id).toBe('3');
    expect(() => matchPipeline(pipelines, 'ALPHA')).toThrow('ambiguous');
    expect(() => matchPipeline(pipelines, 'gamma')).toThrow("No pipeline named 'gamma'");
  });
});
