#!/usr/bin/env bash
# The real-system and browser tiers as one pass on this machine, from a copy of the tree: the deploy's
# real-system-test step runs it over SSH. The tests share a pool of hosts. The two tiers run in turn,
# because the real-system tests can reset a host the browser tests use. The hosts always come down, and
# then this copy of the tree goes too. A watchdog stops early when the machine runs short, because it
# also runs prod, and at a deadline. A cancelled step reaches this script as SIGHUP, which needs the terminal the step asks SSH for.
#
#   tools/real-system/gate.sh
#
# Env: GATE_SLOTS (default "7 8 9", away from the slots people use), GATE_MIN_START_GIB (default 8),
# GATE_MIN_RUN_GIB (default 4), GATE_DEADLINE_SECONDS (default 1700, under the step's timeout).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.bun/bin:$PATH"
# shellcheck disable=SC1091
[ ! -s "$HOME/.nvm/nvm.sh" ] || . "$HOME/.nvm/nvm.sh"

read -r -a slots <<< "${GATE_SLOTS:-7 8 9}"
min_start="${GATE_MIN_START_GIB:-8}"
min_run="${GATE_MIN_RUN_GIB:-4}"
deadline=$((SECONDS + ${GATE_DEADLINE_SECONDS:-1700}))
TEST_TARGETS="$(for s in "${slots[@]}"; do printf 'http://localhost:%d,' $((5000 + s * 10)); done)"
export TEST_TARGETS="${TEST_TARGETS%,}"

available_gib() { awk '/MemAvailable/ { print int($2 / 1048576) }' /proc/meminfo; }
down() {
  local pids=() pid
  for s in "${slots[@]}"; do tools/real-system/host.sh down "$s" >/dev/null 2>&1 & pids+=($!); done
  for pid in "${pids[@]}"; do wait "$pid" || true; done
}
# The first slot builds the server every slot serves; the others start on it at once.
up() {
  tools/real-system/host.sh up "${slots[0]}"
  local pids=() pid
  for s in "${slots[@]:1}"; do NO_BUILD=1 tools/real-system/host.sh up "$s" & pids+=($!); done
  for pid in "${pids[@]}"; do wait "$pid"; done
}

free_gib="$(available_gib)"
if [ "$free_gib" -lt "$min_start" ]; then
  echo "gate: only ${free_gib} GiB available, the tiers need ${min_start} GiB; rerun this step once the machine has room" >&2
  exit 1
fi

down
# Only one attempt of the step runs at a time, so any other copy is one a cancelled step left.
[ ! -d "$HOME/.cache/olve-gate" ] || find "$HOME/.cache/olve-gate" -mindepth 1 -maxdepth 1 ! -path "$PWD" -exec rm -rf {} +

gate=$$
(
  while sleep 5; do
    free_gib="$(available_gib)"
    if [ "$free_gib" -lt "$min_run" ]; then
      echo "gate: ${free_gib} GiB available, stopping the tiers" >&2
      break
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "gate: the deadline passed, stopping the tiers" >&2
      break
    fi
  done
  kill -TERM "$gate"
) &
watchdog=$!

cleanup() {
  kill "$watchdog" 2>/dev/null || true
  down
  case "$PWD" in
    "$HOME/.cache/olve-gate/"*) cd / && rm -rf "$OLDPWD" ;;
  esac
}
trap cleanup EXIT

stop() {
  trap - TERM HUP INT
  pkill -TERM -P "$gate" || true
  pkill -TERM -f "$PWD/" || true
  exit 1
}
trap stop TERM HUP INT

# A signal waits for a foreground command, which `dotnet run` outlives a hangup in. It interrupts `wait`.
stage() {
  local start=$SECONDS
  "$@" &
  wait $!
  echo "gate: $* took $((SECONDS - start))s"
}

contract() { cd src/spec && bun install --frozen-lockfile && bun run build; }
frontend() { cd src/frontend && npm ci --no-audit --no-fund; }
browser() {
  cd src/frontend/e2e
  npm ci --no-audit --no-fund
  npx playwright install chromium
  # The traces go with this copy of the tree, so a failure's page snapshot goes to the log.
  npm test || {
    find ../../../artifacts/real-system/browser/results -name error-context.md -exec sh -c 'echo "--- $1"; cat "$1"' _ {} \;
    return 1
  }
}

stage contract
stage up
stage dotnet run --project src/backend/Olve.Pipelines.RealSystemTests -- --no-progress --output Detailed
stage frontend
stage tools/real-system/host.sh ui
stage browser
