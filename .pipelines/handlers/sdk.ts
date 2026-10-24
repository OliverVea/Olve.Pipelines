import { mkdirSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { sdk } from "../pipeline";

export default implement(sdk, async ({ source }, ctx) => {
  const dir = join(await ctx.files.fetch(source.tree), "sdk");
  const { name, version } = await ctx.readJson<{ name: string; version: string }>(join(dir, "package.json"));

  // Clean env for the checks: the harness's OLVE_* variables would leak into the SDK's own tests.
  const home = join(ctx.tmpDir, "home");
  mkdirSync(home, { recursive: true });
  const clean = ["env", "-i", `PATH=${process.env.PATH ?? "/usr/local/bin:/usr/bin:/bin"}`, `HOME=${home}`, `TMPDIR=${ctx.tmpDir}`];
  await ctx.exec([...clean, "bun", "install", "--frozen-lockfile"], { cwd: dir });
  await ctx.exec([...clean, "bun", "run", "check"], { cwd: dir });

  const out = join(ctx.tmpDir, "pack");
  mkdirSync(out, { recursive: true });
  await ctx.exec([...clean, "bun", "pm", "pack", "--destination", out], { cwd: dir });
  const packed = readdirSync(out).filter((f) => f.endsWith(".tgz"));
  if (packed.length !== 1) throw new Error(`bun pm pack wrote ${JSON.stringify(packed)}, expected one .tgz`);
  const tarball = await ctx.output.file(join(out, packed[0]!), { name: "package.tgz" });
  ctx.log("SDK checked and packed", { name, version });
  return { name, version, tarball };
});
