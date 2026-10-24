import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { implement, npmHasVersion } from "@olivervea/pipelines";
import { publishSdk } from "../pipeline";

export default implement(publishSdk, async ({ pkg }, ctx) => {
  const npm = ctx.config.publish.npm;
  const id = `${pkg.name}@${pkg.version}`;
  if (!npm.enabled) return ctx.skip(`npm publishing is turned off (publish.npm.enabled): ${id} not published`);
  if (await npmHasVersion(ctx, pkg.name, pkg.version)) return ctx.skip(`${id} is already on npm`);

  const token = process.env.NPM_TOKEN?.trim();
  if (!token) throw new Error(`${id} is not on npm, but NPM_TOKEN is not set`);

  const tarball = await ctx.files.fetch(pkg.tarball);
  const home = join(ctx.tmpDir, "home");
  mkdirSync(home, { recursive: true });
  // The token goes in the env, never argv (ctx.exec errors quote argv). The output goes to a file so a
  // failure can be told apart; it is replayed into the log either way.
  const out = join(ctx.tmpDir, "publish.log");
  try {
    await ctx.sh(`bun publish ${tarball} --tag ${npm.prereleaseTag} --access public > ${out} 2>&1`, { cwd: ctx.tmpDir, env: { HOME: home, NPM_CONFIG_TOKEN: token } });
  } catch (e) {
    const output = existsSync(out) ? readFileSync(out, "utf8") : "";
    process.stderr.write(output);
    // Another run published this version after our lookup: npm's view of new versions lags a minute or more.
    if (output.includes("cannot publish over the previously published versions")) return ctx.skip(`${id} was published by another run`);
    throw e;
  }
  process.stderr.write(readFileSync(out, "utf8"));
  ctx.log("published the SDK", { package: id, tag: npm.prereleaseTag });
  return { name: pkg.name, version: pkg.version };
});
