import type { FrontendConfig } from '@olve/api';
import { describe, expect, test } from 'bun:test';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { codeChallenge } from '../src/auth/oidc';
import { fakeFetch, pl } from './helpers';

const API = 'https://pl.example';
const ID = 'https://id.example';

const frontend: FrontendConfig = {
  oidcAuthority: ID,
  oidcClientId: 'pl-cli',
  oidcScopes: 'openid groups',
  apiReference: false,
};

/** The instance and its OIDC provider. `token` answers each token request in turn. */
function provider(
  options: { device?: boolean; frontend?: FrontendConfig; token?: unknown[] } = {},
) {
  const token = options.token ?? [
    { access_token: 'access-1', refresh_token: 'refresh-1', expires_in: 3600 },
  ];
  return fakeFetch((req) => {
    if (req.url === `${API}/api/config/frontend`)
      return { body: options.frontend ?? frontend };
    if (req.url === `${ID}/.well-known/openid-configuration`)
      return {
        body: {
          authorization_endpoint: `${ID}/auth`,
          token_endpoint: `${ID}/token`,
          ...(options.device === false
            ? {}
            : { device_authorization_endpoint: `${ID}/device/code` }),
        },
      };
    if (req.url === `${ID}/device/code`)
      return {
        body: {
          device_code: 'dc',
          user_code: 'ABCD-EFGH',
          verification_uri: `${ID}/device`,
          verification_uri_complete: `${ID}/device?user_code=ABCD-EFGH`,
          interval: 5,
        },
      };
    if (req.url === `${ID}/token`) {
      const next = token.length > 1 ? token.shift() : token[0];
      return { status: (next as { error?: string }).error ? 400 : 200, body: next };
    }
    return { status: 404, body: {} };
  });
}

const form = (body: unknown) => Object.fromEntries(new URLSearchParams(String(body)));
const saved = (home: string) => JSON.parse(readFileSync(join(home, '.pl'), 'utf8'));

describe('pl login --device', () => {
  test('shows the code and links, waits for approval, and saves the tokens to the profile', async () => {
    const home = mkdtempSync(join(tmpdir(), 'pl-login-'));
    const { fetch, requests } = provider({
      token: [
        { error: 'authorization_pending' },
        { access_token: 'access-1', refresh_token: 'refresh-1', expires_in: 3600 },
      ],
    });

    const out = await pl(['login', '--device', '--api-url', API, '--profile', 'beta'], {
      fetch,
      home,
    });

    expect(out.code).toBe(0);
    expect(out.stderr).toContain(
      `  URL:  ${ID}/device\n  Link: ${ID}/device?user_code=ABCD-EFGH\n  Code: ABCD-EFGH`,
    );
    expect(out.stdout).toBe(
      `Logged in to ${API}.\nToken saved to ${join(home, '.pl')} (profile 'beta').\nAccess token expires 2026-10-28T13:00:00.000Z.\n`,
    );
    expect(form(requests.find((r) => r.url === `${ID}/device/code`)!.body)).toEqual({
      client_id: 'pl-cli',
      scope: 'openid groups offline_access',
    });
    expect(requests.filter((r) => r.url === `${ID}/token`)).toHaveLength(2);
    expect(saved(home).profiles.beta).toEqual({
      apiUrl: API,
      auth: {
        accessToken: 'access-1',
        refreshToken: 'refresh-1',
        tokenEndpoint: `${ID}/token`,
        clientId: 'pl-cli',
        expiresAt: '2026-10-28T13:00:00.000Z',
      },
    });
  });

  test('without a refresh token, warns that the login will need repeating', async () => {
    const home = mkdtempSync(join(tmpdir(), 'pl-login-'));
    const { fetch } = provider({ token: [{ access_token: 'access-1' }] });

    const out = await pl(['login', '--device', '--api-url', API], { fetch, home });

    expect(out.stdout).toContain(
      'Warning: no refresh token issued (offline_access not granted) — re-run `pl login` when it expires.',
    );
    expect(saved(home).profiles.default.auth.refreshToken).toBeNull();
  });

  test('an older server with no scopes gets the default scopes', async () => {
    const { fetch, requests } = provider({ frontend: { ...frontend, oidcScopes: '' } });

    await pl(['login', '--device', '--api-url', API], { fetch });

    expect(form(requests.find((r) => r.url === `${ID}/device/code`)!.body).scope).toBe(
      'openid profile email offline_access',
    );
  });
});

