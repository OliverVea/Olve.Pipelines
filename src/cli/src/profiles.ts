/**
 * `~/.pl`: one profile per instance, with its login. The file is shared with every running `pl`
 * (saves go through a lock file beside it), owner-only, and keeps the shape the C# `pl` wrote:
 * `{ "profiles": { "<name>": { "apiUrl", "auth": { accessToken, refreshToken, tokenEndpoint,
 * clientId, expiresAt } } } }`; a file from before profiles existed reads as `default`.
 */
import {
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import type { Args } from './args';
import { CliError, describe } from './errors';
import { refresh, type TokenSet } from './auth/oidc';

export const DEFAULT_PROFILE = 'default';
export const DEFAULT_API_URL = 'https://pipelines-private.ovea.pro';
export const CONFIG_ENV = 'PIPELINES_CONFIG';
export const PROFILE_ENV = 'PIPELINES_PROFILE';
export const API_URL_ENV = 'PIPELINES_API_URL';
export const TOKEN_ENV = 'PIPELINES_API_TOKEN';

export type Env = Record<string, string | undefined>;

export type AuthConfig = {
  accessToken?: string | null;
  refreshToken?: string | null;
  tokenEndpoint?: string | null;
  clientId?: string | null;
  /** ISO timestamp. */
  expiresAt?: string | null;
};

export type Profile = {
  name: string;
  apiUrl?: string | null;
  auth?: AuthConfig | null;
};

type ProfileFile = {
  profiles?: Record<string, Omit<Profile, 'name'>>;
  apiUrl?: string | null;
  auth?: AuthConfig | null;
};

/** `$PIPELINES_CONFIG`, else `~/.pl`. */
export function configPath(env: Env): string {
  const override = env[CONFIG_ENV];
  if (override) return override;
  const home = env.HOME || env.USERPROFILE || '.';
  return join(home, '.pl');
}

/** `--profile`, else `$PIPELINES_PROFILE`, else `default`. */
export function resolveProfileName(args: Args | undefined, env: Env): string {
  return args?.option('profile') || env[PROFILE_ENV] || DEFAULT_PROFILE;
}

export function loadProfiles(path: string): Record<string, Profile> {
  let text: string;
  try {
    text = readFileSync(path, 'utf8');
  } catch (error) {
    if ((error as { code?: string }).code === 'ENOENT') return {};
    throw new CliError(`Could not read config '${path}': ${describe(error)}`);
  }
  let file: ProfileFile;
  try {
    file = JSON.parse(text.replace(/^﻿/, '')) as ProfileFile;
  } catch (error) {
    throw new CliError(`Could not read config '${path}': ${describe(error)}`);
  }
  const profiles: Record<string, Profile> = {};
  for (const [name, profile] of Object.entries(file?.profiles ?? {}))
    profiles[name] = { ...profile, name };
  if (!file?.profiles && (file?.apiUrl || file?.auth))
    profiles[DEFAULT_PROFILE] = {
      name: DEFAULT_PROFILE,
      apiUrl: file.apiUrl,
      auth: file.auth,
    };
  return profiles;
}

export function loadProfile(path: string, name: string): Profile {
  return loadProfiles(path)[name] ?? { name };
}

/** Writes one profile, keeping the others; owner-only, via a temp file and a lock. */
export function saveProfile(path: string, profile: Profile): void {
  withLock(path, () => saveLocked(path, profile));
}

function saveLocked(path: string, profile: Profile): void {
  const profiles = loadProfiles(path);
  profiles[profile.name] = profile;
  const file: ProfileFile = {
    profiles: Object.fromEntries(
      Object.entries(profiles).map(([name, { name: _, ...rest }]) => [
        name,
        { apiUrl: rest.apiUrl ?? null, auth: rest.auth ?? null },
      ]),
    ),
  };
  const temp = `${path}.${process.pid}.tmp`;
  try {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(temp, JSON.stringify(file, null, 2), { mode: 0o600 });
    renameSync(temp, path);
  } catch (error) {
    throw new CliError(`Could not write config '${path}': ${describe(error)}`);
  }
}

/** A lock file beside the config, held while reading and writing it; a lock older than a minute is stale. */
// A directory beside the config, created atomically; the C# pl's `~/.pl.lock` is a separate file.
const lockDir = (path: string) => `${path}.lock.d`;
const LOCK_STALE_MS = 60_000;
const LOCK_WAIT_MS = 10_000;

function tryLock(path: string): boolean {
  const dir = lockDir(path);
  try {
    mkdirSync(dirname(path), { recursive: true });
    mkdirSync(dir);
    return true;
  } catch (error) {
    if ((error as { code?: string }).code !== 'EEXIST')
      throw new CliError(`Could not lock config '${path}': ${describe(error)}`);
    try {
      // A pl that died holding it: take it over.
      if (Date.now() - statSync(dir).mtimeMs > LOCK_STALE_MS)
        rmSync(dir, { recursive: true, force: true });
    } catch {
      // Released meanwhile: the next try takes it.
    }
    return false;
  }
}

const unlock = (path: string) => rmSync(lockDir(path), { recursive: true, force: true });

const busy = (path: string) =>
  new CliError(`Could not lock config '${path}': another pl holds it.`);

/** Holds the config lock while `fn` runs (reads and writes of ~/.pl). */
export function withLock<T>(path: string, fn: () => T): T {
  const deadline = Date.now() + LOCK_WAIT_MS;
  while (!tryLock(path)) {
    if (Date.now() > deadline) throw busy(path);
    Bun.sleepSync(50);
  }
  try {
    return fn();
  } finally {
    unlock(path);
  }
}

/** {@link withLock} for work that awaits, e.g. a token refresh between the read and the save. */
export async function withLockAsync<T>(path: string, fn: () => Promise<T>): Promise<T> {
  const deadline = Date.now() + LOCK_WAIT_MS;
  while (!tryLock(path)) {
    if (Date.now() > deadline) throw busy(path);
    await Bun.sleep(50);
  }
  try {
    return await fn();
  } finally {
    unlock(path);
  }
}

const SKEW_MS = 60_000;
const RETRY_AFTER_FAILURE_MS = 60_000;
let lastRefreshFailure = 0;

export const isRefreshable = (
  auth: AuthConfig | null | undefined,
): auth is AuthConfig & {
  refreshToken: string;
  tokenEndpoint: string;
  clientId: string;
} => !!auth?.refreshToken && !!auth.tokenEndpoint && !!auth.clientId;

export const isExpiring = (auth: AuthConfig, now: Date): boolean =>
  !auth.expiresAt || Date.parse(auth.expiresAt) <= now.getTime() + SKEW_MS;

/**
 * Refreshes the profile's access token when it has expired or is about to, saving the new one.
 * Another `pl` may have refreshed first (its refresh token replaced ours by rotation), so the file
 * is re-read under the lock. Best-effort: a refused refresh is logged and retried a minute later.
 */
export async function refreshIfExpiring(
  path: string,
  profile: Profile,
  io: { fetch: typeof fetch; log(message: string): void; now(): Date },
): Promise<void> {
  if (
    !isRefreshable(profile.auth) ||
    !isExpiring(profile.auth, io.now()) ||
    Date.now() - lastRefreshFailure < RETRY_AFTER_FAILURE_MS
  )
    return;
  try {
    // Held across read, refresh and save: the refresh token rotates, so a second pl refreshing
    // with the old one at the same time would fail, or revoke the session.
    await withLockAsync(path, async () => {
      const current = loadProfile(path, profile.name);
      if (current.auth) profile.auth = current.auth;
      const auth = profile.auth;
      if (!isRefreshable(auth)) return;
      if (!isExpiring(auth, io.now())) {
        io.log('access token already refreshed by another pl process');
        return;
      }

      io.log('access token expired or expiring — refreshing via refresh_token');
      let renewed: TokenSet;
      try {
        renewed = await refresh(
          io.fetch,
          auth.tokenEndpoint,
          auth.clientId,
          auth.refreshToken,
          io.now,
        );
      } catch (error) {
        lastRefreshFailure = Date.now();
        io.log(`token refresh failed (continuing with stale token): ${describe(error)}`);
        return;
      }
      profile.auth = {
        ...auth,
        accessToken: renewed.accessToken,
        refreshToken: renewed.refreshToken || auth.refreshToken,
        expiresAt: renewed.expiresAt ?? null,
      };
      try {
        saveLocked(path, profile);
        io.log('access token refreshed');
      } catch (error) {
        io.log(`refreshed token but could not persist it: ${describe(error)}`);
      }
    });
  } catch (error) {
    io.log(`token refresh skipped: ${describe(error)}`);
  }
}

export function resolveApiUrl(
  args: Args | undefined,
  env: Env,
  profile: Profile,
): { url: string; source: string } {
  const flag = args?.option('api-url');
  if (flag) return { url: flag, source: '--api-url' };
  if (env[API_URL_ENV]) return { url: env[API_URL_ENV]!, source: API_URL_ENV };
  if (profile.apiUrl)
    return { url: profile.apiUrl, source: `~/.pl profile '${profile.name}'` };
  return { url: DEFAULT_API_URL, source: 'default' };
}

/** `--token`, `$PIPELINES_API_TOKEN`, or the profile's login when it is for the same server. */
export function resolveToken(
  args: Args | undefined,
  env: Env,
  profile: Profile,
  baseUrl: string,
): { token?: string; source: string; owned: boolean } {
  const flag = args?.option('token');
  if (flag) return { token: flag, source: '--token', owned: false };
  if (env[TOKEN_ENV]) return { token: env[TOKEN_ENV], source: TOKEN_ENV, owned: false };
  if (profile.auth?.accessToken) {
    const profileUrl = profile.apiUrl || DEFAULT_API_URL;
    return sameServer(baseUrl, profileUrl)
      ? {
          token: profile.auth.accessToken,
          source: `~/.pl profile '${profile.name}'`,
          owned: true,
        }
      : {
          source: `none (profile '${profile.name}' is logged in to ${profileUrl})`,
          owned: true,
        };
  }
  return { source: 'none', owned: true };
}

function sameServer(a: string, b: string): boolean {
  try {
    const x = new URL(a);
    const y = new URL(b);
    return x.protocol === y.protocol && x.host.toLowerCase() === y.host.toLowerCase();
  } catch {
    return false;
  }
}
