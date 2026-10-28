# harness

The step pod runtime for Olve.Pipelines v2, "design B" (see `docs/design/pipelines-v2-spikes.md`,
decision 2). One stable init image, `harness-base`, fetches the per-commit step binary and the
run's `input.json` into an emptyDir. The step container can then be any x86-64 image, glibc or
musl, with or without a shell. It runs the binary through the bundled glibc loader.

```
harness/
  base/Dockerfile     harness-base image (static busybox + /opt/olve/lib + olve-init)
  base/olve-init      init container entrypoint (POSIX sh)
  base/build.sh       build + push, content-derived tag
  validate/           end-to-end check on the beta cluster (validate.sh, job.yaml, step.ts, s3.ts)
```

## Image

`ghcr.io/olivervea/olve-pipelines-harness-base:<tag>`, public. The pipeline's `publish-harness` step
(`steps.imageBuild` with `push` and `contentTag`) builds and pushes it whenever `src/harness/base`
changes, and `deploy-beta` / `deploy-prod` set the chart's `harness.image` from that step's output, so
a deployed server always runs the image of its own commit. The server's built-in default (used
without that value, e.g. by `pl bootstrap` and `pl dev run --docker`) is `HarnessImage.Default` in
`src/backend/Olve.Pipelines/Kubernetes/Harness/HarnessImage.cs`.

The image is 2.4 MB compressed and contains:

| path | what |
|---|---|
| `/bin/*` | `busybox:1.37-musl` (static): `sh`, `wget` (plain HTTP is fine), `tar`/`gzip`, `cp` |
| `/opt/olve/zstd/` | `zstd` from Alpine with its musl loader and `libzstd` (run through the loader; busybox has no zstd) |
| `/opt/olve/lib/` | `ld-linux-x86-64.so.2`, `libc.so.6`, `libpthread.so.0`, `libdl.so.2`, `libm.so.6`, copied with `cp -L` from `debian:bookworm-slim` |
| `/opt/olve/bin/olve-init` | entrypoint |

