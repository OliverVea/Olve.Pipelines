#!/bin/sh
# Build the `pl` operator CLI for linux + windows and publish the binaries to this
# instance's MinIO, where the app serves them anonymously from GET /download/<asset>.
# Runs as the LAST processing step (after deploy): a CLI build failure must not block the
# app deploy, and running here lets us stamp the binaries with the same VERSION as the
# deployed app (read from the bundle).
#
# Platform note: linux-x64 is a real Native-AOT binary (small, no runtime). Native AOT
# CANNOT cross-compile to Windows from a Linux runner, so win-x64 ships as a self-contained
# single-file IL build instead — one .exe, nothing to install, just larger (~70MB).
set -e

# Fetch the shared helper library (see build.sh for the fetch-to-file rationale). Swap `main` to pin.
mkdir -p /tmp
wget --no-check-certificate -qO /tmp/olve-lib.sh \
  https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/.pipelines/scripts/olve-lib.sh
. /tmp/olve-lib.sh

REPO=OliverVea/Olve.Pipelines
BRANCH=main
SRC=/src
CLI="$SRC/src/Olve.Pipelines.Cli"

# The app's in-cluster MinIO (cross-namespace from the olve-runners job) and the bucket the
# app serves /download from — must match helm values (Storage__Endpoint / Storage__Bucket).
# Credentials come from the pipeline secret (olve-pipeline-{id}), auto-mounted as env vars:
#   MINIO_ACCESS_KEY / MINIO_SECRET_KEY  (declared in config.yaml secrets:).
MINIO_ENDPOINT=http://olve-pipelines-minio.apps.svc.cluster.local:9000
MINIO_BUCKET=olve-pipelines

# Canonical version: reuse the build step's stamp from the bundle so the published CLI is
# tagged with the same version as the app this run just deployed.
INPUT_DIR=$(olve_bundle_input)
VERSION=$(cat "$INPUT_DIR/version.txt")

# Native-AOT linux link needs clang + zlib headers (the SDK image ships neither); curl pulls mc.
apt-get update -qq
apt-get install -y --no-install-recommends clang zlib1g-dev curl ca-certificates >/dev/null
curl -fsSL https://dl.min.io/client/mc/release/linux-amd64/mc -o /usr/local/bin/mc
chmod +x /usr/local/bin/mc

olve_fetch_repo "$REPO" "$BRANCH" "$SRC"

# VERSION is a timestamp (YYYYMMDD-HHMMSS), so stamp it as InformationalVersion (free-form)
# rather than -p:Version, which sets AssemblyVersion/FileVersion and rejects the hyphen.

# linux-x64: Native AOT (PublishAot=true lives in the csproj) -> /out/linux/pl
dotnet publish "$CLI" -c Release -r linux-x64 \
  -p:InformationalVersion="$VERSION" -o /out/linux

# win-x64: AOT off + self-contained single-file (cross-compiles cleanly from Linux) -> /out/win/pl.exe
dotnet publish "$CLI" -c Release -r win-x64 \
  -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true \
  -p:InformationalVersion="$VERSION" -o /out/win

mc alias set store "$MINIO_ENDPOINT" "$MINIO_ACCESS_KEY" "$MINIO_SECRET_KEY"

# Publish to a stable `latest/` (what bootstrap.sh/.ps1 fetch) and an immutable `<version>/` archive.
for channel in latest "$VERSION"; do
  mc cp /out/linux/pl     "store/$MINIO_BUCKET/cli/$channel/pl-linux-x64"
  mc cp /out/win/pl.exe   "store/$MINIO_BUCKET/cli/$channel/pl-win-x64.exe"
done

echo "Published pl $VERSION (linux-x64 AOT, win-x64 self-contained) to $MINIO_BUCKET/cli/{latest,$VERSION}/"
