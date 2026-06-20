import {
  AllowedHostsValidator,
  BaseBearerTokenAuthenticationProvider,
  type AccessTokenProvider,
} from '@microsoft/kiota-abstractions';
import { FetchRequestAdapter } from '@microsoft/kiota-http-fetchlibrary';
import { createApiClient } from '@olve/olve-pipelines-client/src/apiClient.js';
import { getAccessToken, login } from './auth.js';

const allowAllHosts = new AllowedHostsValidator();

const tokenProvider: AccessTokenProvider = {
  getAuthorizationToken: async () => {
    const token = await getAccessToken();
    if (!token) {
      await login();
      return '';
    }
    return token;
  },
  getAllowedHostsValidator: () => allowAllHosts,
};

const authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
const adapter = new FetchRequestAdapter(authProvider);
adapter.baseUrl = '';

export const client = createApiClient(adapter);

/**
 * Turn a thrown value into a human-readable message.
 *
 * Kiota does not throw `Error` instances for API failures: on a mapped error
 * status it deserializes the response body into a plain object (our
 * `ResultProblem`) and throws that. Such objects stringify to `[object Object]`,
 * so a naive `String(err)` shows nothing useful. Pull the server-supplied text
 * out of the known `ResultProblem` shape first (Kiota renames the wire `message`
 * field to `messageEscaped` to avoid colliding with `Error.message`), then fall
 * back to `Error.message`, then to a string.
 */
export function extractErrorMessage(err: unknown): string {
  if (err && typeof err === 'object') {
    const p = err as {
      messageEscaped?: unknown;
      message?: unknown;
      exceptionSummary?: unknown;
    };
    const candidate = p.messageEscaped ?? p.message ?? p.exceptionSummary;
    if (typeof candidate === 'string' && candidate.length > 0) return candidate;
  }
  if (err instanceof Error && err.message) return err.message;
  const s = String(err);
  return s === '[object Object]' ? 'An unexpected error occurred.' : s;
}
