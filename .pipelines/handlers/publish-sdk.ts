import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { implement } from "@olve/pipelines";
import { publishSdk } from "../pipeline";

export default implement(publishSdk, async ({ source }, ctx) => {
  const sdk = join(await ctx.files.fetch(source.tree), "sdk");
  const { name, version } = await ctx.readJson<{ name: string; version: string }>(join(sdk, "package.json"));

  const res = await ctx.http.fetch(`https://registry.npmjs.org/${name.replace("/", "%2f")}`);
  if (res.ok) {
    const doc = (await res.json()) as { versions?: Record<string, unknown> };
    if (doc.versions?.[version]) return ctx.skip(`${name}@${version} is already on npm`);
  } else if (res.status !== 404) {
    throw new Error(`npm registry lookup of ${name} failed: HTTP ${res.status}`);
  }

  // Not ctx.secrets: it throws when unset, and an unset token means "skip", not "fail".
  const token = process.env.NPM_TOKEN?.trim();
  if (!token) return ctx.skip(`${name}@${version} is not on npm, but NPM_TOKEN is not set (pl secret set "Olve.Pipelines v2" NPM_TOKEN)`);

  // Clean env for the checks: the harness's OLVE_* variables would leak into the SDK's own tests.
  const home = join(ctx.tmpDir, "home");
  mkdirSync(home, { recursive: true });
  const clean = ["env", "-i", `PATH=${process.env.PATH ?? "/usr/local/bin:/usr/bin:/bin"}`, `HOME=${home}`, `TMPDIR=${ctx.tmpDir}`];
  await ctx.exec([...clean, "bun", "install", "--frozen-lockfile"], { cwd: sdk });
  await ctx.exec([...clean, "bun", "run", "check"], { cwd: sdk });

  // Tag and access come from publishConfig. The token goes in the env, never argv (ctx.exec errors quote argv).
  await ctx.exec(["bun", "publish"], { cwd: sdk, env: { HOME: home, NPM_CONFIG_TOKEN: token } });
  ctx.log("published the SDK", { name, version });
  return { name, version };
});
