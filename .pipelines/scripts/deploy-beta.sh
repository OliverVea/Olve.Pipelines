#!/bin/sh
# Deploy to apps-beta and gate prod: import the built image into the homelab k3s
# containerd, helm-upgrade the beta release with beta overrides, then verify the
# beta rollout is healthy. A failure here stops the chain so prod never deploys.
# The ssh/import/helm footguns live in olve-lib.sh (see issue #17).
set -e

# Fetch the shared helper library (see build.sh for the fetch rationale). Swap `main` to pin.
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

# curl is needed by the post-deploy health loop below; olve_ssh_host installs only the
# ssh client, so add curl here (prod's deploy.sh does not need it).
apk add --no-cache curl

HOST=oliver@bulwark-m2
RELEASE=olve-pipelines

olve_ssh_host bulwark-m2

INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")

echo "Deploying $RELEASE:$VERSION to apps-beta"

olve_image_import "$INPUT_DIR/image.tar" "$HOST"

# Beta values are chart-relative (resolved from inside the copied chart dir by the helper).
# slo.enabled=false: this chart defines an slo block defaulting to true, but the sloth
# CRD is not installed cluster-wide — passed through here, not baked into the shared lib.
olve_helm_deploy "$HOST" "$RELEASE" apps-beta "$INPUT_DIR/helm" "$VERSION" \
  -f values-beta.yaml --set slo.enabled=false

# Wait for the rollout, then verify reachability — if beta is unhealthy, fail so
# prod does not deploy.
echo "Waiting for beta rollout..."
ssh -o StrictHostKeyChecking=no "$HOST" \
  "kubectl -n apps-beta rollout status deploy/olve-pipelines --timeout=120s"

echo "Verifying beta /api/health..."
for i in 1 2 3 4 5; do
  if curl -skf -o /dev/null https://pipelines-beta.ovea.pro/api/health; then
    echo "Beta health OK"
    exit 0
  fi
  sleep 5
done
echo "Beta health check failed" >&2
exit 1
