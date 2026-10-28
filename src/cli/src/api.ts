import { createClient, createConfig, type Client } from '@olve/api/client';
import { CliError, describe } from './errors';

export type ApiSettings = {
  baseUrl: string;
  /** The bearer for each request; resolved per request so a refresh mid-command is picked up. */
  token: () => Promise<string | undefined>;
  fetch: typeof fetch;
  log(message: string): void;
};

/** The contract's client for one invocation: the bearer attached per request, requests logged with `--verbose`. */
export function createApi(settings: ApiSettings): Client {
  const client = createClient(
    createConfig({
      baseUrl: settings.baseUrl.replace(/\/+$/, ''),
      headers: { Accept: 'application/json' },
      fetch: settings.fetch,
      throwOnError: false,
    }),
  );
  client.interceptors.request.use(async (request) => {
    const token = await settings.token();
    if (token && !request.headers.has('Authorization'))
      request.headers.set('Authorization', `Bearer ${token}`);
    settings.log(`${request.method} ${new URL(request.url).pathname}`);
    return request;
  });
  client.interceptors.response.use((response) => {
    settings.log(`-> ${response.status} ${response.statusText}`);
    return response;
  });
  return client;
}

type SdkResult<T> = { data?: T; error?: unknown; request?: Request; response?: Response };

/**
 * Unwraps a contract call: the data on 2xx; a {@link CliError} with one problem per server error
 * (`field: message`) otherwise, retryable for a 5xx, a 429 or no response at all.
 */
export async function call<T>(promise: Promise<SdkResult<T>>): Promise<T> {
  const result = await promise;
  if (!result.response) {
    const path = result.request ? new URL(result.request.url).pathname : '(unknown)';
    throw new CliError(`Request to ${path} failed: ${describe(result.error)}`, true);
  }
  if (result.response.ok) return result.data as T;
  throw apiError(result.response.status, result.response.statusText, result.error);
}

/** The problems of a non-2xx answer, in the C# `pl`'s words. */
export function apiError(status: number, statusText: string, body: unknown): CliError {
  if (status === 401)
    return new CliError(`Not authenticated (${status}). Run \`pl login\`.`);
  if (status === 403)
    return new CliError(`Forbidden (${status}): this account may not do that.`);
  const retryable = status >= 500 || status === 429;
  const problems = serverProblems(body);
  if (problems.length) return new CliError(problems, retryable);
  const text = typeof body === 'string' ? body : body ? JSON.stringify(body) : '';
  const detail = text.trim() ? ` — ${snippet(text)}` : '';
  return new CliError(
    `Request failed: ${status} ${statusText}${detail}`.trimEnd(),
    retryable,
  );
}

/** `{ errors: [{ message, field? }] }` (an ApiProblem), or a bare problem array. */
export function serverProblems(body: unknown): string[] {
  const list = Array.isArray(body)
    ? body
    : body &&
        typeof body === 'object' &&
        Array.isArray((body as { errors?: unknown }).errors)
      ? ((body as { errors: unknown[] }).errors as unknown[])
      : [];
  return list
    .map((p) => {
      const { message, field } = (p ?? {}) as { message?: unknown; field?: unknown };
      if (typeof message !== 'string' || !message) return '';
      return typeof field === 'string' && field ? `${field}: ${message}` : message;
    })
    .filter((m) => m.length > 0);
}

function snippet(body: string): string {
  const line = body.trim().replace(/\s+/g, ' ');
  return line.length <= 200 ? line : `${line.slice(0, 199)}…`;
}
