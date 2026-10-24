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
