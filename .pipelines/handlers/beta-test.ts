import { checkSpa, httpHealthCheck, implement, serviceUrl } from "@olivervea/pipelines";
import { betaTest } from "../pipeline";

function check(ok: boolean, what: string): asserts ok {
  if (!ok) throw new Error(`beta-test: ${what}`);
}

export default implement(betaTest, async ({ release }, ctx) => {
  const BETA_URL = release.url ?? serviceUrl(release);
  const auth = ctx.config.target.beta.auth ?? ctx.config.auth;
  await httpHealthCheck(ctx, `${BETA_URL}/api/ready`, { attempts: 12, delaySeconds: 5 });

  const pipelines = await ctx.http.getJson<unknown>(`${BETA_URL}/api/pipelines`);
  check(Array.isArray(pipelines), "/api/pipelines did not answer a list");

  await checkSpa(ctx, BETA_URL);

  const attempts = await ctx.http.getJson<unknown>(`${BETA_URL}/api/attempts?status=active`);
  check(Array.isArray(attempts), "/api/attempts did not answer a list");

  if (auth.mode !== "none") {
    const anonymousTrigger = await ctx.http.fetch(`${BETA_URL}/api/pipelines/00000000-0000-0000-0000-000000000000/runs`, { method: "POST", body: "{}" });
    check(anonymousTrigger.status === 401, `an anonymous trigger answered ${anonymousTrigger.status}, expected 401`);
  }

  const missing = await ctx.http.fetch(`${BETA_URL}/api/pipelines/00000000-0000-0000-0000-000000000000/runs/1`);
  check(missing.status === 404, `an unknown pipeline answered ${missing.status}, expected 404`);
  check(missing.headers.get("content-type")?.startsWith("application/problem+json") ?? false, "a 404 is not application/problem+json");
  const problem = (await missing.json()) as { status?: number; errors?: unknown[] };
  check(problem.status === 404 && Array.isArray(problem.errors), "the 404 body is not an ApiProblem");

  const events = await ctx.http.fetch(`${BETA_URL}/api/events`, { signal: AbortSignal.any([ctx.signal, AbortSignal.timeout(10_000)]) });
  check(events.ok && events.body !== null, `/api/events answered ${events.status}`);
  const reader = events.body.getReader();
  let seen = "";
  try {
    while (!seen.includes("event: hello")) {
      const { done, value } = await reader.read();
      if (done) break;
      seen += new TextDecoder().decode(value);
    }
  } catch {
    // Timed out: reported by the check below.
  } finally {
    await reader.cancel().catch(() => {});
  }
  check(seen.includes("event: hello"), "/api/events sent no hello");

  const install = await (await ctx.http.expectOk(`${BETA_URL}/install.sh`)).text();
  check(install.startsWith("#!/bin/sh"), "/install.sh is not the install script");

  ctx.log("beta is healthy", { url: BETA_URL });
});
