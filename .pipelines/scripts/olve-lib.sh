# olve-lib.sh — shared shell helpers for Olve.Pipelines step scripts.
#
# Captures the hard-won footgun knowledge for building with Kaniko and deploying to
# the homelab k3s cluster, so each pipeline sources it instead of re-vendoring ~40
# lines of fiddly shell. See https://github.com/OliverVea/Olve.Pipelines/issues/17.
#
# This is NOT a script to execute — it is a function library to source. A step's
# script fetches and sources it (fetch-to-file, NOT `. <(...)`: busybox/ash have no
# process substitution), then calls the helpers:
#
#   #!/bin/sh
#   set -e
#   wget -qO /tmp/olve-lib.sh https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
#   . /tmp/olve-lib.sh
#   olve_fetch_repo  OliverVea/Olve.Pipelines main /kaniko/build-context
#   ...
#
# Pin a revision by swapping `main` in the URL for a tag or commit SHA.
#
# POSIX sh only (these run in busybox `kaniko:debug` and alpine `ash` — no bashisms).

# olve_version — the build version stamp shared by build + deploy.
# Echoes a UTC-ish timestamp; capture it with VERSION=$(olve_version).
olve_version() {
  date +%Y%m%d-%H%M%S
}

# olve_fetch_repo <owner/repo> <branch> <dest-dir>
# Fetch the repo tarball from the GitHub API into <dest-dir> and unpack it in place
# (strip-components=1, so files land at <dest-dir>/... not <dest-dir>/<repo-sha>/...).
# Requires $GITHUB_TOKEN in the env.
#
# Footgun: the Kaniko debug image ships busybox wget, which fails TLS against the
# GitHub API without --no-check-certificate.
olve_fetch_repo() {
  repo=$1
  branch=$2
  dest=$3
  mkdir -p "$dest"
  cd "$dest"
  wget --no-check-certificate -q --header="Authorization: token $GITHUB_TOKEN" \
    -O repo.tar.gz "https://api.github.com/repos/$repo/tarball/$branch"
  tar xzf repo.tar.gz --strip-components=1
  rm repo.tar.gz
}

# olve_stage_artifact <src> <dest>
# Copy a deploy artifact from the build context into /output BEFORE Kaniko runs.
# Kaniko wipes the context root (including /workspace) between multi-stage build
# stages, so anything the deploy steps need (helm chart, version file) must be
# carried forward into the bundle first.
olve_stage_artifact() {
  src=$1
  dest=$2
  if [ -d "$src" ]; then
    cp -r "$src" "$dest"
  else
    cp "$src" "$dest"
  fi
}

# olve_kaniko_build <context-dir> <destination-image:tag>
# Build to an image tar (no registry push); the deploy steps import the tar onto the
# host. Writes the tar to /output/image.tar so it travels in the bundle.
#
# Footguns:
#  - Build context must live at /kaniko/build-context, NOT /workspace: Kaniko wipes /
#    (including /workspace) between multi-stage builds, breaking stage 2's COPY of
#    generated clients. The /kaniko dir survives stage transitions.
#  - No --single-snapshot: it is incompatible with the multi-stage Dockerfile, whose
#    stage 2 needs the build context intact.
olve_kaniko_build() {
  ctx=$1
  destination=$2
  /kaniko/executor \
    --context="$ctx" \
    --dockerfile="$ctx/Dockerfile" \
    --no-push \
    --tar-path=/output/image.tar \
    --destination="$destination"
}

# olve_bundle_input — resolve the build step's bundle dir and echo it (trailing slash).
# The bundle has one dir per production step, named with step GUIDs. Select the build
# output by the artifact it alone produces — version.txt — so the parallel code-test
# step's (empty) output dir is ignored. Capture with INPUT_DIR=$(olve_bundle_input).
olve_bundle_input() {
  dirname "$(ls /input/*/version.txt | head -1)"
  # dirname strips the trailing slash; callers append it (or use "$INPUT_DIR/file").
}

# olve_ssh_host <host>
# Install the ssh client and set up key auth + known_hosts so subsequent ssh/scp to
# <host> work non-interactively. Requires $SSH_PRIVATE_KEY in the env.
# Run once before olve_image_import / olve_helm_deploy.
olve_ssh_host() {
  host=$1
  apk add --no-cache openssh-client
  mkdir -p ~/.ssh
  echo "$SSH_PRIVATE_KEY" > ~/.ssh/id_ed25519
  chmod 600 ~/.ssh/id_ed25519
  ssh-keyscan -H "$host" >> ~/.ssh/known_hosts 2>/dev/null || true
}

# olve_image_import <image-tar> <ssh-target>
# Stream an image tar over SSH and load it into the homelab k3s containerd so pods
# can see it.
#
# Footgun: import via the k3s containerd socket (/run/k3s/containerd/containerd.sock),
# NOT the default one — kubelet only sees images in the k3s instance (else
# ErrImageNeverPull).
olve_image_import() {
  tar_path=$1
  target=$2
  cat "$tar_path" | ssh -o StrictHostKeyChecking=no "$target" \
    "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load"
}

# olve_helm_deploy <ssh-target> <release> <namespace> <local-chart-dir> <version> [extra helm args...]
# Copy the helm chart to the host and run helm upgrade --install against the imported
# image, then clean up the copied chart. Extra args (e.g. -f values-beta.yaml or
# chart-specific --set flags) are passed through verbatim.
#
# The upgrade runs from inside the copied chart dir (`helm ... .`), so a chart-relative
# values path like `-f values-beta.yaml` resolves without the caller knowing the remote
# /tmp path. Extra args land before the baked image --set flags, so the --set image.tag
# override still wins.
#
# This helper bakes in ONLY the image overrides that are universal to the import-then-deploy
# flow (image.repository / image.tag / image.pullPolicy=Never — the image is local to the
# node, imported above, not pulled). Chart-specific flags (e.g. --set slo.enabled=false for
# a chart that defines an slo block) are the caller's to pass through, so the lib stays
# repo-agnostic.
olve_helm_deploy() {
  target=$1
  release=$2
  namespace=$3
  chart=$4
  version=$5
  shift 5
  remote_chart="/tmp/$release-helm-$namespace"

  ssh -o StrictHostKeyChecking=no "$target" "rm -rf $remote_chart"
  scp -o StrictHostKeyChecking=no -r "$chart" "$target:$remote_chart"
  ssh -o StrictHostKeyChecking=no "$target" \
    "cd $remote_chart && helm upgrade --install $release . -n $namespace $* \
       --set image.repository=docker.io/library/$release \
       --set image.tag=$version --set image.pullPolicy=Never \
     && cd / && rm -rf $remote_chart"
}
