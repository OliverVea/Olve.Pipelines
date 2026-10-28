#!/usr/bin/env bash
# The checks the deploy runs (`spec`, `test`, `build-sdk`, `build-frontend`, `build-cli`), locally, per project.
#
#   tools/check.sh                 fast tier, every project: formatting, lint, type checks, the spec compiles, generated files current, pipeline valid
#   tools/check.sh --changed       only the projects with changes against the upstream branch (plus uncommitted work)
#   tools/check.sh --full          adds the unit tests, the SDK test suite and the frontend build
#   tools/check.sh --fix           formats and regenerates first (prettier, dotnet format, manifest schema), then checks
#   tools/check.sh --hook          --changed, quiet on success, failures on stderr, exit 2 (for pre-push hooks)
set -u
cd "$(dirname "$0")/.." || exit 2

full=0 hook=0 fix=0 changed=0
for a in "$@"; do
  case "$a" in
    --full) full=1 ;; --hook) hook=1; changed=1 ;; --fix) fix=1 ;; --changed) changed=1 ;;
    *) echo "usage: tools/check.sh [--changed] [--full] [--fix] [--hook]" >&2; exit 2 ;;
  esac
done

log=$(mktemp -d); trap 'rm -rf "$log"' EXIT
failed=()
prettier=src/sdk/node_modules/.bin/prettier
TS_GLOBS=("src/sdk/src/**/*.ts" "src/sdk/tests/**/*.ts" "src/sdk/scripts/*.ts" ".pipelines/**/*.ts")

# ── which projects changed ──
declare -A want=([sdk]=1 [pipelines]=1 [frontend]=1 [spec]=1 [cli]=1 [dotnet]=1)
if [ $changed = 1 ]; then
  base=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null || echo origin/main)
  files=$( { git diff --name-only "$base" -- ; git ls-files --others --exclude-standard; } | sort -u)
  want=()
  while IFS= read -r f; do
    case "$f" in
      src/sdk/*) want[sdk]=1 ;;
      .pipelines/*) want[pipelines]=1 ;;
      src/frontend/*) want[frontend]=1; want[spec]=1 ;;
      src/cli/*) want[cli]=1; want[spec]=1 ;;
      src/spec/*) want[spec]=1; want[frontend]=1; want[cli]=1; want[dotnet]=1 ;;
      src/*|tools/*|Directory.*|global.json|*.slnx) want[dotnet]=1 ;;
    esac
  done <<<"$files"
  if [ ${#want[@]} = 0 ]; then
    [ $hook = 1 ] || echo "no project changed against $base; nothing to check"
    exit 0
  fi
  [ $hook = 1 ] || echo "changed against $base: ${!want[*]}"
fi

[ -x "$prettier" ] || { echo "src/sdk/node_modules missing: run (cd src/sdk && bun install)" >&2; exit 2; }
[ -z "${want[frontend]:-}" ] || [ -d src/frontend/node_modules ] || { echo "src/frontend/node_modules missing: run (cd src/frontend && npm install)" >&2; exit 2; }
[ -z "${want[cli]:-}" ] || [ -d src/cli/node_modules ] || { echo "src/cli/node_modules missing: run (cd src/cli && bun install)" >&2; exit 2; }

run() { # run <name> <command...>
  local name=$1; shift
  local out="$log/${name//[^a-zA-Z0-9]/_}.log"
  if "$@" >"$out" 2>&1; then
    [ $hook = 1 ] || echo "ok    $name"
  else
    failed+=("$name")
    echo "FAIL  $name" >&2
    tail -n 30 "$out" | sed 's/^/      /' >&2
  fi
}

manifest_schema_current() {
  (cd src/sdk && bun -e '
    import { manifestJsonSchema } from "./src/describe/manifest-schema.ts";
    import { canonicalJson } from "./src/util/canonical.ts";
    if (canonicalJson(manifestJsonSchema()) !== (await Bun.file("manifest.schema.json").text())) {
      console.error("src/sdk/manifest.schema.json is out of date: run `bun scripts/gen-manifest-schema.ts`"); process.exit(1); }')
}

pipeline_valid() {
  command -v pl >/dev/null || { echo "pl is not installed (curl -fsSL https://pipelines-private.ovea.pro/install.sh | sh)"; return 1; }
  pl dev validate
}

# ── fix ──
if [ $fix = 1 ]; then
  echo "fixing..."
  [ -z "${want[sdk]:-}${want[pipelines]:-}" ] || { "$prettier" --write "${TS_GLOBS[@]}" >/dev/null; (cd src/sdk && bun scripts/gen-manifest-schema.ts >/dev/null); }
  [ -z "${want[frontend]:-}" ] || (cd src/frontend && npm run -s format >/dev/null && npx eslint --fix src/ >/dev/null) || true
  [ -z "${want[cli]:-}" ] || (cd src/cli && bun run format >/dev/null) || true
  [ -z "${want[dotnet]:-}" ] || dotnet format Olve.Pipelines.slnx --no-restore -v quiet
fi

# ── check ──
if [ -n "${want[sdk]:-}" ]; then
  run "sdk: prettier"                  "$prettier" --check "src/sdk/src/**/*.ts" "src/sdk/tests/**/*.ts" "src/sdk/scripts/*.ts"
  run "sdk: tsc"                       bash -c 'cd src/sdk && bunx tsc --noEmit -p .'
  run "sdk: manifest.schema.json"      manifest_schema_current
  [ $full = 0 ] || run "sdk: compile-fail + tests" bash -c 'cd src/sdk && bun scripts/check-compile-fail.ts && bun test'
fi
if [ -n "${want[pipelines]:-}" ]; then
  run "pipelines: prettier"            "$prettier" --check ".pipelines/**/*.ts"
  run "pipelines: pl dev validate"     pipeline_valid
fi
# The contract first: the server compiles its generated surface and the frontend type-checks against its client.
if [ -n "${want[spec]:-}" ]; then
  run "spec: contract + client"    bash -c 'cd src/spec && bun install --frozen-lockfile >/dev/null && bun run build'
fi
if [ -n "${want[frontend]:-}" ]; then
  run "frontend: lint"                 bash -c 'cd src/frontend && npm run -s lint'
  run "frontend: prettier"             bash -c 'cd src/frontend && npm run -s format:check'
  run "frontend: typecheck"            bash -c 'cd src/frontend && npm run -s typecheck'
  [ $full = 0 ] || run "frontend: build" bash -c 'cd src/frontend && npm run -s build'
fi
if [ -n "${want[cli]:-}" ]; then
  run "cli: prettier"                  bash -c 'cd src/cli && bun run format:check'
  run "cli: typecheck"                 bash -c 'cd src/cli && bun run typecheck'
  [ $full = 0 ] || run "cli: tests"    bash -c 'cd src/cli && bun test'
fi
if [ -n "${want[dotnet]:-}" ]; then
  run "dotnet: build"                  dotnet build Olve.Pipelines.slnx --nologo -v quiet
  run "dotnet: format"                 dotnet format Olve.Pipelines.slnx --verify-no-changes --no-restore -v quiet
  [ $full = 0 ] || run "dotnet: unit tests" dotnet test --project src/backend/Olve.Pipelines.UnitTests --no-restore
fi

if [ ${#failed[@]} -gt 0 ]; then
  echo "${#failed[@]} check(s) failed: ${failed[*]}" >&2
  [ $hook = 1 ] && echo "The deploy runs these same checks; fix them (tools/check.sh --fix handles formatting and generated files) before pushing." >&2
  [ $hook = 1 ] && exit 2 || exit 1
fi
[ $hook = 1 ] || echo "all checks passed"
