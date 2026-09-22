#!/bin/sh
# Run the code-only test suite (unit tests + any tests that need no live server) as a
# production step in PARALLEL with build-and-package — not inside the Docker build, so
# tests and image build run concurrently instead of serialized. A failure here fails the
# production job group, which gates the whole processing cascade (deploy never runs).
#
# Code comes from the GitHub tarball, the same way build.sh fetches it (the runner has no
# git checkout). The tarball is `git archive`-equivalent: source only, no .git dir — so
# tests must not depend on .git to locate the repo root (see PipelinesTestFixtureConfigTests).
set -e

# Fetch the shared helper library (see build.sh for why fetch-to-file + --no-check-certificate
# and why /tmp must be created first).
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.Pipelines
BRANCH=main

olve_fetch_repo "$REPO" "$BRANCH" /src

# Unit suite only: RunUnitTests is on by default; server-dependent integration tests run
# against beta as a processing step (see project_pipeline_self_testing), not here.
#
# Build single-node with no node reuse / shared compiler server: the pod has no memory
# limit and runs alongside the Kaniko build, and under gVisor MSBuild sees the host's
# full core count and fans out one worker per core. A worker died that way (MSB4166
# "Child node exited prematurely", most likely OOM), failing the run with no test failure.
export MSBUILDDISABLENODEREUSE=1
dotnet test test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj -c Release \
  -m:1 -p:UseSharedCompilation=false

echo "Tests passed"
