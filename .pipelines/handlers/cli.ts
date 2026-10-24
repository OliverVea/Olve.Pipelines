import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { cli } from "../pipeline";

const CLI = "src/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj";

export default implement(cli, async ({ source }, ctx) => {
  const apt = { DEBIAN_FRONTEND: "noninteractive" };
  await ctx.exec(["apt-get", "update", "-qq"], { env: apt });
  await ctx.exec(["apt-get", "install", "-y", "-qq", "--no-install-recommends", "clang", "zlib1g-dev"], { env: apt });

  const tree = await ctx.files.fetch(source.tree);
  const out = join(ctx.tmpDir, "cli");
  const env = { MSBUILDDISABLENODEREUSE: "1" };
  const common = ["-c", "Release", "-m:1", "-p:UseSharedCompilation=false",
    `-p:InformationalVersion=${ctx.run.version}`, "-p:IncludeSourceRevisionInInformationalVersion=false"];

  const linux = join(out, "linux");
  const win = join(out, "win");
  await ctx.exec(["dotnet", "publish", CLI, "-r", "linux-x64", ...common, "-o", linux], { cwd: tree, env });
  await ctx.exec(["dotnet", "publish", CLI, "-r", "win-x64", ...common,
    "-p:PublishAot=false", "-p:PublishSingleFile=true", "-p:SelfContained=true", "-o", win], { cwd: tree, env });

  await ctx.exec([join(linux, "pl"), "--version"]);

  const linuxX64 = await ctx.output.file(join(linux, "pl"), { name: "pl-linux-x64" });
  const winX64 = await ctx.output.file(join(win, "pl.exe"), { name: "pl-win-x64.exe" });
  ctx.log("built pl", { version: ctx.run.version });
  return { version: ctx.run.version, linuxX64, winX64 };
});
