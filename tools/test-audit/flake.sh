#!/bin/bash
# Flakiness and parallel-scaling measurement. Prints one CSV line per run.
W=$(cd "$(dirname "$0")/../.." && pwd)
B=$W/src/backend/Olve.Pipelines.UnitTests/bin/Release/net11.0/Olve.Pipelines.UnitTests
O=$(realpath -m "${1:?usage: flake.sh <empty-output-dir>}"); mkdir -p "$O"
cd "$W" || exit 1
CSV=$O/runs.csv; echo "phase,run,par,exit,wall_s" > "$CSV"

one() { # phase run par
  local d="$O/$1-$2"; mkdir -p "$d"
  local args=(--no-progress --disable-logo --results-directory "$d" --report-trx --report-trx-filename r.trx --timeout 5m)
  [ "$3" != auto ] && args+=(--maximum-parallel-tests "$3")
  local t0=$(date +%s.%N)
  "$B" "${args[@]}" > "$d/out.txt" 2>&1
  local rc=$?
  echo "$1,$2,$3,$rc,$(echo "$(date +%s.%N) - $t0" | bc)" >> "$CSV"
}

for p in 1 2 4 8 16 32 auto; do for r in 1 2 3; do one scale "$p$r" "$p"; done; done
for r in $(seq 1 30); do one idle "$r" auto; done

burn=()
for i in $(seq 1 32); do (while :; do :; done) & burn+=($!); done
for r in $(seq 1 30); do one load "$r" auto; done
kill "${burn[@]}" 2>/dev/null; wait "${burn[@]}" 2>/dev/null

for r in $(seq 1 10); do
  for c in 1 2 3 4; do one concurrent "$r$c" auto & done
  wait
done
echo done > "$O/finished"
