import { test as base, expect, type Page } from '@playwright/test';
import { HostApi } from './api.ts';
import { loadTargets, type TestTarget } from './hosts.ts';

type WorkerFixtures = { target: TestTarget };
type TestFixtures = { api: HostApi; pageErrors: void };

const signedInUser = {
  access_token: 'unchecked',
  token_type: 'Bearer',
  profile: { sub: 'e2e', iss: 'e2e', aud: 'e2e', exp: 0, iat: 0 },
  expires_at: 4_102_444_800,
};

export const test = base.extend<TestFixtures, WorkerFixtures>({
  target: [
    async ({}, use, worker) => {
      const targets = loadTargets();
      await use(targets[worker.parallelIndex % targets.length]!);
    },
    { scope: 'worker' },
  ],
  baseURL: async ({ target }, use) => use(target.apiUrl),
  api: async ({ target }, use) => use(new HostApi(target.apiUrl)),

  // The host has no sign-in: the app reads the session oidc-client-ts would have stored.
  context: async ({ context, api }, use) => {
    const { oidcAuthority, oidcClientId } = await api.frontendConfig();
    await context.addInitScript(({ key, user }) => sessionStorage.setItem(key, user), {
      key: `oidc.user:${oidcAuthority}:${oidcClientId}`,
      user: JSON.stringify(signedInUser),
    });
    await use(context);
  },

  pageErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await use();
      expect(errors, 'uncaught errors in the page').toEqual([]);
    },
    { auto: true },
  ],
});

/** A run page's status, next to its title. */
export const runStatus = (page: Page) => page.locator('.line .st');

/** Long enough for a run on a fresh host, whose first step pulls the image. */
export const runTimeout = { timeout: 150_000 };

export { expect };
