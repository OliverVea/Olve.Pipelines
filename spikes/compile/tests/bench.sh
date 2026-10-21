#!/bin/sh
# Mean wall time of N invocations: tests/bench.sh <N> <cmd...>
N=$1; shift
"$@" >/dev/null 2>&1  # warm page cache
s=$(date +%s%N); i=0; while [ $i -lt $N ]; do "$@" >/dev/null 2>&1; i=$((i+1)); done; e=$(date +%s%N)
echo "$(( (e - s) / N / 1000000 )) ms mean over $N: $*"
