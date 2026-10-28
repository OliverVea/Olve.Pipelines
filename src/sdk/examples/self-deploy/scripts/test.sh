#!/bin/sh
# Fake test runner: prints the TestReport.
set -eu
echo "testing" >&2
printf '{"passed": 3, "failed": 0}\n'
