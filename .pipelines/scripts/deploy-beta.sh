#!/bin/sh
# Deploy to apps-beta and gate prod: import the built image into the homelab k3s
# containerd, helm-upgrade the beta release with beta overrides, then verify the
# beta rollout is healthy. A failure here stops the chain so prod never deploys.
set -e
apk add --no-cache openssh-client curl

mkdir -p ~/.ssh
echo "$SSH_PRIVATE_KEY" > ~/.ssh/id_ed25519
chmod 600 ~/.ssh/id_ed25519
ssh-keyscan -H bulwark-m2 >> ~/.ssh/known_hosts 2>/dev/null || true

# The bundle path uses step GUIDs, so glob for the single production output dir.
INPUT_DIR=$(ls -d /input/*/)
VERSION=$(cat "${INPUT_DIR}version.txt")
HOST=oliver@bulwark-m2
RELEASE=olve-pipelines

echo "Deploying $RELEASE:$VERSION to apps-beta"

# Import the image into k3s containerd (the k3s socket, not the default one).
cat "${INPUT_DIR}image.tar" | ssh -o StrictHostKeyChecking=no "$HOST" \
  "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load"

# Copy the helm chart (clean destination first to avoid scp nesting).
ssh -o StrictHostKeyChecking=no "$HOST" "rm -rf /tmp/$RELEASE-helm-beta"
scp -o StrictHostKeyChecking=no -r "${INPUT_DIR}helm" "$HOST:/tmp/$RELEASE-helm-beta"

# Helm upgrade with beta values. slo.enabled=false: the sloth CRD is not installed
# cluster-wide. pullPolicy=Never — the image is local to the node.
ssh -o StrictHostKeyChecking=no "$HOST" \
  "helm upgrade --install $RELEASE /tmp/$RELEASE-helm-beta -n apps-beta \
     -f /tmp/$RELEASE-helm-beta/values-beta.yaml \
     --set image.repository=docker.io/library/$RELEASE \
     --set image.tag=$VERSION --set image.pullPolicy=Never --set slo.enabled=false \
   && rm -rf /tmp/$RELEASE-helm-beta"

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
