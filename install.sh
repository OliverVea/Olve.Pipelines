#!/bin/sh
# Install the `pl` operator CLI on Linux.
#
#   curl -fsSL https://<instance>/install.sh | sh
#
# Downloads the latest pl binary from an Olve.Pipelines instance (served anonymously at
# /download/pl-linux-x64) and drops it on your PATH. Fetched from an instance, the default
# below is that instance (the app rewrites it when serving this script); fetched from the repo,
# it is v1 prod over Tailscale. Override the instance with PL_API_URL and the target dir with
# PL_INSTALL_DIR.
set -e

BASE="${PL_API_URL:-https://pipelines-private.ovea.pro}"
BASE="${BASE%/}"
DEST="${PL_INSTALL_DIR:-$HOME/.local/bin}"

arch=$(uname -m)
case "$arch" in
  x86_64 | amd64) asset=pl-linux-x64 ;;
  *) echo "Unsupported architecture: $arch (only linux x86_64 is published)" >&2; exit 1 ;;
esac

mkdir -p "$DEST"
echo "Downloading $asset from $BASE ..."
# Download next to the target and move into place, so a failed download never leaves a broken pl.
curl -fsSL "$BASE/download/$asset" -o "$DEST/pl.download"
chmod +x "$DEST/pl.download"
mv -f "$DEST/pl.download" "$DEST/pl"
echo "Installed $("$DEST/pl" --version 2>/dev/null || echo pl) to $DEST/pl"

case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo "Note: $DEST is not on your PATH — add it, e.g. export PATH=\"$DEST:\$PATH\"" ;;
esac

# pl's built-in default instance is v1 prod; `pl login --api-url` saves this one in ~/.pl.
echo "Next: pl login --api-url $BASE"
