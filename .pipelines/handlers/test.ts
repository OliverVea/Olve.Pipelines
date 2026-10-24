import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { test } from "../pipeline";

const TESTS = "test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj";
const CLIENT = "clients/olve-pipelines-client-ts/src";

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const env = { MSBUILDDISABLENODEREUSE: "1" };
  const committedApi = await Bun.file(join(tree, "api.json")).text();

  await ctx.exec(["dotnet", "build", TESTS, "-c", "Release", "-m:1", "-p:UseSharedCompilation=false"], { cwd: tree, env });
  await ctx.exec(["dotnet", "test", "--project", TESTS, "-c", "Release", "--no-build"], { cwd: tree, env });

  // The build regenerates api.json; the committed one and the TS client generated from it must match.
  if ((await Bun.file(join(tree, "api.json")).text()) !== committedApi)
    throw new Error("api.json is out of date with the API: build and commit it, then regenerate the TS client");
  const generated = join(ctx.tmpDir, "client");
  await ctx.exec(["dotnet", "tool", "restore"], { cwd: tree, env });
  await ctx.exec(["dotnet", "kiota", "generate", "-l", "typescript", "-d", "api.json", "-o", generated, "-n", "OlvePipelinesClient", "--clean-output"], { cwd: tree, env });
  // kiota-lock.json's description hash follows every api.json change without the code changing.
  await ctx.exec(["diff", "-r", "-x", "kiota-lock.json", "-x", ".kiota.log", generated, join(tree, CLIENT)]).catch(() => {
    throw new Error(`${CLIENT} is out of date with api.json: regenerate it (see CLAUDE.md) and commit`);
  });

  await ctx.exec(["dotnet", "run", "tools/api-reference.cs", "--", "--check"], { cwd: tree, env }).catch(() => {
    throw new Error("docs/setup/api-reference.md is out of date with api.json: run `dotnet run tools/api-reference.cs` and commit");
  });

  ctx.log("tests passed; api.json, the TS client and the API reference match the API");
});
