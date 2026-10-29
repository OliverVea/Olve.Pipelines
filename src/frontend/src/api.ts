import { runStepsDownload } from '@olve/api';
import { ApiError, createApiClient } from '@olve/client';
import { getAccessToken, login, devAuthBypass } from './auth.js';

export { ApiError };

/**
 * The client of the contract's operations (`@olve/api`, generated from src/spec): pass it as
 * `client` to every SDK call. Same origin, the bearer attached per request (signing in first when
 * there is none), and a non-2xx answer thrown as {@link ApiError}.
 */
export const api = createApiClient({
  baseUrl: '',
  throwOnError: true,
  token: async () => {
    const token = await getAccessToken();
    if (!token && !devAuthBypass) await login();
    return token;
  },
});

/** One stored file of an attempt, handed to the browser as a download. */
export async function downloadFile(
  file: {
    pipelineId: string;
    number: number;
    step: string;
    attempt: number;
    path: string;
  },
  fileName: string,
): Promise<void> {
  const { pipelineId, number, step, attempt, path } = file;
  const { data } = await runStepsDownload({
    client: api,
    path: { pipelineId, number, step, path },
    query: { attempt },
    parseAs: 'blob',
    throwOnError: true,
  });
  const objectUrl = URL.createObjectURL(data);
  const a = document.createElement('a');
  a.href = objectUrl;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
}

/** A thrown value as one message: an {@link ApiError}'s problems, an `Error`'s message, else its text. */
export function extractErrorMessage(err: unknown): string {
  return errorMessages(err).join('\n');
}

/** Problem messages of a thrown value, one per line item. */
export function errorMessages(err: unknown): string[] {
  if (err instanceof ApiError) {
    if (err.problems.length) return err.problems;
    if (err.status === 401) return ['Sign in to do this (HTTP 401).'];
    if (err.status === 403) return ['Not allowed (HTTP 403).'];
    return [err.message];
  }
  if (err instanceof Error && err.message) return err.message.split('\n');
  const s = String(err);
  return [s === '[object Object]' ? 'An unexpected error occurred.' : s];
}
