#!/usr/bin/env bash
# Finds order dependence the test binary can't shuffle for: runs tests with their usual predecessors removed.
# Each run is serial inside its process, so neighbours run in the sorted order and not by chance.
# Usage: order-isolation.sh <results-dir> [parallel-jobs=4] [subset-runs=20] [seed=1]
# Prerequisite: dotnet build src/backend/Olve.Pipelines.UnitTests -c Release
set -u
OUT=$(realpath -m "$1"); JOBS=${2:-4}; RUNS=${3:-20}; SEED=${4:-1}
REPO=$(git rev-parse --show-toplevel)
BIN=$REPO/src/backend/Olve.Pipelines.UnitTests/bin/Release/net10.0/Olve.Pipelines.UnitTests
COMMON=(--no-ansi --disable-logo --no-progress --maximum-parallel-tests 1)
mkdir -p "$OUT/logs"; cd "$(dirname "$BIN")" || exit 1

"$BIN" --no-ansi --disable-logo --no-progress --report-trx --report-trx-filename all.trx --results-directory "$OUT" > "$OUT/logs/all.log" 2>&1
[ $? -eq 0 ] || { echo "the full run failed; fix that first"; exit 1; }
python3 - "$OUT" <<'PY'
import re, sys
out = sys.argv[1]
pairs = set(re.findall(r'<TestMethod [^>]*className="([^"]+)" name="([^"]+)"', open(f"{out}/all.trx").read()))
classes = sorted({c.split(".")[-1] for c, _ in pairs})
open(f"{out}/classes.txt", "w").write("\n".join(classes) + "\n")
open(f"{out}/methods.txt", "w").write("\n".join(sorted(f'{c.split(".")[-1]}/{m}' for c, m in pairs)) + "\n")
PY
echo "$(wc -l < "$OUT/classes.txt") classes, $(wc -l < "$OUT/methods.txt") methods"

run_one() { # label filter
  local log="$OUT/logs/$1.log"
  "$BIN" "${COMMON[@]}" --treenode-filter "$2" --results-directory "$OUT/r/$1" > "$log" 2>&1
  local code=$?
  rm -rf "$OUT/r/$1"
  case $code in
    0) ;;
    8) echo "NOMATCH $1 $2" ;;   # zero tests ran: a filter problem such as a nested or generic class, not a pass
    *) echo "FAIL($code) $1 $2" ;;
  esac
}
export -f run_one; export OUT BIN; export COMMON_S="${COMMON[*]}"
wrapper='COMMON=($COMMON_S); run_one "$1" "$2"'

echo "== each class alone, serial within the class (a test relying on another class)"
while read -r c; do
  printf '%s\0%s\0' "class-$c" "/*/*/$c/*"
done < "$OUT/classes.txt" | xargs -0 -n2 -P "$JOBS" bash -c "$wrapper" _ | tee "$OUT/class.result"

echo "== each method alone (a test relying on an earlier test in its class)"
while IFS=/ read -r c m; do
  printf '%s\0%s\0' "method-$c-$m" "/*/*/$c/$m"
done < "$OUT/methods.txt" | xargs -0 -n2 -P "$JOBS" bash -c "$wrapper" _ | tee "$OUT/method.result"

echo "== random half of the classes, serial, in one process (a test disturbed or needed by a neighbour)"
for i in $(seq 1 "$RUNS"); do
  picked=$(shuf --random-source=<(openssl enc -aes-256-ctr -pass "pass:$SEED-$i" -nosalt </dev/zero 2>/dev/null) "$OUT/classes.txt" \
    | head -n $(( $(wc -l < "$OUT/classes.txt") / 2 )) | paste -sd'|')
  run_one "subset-$i" "/*/*/($picked)/*" | tee -a "$OUT/subset.result"
  echo "$i $picked" >> "$OUT/subset.classes"
done
cat "$OUT"/*.result | sort | uniq -c | sort -rn | head -50
