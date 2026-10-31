import {
  checkSpa,
  httpHealthCheck,
  implement,
  serviceUrl,
  type Ctx,
  step,
  HelmRelease,
  Source,
} from '@olivervea/pipelines';
import { AUTH, BETA_TOKEN } from '../config';

function check(ok: boolean, what: string): asserts ok {
  if (!ok) throw new Error(`beta-test: ${what}`);
}

const FIXTURE = 'v2 test fixture';

type Run = { number: number; status: string };

const bearer = (ctx: Ctx<'BETA_TOKEN'>) => ({
  authorization: `Bearer ${ctx.secrets.BETA_TOKEN}`,
});

/**
 * Reconciles beta's test fixture and runs it at `sha`, as the deployer (BETA_TOKEN): its called-back and polled
 * steps only finish when beta's signal-test service answers them. Throws unless the run succeeds in time.
 */
async function runFixture(ctx: Ctx<'BETA_TOKEN'>, base: string, sha: string) {
  const read = { headers: bearer(ctx) };
  const auth = { ...read.headers, 'content-type': 'application/json' };
  const pipelines = await ctx.http.getJson<{ id: string; name: string }[]>(
    `${base}/api/pipelines`,
    read,
  );
  const fixture = pipelines.find((p) => p.name === FIXTURE);
  check(fixture !== undefined, `beta has no pipeline '${FIXTURE}'`);
  const api = `${base}/api/pipelines/${fixture.id}`;
  const deadline = Date.now() + 12 * 60_000;
  const tick = () => new Promise((r) => setTimeout(r, 5_000));

  const reconcile = await ctx.http.fetch(`${api}/binding/reconcile?wait=true`, {
    method: 'POST',
    headers: auth,
  });
  check(
    reconcile.ok,
    `reconciling ${FIXTURE} answered ${reconcile.status}: ${await reconcile.text()}`,
  );

  const trigger = await ctx.http.fetch(`${api}/runs`, {
    method: 'POST',
    headers: auth,
    body: JSON.stringify({ sha }),
  });
  check(
    trigger.ok,
    `triggering ${FIXTURE} answered ${trigger.status}: ${await trigger.clone().text()}`,
  );
  const { run } = (await trigger.json()) as { run: { number: number } | null };
  check(run !== null, `${FIXTURE} queued the run instead of starting it`);
  for (;;) {
    const current = await ctx.http.getJson<Run>(`${api}/runs/${run.number}`, read);
    if (current.status === 'Succeeded') return run.number;
    check(current.status === 'Running', `${FIXTURE}#${run.number} ${current.status}`);
    check(
      Date.now() < deadline,
      `${FIXTURE}#${run.number} still running after 12 minutes`,
    );
    await tick();
  }
}

export const betaTest = implement(
  step({
    name: 'beta-test',
    description:
      "Smoke-tests the freshly deployed beta: readiness, the API, the frontend bundle, the error model and SSE; then reconciles and runs beta's test fixture at this commit, whose steps wait for a callback and a poll answered by beta's signal-test service.",
    image: 'alpine:3.22',
    input: { release: HelmRelease, source: Source },
    secrets: [BETA_TOKEN],
    config: [AUTH],
    timeoutSeconds: 900,
  }),
  async ({ release, source }, ctx) => {
    const BETA_URL = release.url ?? serviceUrl(release);
    const auth = ctx.config.auth;
    const read = { headers: bearer(ctx) };
    await httpHealthCheck(ctx, `${BETA_URL}/api/ready`, {
      attempts: 12,
      delaySeconds: 5,
    });

    const pipelines = await ctx.http.getJson<unknown>(`${BETA_URL}/api/pipelines`, read);
    check(Array.isArray(pipelines), '/api/pipelines did not answer a list');

    await checkSpa(ctx, BETA_URL);

    const attempts = await ctx.http.getJson<unknown>(
      `${BETA_URL}/api/attempts?status=active`,
      read,
    );
    check(Array.isArray(attempts), '/api/attempts did not answer a list');

    if (auth.mode !== 'none') {
      const anonymousRead = await ctx.http.fetch(`${BETA_URL}/api/pipelines`);
      check(
        anonymousRead.status === 401,
        `an anonymous read answered ${anonymousRead.status}, expected 401`,
      );
      const anonymousTrigger = await ctx.http.fetch(
        `${BETA_URL}/api/pipelines/00000000-0000-0000-0000-000000000000/runs`,
        { method: 'POST', body: '{}' },
      );
      check(
        anonymousTrigger.status === 401,
        `an anonymous trigger answered ${anonymousTrigger.status}, expected 401`,
      );
    }

    const missing = await ctx.http.fetch(
      `${BETA_URL}/api/pipelines/00000000-0000-0000-0000-000000000000/runs/1`,
      read,
    );
    check(
      missing.status === 404,
      `an unknown pipeline answered ${missing.status}, expected 404`,
    );
    check(
      missing.headers.get('content-type')?.startsWith('application/json') ?? false,
      'a 404 is not application/json',
    );
    const problem = (await missing.json()) as { status?: number; errors?: unknown[] };
    check(
      problem.status === 404 && Array.isArray(problem.errors),
      'the 404 body is not an ApiProblem',
    );

    const events = await ctx.http.fetch(`${BETA_URL}/api/events`, {
      ...read,
      signal: AbortSignal.any([ctx.signal, AbortSignal.timeout(10_000)]),
    });
    check(events.ok && events.body !== null, `/api/events answered ${events.status}`);
    const reader = events.body.getReader();
    let seen = '';
    try {
      while (!seen.includes('event: hello')) {
        const { done, value } = await reader.read();
        if (done) break;
        seen += new TextDecoder().decode(value);
      }
    } catch {
      // Timed out: reported by the check below.
    } finally {
      await reader.cancel().catch(() => {});
    }
    check(seen.includes('event: hello'), '/api/events sent no hello');

    const install = await (await ctx.http.expectOk(`${BETA_URL}/install.sh`)).text();
    check(install.startsWith('#!/bin/sh'), '/install.sh is not the install script');

    ctx.log('beta is healthy; running the fixture', { url: BETA_URL, sha: source.sha });
    const fixture = await runFixture(ctx, BETA_URL, source.sha);
    ctx.log('the fixture run succeeded, external inputs included', {
      run: `${FIXTURE}#${fixture}`,
    });
  },
);
