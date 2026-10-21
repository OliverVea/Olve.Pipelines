#!/bin/sh
# Exercises every harness mode against a compiled binary.  usage: tests/modes.sh [binary]
set -u
BIN=$(realpath "${1:-dist/bun-linux-x64/step}")
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
SHA=0123456789abcdef0123456789abcdef01234567
say() { printf '\n=== %s\n' "$*"; }

say "describe never evaluates handlers (checkout.ts has a top-level console.error)"
"$BIN" describe 2>"$T/err" >/dev/null; echo "exit=$? stderr_lines=$(wc -l <"$T/err")"

say "map-trigger push (main → prod, applies defaults)"
echo "{\"ref\":\"refs/heads/main\",\"after\":\"$SHA\",\"repository\":{\"full_name\":\"OliverVea/Olve.Pipelines\"}}" > "$T/event.json"
OLVE_DIR=$T "$BIN" map-trigger push; echo "exit=$?"
say "map-trigger push with a bad sha → invalid pipeline input"
echo '{"ref":"refs/heads/dev","after":"nope","repository":{"full_name":"x/y"}}' > "$T/event.json"
OLVE_DIR=$T "$BIN" map-trigger push; echo "exit=$?"

say "run checkout"
mkdir -p "$T/checkout"; echo "{\"repo\":\"OliverVea/Olve.Pipelines\",\"sha\":\"$SHA\"}" > "$T/checkout/input.json"
OLVE_DIR=$T/checkout "$BIN" run checkout 2>/dev/null; echo "exit=$?"; cat "$T/checkout/output.json"; ls "$T/checkout/files"

say "run test (ctx.sh runs an embedded shell script; streams stderr)"
mkdir -p "$T/test/files"; cp "$T/checkout/files/tree.txt" "$T/test/files/"
jq -n --argjson s "$(jq .output "$T/checkout/output.json")" '{source:$s}' > "$T/test/input.json"
OLVE_DIR=$T/test "$BIN" run test; echo "exit=$?"; cat "$T/test/output.json"

say "run build (input validation failure: source.sha missing)"
mkdir -p "$T/build"; echo '{"source":{"repo":"r","tree":{"path":"tree.txt"}}}' > "$T/build/input.json"
OLVE_DIR=$T/build "$BIN" run build; echo "exit=$?"
say "run build"
jq -n --argjson s "$(jq .output "$T/checkout/output.json")" '{source:$s}' > "$T/build/input.json"
OLVE_RUN_VERSION=2026.10.21.412 OLVE_DIR=$T/build "$BIN" run build; echo "exit=$?"; cat "$T/build/output.json"

say "run deploy-beta (output validation failure: url is not a URL)"
mkdir -p "$T/beta"; jq -n --argjson a "$(jq .output "$T/build/output.json")" '{artifact:$a}' > "$T/beta/input.json"
BAD_URL=not-a-url OLVE_DIR=$T/beta "$BIN" run deploy-beta; echo "exit=$?"
say "run deploy-beta (Skipped)"
SKIP_BETA=1 OLVE_DIR=$T/beta "$BIN" run deploy-beta; echo "exit=$?"; cat "$T/beta/output.json"
say "run deploy-beta"
OLVE_DIR=$T/beta "$BIN" run deploy-beta; echo "exit=$?"; cat "$T/beta/output.json"

say "check approve-prod (notYet, then resolved)"
mkdir -p "$T/approve"; jq -n --argjson b "$(jq .output "$T/beta/output.json")" '{beta:$b}' > "$T/approve/input.json"
(cd "$T/approve" && OLVE_DIR=. "$BIN" check approve-prod; echo "exit=$?"; cat output.json)
jq -r .output.version "$T/beta/output.json" > "$T/approve/approved.txt"
(cd "$T/approve" && OLVE_DIR=. "$BIN" check approve-prod; echo "exit=$?"; cat output.json)

say "run on a wait step / unknown step"
OLVE_DIR=$T "$BIN" run approve-prod; echo "exit=$?"
OLVE_DIR=$T "$BIN" run nope; echo "exit=$?"
