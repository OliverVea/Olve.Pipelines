#!/usr/bin/env bash
# The real-system and browser tiers as one pass on this machine, from a copy of the tree: the deploy's
# real-system-test step runs it over SSH. The two tiers run in turn, because the real-system tests can
# reset the host the browser tests use. The host always comes down, and then this copy of the tree goes
# too. A watchdog stops early when the machine runs short, because it also runs prod, and at a deadline.
# A cancelled step reaches this script as SIGHUP, which needs the terminal the step asks SSH for.
#
#   tools/real-system/gate.sh
#
# Env: GATE_SLOT (default 9, away from the slots people use), GATE_MIN_START_GIB (default 8),
# GATE_MIN_RUN_GIB (default 4), GATE_DEADLINE_SECONDS (default 1700, under the step's timeout).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.bun/bin:$PATH"
# shellcheck disable=SC1091
[ ! -s "$HOME/.nvm/nvm.sh" ] || . "$HOME/.nvm/nvm.sh"

slot="${GATE_SLOT:-9}"
min_start="${GATE_MIN_START_GIB:-8}"
min_run="${GATE_MIN_RUN_GIB:-4}"
deadline=$((SECONDS + ${GATE_DEADLINE_SECONDS:-1700}))
export TEST_TARGETS="http://localhost:$((5000 + slot * 10))"

available_gib() { awk '/MemAvailable/ { print int($2 / 1048576) }' /proc/meminfo; }
host() { tools/real-system/host.sh "$1" "$slot"; }

free_gib="$(available_gib)"
if [ "$free_gib" -lt "$min_start" ]; then
  echo "gate: only ${free_gib} GiB available, the tiers need ${min_start} GiB; rerun this step once the machine has room" >&2
  exit 1
fi

host down >/dev/null 2>&1 || true
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
  host down >/dev/null 2>&1 || true
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
  "$@" &
  wait $!
}

contract() { cd src/spec && bun install --frozen-lockfile && bun run build; }
frontend() { cd src/frontend && npm ci --no-audit --no-fund; }
browser() {
  cd src/frontend/e2e
  npm ci --no-audit --no-fund
  npx playwright install chromium
  npm test
}

stage contract
stage host up
stage dotnet run --project src/backend/Olve.Pipelines.RealSystemTests -- --no-progress
stage frontend
stage tools/real-system/host.sh ui
stage browser
