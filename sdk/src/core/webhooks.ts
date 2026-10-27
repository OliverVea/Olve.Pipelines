import type { Secret } from './secrets.ts';

/** How a webhook trigger authenticates deliveries. Build one with `hmacSha256`, `githubSignature` or `bearer`. */
export type WebhookVerify =
  | {
      readonly scheme: 'hmac-sha256';
      readonly secret: Secret;
      readonly header: string;
      readonly prefix: string;
    }
  | { readonly scheme: 'bearer'; readonly secret: Secret };

/** HMAC-SHA256 of the raw body keyed by `secret`, hex, in `header` after `prefix`. */
export function hmacSha256(o: {
  secret: Secret;
  header: string;
  prefix?: string;
}): WebhookVerify {
  return Object.freeze({
    scheme: 'hmac-sha256',
    secret: o.secret,
    header: o.header,
    prefix: o.prefix ?? '',
  });
}

/** GitHub's delivery signature: `X-Hub-Signature-256: sha256=<hex>`. */
export function githubSignature(secret: Secret): WebhookVerify {
  return hmacSha256({ secret, header: 'X-Hub-Signature-256', prefix: 'sha256=' });
}

/** `Authorization: Bearer <secret>`. */
export function bearer(secret: Secret): WebhookVerify {
  return Object.freeze({ scheme: 'bearer', secret });
}

/**
 * What a webhook trigger mapper receives: the request headers (lower-case names, without `authorization`
 * and the signature header) and the body (parsed JSON, or the raw text when it isn't JSON).
 */
export interface WebhookEvent {
  readonly headers: Readonly<Record<string, string>>;
  readonly body: unknown;
}
