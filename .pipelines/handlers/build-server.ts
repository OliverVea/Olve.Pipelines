import { implement } from "@olivervea/pipelines";
import { buildServer } from "../pipeline";
import { dockerConfig } from "../registry";

const BASE_IMAGES = {
  aot: "mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled",
  jit: "mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled",
} as const;

export default implement(buildServer, async ({ server, frontend }, ctx) => {
  const target = ctx.config.publish.server;
  const { repository, insecure } = target;
  const tag = ctx.run.attempt > 1 ? `${ctx.run.version}-attempt${ctx.run.attempt}` : ctx.run.version;
  const layers = [await ctx.files.fetch(server.layer), await ctx.files.fetch(frontend.layer)];
  const opts = { env: { DOCKER_CONFIG: dockerConfig(ctx, [[repository, target]]) }, idleTimeoutSeconds: 120 };

  const base = BASE_IMAGES[server.runtime];
  // crane under gVisor can stall on mcr.microsoft.com, like kaniko; a pull-through cache doesn't.
  const mirror = Object.entries(target.registryMap ?? {}).find(([from]) => base.startsWith(`${from}/`));
  const bases = mirror ? [mirror[1] + base.slice(mirror[0].length), base] : [base];
  const mutate = (from: string) => ctx.exec([
    "crane", "mutate", from, "--platform", "linux/amd64",
    ...layers.flatMap(l => ["--append", l]),
    "--entrypoint", "./Olve.Pipelines", "--cmd", "", "--workdir", "/app", "--exposed-ports", "5000/tcp",
    "-t", `${repository}:${tag}`, ...(insecure ? ["--insecure"] : []),
  ], opts);
  for (const [i, from] of bases.entries()) {
    try {
      await mutate(from);
      break;
    } catch (e) {
      if (i === bases.length - 1) throw e;
      ctx.log("base image pull failed; trying upstream", { base: from, error: String(e) });
    }
  }

  const digest = (await ctx.sh.text(["crane", "digest", `${repository}:${tag}`, ...(insecure ? ["--insecure"] : [])].join(" "), opts)).trim();
  if (!/^sha256:[0-9a-f]{64}$/.test(digest)) throw new Error(`crane digest ${repository}:${tag} answered '${digest}'`);
  ctx.log("image pushed", { image: `${repository}:${tag}`, digest, runtime: server.runtime, base });
  return { repository, tag, digest };
});
