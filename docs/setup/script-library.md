# Shared Script Library (`olve-lib.sh`)

[← Index](index.md) · [Subject Index](subjects.md)

Step scripts re-vendor the same fiddly shell across every pipeline: the Kaniko build
block, the SSH/import/Helm deploy block. The friction isn't *structural* — it's
**knowledge**. Those blocks carry hard-won footguns (Kaniko wiping `/workspace`, the
k3s containerd socket, the busybox-`wget` TLS quirk). Copy-pasting them across repos
means re-learning — or re-breaking — each footgun per repo.

`olve-lib.sh` captures that knowledge **once**, as a library of POSIX-`sh` functions
sourced at runtime into a step's script. **This is pure scripting convenience** — it
adds no config field and no server feature. The config model, `$ref`, the bundle flow,
and validation are untouched; this lives entirely in userland.

## How a step sources it

The first lines of a step script fetch the library to a file and source it:

```sh
#!/bin/sh
set -e
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh
```

Notes:

- **`mkdir -p /tmp` first.** The `kaniko:debug` rootfs ships **no** `/tmp` directory, so
  `wget -O /tmp/olve-lib.sh` fails with ENOENT before it ever fetches. Create it first;
  it is a no-op in images that already have one.
- **Fetch-to-file, not `. <(...)`.** Process substitution (`<()`) is a bashism. Steps
  run in minimal images — busybox (`kaniko:debug`) and ash (`alpine`) — where it is a
  syntax error. Fetch to a file, then `.` (source) it.
- **`wget`, not `curl`.** `wget` is always present in busybox; `curl` is an extra
  `apk add` in alpine.
- **`--no-check-certificate`.** The Kaniko debug image's busybox `wget` fails TLS
  against some hosts without it; kept for parity across the build images.
- **Pin a revision** by swapping `main` in the URL for a tag or commit SHA. The library
  is served straight from the public repo, so it needs no token and no separate host —
  and a broken app deploy can't take down the library the next build needs to recover.

## Functions

POSIX `sh` only. Callers use UPPERCASE variables; helpers use lowercase — keep that
convention if you add functions (there is no `local` in POSIX `sh`, so names leak).

| Function | Purpose |
|---|---|
| `olve_version` | Echo the build version stamp (`date +%Y%m%d-%H%M%S`). Capture with `VERSION=$(olve_version)`. |
| `olve_fetch_repo <owner/repo> <branch> <dest>` | Fetch + unpack the repo tarball from the GitHub API into `<dest>` (strip-components=1). Needs `$GITHUB_TOKEN`. Carries the busybox-`wget` TLS workaround. |
| `olve_stage_artifact <src> <dest>` | Copy a deploy artifact (file or dir) into `/output` **before** Kaniko runs, so it travels in the bundle — Kaniko wipes the context root between multi-stage stages. |
| `olve_kaniko_build <context-dir> <image:tag>` | Build to `/output/image.tar` (no registry push). Uses the correct `/kaniko/build-context` and the multi-stage-safe flags (no `--single-snapshot`). |
| `olve_bundle_input` | Echo the build step's bundle dir (no trailing slash) by locating the dir that contains `version.txt` — ignoring the parallel code-test step's empty output. Use `"$INPUT_DIR/file"`. |
| `olve_ssh_host <host>` | Install the ssh client and set up key auth + `known_hosts` from `$SSH_PRIVATE_KEY`. Run once before import/deploy. |
| `olve_image_import <image-tar> <ssh-target>` | Stream an image tar over SSH and load it into the homelab **k3s** containerd (`/run/k3s/containerd/containerd.sock`) so pods can see it. |
| `olve_helm_deploy <ssh-target> <release> <namespace> <chart-dir> <version> [helm args…]` | Copy the chart to the host and `helm upgrade --install` from inside it (so `-f values-beta.yaml` resolves chart-relative), with `image.pullPolicy=Never` and `slo.enabled=false`. |

## Worked example — this repo's own steps

The dogfood: this repo's `.pipelines/scripts/*.sh` source the library. A build step
shrinks from ~40 lines of inline Kaniko shell to:

```sh
#!/bin/sh
set -e
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

VERSION=$(olve_version)
CTX=/kaniko/build-context

olve_fetch_repo OliverVea/Olve.Pipelines main "$CTX"
olve_stage_artifact "$CTX/helm" /output/helm
echo "$VERSION" > /output/version.txt
olve_kaniko_build "$CTX" "olve-pipelines:$VERSION"
```

And a beta deploy step. Note the chart-specific flags passed **through** the helper:
`-f values-beta.yaml` and `--set slo.enabled=false` (this chart defines an `slo` block
defaulting to `true`, but the sloth CRD isn't installed cluster-wide — so it's the
caller's to pass, not baked into the shared lib). `curl` is added for the post-deploy
health check, since `olve_ssh_host` installs only the ssh client:

```sh
#!/bin/sh
set -e
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh
apk add --no-cache curl

HOST=oliver@bulwark-m2
RELEASE=olve-pipelines

olve_ssh_host bulwark-m2
INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")

olve_image_import "$INPUT_DIR/image.tar" "$HOST"
olve_helm_deploy "$HOST" "$RELEASE" apps-beta "$INPUT_DIR/helm" "$VERSION" \
  -f values-beta.yaml --set slo.enabled=false
```

The shared lib is genuinely multi-repo: [QuestionBank](https://github.com/OliverVea/QuestionBank/tree/main/.pipelines/scripts)
sources the same `olve-lib.sh`, passing its own chart flags
(`-f values-minimal.yaml --set config.QB_ALLOW_DEFAULT_CUSTOMER=…`) through `olve_helm_deploy`
and keeping its repo-specific bits (Anthropic-key materialization, in-cluster health URLs) inline.

See the full scripts in
[`.pipelines/scripts/`](https://github.com/OliverVea/Olve.Pipelines/tree/main/.pipelines/scripts).

## See also

- [`config.yaml` Reference](config-reference.md) — `scriptFile`, the step `(image, script, env)` shape
- [Bundles & Execution](bundles-and-execution.md) — what `/input` and `/output` are, and how the bundle flows
- [Troubleshooting](troubleshooting.md) — symptoms → causes → fixes
