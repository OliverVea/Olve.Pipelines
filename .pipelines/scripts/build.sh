#!/bin/sh
# Build the app image with Kaniko and stage the deploy artifacts (image tar, helm
# chart, version) into /output so the deploy steps can pick them up from the bundle.
set -e

REPO=OliverVea/Olve.Pipelines
BRANCH=main
VERSION=$(date +%Y%m%d-%H%M%S)

# Build context lives at /kaniko/build-context, NOT /workspace: Kaniko wipes /
# (including /workspace) between multi-stage builds, which breaks stage 2's
# COPY of generated clients. The /kaniko dir survives stage transitions.
CTX=/kaniko/build-context
mkdir -p "$CTX"
cd "$CTX"

# Fetch the repo tarball. The Kaniko debug image ships busybox wget, which needs
# --no-check-certificate against the GitHub API.
wget --no-check-certificate -q --header="Authorization: token $GITHUB_TOKEN" \
  -O repo.tar.gz "https://api.github.com/repos/$REPO/tarball/$BRANCH"
tar xzf repo.tar.gz --strip-components=1
rm repo.tar.gz

# Carry the helm chart and version forward as build artifacts before Kaniko runs
# (Kaniko wipes the context root between stages).
cp -r "$CTX/helm" /output/helm
echo "$VERSION" > /output/version.txt

# Build to a tar (no registry); the deploy steps import it onto the host.
# No --single-snapshot: it is incompatible with the multi-stage Dockerfile, whose
# stage 2 needs the build context intact.
/kaniko/executor \
  --context="$CTX" \
  --dockerfile="$CTX/Dockerfile" \
  --no-push \
  --tar-path=/output/image.tar \
  --destination="olve-pipelines:$VERSION"

echo "Build complete: olve-pipelines:$VERSION"
