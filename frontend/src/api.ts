import {
  AllowedHostsValidator,
  BaseBearerTokenAuthenticationProvider,
  type AccessTokenProvider,
} from '@microsoft/kiota-abstractions';
import { FetchRequestAdapter } from '@microsoft/kiota-http-fetchlibrary';
import { createApiClient } from '@olve/olve-pipelines-client/src/apiClient.js';
import { getAccessToken, login, devAuthBypass } from './auth.js';

const allowAllHosts = new AllowedHostsValidator();

const tokenProvider: AccessTokenProvider = {
  getAuthorizationToken: async () => {
    const token = await getAccessToken();
    if (!token) {
      if (!devAuthBypass) await login();
      return '';
    }
    return token;
  },
  getAllowedHostsValidator: () => allowAllHosts,
};

const authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
const adapter = new FetchRequestAdapter(authProvider);
adapter.baseUrl = '';

/**
 * Kiota client — used for the typed reads. Mutations and the untyped/binary
 * endpoints go through {@link apiFetch} instead: Kiota maps a 400 to a single
 * `ResultProblem` while the API returns an array (all messages are lost), sends
 * a `JsonElement` body via `setContentFromScalar` (fails for objects), and would
 * escape `/` in the `{*path}` catch-all of file downloads.
 */
export const client = createApiClient(adapter);

/** A failed API call, carrying every server-supplied problem message. */
export class ApiError extends Error {
  readonly status: number;
  readonly problems: string[];
  constructor(status: number, problems: string[]) {
    super(problems.length ? problems.join('\n') : `Request failed (HTTP ${status}).`);
    this.status = status;
    this.problems = problems.length ? problems : [`Request failed (HTTP ${status}).`];
  }
}

function problemsFromBody(body: unknown): string[] {
  const list = Array.isArray(body) ? body : body && typeof body === 'object' ? [body] : [];
  return list
    .map((p) => {
      if (p && typeof p === 'object') {
        const o = p as { message?: unknown; title?: unknown; detail?: unknown };
        const m = o.message ?? o.detail ?? o.title;
        return typeof m === 'string' ? m : '';
      }
      return typeof p === 'string' ? p : '';
    })
    .filter((m) => m.length > 0);
}

async function authHeaders(): Promise<Record<string, string>> {
  const token = await getAccessToken();
  if (!token && !devAuthBypass) await login();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** Encode each segment of a path separately (keeps `/` for catch-all routes). */
export function encodePath(path: string): string {
  return path.split('/').map(encodeURIComponent).join('/');
}

/**
 * Plain fetch against the API with the bearer token. Throws {@link ApiError}
 * with every `ResultProblem` message on a non-2xx response. Returns the parsed
 * JSON body (or `undefined` for an empty body).
 */
export async function apiFetch<T = unknown>(
  path: string,
  init: { method?: string; body?: unknown } = {},
): Promise<T> {
  const headers: Record<string, string> = { ...(await authHeaders()), Accept: 'application/json' };
  let body: string | undefined;
  if (init.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(init.body);
  }
  const res = await fetch(path, { method: init.method ?? 'GET', headers, body });
  const text = await res.text();
  let parsed: unknown = undefined;
  if (text.length) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = text;
    }
  }
  if (!res.ok) {
    const problems = problemsFromBody(parsed);
    if (!problems.length && res.status === 401) problems.push('Sign in to do this (HTTP 401).');
    if (!problems.length && res.status === 403) problems.push('Not allowed (HTTP 403).');
    throw new ApiError(res.status, problems);
  }
  return parsed as T;
}

/** POST with a JSON body (or none). */
export function apiPost<T = unknown>(path: string, body?: unknown): Promise<T> {
  return apiFetch<T>(path, { method: 'POST', body });
}

/** Fetch an authenticated file and hand it to the browser as a download. */
export async function downloadFile(url: string, fileName: string): Promise<void> {
  const res = await fetch(url, { headers: await authHeaders() });
  if (!res.ok) {
    let parsed: unknown;
    try {
      parsed = await res.json();
    } catch {
      parsed = undefined;
    }
    throw new ApiError(res.status, problemsFromBody(parsed));
  }
  const blob = await res.blob();
  const objectUrl = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = objectUrl;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
}

/** Text endpoint (logs are declared as a JSON string but served as text). */
export async function apiText(path: string): Promise<string> {
  const res = await fetch(path, { headers: await authHeaders() });
  const text = await res.text();
  if (!res.ok) {
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = undefined;
    }
    throw new ApiError(res.status, problemsFromBody(parsed));
  }
  // Tolerate a JSON-encoded string body.
  if (text.startsWith('"')) {
    try {
      const s = JSON.parse(text);
      if (typeof s === 'string') return s;
    } catch {
      /* raw text */
    }
  }
  return text;
}

/**
 * Convert a Kiota `UntypedNode` (or any value that may contain them) to plain
 * JSON. `JsonElement` members of the API surface as UntypedNodes.
 */
export function untypedToJson(node: unknown): unknown {
  if (node === null || node === undefined) return node ?? null;
  if (typeof node === 'object' && 'getValue' in node && typeof (node as { getValue: unknown }).getValue === 'function') {
    return untypedToJson((node as { getValue: () => unknown }).getValue());
  }
  if (Array.isArray(node)) return node.map(untypedToJson);
  if (typeof node === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(node as Record<string, unknown>)) out[k] = untypedToJson(v);
    return out;
  }
  return node;
}

/** Numeric map from a Kiota additionalData holder (`stepVersions` etc.). */
export function numberMap(holder: { additionalData?: Record<string, unknown> } | null | undefined): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [k, v] of Object.entries(holder?.additionalData ?? {})) {
    const n = typeof v === 'number' ? v : Number(v);
    if (!Number.isNaN(n)) out[k] = n;
  }
  return out;
}

/**
 * Turn a thrown value into a human-readable message.
 *
 * Kiota does not throw `Error` instances for API failures: on a mapped error
 * status it deserializes the response body into a plain object (our
 * `ResultProblem`) and throws that. The API returns an *array* of problems, which
 * Kiota parses into `additionalData` keyed "0", "1", …; pull messages from there
 * too. Kiota renames the wire `message` field to `messageEscaped`.
 */
export function extractErrorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  if (err && typeof err === 'object') {
    const p = err as {
      messageEscaped?: unknown;
      message?: unknown;
      exceptionSummary?: unknown;
      additionalData?: Record<string, unknown>;
      responseStatusCode?: unknown;
    };
    const fromArray = problemsFromBody(Object.values(p.additionalData ?? {}));
    if (fromArray.length) return fromArray.join('\n');
    if (p.responseStatusCode === 401) return 'Sign in to see this (HTTP 401).';
    if (p.responseStatusCode === 403) return 'Not allowed (HTTP 403).';
    const candidate = p.messageEscaped ?? p.message ?? p.exceptionSummary;
    if (typeof candidate === 'string' && candidate.length > 0) return candidate;
    if (typeof p.responseStatusCode === 'number') return `Request failed (HTTP ${p.responseStatusCode}).`;
  }
  if (err instanceof Error && err.message) return err.message;
  const s = String(err);
  return s === '[object Object]' ? 'An unexpected error occurred.' : s;
}

/** Problem messages of a thrown value, one per line item. */
export function errorMessages(err: unknown): string[] {
  if (err instanceof ApiError) return err.problems;
  return extractErrorMessage(err).split('\n');
}
