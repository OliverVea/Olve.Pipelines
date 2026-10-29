import { createClient, createConfig, type Client } from '@olve/api/client';

export type ApiClientSettings = {
  /** The server's origin; `''` for the page's own. */
  baseUrl: string;
  /** The bearer for each request, resolved per request so a refreshed token is picked up. */
  token: () => Promise<string | null | undefined>;
  fetch?: typeof fetch;
  headers?: Record<string, string>;
  /** A non-2xx answer throws an {@link ApiError}; otherwise it is the call's `error`. */
  throwOnError?: boolean;
};

/** The client of the contract's operations (`@olve/api`): pass it as `client` to every call. */
export function createApiClient(settings: ApiClientSettings): Client {
  const client = createClient(
    createConfig({
      baseUrl: settings.baseUrl.replace(/\/+$/, ''),
      fetch: settings.fetch,
      headers: settings.headers,
      throwOnError: settings.throwOnError ?? false,
    }),
  );
  client.interceptors.request.use(async (request) => {
    const token = await settings.token();
    if (token && !request.headers.has('Authorization'))
      request.headers.set('Authorization', `Bearer ${token}`);
    return request;
  });
  if (settings.throwOnError)
    client.interceptors.response.use(async (response) => {
      if (!response.ok) throw await ApiError.from(response);
      return response;
    });
  return client;
}

/** A non-2xx answer: its status and the server's problem messages. */
export class ApiError extends Error {
  readonly status: number;
  readonly statusText: string;
  /** One per server problem (`field: message`); empty when the body had none. */
  readonly problems: string[];
  /** The parsed body: JSON, else its text. */
  readonly body: unknown;

  constructor(status: number, statusText: string, body: unknown) {
    const problems = serverProblems(body);
    super(problems.length ? problems.join('\n') : `Request failed (HTTP ${status}).`);
    this.name = 'ApiError';
    this.status = status;
    this.statusText = statusText;
    this.problems = problems;
    this.body = body;
  }

  static async from(response: Response): Promise<ApiError> {
    const text = await response.text().catch(() => '');
    let body: unknown = text;
    try {
      body = text ? JSON.parse(text) : '';
    } catch {
      body = text;
    }
    return new ApiError(response.status, response.statusText, body);
  }
}

/** Messages of an error body: `{ errors: [{ message, field? }] }` (an ApiProblem), or a bare problem list. */
export function serverProblems(body: unknown): string[] {
  const errors =
    body && typeof body === 'object' ? (body as { errors?: unknown }).errors : undefined;
  const list = Array.isArray(errors)
    ? errors
    : Array.isArray(body)
      ? body
      : body && typeof body === 'object'
        ? [body]
        : [];
  return list
    .map((p) => {
      if (typeof p === 'string') return p;
      const o = (p ?? {}) as Record<string, unknown>;
      const message = o.message ?? o.detail ?? o.title;
      if (typeof message !== 'string' || !message) return '';
      return typeof o.field === 'string' && o.field ? `${o.field}: ${message}` : message;
    })
    .filter((m) => m.length > 0);
}
