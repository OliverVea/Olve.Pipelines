import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions';
import { FetchRequestAdapter } from '@microsoft/kiota-http-fetchlibrary';
import { createOlvePipelinesClient } from '@olve/olve-pipelines-client/src/olvePipelinesClient.js';

const authProvider = new AnonymousAuthenticationProvider();
const adapter = new FetchRequestAdapter(authProvider);
adapter.baseUrl = '';

export const client = createOlvePipelinesClient(adapter);
