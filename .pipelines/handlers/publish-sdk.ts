import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { publishSdk } from "../pipeline";

export default implement(publishSdk, async ({ source }, ctx) => {
  const sdk = join(await ctx.files.fetch(source.tree), "sdk");
  const { name, version } = await ctx.readJson<{ name: string; version: string }>(join(sdk, "package.json"));

  const onNpm = async () => {
    const res = await ctx.http.fetch(`https://registry.npmjs.org/${name.replace("/", "%2f")}`, { headers: { "cache-control": "no-cache" } });
    if (res.status === 404) return false;
    if (!res.ok) throw new Error(`npm registry lookup of ${name} failed: HTTP ${res.status}`);
    return Boolean(((await res.json()) as { versions?: Record<string, unknown> }).versions?.[version]);
  };
  if (await onNpm()) return ctx.skip(`${name}@${version} is already on npm`);

  const token = process.env.NPM_TOKEN?.trim();
  if (!token) throw new Error(`${name}@${version} is not on npm, but NPM_TOKEN is not set (pl secret set "Olve.Pipelines v2" NPM_TOKEN)`);

  // Clean env for the checks: the harness's OLVE_* variables would leak into the SDK's own tests.
  const home = join(ctx.tmpDir, "home");
  mkdirSync(home, { recursive: true });
  const clean = ["env", "-i", `PATH=${process.env.PATH ?? "/usr/local/bin:/usr/bin:/bin"}`, `HOME=${home}`, `TMPDIR=${ctx.tmpDir}`];
  await ctx.exec([...clean, "bun", "install", "--frozen-lockfile"], { cwd: sdk });
  await ctx.exec([...clean, "bun", "run", "check"], { cwd: sdk });

  // Tag and access come from publishConfig. The token goes in the env, never argv (ctx.exec errors quote argv).
  try {
    await ctx.exec(["bun", "publish"], { cwd: sdk, env: { HOME: home, NPM_CONFIG_TOKEN: token } });
  } catch (e) {
    // A concurrent run may have published this version moments ago; the registry lags behind.
    for (let i = 0; i < 6; i++) {
      if (await onNpm()) return ctx.skip(`${name}@${version} was published by another run`);
      await Bun.sleep(5000);
    }
    throw e;
  }
  ctx.log("published the SDK", { name, version });
  return { name, version };
});
