// Run the code-only test suite (unit tests + any tests that need no live server) in PARALLEL with
// the image build, not inside the Docker build. A failure fails the stage, and the stage barrier
// keeps deploy-beta from running.
//
// The tree is the GitHub tarball (source step): `git archive`-equivalent, source only, no .git dir —
// so tests must not depend on .git to locate the repo root (see PipelinesTestFixtureConfigTests).
import { implement } from "@olve/pipelines";
import { test } from "../pipeline";

const TESTS = "test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj";

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  // Build single-node with no node reuse / shared compiler server: the pod has no memory limit and
  // runs alongside the kaniko build, and under gVisor MSBuild sees the host's full core count and
  // fans out one worker per core. A worker died that way (MSB4166 "Child node exited prematurely",
  // most likely OOM), failing the run with no test failure.
  // Build separately: under Microsoft.Testing.Platform (global.json), `dotnet test` forwards
  // unrecognized args like -m:1 to the TUnit app, which rejects them.
  const env = { MSBUILDDISABLENODEREUSE: "1" };
  await ctx.exec(["dotnet", "build", TESTS, "-c", "Release", "-m:1", "-p:UseSharedCompilation=false"], { cwd: tree, env });
  // Unit suite only: RunUnitTests is on by default; server-dependent integration tests are not run here.
  await ctx.exec(["dotnet", "test", "--project", TESTS, "-c", "Release", "--no-build"], { cwd: tree, env });
  ctx.log("tests passed");
});
