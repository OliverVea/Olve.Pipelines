import { describe, expect, test } from 'bun:test';
import { join } from 'node:path';
import {
  authorizeUrl,
  codeChallenge,
  discover,
  exchangeCode,
  pollDevice,
  refresh,
  startDevice,
  type DeviceStart,
} from '../src/auth/oidc';
import { fakeFetch } from './helpers';

const recording = join(import.meta.dir, '../../../tools/recordings/dex/discovery.json');
const now = () => new Date('2026-10-28T12:00:00Z');
const TOKEN = 'https://id.example/token';

const failure = (promise: Promise<unknown>) =>
  promise.then(
    () => 'resolved',
    (error: Error) => error.message,
  );

const form = (body: unknown) => Object.fromEntries(new URLSearchParams(String(body)));

describe('discover', () => {
  test('reads the endpoints from what the pinned Dex serves', async () => {
    const { request, response } = await Bun.file(recording).json();
    const requested: string[] = [];
    const fetchFn = (async (url: string) => {
      requested.push(url);
      return Response.json(response.body, { status: response.status });
    }) as unknown as typeof fetch;

    const discovery = await discover(fetchFn, 'http://localhost:5556/dex/');

    expect(requested).toEqual([`http://localhost:5556${request.url}`]);
    expect(discovery).toEqual({
      authorizationEndpoint: 'http://localhost:5556/dex/auth',
      tokenEndpoint: 'http://localhost:5556/dex/token',
      deviceAuthorizationEndpoint: 'http://localhost:5556/dex/device/code',
    });
  });

  test.each([
    [404, { authorization_endpoint: 'a', token_endpoint: 't' }],
    [200, { authorization_endpoint: 'a' }],
  ])('a %i answer missing an endpoint fails naming the URL', async (status, body) => {
    const { fetch } = fakeFetch(() => ({ status, body }));

    expect(await failure(discover(fetch, 'https://id.example'))).toBe(
      `OIDC discovery at https://id.example/.well-known/openid-configuration failed (${status}): no authorization/token endpoint.`,
    );
  });

  test('an unreachable provider names the URL and the cause', async () => {
    const { fetch } = fakeFetch(() => new Error('ECONNREFUSED'));

    expect(await failure(discover(fetch, 'https://id.example'))).toBe(
      'Request to https://id.example/.well-known/openid-configuration failed: ECONNREFUSED',
    );
  });
});

describe('authorization code + PKCE', () => {
  // Computed with `openssl dgst -sha256 -binary | base64url`, not by the code under test.
  test('the challenge is the unpadded base64url SHA-256 of the verifier', () => {
    expect(codeChallenge('dBjftJeZ4CVP-mJ92K1xoTr3e0p4v1_Lo7k6nGjDXZ')).toBe(
      'J017IaR_e7GA4H2W2dyUk8MppD3fK4YKul8PPXVpuHQ',
    );
  });

  test('the authorization URL asks for a code with an S256 challenge', () => {
    const url = new URL(
      authorizeUrl(
        { authorizationEndpoint: 'https://id.example/auth', tokenEndpoint: TOKEN },
        'pl',
        'openid offline_access',
        'http://127.0.0.1:48650/callback',
        'challenge',
        'state-1',
      ),
    );

    expect(url.origin + url.pathname).toBe('https://id.example/auth');
    expect(Object.fromEntries(url.searchParams)).toEqual({
      response_type: 'code',
      client_id: 'pl',
      redirect_uri: 'http://127.0.0.1:48650/callback',
      scope: 'openid offline_access',
      state: 'state-1',
      code_challenge: 'challenge',
      code_challenge_method: 'S256',
    });
  });

  test('the code exchange sends the verifier, no secret, and dates the expiry from now', async () => {
    const { fetch, requests } = fakeFetch(() => ({
      body: { access_token: 'a', refresh_token: 'r', expires_in: 3600 },
    }));

    const tokens = await exchangeCode(
      fetch,
      TOKEN,
      'pl',
      'c1',
      'http://127.0.0.1:48650/callback',
      'v1',
      now,
    );

    expect(tokens).toEqual({
      accessToken: 'a',
      refreshToken: 'r',
      expiresAt: '2026-10-28T13:00:00.000Z',
    });
    expect(requests[0]!.method).toBe('POST');
    expect(requests[0]!.headers.get('content-type')).toBe(
      'application/x-www-form-urlencoded',
    );
    expect(form(requests[0]!.body)).toEqual({
      grant_type: 'authorization_code',
      code: 'c1',
      redirect_uri: 'http://127.0.0.1:48650/callback',
      client_id: 'pl',
      code_verifier: 'v1',
    });
  });
});

