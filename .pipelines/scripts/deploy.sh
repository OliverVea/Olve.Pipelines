#!/bin/sh
# Deploy to prod (apps). Runs only after deploy-beta succeeds: import the built
# image into the homelab k3s containerd and helm-upgrade the prod release.
set -e
apk add --no-cache openssh-client

mkdir -p ~/.ssh
echo "$SSH_PRIVATE_KEY" > ~/.ssh/id_ed25519
chmod 600 ~/.ssh/id_ed25519
ssh-keyscan -H bulwark-m2 >> ~/.ssh/known_hosts 2>/dev/null || true

# The bundle path uses step GUIDs, so glob for the single production output dir.
INPUT_DIR=$(ls -d /input/*/)
VERSION=$(cat "${INPUT_DIR}version.txt")
HOST=oliver@bulwark-m2
RELEASE=olve-pipelines

echo "Deploying $RELEASE:$VERSION"

# Import the image into k3s containerd (the k3s socket, not the default one).
cat "${INPUT_DIR}image.tar" | ssh -o StrictHostKeyChecking=no "$HOST" \
  "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load"

# Verify the image is visible to CRI.
ssh -o StrictHostKeyChecking=no "$HOST" "sudo crictl images | grep olve-pipelines"

# Copy the helm chart (clean destination first to avoid scp nesting).
ssh -o StrictHostKeyChecking=no "$HOST" "rm -rf /tmp/$RELEASE-helm"
scp -o StrictHostKeyChecking=no -r "${INPUT_DIR}helm" "$HOST:/tmp/$RELEASE-helm"

# Helm upgrade against the imported image. slo.enabled=false: the sloth CRD is not
# installed cluster-wide. pullPolicy=Never — the image is local to the node.
ssh -o StrictHostKeyChecking=no "$HOST" \
  "helm upgrade --install $RELEASE /tmp/$RELEASE-helm -n apps \
     --set image.repository=docker.io/library/$RELEASE \
     --set image.tag=$VERSION --set image.pullPolicy=Never --set slo.enabled=false \
   && rm -rf /tmp/$RELEASE-helm"

echo "Deploy complete: $RELEASE:$VERSION"
