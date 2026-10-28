// The server's default harness image (HarnessImage.cs) must be the tag publish-harness pushes for the
// current src/harness/base: imageBuild's content tag of that directory.
import { describe, expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { contentTagOf } from '../../src/library/image-build.ts';
import { tmp } from './helpers.ts';

const repo = join(import.meta.dir, '../../../..');

describe('harness image default', () => {
  // The tag covers file modes, which a Windows checkout doesn't have.
  test.skipIf(process.platform === 'win32')(
    'HarnessImage.Tag is the content tag of src/harness/base',
    () => {
      const source = readFileSync(
        join(repo, 'src/backend/Olve.Pipelines/Kubernetes/Harness/HarnessImage.cs'),
        'utf8',
      );
      const tag = /public const string Tag = "([^"]+)";/.exec(source)?.[1];
      expect(tag).toBe(contentTagOf(join(repo, 'src/harness/base'), tmp('harness-tag')));
    },
  );
});