Both base images are pinned by digest in the Dockerfile, so the build needs no local files and is
reproducible. The tag is `src-` plus the first 12 hex characters of the sha256 of the directory's
deterministic tar (the SDK's `contentTagOf`, covering every file, its content and its exec bit). The
same content always gives the same tag. `src/sdk/tests/runtime/harness-image.test.ts` fails when
`HarnessImage.Tag` no longer matches the directory.

The step binary must be built with `bun build --compile --target=bun-linux-x64` on a glibc Bun
image. Its PT_INTERP is `/lib64/ld-linux-x86-64.so.2`, but it is always launched through
`/olve/lib/ld-linux-x86-64.so.2` instead. There is no patchelf and no `LD_LIBRARY_PATH`, so child
processes see the image's own libc.

## olve-init contract

The init container mounts the emptyDir at **`/shared`**; the step container mounts it at `/olve`.
The image keeps its own files under `/opt/olve`, outside `/olve`: a step that builds this image
(kaniko in `build-harness`) runs with the emptyDir at `/olve` and its own loader in `/olve/lib`, so
an image writing there would collide with it ("text file busy"), and kaniko leaves mount points out
of its snapshots.

| env | required | meaning |
|---|---|---|
| `OLVE_STEP_URL` | yes | Pre-signed GET of a zstd- or gzip-compressed tar with `step` at its root (told apart by its first bytes) |
| `OLVE_INPUT_URL` | no | Pre-signed GET of `input.json`. If unset, no `input.json` is written |
| `OLVE_SHARED` | no | Shared mount (default `/shared`) |
| `OLVE_FETCH_RETRIES` | no | Attempts per download (default 5, linear backoff 2s, 4s, …) |
| `OLVE_FETCH_TIMEOUT` | no | wget network idle timeout in seconds (default 60) |

On success, `<shared>` (the step container's `/olve`) contains:

```
step            0755, the compiled harness binary
lib/            glibc loader + libs
input.json      if OLVE_INPUT_URL was given
work/           1777, the step writes work/output.json here
work/files/     1777, output files
tmp/            1777, the step's TMPDIR
```

The 1777 permissions let step images that run as non-root write there. This was validated with
`runAsUser: 65534`.

Behaviour and exit codes:

- `0`: ready. The last log line is a parseable timing line:
  `olve-init: timing step_fetch=… extract=… input_fetch=… lib=… total=…`
- `64`: bad config, such as `OLVE_STEP_URL` unset, a non-http(s) URL, or a missing mount.
- `65`: download failed.
  - Retries cover DNS errors, refused or reset connections, timeouts, 5xx, 408 and 429.
  - Any other **4xx fails immediately**, because an expired or tampered pre-signed URL never heals.
  - On cluster, a 403 failed in under 1 s, with a readable message.
- `66`: the archive is neither zstd nor gzip, doesn't unpack, or has no `step` at its root.
- `70`: a filesystem error.
- Downloads go to a file first and are removed before each retry. Nothing is streamed into tar.
- **URLs are never logged past the path.** Everything from `?` onwards, which includes
  `X-Amz-Credential`, is stripped, in error messages too.

## Pod template (what the B5 executor should generate)

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: <job-name>
  namespace: olve-runners-<env>
  labels: { app: olve-pipelines, … }      # app=olve-pipelines on the POD template is what the
spec:                                     # runner NetworkPolicy selects (egress to apps-*/Garage)
  backoffLimit: 0
  activeDeadlineSeconds: <step timeout>
  ttlSecondsAfterFinished: <ttl>
  template:
    metadata:
      labels: { app: olve-pipelines, … }
    spec:
      runtimeClassName: gvisor
      restartPolicy: Never
      automountServiceAccountToken: false
      enableServiceLinks: false
      volumes:
        - name: olve
          emptyDir: { sizeLimit: 1Gi }
      initContainers:
        - name: olve-init
          image: ghcr.io/olivervea/olve-pipelines-harness-base:src-8d80fb6d74fc   # pin; never :latest
          imagePullPolicy: IfNotPresent
          env:
            - { name: OLVE_STEP_URL,  value: "<pre-signed GET versions/<p>/<version>/step.tar.gz>" }
            - { name: OLVE_INPUT_URL, value: "<pre-signed GET runs/<p>/<run>/<step>/<attempt>/input.json>" }
          volumeMounts: [{ name: olve, mountPath: /shared }]
          resources: { requests: { cpu: 100m, memory: 64Mi }, limits: { memory: 256Mi } }
      containers:
        - name: step
          image: <step image from the manifest>
          command: ["/olve/lib/ld-linux-x86-64.so.2", "--library-path", "/olve/lib", "/olve/step", "run", "<step-name>"]
          env:
            - { name: OLVE_DIR, value: /olve }
            - { name: TMPDIR,   value: /olve/tmp }
            # declared secrets only, one secretKeyRef each:
            # - { name: <SECRET>, valueFrom: { secretKeyRef: { name: olve-pipeline-<id>, key: <SECRET> } } }
          volumeMounts: [{ name: olve, mountPath: /olve }]
          resources: <from step config>
```

Notes for the executor:

- **Pre-sign against the in-cluster Garage host.** For beta that is
  `http://olve-pipelines-v2-garage.apps-beta.svc.cluster.local:3900`, path-style, region
  `us-east-1`. `Host` is part of the SigV4 signature, so a URL signed for any other host gets a 403.
- **Expiry.** The URL only has to outlive image pull plus init, which is seconds. A few minutes to
  an hour is plenty.
- **The URLs are secrets.** They sit in the pod spec env and carry the access key id, so don't log
  the Job spec. If that matters, move them into a per-Job K8s Secret with `secretKeyRef`.
- **Output (as implemented, `KubernetesJobExecutor`).** The harness PUTs `result.tar.gz` to
  `OLVE_RESULT_URL` (`runs/<p>/<run>/<step>/<attempt>/result.tar.gz`). Once the pod has a verdict, the
  executor copies the `step` log (and the `olve-init` log when the step never ran) to `…/log`, unpacks
  the archive into `…/output.json` and `…/files/…`, resolves the outcome (archive envelope first, the
  stdout result block as a fallback for the message), then deletes the archive and the per-attempt URL
  Secret `olve-urls-<attemptId>`. A container stuck in ErrImagePull / CreateContainerConfigError fails
  the attempt after ~90 s and its Job is deleted. Map-trigger Jobs (`olve-map-*`) use harness-base as
  the main image too and are read only from the result block in the log.
- **Pod Security.** The pod passes the namespace's `baseline` level. `kubectl` warns about
  `restricted`. That is fine for now.

## Timings (beta cluster, gVisor, 2026-10-21)

These were measured with a 42 MB `step.tar.gz` (a 91 MB binary) from v2 beta Garage.

- harness-base was pulled cold in the first batch.
- **All step images were already on the nodes.** The pod-to-done figure therefore excludes a cold
  pull of the step image.

| phase | 2 pods concurrently | 3 pods concurrently |
|---|---|---|
| harness-base pull (2.4 MB) | cached | 0.5 s (cold) |
| step fetch (plain HTTP, Garage) | 0.43 s | 1.0–1.3 s |
| extract (busybox gunzip + tar) | 1.3 s | 1.4–1.55 s |
| input fetch | 0.01 s | 0.01 s |
| copy lib | 0.01 s | 0.01 s |
| **olve-init total** | **1.7–1.8 s** | **2.4–2.9 s** |
| pod created → step container done | ~5 s | ~5 s |

Extraction dominated, so olve-init also unpacks zstd. Locally, the same binary as zstd -19 is 35 MB
and unpacks in 0.4 s against 0.96 s for gzip, with the same busybox tar. Beyond that: the shared
runtime plus a per-commit JS bundle (~1 MB), see spikes decision 9.

## Validation results

All of these ran on 2026-10-21 in `olve-runners-beta` with `runtimeClassName: gvisor`.

| step image | output.json | child via image shell (exit 3 propagated, no LD leak) |
|---|---|---|
| `debian:bookworm-slim` | yes | `/bin/sh`, Debian 12 |
| `alpine:latest` (musl) | yes | `/bin/sh`, Alpine |
| `gcr.io/kaniko-project/executor:debug` | yes | `/bin/sh` |
| `debian:bookworm-slim`, step `runAsUser: 65534` | yes, as uid 65534 | `/bin/sh` |
| tampered signature (negative) | init exit 65 in <1 s: `HTTP/1.1 403 Forbidden` | n/a |

To re-run (needs `kubectl` access to `apps-beta` and `olve-runners-beta`, plus `bun`):

```bash
src/harness/validate/validate.sh            # uses harness-base:<current content tag>
```

The script works in these steps:

1. It compiles `validate/step.ts`.
2. It uploads `step.tar.gz` and `input.json` to Garage under `spike/harness-base/`, through a
   `kubectl port-forward`. The credentials come from the `olve-pipelines-minio` secret and are
   never printed.
3. It pre-signs URLs for the in-cluster host.
4. It runs one Job per step image, labelled `spike: v2-harness-base`.
5. It prints the timing and `OUTPUT` lines.
6. It deletes the Jobs and the Garage objects when it exits.

## Rebuild / push

The pipeline's `publish-harness` step does this on every push that changes `src/harness/base`. By hand:

```bash
src/harness/base/build.sh          # docker buildx, --platform linux/amd64, pushes :<tag>
PUSH=0 src/harness/base/build.sh   # build only
```

You need Bun (for the tag), Docker without sudo and `docker login ghcr.io`. Run it from a Linux
checkout: the tag covers exec bits and line endings. After changing `olve-init` or the Dockerfile:

1. Rebuild.
2. Run `validate.sh`.
3. Set `HarnessImage.Tag` to the new tag (the SDK test above prints the expected one), and
   `HARNESS_BASE_IMAGE` in `src/cli/src/dev/session.ts` (`pl dev --docker`) to match. The
   executor pins a tag; it never uses `latest`.
4. To bump the glibc libs, change the `debian:bookworm-slim@sha256:` digest. The step binary only
   needs a glibc at least as new as the one Bun was linked against.
