#!/usr/bin/env bash
# The throwaway host the real-system tests run against. The tests never create or start it.
#
#   tools/real-system/host.sh up [slot]      build the server, start the host, write its descriptor
#   tools/real-system/host.sh ui [slot]      build the web UI into the host, for the browser tests
#   tools/real-system/host.sh reset [slot]   wipe the host's state; images stay cached
#   tools/real-system/host.sh status [slot]  show the host's containers
#   tools/real-system/host.sh logs [slot]    follow the server log
#   tools/real-system/host.sh down [slot]    remove the host and all its data
#   tools/real-system/host.sh kubectl [slot] -- <args>   kubectl inside the throwaway cluster
#   tools/real-system/host.sh sweep          remove what tests left that is over two hours old, on every target
#
# Slot 0 is the default host, http://localhost:5000. Slot n listens on 5000+10n and has its own compose
# project, network and data, so a pool of three is `up 0`, `up 1`, `up 2`, then
# TEST_TARGETS=http://localhost:5000,http://localhost:5010,http://localhost:5020 for the tests.
#
# Run the tests with:
#   dotnet run --project src/backend/Olve.Pipelines.RealSystemTests
#
# Every cluster action goes through `docker compose exec` in this host's project: nothing here reads
# ~/.kube/config or calls kubectl or k3s on the machine.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
REPO_ROOT="$(cd ../.. && pwd)"

command="${1:-}"
slot="${2:-0}"
case "$slot" in ''|*[!0-9]*) echo "slot must be a number, got '$slot'" >&2; exit 64 ;; esac

export API_PORT=$((5000 + slot * 10))
export S3_PORT=$((15900 + slot * 10))
export KUBE_PORT=$((15643 + slot * 10))
export GITHUB_PORT=$((15680 + slot * 10))
export SUBNET=$((81 + slot))
export GARAGE_ACCESS_KEY=GK000000000000000000000000
export GARAGE_SECRET_KEY=0000000000000000000000000000000000000000000000000000000000000000
export SERVER_DIR="$REPO_ROOT/artifacts/real-system/server"

STATE_DIR="$REPO_ROOT/artifacts/real-system"
DESCRIPTOR="$STATE_DIR/localhost-$API_PORT.json"
CLUSTER_ID_FILE="$STATE_DIR/localhost-$API_PORT.cluster-id"
PROJECT="olve-real-system-$slot"

mkdir -p "$STATE_DIR"
[ -s "$CLUSTER_ID_FILE" ] || cat /proc/sys/kernel/random/uuid > "$CLUSTER_ID_FILE"
export CLUSTER_ID="$(cat "$CLUSTER_ID_FILE")"

compose() { docker compose -p "$PROJECT" -f compose.yml "$@"; }
in_cluster() { compose exec -T k3s kubectl "$@"; }

wait_ready() {
  for _ in $(seq 1 120); do
    curl -fsS "http://localhost:$API_PORT/api/ready" >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "the server did not become ready; see: tools/real-system/host.sh logs $slot" >&2
  return 1
}

write_descriptor() {
  local token ca
  token="$(compose exec -T k3s kubectl -n olve-runners get secret olve-pipelines-token -o jsonpath='{.data.token}' | base64 -d)"
  ca="$(compose exec -T k3s kubectl -n olve-runners get secret olve-pipelines-token -o jsonpath='{.data.ca\.crt}' | base64 -d)"
  TOKEN="$token" CA="$ca" bun -e '
    const e = process.env;
    await Bun.write(process.argv[1], JSON.stringify({
      apiUrl: `http://localhost:${e.API_PORT}`,
      clusterId: e.CLUSTER_ID,
      kubernetes: { server: `https://127.0.0.1:${e.KUBE_PORT}`, token: e.TOKEN, caCertificate: e.CA, namespace: "olve-runners" },
      storage: { endpoint: `http://127.0.0.1:${e.S3_PORT}`, bucket: "olve-pipelines", accessKey: e.GARAGE_ACCESS_KEY, secretKey: e.GARAGE_SECRET_KEY },
      gitHubUrl: `http://127.0.0.1:${e.GITHUB_PORT}`,
      resetCommand: `${process.argv[2]} reset ${process.argv[3]}`,
    }, null, 2));' "$DESCRIPTOR" "$REPO_ROOT/tools/real-system/host.sh" "$slot"
  chmod 600 "$DESCRIPTOR"
}

case "$command" in
  up)
    (cd "$REPO_ROOT" && dotnet publish src/backend/Olve.Pipelines -c Release -p:PublishAot=false -p:DebugType=none -o "$SERVER_DIR" -v quiet)
    compose up -d --wait server garage k3s github
    wait_ready
    write_descriptor
    echo "host ready: http://localhost:$API_PORT (descriptor: $DESCRIPTOR)"
    ;;
  ui)
    (cd "$REPO_ROOT/src/frontend" && npm install --no-audit --no-fund --silent && npm run -s build)
    cp -r "$REPO_ROOT/src/frontend/dist/." "$SERVER_DIR/wwwroot/"
    ;;
  reset)
    compose rm -fsv server garage bootstrap >/dev/null
    in_cluster -n olve-runners delete jobs,pods --all --wait=false >/dev/null
    in_cluster -n olve-runners get secrets -o name \
      | { grep -v -E '^secret/(olve-pipelines-token|olve-real-system-marker)$' || true; } \
      | while read -r secret; do in_cluster -n olve-runners delete "$secret" >/dev/null; done
    docker volume rm -f "${PROJECT}_garage-meta" "${PROJECT}_garage-data" >/dev/null
    compose up -d --wait --quiet-pull server garage 2>/dev/null
    wait_ready
    ;;
  status) compose ps ;;
  logs) compose logs -f server ;;
  down)
    compose down -v
    rm -f "$DESCRIPTOR" "$CLUSTER_ID_FILE"
    ;;
  sweep)
    cd "$REPO_ROOT"
    dotnet run --project src/backend/Olve.Pipelines.RealSystemTests -- --treenode-filter '/*/*/SweepCommand/*' --output Detailed --no-progress
    ;;
  kubectl)
    shift 2
    [ "${1:-}" = "--" ] && shift
    in_cluster "$@"
    ;;
  *)
    sed -n '2,18p' host.sh | sed 's/^# \{0,1\}//' >&2
    exit 64
    ;;
esac
