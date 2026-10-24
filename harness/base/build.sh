#!/usr/bin/env bash
# Builds and pushes ghcr.io/olivervea/olve-pipelines-harness-base:<content-tag> by hand. The pipeline's
# publish-harness step (steps.imageBuild with contentTag) normally does this; both use the same tag:
# `src-` + the first 12 hex chars of the sha256 of this directory's deterministic tar (the SDK's
# contentTagOf), so equal content => equal tag. Prints the tag and the pushed digest.
#
#   harness/base/build.sh            # build + push
#   PUSH=0 harness/base/build.sh     # build only (local smoke tests)
#
# Requires bun (for the tag), docker (buildx) usable without sudo and `docker login ghcr.io`.
# Run it from a Linux checkout: the tag covers file modes and line endings.
set -euo pipefail
cd "$(dirname "$0")"

REPO=${REPO:-ghcr.io/olivervea/olve-pipelines-harness-base}
TAG=$(bun -e 'import { contentTagOf } from "../../sdk/src/library/image-build.ts"; import { tmpdir } from "node:os"; console.log(contentTagOf(".", tmpdir()));')

docker build --platform linux/amd64 --provenance=false --pull -t "$REPO:$TAG" .

if [ "${PUSH:-1}" = 1 ]; then
  docker push -q "$REPO:$TAG"
  digest=$(docker inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$REPO:$TAG" | grep "^$REPO@" | head -1)
  echo "pushed $REPO:$TAG"
  echo "digest $digest"
else
  echo "built $REPO:$TAG (not pushed)"
fi
