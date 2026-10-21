#!/usr/bin/env bash
# Rebuilds the spike harness images: compile step.ts for glibc + musl, extract each libc's
# loader/libs from a base image, build the two-layer harness image, push to registry.ovea.pro.
set -euo pipefail
cd "$(dirname "$0")/harness"
TAG=${TAG:-2}; B=${BUN:-~/.bun/bin/bun}
$B build --compile --target=bun-linux-x64      step.ts --outfile out/step-glibc
$B build --compile --target=bun-linux-x64-musl step.ts --outfile out/step-musl
mkdir -p rt/glibc rt/musl
docker run --rm -v "$PWD/rt:/rt" debian:bookworm-slim sh -c 'cp -L /lib64/ld-linux-x86-64.so.2 /lib/x86_64-linux-gnu/libc.so.6 /lib/x86_64-linux-gnu/libpthread.so.0 /lib/x86_64-linux-gnu/libdl.so.2 /lib/x86_64-linux-gnu/libm.so.6 /rt/glibc/; chown -R '"$(id -u)"' /rt'
docker run --rm -v "$PWD/rt:/rt" alpine:3.22 sh -c 'apk add -q libstdc++ libgcc >/dev/null; cp -L /lib/ld-musl-x86_64.so.1 /usr/lib/libstdc++.so.6 /usr/lib/libgcc_s.so.1 /rt/musl/; chown -R '"$(id -u)"' /rt'
for libc in glibc musl; do
  d=img-$libc; rm -rf $d/lib $d/bin; mkdir -p $d/lib $d/bin; cp rt/$libc/* $d/lib/; cp out/step-$libc $d/bin/step
  cp Dockerfile.harness $d/Dockerfile
  docker build --provenance=false -q -t registry.ovea.pro/olve-pipelines-v2/spike-harness-$libc:$TAG $d
  docker push -q registry.ovea.pro/olve-pipelines-v2/spike-harness-$libc:$TAG
done
