#!/bin/sh
# Fake deploy: checks its inputs exist and prints what it would do.
set -eu
test -f "$IMAGE_TAR"
test -d "$CHART_DIR"
echo "deploying $RELEASE $VERSION from $IMAGE_TAR with chart $CHART_DIR" >&2
