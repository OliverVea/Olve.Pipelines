import { createClient, createConfig } from '@olve/api/client';
import { getAccessToken, login, devAuthBypass } from './auth.js';

/**
 * The client of the contract's operations (`@olve/api`, generated from src/spec): pass it as
 * `client` to every SDK call. Same origin, the bearer attached per request, and a non-2xx answer
 * thrown as {@link ApiError} with every problem message.
 */
export const api = createClient(createConfig({ baseUrl: '', throwOnError: true }));
api.interceptors.request.use(async (request) => {
  for (const [name, value] of Object.entries(await authHeaders()))
    request.headers.set(name, value);
  return request;
});
api.interceptors.response.use(async (response) => {
  if (response.ok) return response;
  const text = await response.text();
  let parsed: unknown = undefined;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = text;
  }
  const problems = problemsFromBody(parsed);
  if (!problems.length && response.status === 401)
    problems.push('Sign in to do this (HTTP 401).');
  if (!problems.length && response.status === 403)
    problems.push('Not allowed (HTTP 403).');
  throw new ApiError(response.status, problems);
});

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

/** Messages of an error body: `{ errors: [{ message, field? }] }`, or a bare problem array. */
function problemsFromBody(body: unknown): string[] {
  const errors =
    body && typeof body === 'object' ? (body as { errors?: unknown }).errors : undefined;
  if (Array.isArray(errors)) {
    return errors
      .map((e) => {
        const { message, field } = (e ?? {}) as { message?: unknown; field?: unknown };
        if (typeof message !== 'string' || !message) return '';
        return typeof field === 'string' && field ? `${field}: ${message}` : message;
      })
      .filter((m) => m.length > 0);
  }
  const list = Array.isArray(body)
    ? body
    : body && typeof body === 'object'
      ? [body]
      : [];
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
 * Server-sent events from an authenticated endpoint. `EventSource` can't send the bearer token, so this
 * reads the stream with fetch. Ends when the server closes the stream or `signal` aborts.
 */
export async function* apiEvents(
  path: string,
  signal: AbortSignal,
): AsyncGenerator<{ event: string; data: string }> {
  const res = await fetch(path, {
    headers: { ...(await authHeaders()), Accept: 'text/event-stream' },
    signal,
  });
  if (!res.ok || !res.body)
    throw new ApiError(res.status, [`Event stream answered HTTP ${res.status}.`]);
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) return;
      buffer += value;
      let end: number;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        let event = 'message';
        const data: string[] = [];
        for (const line of block.split('\n')) {
          if (line.startsWith('event:')) event = line.slice(6).trim();
          else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
        }
        if (data.length) yield { event, data: data.join('\n') };
      }
    }
  } finally {
    reader.cancel().catch(() => {});
  }
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

/** A thrown value as one message: an {@link ApiError}'s problems, an `Error`'s message, else its text. */
export function extractErrorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  if (err instanceof Error && err.message) return err.message;
  const s = String(err);
  return s === '[object Object]' ? 'An unexpected error occurred.' : s;
}

/** Problem messages of a thrown value, one per line item. */
export function errorMessages(err: unknown): string[] {
  if (err instanceof ApiError) return err.problems;
  return extractErrorMessage(err).split('\n');
}
