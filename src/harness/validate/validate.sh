#!/usr/bin/env bash
# End-to-end validation of a step pod on a cluster you choose:
#   compile step.ts (bun-linux-x64) -> step.tar.gz + input.json into S3 under spike/harness-base/ ->
#   pre-sign GETs for the S3 host the pods reach -> one Job per step image (debian, alpine, kaniko:debug)
#   -> print logs + timings -> delete Jobs and S3 objects.
#
#   src/harness/validate/validate.sh [harness-image]   (default: $REPO:<current content tag>)
#
# Env, all required unless a default is given:
#   NAMESPACE                        where the Jobs run, in kubectl's current context
#   RUNTIME_CLASS                    the pods' runtimeClassName (default: none)
#   REPO                             (default: ghcr.io/olivervea/olve-pipelines-harness-base)
#   S3_UPLOAD_ENDPOINT               reachable from here (e.g. a kubectl port-forward)
#   S3_SIGN_ENDPOINT                 reachable from the pods
#   S3_BUCKET, S3_ACCESS_KEY, S3_SECRET_KEY
#
# Pre-signed URLs and S3 credentials are kept in a private temp dir or the environment and never printed.
set -euo pipefail
cd "$(dirname "$0")"

: "${NAMESPACE:?set NAMESPACE}" "${S3_UPLOAD_ENDPOINT:?set S3_UPLOAD_ENDPOINT}" "${S3_SIGN_ENDPOINT:?set S3_SIGN_ENDPOINT}"
: "${S3_BUCKET:?set S3_BUCKET}" "${S3_ACCESS_KEY:?set S3_ACCESS_KEY}" "${S3_SECRET_KEY:?set S3_SECRET_KEY}"
export NAMESPACE S3_UPLOAD_ENDPOINT S3_SIGN_ENDPOINT S3_BUCKET S3_ACCESS_KEY S3_SECRET_KEY
RUNTIME_CLASS=${RUNTIME_CLASS:-}
BUN=${BUN:-$(command -v bun || echo ~/.bun/bin/bun)}
if [ -n "${1:-}" ]; then
  HARNESS_IMAGE=$1
else
  TAG=$(cd ../base && "$BUN" -e 'import { contentTagOf } from "../../sdk/src/library/image-build.ts"; import { tmpdir } from "node:os"; console.log(contentTagOf(".", tmpdir()));')
  HARNESS_IMAGE=${REPO:-ghcr.io/olivervea/olve-pipelines-harness-base}:$TAG
fi
export HARNESS_IMAGE
PREFIX=spike/harness-base/
W=$(mktemp -d); chmod 700 "$W"

cleanup() {
  kubectl -n "$NAMESPACE" delete job -l spike=v2-harness-base --ignore-not-found --wait=false >/dev/null || true
  "$BUN" s3.ts rm-prefix "$PREFIX" || true
  rm -rf "$W"
}
trap cleanup EXIT

mkdir -p "$W/pkg"
"$BUN" build --compile --target=bun-linux-x64 step.ts --outfile "$W/pkg/step" >/dev/null
tar -czf "$W/step.tar.gz" -C "$W/pkg" step
echo '{"hello":"harness-base","n":7}' >"$W/input.json"
"$BUN" s3.ts put "$W/step.tar.gz" "${PREFIX}step.tar.gz"
"$BUN" s3.ts put "$W/input.json" "${PREFIX}input.json"
"$BUN" s3.ts presign "${PREFIX}step.tar.gz" "$W/step.url" 900 >/dev/null
"$BUN" s3.ts presign "${PREFIX}input.json" "$W/input.url" 900 >/dev/null
STEP_URL=$(cat "$W/step.url"); INPUT_URL=$(cat "$W/input.url"); export STEP_URL INPUT_URL

runtime_class() {
  if [ -n "$RUNTIME_CLASS" ]; then sed "s/^\( *\)restartPolicy:/\1runtimeClassName: $RUNTIME_CLASS\n&/"; else cat; fi
}
render() {
  JOB_NAME=$1 CASE=$2 STEP_IMAGE=$3 envsubst \
    '${JOB_NAME} ${CASE} ${STEP_IMAGE} ${HARNESS_IMAGE} ${STEP_URL} ${INPUT_URL} ${NAMESPACE}' <job.yaml | runtime_class
}
{
  render hb-debian debian debian:bookworm-slim; echo ---
  render hb-alpine alpine alpine:latest; echo ---
  render hb-kaniko kaniko gcr.io/kaniko-project/executor:v1.24.0-debug@sha256:2562c4fe551399514277ffff7dcca9a3b1628c4ea38cb017d7286dc6ea52f4cd
} | kubectl apply -f - 2>/dev/null
kubectl -n "$NAMESPACE" wait --for=condition=complete job -l spike=v2-harness-base --timeout=300s || true

ok=0
for c in debian alpine kaniko; do
  echo "=== $c"
  P=$(kubectl -n "$NAMESPACE" get pod -l spike=v2-harness-base,spike-case=$c -o jsonpath='{.items[0].metadata.name}')
  kubectl -n "$NAMESPACE" logs "$P" -c olve-init | grep -E 'timing|ERROR' || true
  kubectl -n "$NAMESPACE" logs "$P" -c step | grep '^OUTPUT ' && ok=$((ok + 1)) || echo "no output.json"
  kubectl -n "$NAMESPACE" get pod "$P" -o jsonpath='init {.status.initContainerStatuses[0].state.terminated.startedAt}..{.status.initContainerStatuses[0].state.terminated.finishedAt}  step rc={.status.containerStatuses[0].state.terminated.exitCode}{"\n"}'
done
echo "$ok/3 step images produced output.json"
[ "$ok" = 3 ]
