import { fileURLToPath } from 'node:url';
import { defineConfig } from '@playwright/test';

const results = fileURLToPath(
  new URL('../../../artifacts/real-system/browser', import.meta.url),
);

export default defineConfig({
  testDir: 'flows',
  tsconfig: './tsconfig.json',
  globalSetup: './support/guard-hosts.ts',
  fullyParallel: true,
  // Steps run on the host's 4 Job slots: more workers only queue there.
  workers: 4,
  retries: 0,
  // A fresh host pulls the step image and builds the fixture before the first run ends.
  timeout: 180_000,
  outputDir: `${results}/results`,
  reporter: [['list'], ['junit', { outputFile: `${results}/junit.xml` }]],
  use: {
    browserName: 'chromium',
    viewport: { width: 1280, height: 800 },
    trace: 'retain-on-failure',
  },
});
