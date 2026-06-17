#!/bin/sh
# Build the app image with Kaniko and stage the deploy artifacts (image tar, helm
# chart, version) into /output so the deploy steps can pick them up from the bundle.
# The Kaniko/staging footguns live in olve-lib.sh (see issue #17).
set -e

# Fetch the shared helper library. Fetch-to-file, not `. <(...)`: busybox has no
# process substitution. --no-check-certificate: same busybox-wget TLS footgun as the
# repo fetch. Swap `main` for a tag/SHA to pin.
# mkdir -p /tmp: the kaniko:debug rootfs has no /tmp, so wget -O /tmp/... fails ENOENT.
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.Pipelines
BRANCH=main
VERSION=$(olve_version)
CTX=/kaniko/build-context

olve_fetch_repo "$REPO" "$BRANCH" "$CTX"

# Carry the helm chart and version forward as build artifacts before Kaniko runs.
olve_stage_artifact "$CTX/helm" /output/helm
echo "$VERSION" > /output/version.txt

olve_kaniko_build "$CTX" "olve-pipelines:$VERSION"

echo "Build complete: olve-pipelines:$VERSION"
