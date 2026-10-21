#!/usr/bin/env bash
# Generates one Job per (step image, libc[, emptyDir medium]) for spike 1.
# Pod layout = the proposed v2 step-pod shape:
#   init "harness"  (harness image)  : cp -a /olve/. -> emptyDir, timed with /proc/uptime; writes input.json
#   init "ldlist"   (step image)     : bundled loader in list mode -> every dep must resolve under /olve/lib
#   init "cold"     (step image)     : first harness run
#   main "step"     (step image)     : /olve/step run <name> through the bundled loader
set -euo pipefail
TAG=${TAG:-2}
GARAGE=http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900/
FETCH="https://registry.npmjs.org/zod,https://registry.ovea.pro/v2/,$GARAGE"
declare -A IMG=(
  [debian]=debian:bookworm-slim
  [dotnet]=mcr.microsoft.com/dotnet/sdk:10.0-noble
  [alpine]=alpine:latest
  [kaniko]=gcr.io/kaniko-project/executor:debug
  [distroless]=gcr.io/distroless/static-debian12
)
loader() { [ "$1" = glibc ] && echo /olve/lib/ld-linux-x86-64.so.2 || echo /olve/lib/ld-musl-x86_64.so.1; }
job() { # name image libc medium child
  local n=$1 img=$2 libc=$3 medium=$4 child=${5:-} ld; ld=$(loader "$libc")
  cat <<YAML
---
apiVersion: batch/v1
kind: Job
metadata:
  name: spike-h-$n
  namespace: olve-runners-beta
  labels: { spike: v2-harness, app: olve-pipelines }
spec:
  backoffLimit: 0
  ttlSecondsAfterFinished: 3600
  template:
    metadata:
      labels: { spike: v2-harness, app: olve-pipelines, spike-case: "$n" }
    spec:
      runtimeClassName: gvisor
      restartPolicy: Never
      automountServiceAccountToken: false
      enableServiceLinks: false
      volumes:
        - name: olve
          emptyDir: { $( [ "$medium" = mem ] && echo 'medium: Memory, sizeLimit: 256Mi' || echo 'sizeLimit: 512Mi') }
      initContainers:
        - name: harness
          image: registry.ovea.pro/olve-pipelines-v2/spike-harness-$libc:$TAG
          imagePullPolicy: Always
          command: ["/bin/sh", "-c"]
          args:
            - |
              a=\$(cut -d' ' -f1 /proc/uptime); cp -a /olve/. /shared/; b=\$(cut -d' ' -f1 /proc/uptime)
              mkdir -p /shared/work /shared/tmp; echo '{"case":"$n","libc":"$libc"}' > /shared/input.json
              echo "COPY_SECONDS=\$(awk "BEGIN{print \$b-\$a}") SIZE=\$(du -sh /shared | cut -f1)"
          volumeMounts: [{ name: olve, mountPath: /shared }]
          resources: { requests: { cpu: 100m, memory: 64Mi }, limits: { memory: 512Mi } }
        - name: ldlist
          image: $img
          imagePullPolicy: IfNotPresent
          command: ["$ld", "--library-path", "/olve/lib", "--list", "/olve/step"]
          volumeMounts: [{ name: olve, mountPath: /olve }]
          resources: { requests: { cpu: 100m, memory: 64Mi }, limits: { memory: 512Mi } }
        - name: cold
          image: $img
          imagePullPolicy: IfNotPresent
          command: ["$ld", "--library-path", "/olve/lib", "/olve/step", "run", "cold"]
          env: [{ name: TMPDIR, value: /olve/tmp }, { name: OLVE_OUTPUT, value: /olve/work/cold.json }]
          volumeMounts: [{ name: olve, mountPath: /olve }]
          resources: { requests: { cpu: 250m, memory: 128Mi }, limits: { memory: 512Mi } }
      containers:
        - name: step
          image: $img
          imagePullPolicy: IfNotPresent
          command: ["$ld", "--library-path", "/olve/lib", "/olve/step", "run", "$n"]
          env:
            - { name: TMPDIR, value: /olve/tmp }
            - { name: OLVE_FETCH, value: "$FETCH" }
$( [ -n "$child" ] && echo "            - { name: OLVE_CHILD, value: \"$child\" }" )
          volumeMounts: [{ name: olve, mountPath: /olve }]
          resources: { requests: { cpu: 250m, memory: 128Mi }, limits: { memory: 512Mi } }
YAML
}
for k in debian dotnet alpine kaniko distroless; do
  child=""; [ $k = dotnet ] && child="dotnet --version && git --version"
  [ $k = kaniko ] && child="/kaniko/executor version"
  for libc in glibc musl; do job "$k-$libc" "${IMG[$k]}" $libc disk "$child"; done
done
job alpine-glibc-mem alpine:latest glibc mem
job alpine-musl-mem alpine:latest musl mem
