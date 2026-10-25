import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { compileServer } from "../pipeline";
import { layerTar } from "../layers";

const SERVER = "src/Olve.Pipelines";

export default implement(compileServer, async ({ source }, ctx) => {
  const runtime = ctx.config.build.runtime;
  const tree = await ctx.files.fetch(source.tree);
  const root = join(ctx.tmpDir, "server");
  const env = { MSBUILDDISABLENODEREUSE: "1" };
  // JIT keeps the AOT build's JSON behaviour (source-generated only), so both run the same code paths.
  const flavour = runtime === "aot"
    ? ["-p:DebugType=none"]
    : ["-p:PublishAot=false", "--self-contained", "false", "-p:JsonSerializerIsReflectionEnabledByDefault=false"];
  await ctx.exec(["dotnet", "publish", SERVER, "-c", "Release", "-r", "linux-x64", "-m:1", "-p:UseSharedCompilation=false",
    ...flavour, "-o", join(root, "app")], { cwd: tree, env });

  const layer = await ctx.output.file(await layerTar(ctx, root, "server.tar.gz"), { name: "server.tar.gz" });
  ctx.log("published the server", { runtime });
  return { runtime, layer };
});
