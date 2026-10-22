import { httpHealthCheck, implement } from "@olve/pipelines";
import { BETA_URL, betaTest } from "../pipeline";

interface RunView {
  run: { status: string };
  stages: { steps: { name: string; status: string }[] }[];
}

function check(ok: boolean, what: string): asserts ok {
  if (!ok) throw new Error(`beta-test: ${what}`);
}

export default implement(betaTest, async (_input, ctx) => {
  const { pipeline, number } = ctx.run;

  await httpHealthCheck(ctx, `${BETA_URL}/api/ready`, { attempts: 12, delaySeconds: 5 });

  const pipelines = await ctx.http.getJson<{ id: string }[]>(`${BETA_URL}/api/pipelines`);
  check(pipelines.some(p => p.id === pipeline), `/api/pipelines does not list this pipeline (${pipeline})`);

  // This run lives in the instance that just redeployed itself, so it proves persisted state reloaded.
  const view = await ctx.http.getJson<RunView>(`${BETA_URL}/api/pipelines/${pipeline}/runs/${number}`);
  check(view.run.status === "Running", `run #${number} is ${view.run.status}, expected Running`);
  const deploy = view.stages.flatMap(s => s.steps).find(s => s.name === "deploy-beta");
  check(deploy?.status === "succeeded", `run #${number} shows deploy-beta as ${deploy?.status ?? "missing"}`);

  const html = await (await ctx.http.expectOk(`${BETA_URL}/`)).text();
  const bundle = html.match(/src="(\/assets\/index-[^"]+\.js)"/)?.[1];
  check(bundle !== undefined, "/ does not reference the frontend bundle");
  const js = await ctx.http.expectOk(`${BETA_URL}${bundle}`);
  check(js.headers.get("content-type")?.includes("javascript") ?? false, `${bundle} is not served as JavaScript`);

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

  ctx.log("beta is healthy", { url: BETA_URL, run: number });
});
