#!/bin/sh
# Fake test runner: reports one passing test per input key; writes the TestReport to $OLVE_OUTPUT.
set -eu
echo "testing $INPUT_SOURCE" >&2
test -f "$OLVE_INPUT"
printf '{"passed": 3, "failed": 0}\n' > "$OLVE_OUTPUT"
