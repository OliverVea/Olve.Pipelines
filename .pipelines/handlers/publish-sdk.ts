import { existsSync, mkdirSync, readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { implement, npmHasVersion, type Ctx } from "@olivervea/pipelines";
import { publishSdk } from "../pipeline";

export default implement(publishSdk, async ({ pkg }, ctx) => {
  const npm = ctx.config.publish.npm;
  const id = `${pkg.name}@${pkg.version}`;
  if (!npm.enabled) return ctx.skip(`npm publishing is turned off (publish.npm.enabled): ${id} not published`);
  if (await npmHasVersion(ctx, pkg.name, pkg.version)) {
    const differences = await differsFromNpm(ctx, pkg.name, pkg.version, await ctx.files.fetch(pkg.tarball));
    if (differences.length > 0)
      throw new Error(`${id} is already on npm with different content (${differences.slice(0, 5).join(", ")}${differences.length > 5 ? ", …" : ""}): bump the SDK version`);
    ctx.log("already on npm with the same content", { package: id });
    return { name: pkg.name, version: pkg.version };
  }

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

/** The files that differ between a packed tarball and the one npm has for `name@version`. */
async function differsFromNpm(ctx: Pick<Ctx, "http" | "sh" | "tmpDir">, name: string, version: string, tarball: string): Promise<string[]> {
  const meta = await (await ctx.http.fetch(`https://registry.npmjs.org/${name.replace("/", "%2F")}/${encodeURIComponent(version)}`)).json() as { dist?: { tarball?: string } };
  if (!meta.dist?.tarball) throw new Error(`npm has no tarball for ${name}@${version}`);
  const published = join(ctx.tmpDir, "published.tgz");
  await Bun.write(published, await ctx.http.fetch(meta.dist.tarball));
  const unpack = async (from: string, dir: string) => {
    mkdirSync(dir, { recursive: true });
    await ctx.sh(`tar -xzf ${from} -C ${dir}`);
    return dir;
  };
  const ours = await unpack(tarball, join(ctx.tmpDir, "ours"));
  const theirs = await unpack(published, join(ctx.tmpDir, "theirs"));
  const files = (root: string) => new Map((readdirSync(root, { recursive: true, withFileTypes: true }) as import("node:fs").Dirent[])
    .filter((e) => e.isFile()).map((e) => { const path = join(e.parentPath, e.name); return [relative(root, path), readFileSync(path)] as const; }));
  const a = files(ours);
  const b = files(theirs);
  return [...new Set([...a.keys(), ...b.keys()])].filter((f) => !a.get(f) || !b.get(f) || !a.get(f)!.equals(b.get(f)!)).sort();
}
