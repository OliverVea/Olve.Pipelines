#!/usr/bin/env bash
# Local (runc, not gVisor) pre-check of all harness variants across images.
cd "$(dirname "$0")/harness"  # NOTE: rerun build-harness.sh first (binaries are not kept)
. ../variants.sh
IMAGES=${IMAGES:-"debian:bookworm-slim alpine:3.22 gcr.io/distroless/static-debian12 gcr.io/kaniko-project/executor:debug"}
for img in $IMAGES; do
  for v in glibc_native glibc_loader glibc_patched musl_native musl_loader musl_patched; do
    var=V_$v; cmd=${!var}
    out=$(timeout 60 docker run --rm -q --entrypoint "" -e OLVE_OUTPUT=/tmp/o.json -v "$PWD/olve:/olve:ro" "$img" $cmd run build 2>&1); rc=$?
    echo "$img | $v | rc=$rc | $(echo "$out" | tr '\n' ' ' | cut -c1-160)"
  done
done