describe('pl login refusals', () => {
  test.each([
    [
      'no OIDC client',
      { frontend: { oidcScopes: '', apiReference: false } },
      ['login'],
      'This server exposes no frontend OIDC config (Auth:FrontendAuthority/Audience unset) — cannot log in. Use --token instead.',
    ],
    [
      '--device without a device endpoint',
      { device: false },
      ['login', '--device'],
      "This server's OIDC provider does not advertise a device-authorization endpoint.",
    ],
  ])('%s', async (_, options, argv, message) => {
    const home = mkdtempSync(join(tmpdir(), 'pl-login-'));
    const { fetch } = provider(options);

    const out = await pl([...argv, '--api-url', API], { fetch, home });

    expect(out.code).toBe(1);
    expect(out.stderr).toContain(message);
    expect(() => readFileSync(join(home, '.pl'))).toThrow();
  });
});

/** Plays the user's browser: follows the authorization URL to the loopback redirect with `answer`. */
function browser(answer: (state: string) => Record<string, string>) {
  const seen: { url?: URL; page?: Promise<Response> } = {};
  const open = (url: string) => {
    seen.url = new URL(url);
    const redirect = new URL(seen.url.searchParams.get('redirect_uri')!);
    for (const [k, v] of Object.entries(answer(seen.url.searchParams.get('state')!)))
      redirect.searchParams.set(k, v);
    seen.page = globalThis.fetch(redirect);
  };
  return { open, seen };
}

describe('pl login (browser)', () => {
  test.each([
    ['SSH_CONNECTION', { SSH_CONNECTION: '1 2 3 4', DISPLAY: ':0' }, ['login'], 'device'],
    [
      'no display on Linux',
      {},
      ['login'],
      process.platform === 'linux' ? 'device' : 'browser',
    ],
    ['a display', { DISPLAY: ':0' }, ['login'], 'browser'],
    [
      '--browser over SSH',
      { SSH_CONNECTION: '1 2 3 4' },
      ['login', '--browser'],
      'browser',
    ],
  ])('with %s, uses the %p flow', async (_, env, argv, flow) => {
    const { fetch, requests } = provider();
    const user = browser((state) => ({ code: 'c1', state }));

    const out = await pl([...argv, '--api-url', API], {
      fetch,
      env,
      openBrowser: user.open,
    });

    expect(out.code).toBe(0);
    expect(requests.some((r) => r.url === `${ID}/device/code`)).toBe(flow === 'device');
    expect(user.seen.url !== undefined).toBe(flow === 'browser');
  });

  test('exchanges the redirected code with the verifier behind the challenge', async () => {
    const { fetch, requests } = provider();
    const user = browser((state) => ({ code: 'c1', state }));

    const out = await pl(['login', '--browser', '--api-url', API], {
      fetch,
      openBrowser: user.open,
    });

    expect(out.code).toBe(0);
    const authorize = user.seen.url!;
    expect(authorize.origin + authorize.pathname).toBe(`${ID}/auth`);
    expect(authorize.searchParams.get('redirect_uri')).toMatch(
      /^http:\/\/127\.0\.0\.1:4865[0-4]\/callback$/,
    );
    const exchange = form(requests.find((r) => r.url === `${ID}/token`)!.body);
    expect(exchange.code).toBe('c1');
    expect(exchange.redirect_uri).toBe(authorize.searchParams.get('redirect_uri')!);
    expect(codeChallenge(exchange.code_verifier!)).toBe(
      authorize.searchParams.get('code_challenge')!,
    );
    expect(out.stderr).toContain(`  ${authorize.href}`);
    expect((await user.seen.page)!.status).toBe(200);
  });

  test.each([
    [
      'a different state',
      (): Record<string, string> => ({ code: 'c1', state: 'forged' }),
      400,
      'OAuth state mismatch — aborting (possible CSRF or a stale redirect).',
    ],
    [
      'an error',
      (): Record<string, string> => ({
        error: 'access_denied',
        error_description: 'The user said no',
      }),
      200,
      'Authorization failed: access_denied — The user said no',
    ],
    [
      'no code',
      (state: string): Record<string, string> => ({ state }),
      400,
      'Authorization redirect carried no code.',
    ],
  ])(
    'a redirect with %s fails without a token request',
    async (_, answer, page, message) => {
      const { fetch, requests } = provider();
      const user = browser(answer);

      const out = await pl(['login', '--browser', '--api-url', API], {
        fetch,
        openBrowser: user.open,
      });

      expect(out.code).toBe(1);
      expect(out.stderr).toContain(message);
      expect((await user.seen.page)!.status).toBe(page);
      expect(requests.some((r) => r.url === `${ID}/token`)).toBe(false);
    },
  );
});
