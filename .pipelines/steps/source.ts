import { steps } from '@olivervea/pipelines';
import { GITHUB_TOKEN, SOURCE } from '../config';

export const source = steps.gitHubSource({
  name: 'source',
  description: 'Fetches the pushed commit from GitHub.',
  token: GITHUB_TOKEN,
  apiUrl: SOURCE.apiUrl,
});
