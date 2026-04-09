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
