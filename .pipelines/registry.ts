import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { registryTag, type Ctx } from "@olivervea/pipelines";

interface Published {
  repository: string;
  digest: string;
}
interface Target {
  insecure: boolean;
  auth?: { username: string; password: { secret: string } } | undefined;
}

/** Points `tag` at the image's digest in its registry, with the login `target` names. */
export async function tagImage(ctx: Pick<Ctx, "http" | "log" | "secrets">, image: Published, tag: string, target: Target): Promise<void> {
  const password = target.auth ? (ctx.secrets as Record<string, string | undefined>)[target.auth.password.secret] : undefined;
  if (target.auth && !password) throw new Error(`secret ${target.auth.password.secret} (registry login for ${image.repository}) is not set`);
  await registryTag(ctx.http, image.repository, image.digest, tag, {
    insecure: target.insecure,
    ...(target.auth && password ? { username: target.auth.username, password } : {}),
  });
  ctx.log("tagged image", { image: `${image.repository}:${tag}`, digest: image.digest });
}

interface Repository extends Target {
  repository: string;
}

/** A docker config directory (for crane's `DOCKER_CONFIG`) with the logins the targets name. */
export function dockerConfig(ctx: Pick<Ctx, "secrets" | "tmpDir">, targets: readonly (readonly [string, Target])[]): string {
  const dir = join(ctx.tmpDir, `docker-${createHash("sha256").update(targets.map(([r]) => r).join(" ")).digest("hex").slice(0, 8)}`);
  mkdirSync(dir, { recursive: true });
  const auths: Record<string, { auth: string }> = {};
  for (const [repository, target] of targets) {
    if (!target.auth) continue;
    const password = (ctx.secrets as Record<string, string | undefined>)[target.auth.password.secret];
    if (!password) throw new Error(`secret ${target.auth.password.secret} (registry login for ${repository}) is not set`);
    auths[repository.split("/")[0]!] = { auth: Buffer.from(`${target.auth.username}:${password}`).toString("base64") };
  }
  writeFileSync(join(dir, "config.json"), JSON.stringify({ auths }), { mode: 0o600 });
  return dir;
}

/**
 * Copies a verified image (same digest) from where it was built to a release repository and points
 * `tags` at it there. `tag` (the image's build tag) is copied first; a content tag (`src-…`) already
 * in the release repository is kept, since equal content means an equal tag.
 */
export async function releaseImage(
  ctx: Pick<Ctx, "exec" | "log" | "secrets" | "tmpDir">,
  image: Published & { tag: string },
  from: Target,
  to: Repository,
  tags: string[],
): Promise<void> {
  const opts = { env: { DOCKER_CONFIG: dockerConfig(ctx, [[image.repository, from], [to.repository, to]]) } };
  const insecure = from.insecure || to.insecure ? ["--insecure"] : [];

  const built = `${to.repository}:${image.tag}`;
  const kept = image.tag.startsWith("src-") && (await ctx.exec(["crane", "digest", built, ...insecure], opts).then(() => true, () => false));
  if (kept) ctx.log("content tag already released", { image: built });
  else await ctx.exec(["crane", "copy", `${image.repository}@${image.digest}`, built, ...insecure], opts);
  for (const tag of tags) await ctx.exec(["crane", "tag", built, tag, ...insecure], opts);
  ctx.log("released image", { from: kept ? built : `${image.repository}@${image.digest}`, to: to.repository, tags: [image.tag, ...tags] });
}
