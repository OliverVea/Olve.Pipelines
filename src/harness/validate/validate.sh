#!/usr/bin/env bash
# End-to-end validation of a design-B step pod on the beta cluster:
#   compile step.ts (bun-linux-x64) -> step.tar.gz + input.json into v2 beta Garage under
#   spike/harness-base/ -> pre-sign GETs for the in-cluster Garage host -> one Job per step image
#   (debian, alpine, kaniko:debug) in olve-runners-beta under gVisor -> print logs + timings ->
#   delete Jobs and Garage objects.
#
#   src/harness/validate/validate.sh [harness-image]     (default: harness-base:<current content tag>)
#
# Pre-signed URLs and Garage credentials are kept in a private temp dir and never printed.
set -euo pipefail
cd "$(dirname "$0")"

NS=olve-runners-beta
BUN=${BUN:-$(command -v bun || echo ~/.bun/bin/bun)}
TAG=$(cat ../base/Dockerfile ../base/olve-init | sha256sum | cut -c1-12)
export HARNESS_IMAGE=${1:-registry.ovea.pro/olve-pipelines-v2/harness-base:$TAG}
PREFIX=spike/harness-base/
W=$(mktemp -d); chmod 700 "$W"

kubectl -n apps-beta port-forward svc/olve-pipelines-v2-garage 13900:3900 >"$W/pf.log" 2>&1 &
PF=$!
cleanup() {
  kubectl -n "$NS" delete job -l spike=v2-harness-base --ignore-not-found --wait=false >/dev/null || true
  "$BUN" s3.ts rm-prefix "$PREFIX" || true
  kill "$PF" 2>/dev/null || true
  rm -rf "$W"
}
trap cleanup EXIT
sleep 2

S3_ACCESS_KEY=$(kubectl -n apps-beta get secret olve-pipelines-minio -o jsonpath='{.data.root-user}' | base64 -d)
S3_SECRET_KEY=$(kubectl -n apps-beta get secret olve-pipelines-minio -o jsonpath='{.data.root-password}' | base64 -d)
export S3_ACCESS_KEY S3_SECRET_KEY
export S3_UPLOAD_ENDPOINT=http://127.0.0.1:13900
export S3_SIGN_ENDPOINT=http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900
export S3_BUCKET=olve-pipelines-v2-beta

mkdir -p "$W/pkg"
"$BUN" build --compile --target=bun-linux-x64 step.ts --outfile "$W/pkg/step" >/dev/null
tar -czf "$W/step.tar.gz" -C "$W/pkg" step
echo '{"hello":"harness-base","n":7}' >"$W/input.json"
"$BUN" s3.ts put "$W/step.tar.gz" "${PREFIX}step.tar.gz"
"$BUN" s3.ts put "$W/input.json" "${PREFIX}input.json"
"$BUN" s3.ts presign "${PREFIX}step.tar.gz" "$W/step.url" 900 >/dev/null
"$BUN" s3.ts presign "${PREFIX}input.json" "$W/input.url" 900 >/dev/null
STEP_URL=$(cat "$W/step.url"); INPUT_URL=$(cat "$W/input.url"); export STEP_URL INPUT_URL

render() {
  JOB_NAME=$1 CASE=$2 STEP_IMAGE=$3 envsubst \
    '${JOB_NAME} ${CASE} ${STEP_IMAGE} ${HARNESS_IMAGE} ${STEP_URL} ${INPUT_URL}' <job.yaml
}
{
  render hb-debian debian debian:bookworm-slim; echo ---
  render hb-alpine alpine alpine:latest; echo ---
  render hb-kaniko kaniko gcr.io/kaniko-project/executor:debug
} | kubectl apply -f - 2>/dev/null
kubectl -n "$NS" wait --for=condition=complete job -l spike=v2-harness-base --timeout=300s || true

ok=0
for c in debian alpine kaniko; do
  echo "=== $c"
  P=$(kubectl -n "$NS" get pod -l spike=v2-harness-base,spike-case=$c -o jsonpath='{.items[0].metadata.name}')
  kubectl -n "$NS" logs "$P" -c olve-init | grep -E 'timing|ERROR' || true
  kubectl -n "$NS" logs "$P" -c step | grep '^OUTPUT ' && ok=$((ok + 1)) || echo "no output.json"
  kubectl -n "$NS" get pod "$P" -o jsonpath='init {.status.initContainerStatuses[0].state.terminated.startedAt}..{.status.initContainerStatuses[0].state.terminated.finishedAt}  step rc={.status.containerStatuses[0].state.terminated.exitCode}{"\n"}'
done
echo "$ok/3 step images produced output.json"
[ "$ok" = 3 ]
