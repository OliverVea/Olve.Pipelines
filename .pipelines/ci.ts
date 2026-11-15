import { checks } from '@olivervea/pipelines';
import { GITHUB_TOKEN } from './config';
import { buildCli, checkCli } from './steps/build-cli';
import { buildFrontend } from './steps/build-frontend';
import { buildSdk } from './steps/build-sdk';
import { compileServer } from './steps/compile-server';
import { source } from './steps/source';
import { spec } from './steps/spec';
import { test } from './steps/test';

export const ci = checks({
  description:
    'Builds and tests a pull request like a deploy does, and publishes nothing.',
  secrets: [GITHUB_TOKEN],
})
  .stage([source, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(spec)
  .stage(compileServer, buildFrontend, test, buildSdk, checkCli, buildCli);
