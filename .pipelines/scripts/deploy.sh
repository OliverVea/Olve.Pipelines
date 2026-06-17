#!/bin/sh
# Deploy to prod (apps). Runs only after deploy-beta succeeds: import the built
# image into the homelab k3s containerd and helm-upgrade the prod release. The
# ssh/import/helm footguns live in olve-lib.sh (see issue #17).
set -e

# Fetch the shared helper library. alpine ships wget but no busybox TLS quirk here;
# --no-check-certificate kept for parity with the build images. Swap `main` to pin.
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

HOST=oliver@bulwark-m2
RELEASE=olve-pipelines

olve_ssh_host bulwark-m2

INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")

echo "Deploying $RELEASE:$VERSION"

olve_image_import "$INPUT_DIR/image.tar" "$HOST"

# Verify the image is visible to CRI.
ssh -o StrictHostKeyChecking=no "$HOST" "sudo crictl images | grep olve-pipelines"

olve_helm_deploy "$HOST" "$RELEASE" apps "$INPUT_DIR/helm" "$VERSION"

echo "Deploy complete: $RELEASE:$VERSION"
