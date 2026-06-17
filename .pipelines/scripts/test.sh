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

# Fetch the shared helper library (see build.sh for why fetch-to-file + --no-check-certificate).
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.Pipelines
BRANCH=main

olve_fetch_repo "$REPO" "$BRANCH" /src

# Unit suite only: RunUnitTests is on by default; server-dependent integration tests run
# against beta as a processing step (see project_pipeline_self_testing), not here.
dotnet test test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj -c Release

echo "Tests passed"
