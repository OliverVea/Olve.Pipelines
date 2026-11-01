import { secret } from '@olivervea/pipelines';

export const GITHUB_TOKEN = secret(
  'GITHUB_TOKEN',
  'GitHub read token (repo tarball, poll, CLI release).',
);
export const SSH_PRIVATE_KEY = secret(
  'SSH_PRIVATE_KEY',
  'SSH key for image import + helm on the host.',
);
export const RELEASE_HOOK_SECRET = secret(
  'RELEASE_HOOK_SECRET',
  'HMAC key of the GitHub release webhook.',
);
