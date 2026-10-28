import { expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { HARNESS_BASE_IMAGE } from '../src/dev/session';

test('pl dev --docker takes its loader from the harness image the server runs', () => {
  const source = readFileSync(
    join(
      import.meta.dir,
      '../../backend/Olve.Pipelines/Kubernetes/Harness/HarnessImage.cs',
    ),
    'utf8',
  );
  const tag = /public const string Tag = "([^"]+)";/.exec(source)?.[1];
  expect(HARNESS_BASE_IMAGE).toBe(`ghcr.io/olivervea/olve-pipelines-harness-base:${tag}`);
});
