#!/bin/sh
# Describe/reconcile sandbox stand-in: fetch repo tarball -> bun install -> describe -> compile.
set -e
now() { cut -d' ' -f1 /proc/uptime; }
lap() { n=$(now); echo "TIMING $1 $(awk "BEGIN{printf \"%.2f\", $n-$T}")s"; T=$n; }
T0=$(now); T=$T0
cd /work
echo "bun $(bun --version)"
getent hosts codeload.github.com registry.npmjs.org || true; lap dns
wget -q -O src.tgz https://codeload.github.com/OliverVea/Olve.Pipelines/tar.gz/refs/heads/v2
mkdir src && tar xzf src.tgz -C src --strip-components=1; echo "tarball $(du -h src.tgz | cut -f1), files $(find src -type f | wc -l)"; lap fetch+extract
mkdir -p src/.pipelines && cp /spike/package.json /spike/describe.ts src/.pipelines/ && cd src/.pipelines
bun install --no-progress 2>&1 | tail -3; lap bun-install
bun describe.ts > /work/describe.out; grep -c OLVE-MANIFEST /work/describe.out; cat /work/describe.out; lap describe
bun build --compile describe.ts --outfile /work/d-native >/dev/null; lap compile-native-musl
bun build --compile --target=bun-linux-x64 describe.ts --outfile /work/d-glibc >/dev/null; lap compile-cross-glibc
/work/d-native | head -c 80; echo; lap run-compiled
ls -la /work/d-native /work/d-glibc
echo "d-glibc interp: $(grep -c ld-linux-x86-64 /work/d-glibc) d-native musl-interp: $(grep -c ld-musl-x86_64 /work/d-native)"
find / -xdev -name "bun-linux-x64*" 2>/dev/null | head
echo "TIMING total $(awk "BEGIN{printf \"%.2f\", $(now)-$T0}")s"
# talk-back probes (no creds): service API, Garage, K8s API (must be blocked)
for u in http://olve-pipelines-v2.apps-beta.svc.cluster.local/ http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900/ https://kubernetes.default.svc/; do
  s=$(wget -S -T 5 -q -O /dev/null --no-check-certificate "$u" 2>&1 | grep -m1 HTTP/ || echo "no-http-response"); echo "PROBE $u -> $s"
done
