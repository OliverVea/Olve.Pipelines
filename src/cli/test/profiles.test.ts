import { describe, expect, test } from 'bun:test';
import { mkdtempSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { loadProfiles, refreshIfExpiring, saveProfile } from '../src/profiles';
import { fakeFetch, pl } from './helpers';

const home = () => mkdtempSync(join(tmpdir(), 'pl-profiles-'));

describe('~/.pl', () => {
  test('a file from before profiles reads as default and is rewritten with profiles', () => {
    const path = join(home(), '.pl');
    writeFileSync(path, '﻿{"apiUrl":"https://x","auth":{"accessToken":"a"}}');
    const profiles = loadProfiles(path);
    expect(profiles.default?.apiUrl).toBe('https://x');
    saveProfile(path, { ...profiles.default!, apiUrl: 'https://y' });
    const written = JSON.parse(readFileSync(path, 'utf8'));
    expect(Object.keys(written)).toEqual(['profiles']);
    expect(written.profiles.default.apiUrl).toBe('https://y');
    expect(written.profiles.default.auth.accessToken).toBe('a');
  });

  test('saving one profile keeps the others, owner-only', () => {
    const path = join(home(), '.pl');
    saveProfile(path, { name: 'default', apiUrl: 'https://prod' });
    saveProfile(path, { name: 'beta', apiUrl: 'https://beta' });
    const profiles = loadProfiles(path);
    expect(Object.keys(profiles).sort()).toEqual(['beta', 'default']);
    expect(statSync(path).mode & 0o777).toBe(0o600);
  });

  test('--profile beats PIPELINES_PROFILE beats default; the profile picks the instance and token', async () => {
    const dir = home();
    saveProfile(join(dir, '.pl'), {
      name: 'beta',
      apiUrl: 'https://beta.example',
      auth: { accessToken: 'bt', expiresAt: '2027-01-01T00:00:00Z' },
    });
    saveProfile(join(dir, '.pl'), {
      name: 'default',
      apiUrl: 'https://prod.example',
      auth: { accessToken: 'pt', expiresAt: '2027-01-01T00:00:00Z' },
    });
    const { fetch, requests } = fakeFetch(() => ({ body: [] }));
    await pl(['pipeline', 'list', '--profile', 'beta'], {
      fetch,
      home: dir,
      env: { PIPELINES_PROFILE: 'default' },
    });
    expect(requests[0]!.url).toBe('https://beta.example/api/pipelines');
    expect(requests[0]!.headers.get('Authorization')).toBe('Bearer bt');
    await pl(['pipeline', 'list'], { fetch, home: dir });
    expect(requests[1]!.url).toBe('https://prod.example/api/pipelines');
    expect(requests[1]!.headers.get('Authorization')).toBe('Bearer pt');
  });

  test('profile list shows the instance, the login and the selected one', async () => {
    const dir = home();
    saveProfile(join(dir, '.pl'), {
      name: 'beta',
      apiUrl: 'https://beta.example',
      auth: { accessToken: 'bt', expiresAt: '2027-01-01T00:00:00Z' },
    });
    const result = await pl(['profile', 'list', '--profile', 'beta'], { home: dir });
    expect(result.code).toBe(0);
    expect(result.stdout).toContain('* beta');
    expect(result.stdout).toContain('yes, token until 2027-01-01 00:00:00Z');
  });

  test('an expiring token is refreshed and saved; a rotated refresh token is kept', async () => {
    const path = join(home(), '.pl');
    saveProfile(path, {
      name: 'default',
      apiUrl: 'https://prod.example',
      auth: {
        accessToken: 'old',
        refreshToken: 'r1',
        tokenEndpoint: 'https://idp/token',
        clientId: 'pl',
        expiresAt: '2026-10-28T12:00:30Z',
      },
    });
    const { fetch, requests } = fakeFetch(() => ({
      body: { access_token: 'new', refresh_token: 'r2', expires_in: 3600 },
    }));
    const profile = loadProfiles(path).default!;
    await refreshIfExpiring(path, profile, {
      fetch,
      log: () => {},
      now: () => new Date('2026-10-28T12:00:00Z'),
    });
    expect(String(requests[0]!.body)).toContain('grant_type=refresh_token');
    expect(profile.auth?.accessToken).toBe('new');
    expect(loadProfiles(path).default?.auth?.refreshToken).toBe('r2');
    expect(loadProfiles(path).default?.auth?.expiresAt).toBe('2026-10-28T13:00:00.000Z');
  });

  test('a token another pl already refreshed is adopted instead of refreshing with its rotated predecessor', async () => {
    const path = join(home(), '.pl');
    const stale = {
      accessToken: 'old',
      refreshToken: 'r1',
      tokenEndpoint: 'https://idp/token',
      clientId: 'pl',
      expiresAt: '2026-10-28T12:00:30Z',
    };
    saveProfile(path, { name: 'default', apiUrl: 'https://prod.example', auth: stale });
    const profile = loadProfiles(path).default!;
    // Another pl refreshes meanwhile: r1 is spent, r2 is current.
    saveProfile(path, {
      name: 'default',
      apiUrl: 'https://prod.example',
      auth: {
        ...stale,
        accessToken: 'theirs',
        refreshToken: 'r2',
        expiresAt: '2026-10-28T13:00:00Z',
      },
    });
    const { fetch, requests } = fakeFetch(() => ({ body: { access_token: 'x' } }));
    await refreshIfExpiring(path, profile, {
      fetch,
      log: () => {},
      now: () => new Date('2026-10-28T12:00:00Z'),
    });
    expect(requests).toHaveLength(0);
    expect(profile.auth?.accessToken).toBe('theirs');
    expect(profile.auth?.refreshToken).toBe('r2');
  });

  test("two refreshes at once refresh once; the second adopts the first one's token", async () => {
    const path = join(home(), '.pl');
    saveProfile(path, {
      name: 'default',
      apiUrl: 'https://prod.example',
      auth: {
        accessToken: 'old',
        refreshToken: 'r1',
        tokenEndpoint: 'https://idp/token',
        clientId: 'pl',
        expiresAt: '2026-10-28T12:00:30Z',
      },
    });
    const { fetch, requests } = fakeFetch(() => ({
      body: { access_token: 'new', refresh_token: 'r2', expires_in: 3600 },
    }));
    const io = { fetch, log: () => {}, now: () => new Date('2026-10-28T12:00:00Z') };
    const a = loadProfiles(path).default!;
    const b = loadProfiles(path).default!;
    await Promise.all([refreshIfExpiring(path, a, io), refreshIfExpiring(path, b, io)]);
    expect(requests).toHaveLength(1);
    expect(a.auth?.refreshToken).toBe('r2');
    expect(b.auth?.refreshToken).toBe('r2');
  });
});
