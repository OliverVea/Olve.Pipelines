import { implement } from "@olivervea/pipelines";
import { test } from "../pipeline";

const TESTS = "test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj";

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  const env = { MSBUILDDISABLENODEREUSE: "1" };

  await ctx.exec(["dotnet", "build", TESTS, "-c", "Release", "-m:1", "-p:UseSharedCompilation=false"], { cwd: tree, env });
  await ctx.exec(["dotnet", "test", "--project", TESTS, "-c", "Release", "--no-build"], { cwd: tree, env });
  
  ctx.log("tests passed");
});
