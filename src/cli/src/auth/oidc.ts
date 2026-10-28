/**
 * The OIDC pieces `pl login` needs: discovery, the authorization URL (code + PKCE), the code
 * exchange, refresh and the device grant (RFC 8628). Public client only: no secret is ever sent.
 */
import { createHash, randomBytes } from 'node:crypto';
import { CliError } from '../errors';

export type Discovery = {
  authorizationEndpoint: string;
  tokenEndpoint: string;
  deviceAuthorizationEndpoint?: string;
};

export type TokenSet = {
  accessToken: string;
  refreshToken?: string;
  /** ISO timestamp. */
  expiresAt?: string;
};

export type DeviceStart = {
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  verificationUriComplete?: string;
  interval: number;
};

export const base64Url = (bytes: Buffer): string => bytes.toString('base64url');
export const createCodeVerifier = (): string => base64Url(randomBytes(32));
export const codeChallenge = (verifier: string): string =>
  base64Url(createHash('sha256').update(verifier, 'ascii').digest());
export const createState = (): string => base64Url(randomBytes(16));

const str = (value: unknown): string | undefined =>
  typeof value === 'string' && value.length > 0 ? value : undefined;

async function json(
  fetchFn: typeof fetch,
  url: string,
  init?: RequestInit,
): Promise<{ status: number; body: Record<string, unknown> }> {
  let response: Response;
  try {
    response = await fetchFn(url, init);
  } catch (error) {
    throw new CliError(
      `Request to ${url} failed: ${error instanceof Error ? error.message : String(error)}`,
      true,
    );
  }
  const text = await response.text();
  let body: unknown = {};
  try {
    body = text ? JSON.parse(text) : {};
  } catch {
    body = {};
  }
  return {
    status: response.status,
    body: body && typeof body === 'object' ? (body as Record<string, unknown>) : {},
  };
}

export async function discover(
  fetchFn: typeof fetch,
  authority: string,
): Promise<Discovery> {
  const url = `${authority.replace(/\/+$/, '')}/.well-known/openid-configuration`;
  const { status, body } = await json(fetchFn, url);
  const authorizationEndpoint = str(body.authorization_endpoint);
  const tokenEndpoint = str(body.token_endpoint);
  if (status < 200 || status >= 300 || !authorizationEndpoint || !tokenEndpoint)
    throw new CliError(
      `OIDC discovery at ${url} failed (${status}): no authorization/token endpoint.`,
    );
  return {
    authorizationEndpoint,
    tokenEndpoint,
    deviceAuthorizationEndpoint: str(body.device_authorization_endpoint),
  };
}

export function authorizeUrl(
  discovery: Discovery,
  clientId: string,
  scope: string,
  redirectUri: string,
  challenge: string,
  state: string,
): string {
  const query = new URLSearchParams({
    response_type: 'code',
    client_id: clientId,
    redirect_uri: redirectUri,
    scope,
    state,
    code_challenge: challenge,
    code_challenge_method: 'S256',
  });
  return `${discovery.authorizationEndpoint}?${query}`;
}

function form(values: Record<string, string>): RequestInit {
  return {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(values),
  };
}

function tokens(body: Record<string, unknown>, now: Date): TokenSet | undefined {
  const accessToken = str(body.access_token);
  if (!accessToken) return undefined;
  const expiresIn = typeof body.expires_in === 'number' ? body.expires_in : undefined;
  return {
    accessToken,
    refreshToken: str(body.refresh_token),
    expiresAt:
      expiresIn !== undefined
        ? new Date(now.getTime() + expiresIn * 1000).toISOString()
        : undefined,
  };
}

async function tokenRequest(
  fetchFn: typeof fetch,
  endpoint: string,
  values: Record<string, string>,
  now: () => Date,
): Promise<TokenSet> {
  const { status, body } = await json(fetchFn, endpoint, form(values));
  const set = tokens(body, now());
  if (status < 200 || status >= 300 || !set) {
    const detail = str(body.error_description) ?? str(body.error) ?? 'no access_token';
    throw new CliError(`Token request failed (${status}): ${detail}`);
  }
  return set;
}

export const exchangeCode = (
  fetchFn: typeof fetch,
  endpoint: string,
  clientId: string,
  code: string,
  redirectUri: string,
  verifier: string,
  now: () => Date,
): Promise<TokenSet> =>
  tokenRequest(
    fetchFn,
    endpoint,
    {
      grant_type: 'authorization_code',
      code,
      redirect_uri: redirectUri,
      client_id: clientId,
      code_verifier: verifier,
    },
    now,
  );

export const refresh = (
  fetchFn: typeof fetch,
  endpoint: string,
  clientId: string,
  refreshToken: string,
  now: () => Date,
): Promise<TokenSet> =>
  tokenRequest(
    fetchFn,
    endpoint,
    { grant_type: 'refresh_token', refresh_token: refreshToken, client_id: clientId },
    now,
  );

export async function startDevice(
  fetchFn: typeof fetch,
  endpoint: string,
  clientId: string,
  scope: string,
): Promise<DeviceStart> {
  const { status, body } = await json(
    fetchFn,
    endpoint,
    form({ client_id: clientId, scope }),
  );
  const deviceCode = str(body.device_code);
  const userCode = str(body.user_code);
  const verificationUri = str(body.verification_uri);
  if (status < 200 || status >= 300 || !deviceCode || !userCode || !verificationUri) {
    const detail =
      str(body.error_description) ?? str(body.error) ?? 'no device_code/user_code';
    throw new CliError(`Device authorization failed (${status}): ${detail}`);
  }
  return {
    deviceCode,
    userCode,
    verificationUri,
    verificationUriComplete: str(body.verification_uri_complete),
    interval: typeof body.interval === 'number' ? body.interval : 5,
  };
}

/** Polls until the user approves (or denies, or the code expires); honours `slow_down`. */
export async function pollDevice(
  fetchFn: typeof fetch,
  endpoint: string,
  clientId: string,
  device: DeviceStart,
  io: { now(): Date; sleep(ms: number): Promise<void>; signal: AbortSignal },
): Promise<TokenSet> {
  let interval = Math.max(1, device.interval);
  for (;;) {
    await io.sleep(interval * 1000);
    if (io.signal.aborted) throw new CliError('Login cancelled.');
    const { status, body } = await json(
      fetchFn,
      endpoint,
      form({
        grant_type: 'urn:ietf:params:oauth:grant-type:device_code',
        device_code: device.deviceCode,
        client_id: clientId,
      }),
    );
    const set = tokens(body, io.now());
    if (set) return set;
    switch (body.error) {
      case 'authorization_pending':
        continue;
      case 'slow_down':
        interval += 5;
        continue;
      case 'expired_token':
        throw new CliError(
          'The code expired before the login was approved; run `pl login --device` again.',
        );
      case 'access_denied':
        throw new CliError('The login was denied.');
      default:
        throw new CliError(
          `Device token poll failed (${status}): ${str(body.error_description) ?? str(body.error) ?? 'no access_token'}`,
        );
    }
  }
}
