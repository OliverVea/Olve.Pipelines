#!/bin/sh
# Runs inside the throwaway cluster's compose network: applies the controller's RBAC, marks the cluster
# as a test cluster and writes the ServiceAccount files the server and the tests use.
set -eu

NS=olve-runners
OUT=/sa

until [ -s /k3s-output/kubeconfig.yaml ]; do sleep 1; done
sed 's#https://127.0.0.1:6443#https://k3s:6443#' /k3s-output/kubeconfig.yaml > /tmp/kubeconfig
export KUBECONFIG=/tmp/kubeconfig

until kubectl get --raw /readyz >/dev/null 2>&1; do sleep 1; done

kubectl apply -f /rbac/k3s-rbac.yaml
until kubectl -n "$NS" get serviceaccount default >/dev/null 2>&1; do sleep 1; done

# The guard in the tests refuses a target without this Secret, with this id.
kubectl -n "$NS" create secret generic olve-real-system-marker \
  --from-literal=cluster-id="$CLUSTER_ID" --dry-run=client -o yaml | kubectl apply -f -

until token=$(kubectl -n "$NS" get secret olve-pipelines-token -o jsonpath='{.data.token}') && [ -n "$token" ]; do
  sleep 1
done

echo "$token" | base64 -d > "$OUT/token"
kubectl -n "$NS" get secret olve-pipelines-token -o jsonpath='{.data.ca\.crt}' | base64 -d > "$OUT/ca.crt"
printf '%s' "$NS" > "$OUT/namespace"
chmod 644 "$OUT/token" "$OUT/ca.crt" "$OUT/namespace"
