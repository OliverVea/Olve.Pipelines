#!/bin/sh
# Pretend test runner: counts lines of the "tree" file and reports.
set -eu
echo "running tests for $REPO@$SHA" >&2
n=$(wc -l < "$TREE")
echo "{\"passed\": $n, \"failed\": 0}"
