// Checks for deployed apps, used from custom test handlers.
import type { Ctx } from "../ctx/types.ts";

/** The in-cluster URL of a release's Service, assuming the chart names it after the release. */
export function serviceUrl(release: { release: string; namespace: string }): string {
  return `http://${release.release}.${release.namespace}.svc.cluster.local`;
}

/**
 * Checks `url` serves a single-page app: `GET <url>/` references a `/assets/*.js` bundle (Vite's
 * layout), and the bundle is served as JavaScript. Returns the page, for app-specific checks.
 */
export async function checkSpa(ctx: Pick<Ctx, "http" | "log">, url: string): Promise<{ html: string; bundle: string }> {
  const base = url.replace(/\/$/, "");
  const html = await (await ctx.http.expectOk(`${base}/`)).text();
  const bundle = html.match(/src="(\/assets\/[^"]+\.js)"/)?.[1];
  if (bundle === undefined) throw new Error(`${base}/ does not reference an /assets/*.js bundle`);
  const js = await ctx.http.expectOk(`${base}${bundle}`);
  await js.body?.cancel().catch(() => {});
  if (!(js.headers.get("content-type")?.includes("javascript") ?? false)) throw new Error(`${base}${bundle} is not served as JavaScript`);
  ctx.log("SPA served", { url: base, bundle });
  return { html, bundle };
}