describe('refresh', () => {
  test('sends the refresh token; a reply without expiry or a new refresh token leaves them unset', async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: { access_token: 'a2' } }));

    const tokens = await refresh(fetch, TOKEN, 'pl', 'r1', now);

    expect(tokens).toEqual({
      accessToken: 'a2',
      refreshToken: undefined,
      expiresAt: undefined,
    });
    expect(form(requests[0]!.body)).toEqual({
      grant_type: 'refresh_token',
      refresh_token: 'r1',
      client_id: 'pl',
    });
  });

  test.each([
    [
      400,
      { error: 'invalid_grant', error_description: 'Token is revoked' },
      'Token is revoked',
    ],
    [400, { error: 'invalid_grant' }, 'invalid_grant'],
    [200, {}, 'no access_token'],
    [502, 'Bad Gateway', 'no access_token'],
    [400, { access_token: 'a', error: 'invalid_client' }, 'invalid_client'],
  ])('a %i answer of %j fails with its reason', async (status, body, reason) => {
    const { fetch } = fakeFetch(() => ({ status, body }));

    expect(await failure(refresh(fetch, TOKEN, 'pl', 'r1', now))).toBe(
      `Token request failed (${status}): ${reason}`,
    );
  });
});

describe('device grant', () => {
  const device: DeviceStart = {
    deviceCode: 'dc',
    userCode: 'ABCD-EFGH',
    verificationUri: 'https://id.example/device',
    interval: 5,
  };

  test('start sends the client and scope; the interval defaults to 5 s', async () => {
    const { fetch, requests } = fakeFetch(() => ({
      body: {
        device_code: 'dc',
        user_code: 'ABCD-EFGH',
        verification_uri: 'https://id.example/device',
      },
    }));

    const started = await startDevice(
      fetch,
      'https://id.example/device/code',
      'pl',
      'openid',
    );

    expect(started).toEqual({ ...device, verificationUriComplete: undefined });
    expect(form(requests[0]!.body)).toEqual({ client_id: 'pl', scope: 'openid' });
  });

  test.each([
    [
      400,
      { error: 'invalid_client', error_description: 'Unknown client' },
      'Unknown client',
    ],
    [200, { device_code: 'dc', verification_uri: 'u' }, 'no device_code/user_code'],
  ])('a %i start answer of %j fails with its reason', async (status, body, reason) => {
    const { fetch } = fakeFetch(() => ({ status, body }));

    expect(
      await failure(startDevice(fetch, 'https://id.example/device/code', 'pl', 'openid')),
    ).toBe(`Device authorization failed (${status}): ${reason}`);
  });

  const io = (sleeps: number[], signal = new AbortController().signal) => ({
    now,
    sleep: async (ms: number) => {
      sleeps.push(ms);
    },
    signal,
  });

  test('polls at the interval, backs off 5 s on slow_down, and returns the approved tokens', async () => {
    const answers: unknown[] = [
      { error: 'authorization_pending' },
      { error: 'slow_down' },
      { error: 'authorization_pending' },
      { access_token: 'a', expires_in: 60 },
    ];
    const { fetch, requests } = fakeFetch(() => ({ status: 400, body: answers.shift() }));
    const sleeps: number[] = [];

    const tokens = await pollDevice(fetch, TOKEN, 'pl', device, io(sleeps));

    expect(tokens.accessToken).toBe('a');
    expect(tokens.expiresAt).toBe('2026-10-28T12:01:00.000Z');
    expect(sleeps).toEqual([5000, 5000, 10000, 10000]);
    expect(form(requests[0]!.body)).toEqual({
      grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
      device_code: 'dc',
      client_id: 'pl',
    });
  });

  test('an interval under 1 s is polled every second', async () => {
    const { fetch } = fakeFetch(() => ({ body: { access_token: 'a' } }));
    const sleeps: number[] = [];

    await pollDevice(fetch, TOKEN, 'pl', { ...device, interval: 0 }, io(sleeps));

    expect(sleeps).toEqual([1000]);
  });

  test.each([
    [
      { error: 'expired_token' },
      'The code expired before the login was approved; run `pl login --device` again.',
    ],
    [{ error: 'access_denied' }, 'The login was denied.'],
    [
      { error: 'invalid_grant', error_description: 'Bad code' },
      'Device token poll failed (400): Bad code',
    ],
    [{}, 'Device token poll failed (400): no access_token'],
  ])('a poll answer of %j ends the login with %p', async (body, message) => {
    const { fetch } = fakeFetch(() => ({ status: 400, body }));

    expect(await failure(pollDevice(fetch, TOKEN, 'pl', device, io([])))).toBe(message);
  });

  test('a cancelled login stops before the next poll', async () => {
    const { fetch, requests } = fakeFetch(() => ({ body: { access_token: 'a' } }));
    const controller = new AbortController();
    controller.abort();

    expect(
      await failure(pollDevice(fetch, TOKEN, 'pl', device, io([], controller.signal))),
    ).toBe('Login cancelled.');
    expect(requests).toEqual([]);
  });
});
