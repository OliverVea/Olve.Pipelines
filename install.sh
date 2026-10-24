#!/bin/sh
# Install the `pl` operator CLI on Linux.
#
#   curl -fsSL https://<instance>/install.sh | sh                                                    # the instance's pl
#   curl -fsSL https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/v2/install.sh | sh          # the latest release
#
# Served by an instance, it installs the pl that instance was deployed with (/download/pl-linux-x64);
# from the repo, the latest GitHub release. PL_API_URL picks an instance, PL_INSTALL_DIR the target dir.
set -e

INSTANCE="__OLVE_INSTANCE__"
RELEASES="https://github.com/OliverVea/Olve.Pipelines/releases/latest/download"
DEST="${PL_INSTALL_DIR:-$HOME/.local/bin}"

arch=$(uname -m)
case "$arch" in
  x86_64 | amd64) asset=pl-linux-x64 ;;
  *) echo "Unsupported architecture: $arch (only linux x86_64 is published)" >&2; exit 1 ;;
esac

if [ -n "$PL_API_URL" ]; then BASE="${PL_API_URL%/}"; URL="$BASE/download/$asset"
else
  case "$INSTANCE" in
    __OLVE*) BASE=""; URL="$RELEASES/$asset" ;;
    *) BASE="${INSTANCE%/}"; URL="$BASE/download/$asset" ;;
  esac
fi

mkdir -p "$DEST"
echo "Downloading $URL ..."
# Download next to the target and move into place, so a failed download never leaves a broken pl.
curl -fsSL "$URL" -o "$DEST/pl.download"
chmod +x "$DEST/pl.download"
mv -f "$DEST/pl.download" "$DEST/pl"
echo "Installed $("$DEST/pl" --version 2>/dev/null || echo pl) to $DEST/pl"

case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo "Note: $DEST is not on your PATH — add it, e.g. export PATH=\"$DEST:\$PATH\"" ;;
esac

echo "Next: pl login --api-url ${BASE:-https://<instance>}"
