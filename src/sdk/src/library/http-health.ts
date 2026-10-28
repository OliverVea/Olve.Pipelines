// httpHealthCheck — GET a URL until it answers 2xx, or fail.
//
// The general HTTP health check (ported from v1's deploy-beta.sh curl loop). Library steps use it
// (helmDeployOverSsh's `healthUrl`), and it is exported for custom handlers, which get it with
// their own retry policy. Runs in the harness (Bun fetch), so the image needs no curl.
import type { Ctx } from '../ctx/types.ts';

export interface HttpHealthCheckOptions {
  /** Total attempts (v1: 5). */
  attempts?: number;
  /** Delay between attempts, in seconds (v1: 5). */
  delaySeconds?: number;
  /** Request headers (e.g. an Authorization header built from a secret). */
  headers?: Record<string, string>;
}

/** Defaults match v1's deploy-beta.sh: 5 attempts, 5 s apart. */
export const HTTP_HEALTH_DEFAULTS = { attempts: 5, delaySeconds: 5 } as const;

/**
 * GETs `url` until it returns a 2xx status (redirects are followed), retrying network errors and
 * non-2xx statuses. Throws the last error (status + URL, query redacted) once the attempts run out.
 */
export async function httpHealthCheck(
  ctx: Pick<Ctx, 'http' | 'retry' | 'log'>,
  url: string,
  opts: HttpHealthCheckOptions = {},
): Promise<void> {
  const attempts = opts.attempts ?? HTTP_HEALTH_DEFAULTS.attempts;
  const delaySeconds = opts.delaySeconds ?? HTTP_HEALTH_DEFAULTS.delaySeconds;
  ctx.log('checking health', { url: url.split('?')[0]!, attempts });
  await ctx.retry({ attempts, delaySeconds }, () =>
    ctx.http.expectOk(url, opts.headers ? { headers: opts.headers } : undefined),
  );
  ctx.log('health OK', { url: url.split('?')[0]! });
}
