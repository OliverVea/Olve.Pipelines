// npm registry calls for release steps (no npm CLI needed in the image).
import type { Ctx } from "@olivervea/pipelines";

export const NPM_REGISTRY = "https://registry.npmjs.org";

const packagePath = (name: string) => name.replace("/", "%2f");

/** Whether `name@version` is on the registry. */
export async function npmHasVersion(ctx: Pick<Ctx, "http">, name: string, version: string, registry = NPM_REGISTRY): Promise<boolean> {
  const res = await ctx.http.fetch(`${registry}/${packagePath(name)}`, { headers: { "cache-control": "no-cache" } });
  if (res.status === 404) {
    await res.body?.cancel().catch(() => {});
    return false;
  }
  if (!res.ok) throw new Error(`npm registry lookup of ${name} failed: HTTP ${res.status}`);
  return Boolean(((await res.json()) as { versions?: Record<string, unknown> }).versions?.[version]);
}

/** `npm dist-tag add <name>@<version> <tag>`: points the tag at an already published version. */
export async function npmDistTag(
  ctx: Pick<Ctx, "http" | "log">,
  o: { name: string; version: string; tag: string; token: string; registry?: string },
): Promise<void> {
  const url = `${o.registry ?? NPM_REGISTRY}/-/package/${packagePath(o.name)}/dist-tags/${encodeURIComponent(o.tag)}`;
  const res = await ctx.http.fetch(url, {
    method: "PUT",
    headers: { authorization: `Bearer ${o.token}`, "content-type": "application/json" },
    body: JSON.stringify(o.version),
  });
  await res.body?.cancel().catch(() => {});
  if (!res.ok) throw new Error(`npm dist-tag ${o.name}@${o.version} ${o.tag} failed: HTTP ${res.status}`);
  ctx.log("npm dist-tag", { package: `${o.name}@${o.version}`, tag: o.tag });
}
