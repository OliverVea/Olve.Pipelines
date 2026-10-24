#!/bin/sh
# One-shot bootstrap for the local k3s cluster (docker-compose.yml). Waits for k3s, applies the
# controller's namespace + ServiceAccount + RBAC (compose/k3s-rbac.yaml, mirroring
# helm/templates/serviceaccount.yaml), then writes the ServiceAccount token/CA/namespace into the
# shared volume that the pipelines container mounts at the standard in-cluster path, so the app's
# Kubernetes:AuthMode=InCluster works unchanged.
set -eu

NS="${RUNNER_NAMESPACE:-olve-runners}"
OUT=/sa

until [ -s /k3s-output/kubeconfig.yaml ]; do echo "waiting for k3s kubeconfig..."; sleep 2; done
# k3s writes the kubeconfig for 127.0.0.1; reach it by its compose service name instead.
sed 's#https://127.0.0.1:6443#https://k3s:6443#' /k3s-output/kubeconfig.yaml > /tmp/kubeconfig
export KUBECONFIG=/tmp/kubeconfig

until kubectl get --raw /readyz >/dev/null 2>&1; do echo "waiting for k3s API..."; sleep 2; done

kubectl apply -f /bootstrap/k3s-rbac.yaml

# Job pods need the namespace's default ServiceAccount, which the controller-manager creates async.
until kubectl -n "$NS" get serviceaccount default >/dev/null 2>&1; do sleep 1; done

# The token controller fills the long-lived token Secret asynchronously.
until token=$(kubectl -n "$NS" get secret olve-pipelines-token -o jsonpath='{.data.token}') && [ -n "$token" ]; do
  echo "waiting for ServiceAccount token..."; sleep 1
done

echo "$token" | base64 -d > "$OUT/token"
kubectl -n "$NS" get secret olve-pipelines-token -o jsonpath='{.data.ca\.crt}' | base64 -d > "$OUT/ca.crt"
printf '%s' "$NS" > "$OUT/namespace"
chmod 644 "$OUT/token" "$OUT/ca.crt" "$OUT/namespace"

echo "k3s bootstrap done: namespace '$NS', ServiceAccount 'olve-pipelines'."
