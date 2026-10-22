// Build the `pl` operator CLI from the deployed commit and return the binaries as output files.
// The instance serves the latest successful attempt's files at GET /download/<asset>, so the step
// needs no storage credentials: the binaries travel in the attempt's normal result upload.
//
// Platforms (as v1's scripts/publish-cli.sh): linux-x64 is a real Native-AOT binary (small, no
// runtime). Native AOT cannot cross-compile to Windows from Linux, so win-x64 ships as a
// self-contained single-file IL build instead — one .exe, nothing to install, just larger (~70 MB).
import { join } from "node:path";
import { implement } from "@olve/pipelines";
import { publishCli } from "../pipeline";

const CLI = "src/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj";

export default implement(publishCli, async ({ source }, ctx) => {
  // The Native-AOT link needs clang + zlib headers; the SDK image ships neither.
  const apt = { DEBIAN_FRONTEND: "noninteractive" };
  await ctx.exec(["apt-get", "update", "-qq"], { env: apt });
  await ctx.exec(["apt-get", "install", "-y", "-qq", "--no-install-recommends", "clang", "zlib1g-dev"], { env: apt });

  const tree = await ctx.files.fetch(source.tree);
  const out = join(ctx.tmpDir, "cli");
  // Single-node MSBuild, no node reuse / shared compiler server (see handlers/test.ts: under gVisor
  // MSBuild sees every host core). The run's version stamp goes into InformationalVersion, which is
  // what `pl --version` prints (free-form; -p:Version would also set the numeric AssemblyVersion).
  const env = { MSBUILDDISABLENODEREUSE: "1" };
  const common = ["-c", "Release", "-m:1", "-p:UseSharedCompilation=false",
    `-p:InformationalVersion=${ctx.run.version}`, "-p:IncludeSourceRevisionInInformationalVersion=false"];

  const linux = join(out, "linux");
  const win = join(out, "win");
  await ctx.exec(["dotnet", "publish", CLI, "-r", "linux-x64", ...common, "-o", linux], { cwd: tree, env });
  await ctx.exec(["dotnet", "publish", CLI, "-r", "win-x64", ...common,
    "-p:PublishAot=false", "-p:PublishSingleFile=true", "-p:SelfContained=true", "-o", win], { cwd: tree, env });

  // Smoke test: the AOT binary starts and reports the version it was stamped with.
  await ctx.exec([join(linux, "pl"), "--version"]);

  const linuxX64 = await ctx.output.file(join(linux, "pl"), { name: "pl-linux-x64" });
  const winX64 = await ctx.output.file(join(win, "pl.exe"), { name: "pl-win-x64.exe" });
  ctx.log("published pl", { version: ctx.run.version });
  return { version: ctx.run.version, linuxX64, winX64 };
});
