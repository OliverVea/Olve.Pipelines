#!/bin/sh
# Install the `pl` operator CLI on Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/bootstrap.sh | sh
#
# Downloads the latest pl binary from the Olve.Pipelines instance (served at
# /download/pl-linux-x64) and drops it on your PATH. The instance is reachable over
# Tailscale at the default URL below — the same host the CLI talks to by default — so
# if you can use `pl` at all, you can install it from here. Override with PL_API_URL
# (e.g. https://pipelines-beta.ovea.pro) and the target dir with PL_INSTALL_DIR.
set -e

BASE="${PL_API_URL:-https://pipelines-private.ovea.pro}"
DEST="${PL_INSTALL_DIR:-$HOME/.local/bin}"

arch=$(uname -m)
case "$arch" in
  x86_64 | amd64) asset=pl-linux-x64 ;;
  *) echo "Unsupported architecture: $arch (only linux x86_64 is published)" >&2; exit 1 ;;
esac

mkdir -p "$DEST"
echo "Downloading $asset from $BASE ..."
curl -fsSL "$BASE/download/$asset" -o "$DEST/pl"
chmod +x "$DEST/pl"
echo "Installed pl to $DEST/pl"

case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo "Note: $DEST is not on your PATH — add it, e.g. export PATH=\"$DEST:\$PATH\"" ;;
esac
