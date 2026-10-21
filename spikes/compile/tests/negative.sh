#!/bin/sh
# Builds deliberately broken pipelines in a scratch copy and shows describe's errors.
set -u
SRC=$(cd "$(dirname "$0")/.." && pwd); BUN=${BUN:-bun}
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
cp -r "$SRC/sdk" "$SRC/example" "$SRC/build.ts" "$SRC/entry.ts" "$SRC/package.json" "$W/"; ln -s "$SRC/node_modules" "$W/node_modules"
try() { # $1 = description, $2 = sed expression applied to example/pipeline.ts, $3 = extra build flag
  cp "$SRC/example/pipeline.ts" "$W/example/pipeline.ts"; sed -i "$2" "$W/example/pipeline.ts"
  printf '\n=== %s\n' "$1"
  (cd "$W" && "$BUN" build.ts --targets bun-linux-x64 --out dist >/dev/null 2>&1) || { echo "build failed"; return; }
  "$W/dist/bun-linux-x64/step" describe >/dev/null; echo "exit=$?"
}
try "template string in mapper"   's/sha: r.input.sha }/sha: `v${r.input.sha}` }/'
try "ternary in mapper"           's/\[test, (r) => ({ source: r.checkout })\]/[test, (r) => ({ source: r.input.deployTo === "prod" ? r.checkout : r.checkout })]/'
try "method call in mapper"       's/sha: r.input.sha }/sha: r.input.sha.slice(0, 7) }/'
try "unknown field"               's/sha: r.input.sha }/sha: r.input.shaa }/'
try "same-stage sibling ref"      's/\[test, (r) => ({ source: r.checkout })\]/[test, (r) => ({ source: r.build })]/'
try "maybe-available as required" 's/\.stage(deployProd));/.stage(deployProd))\n  .stage(step({ name: "after", image: "x", input: { p: M.ProdDeployment }, output: M.ProdDeployment }));/'
try "invalid when value"          's/deployTo: "prod"/deployTo: "prd"/'
try "when on non-literal field"   's/{ deployTo: "prod" }/{ sha: "abc" }/'
try "duplicate step name"         's/name: "test", image/name: "build", image/'
try "ambiguous by-model match"    's/\.stage(deployBeta)/.stage(step({ name: "build2", image: "x", input: {}, output: M.BuildArtifact }))\n  .stage(deployBeta)/'
