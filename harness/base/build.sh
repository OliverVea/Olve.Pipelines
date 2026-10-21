#!/usr/bin/env bash
# Builds and pushes registry.ovea.pro/olve-pipelines-v2/harness-base:<content-tag> (+ :latest).
#
# The tag is the first 12 hex chars of sha256(Dockerfile + olve-init): both base images are pinned
# by digest inside the Dockerfile, so equal content => equal image. Re-running with unchanged
# content re-pushes the same tag (idempotent). Prints the tag and the pushed digest.
#
#   harness/base/build.sh            # build + push
#   PUSH=0 harness/base/build.sh     # build only (local smoke tests)
#
# Requires docker (buildx) usable without sudo and `docker login registry.ovea.pro`.
set -euo pipefail
cd "$(dirname "$0")"

REPO=${REPO:-registry.ovea.pro/olve-pipelines-v2/harness-base}
TAG=$(cat Dockerfile olve-init | sha256sum | cut -c1-12)

docker build --platform linux/amd64 --provenance=false --pull \
  -t "$REPO:$TAG" -t "$REPO:latest" .

if [ "${PUSH:-1}" = 1 ]; then
  docker push -q "$REPO:$TAG"
  docker push -q "$REPO:latest"
  digest=$(docker inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$REPO:$TAG" | grep "^$REPO@" | head -1)
  echo "pushed $REPO:$TAG"
  echo "digest $digest"
else
  echo "built $REPO:$TAG (not pushed)"
fi
