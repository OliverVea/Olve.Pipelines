#!/bin/sh
# Shows which handlerHash values change for a given edit (scoped vs naive hashing).
set -u
SRC=$(cd "$(dirname "$0")/.." && pwd); BUN=${BUN:-bun}
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
fresh() { rm -rf "$W"/*; cp -r "$SRC/sdk" "$SRC/example" "$SRC/build.ts" "$SRC/entry.ts" "$SRC/package.json" "$W/"; ln -sf "$SRC/node_modules" "$W/node_modules"; }
hashes() { (cd "$W" && "$BUN" build.ts --hash-only >/dev/null 2>&1) && cat "$W/gen/hashes.json"; }
fresh; hashes > "$W/../base.$$.json"
demo() { # $1 description, $2 file, $3 sed expression
  fresh; cp "$W/$2" "$W/$2.orig"; sed -i "$3" "$W/$2"; cmp -s "$W/$2" "$W/$2.orig" && echo "!! edit did not apply: $1"; rm "$W/$2.orig"
  printf '%-44s' "$1"
  hashes | jq -r --slurpfile b "$W/../base.$$.json" '
    [to_entries[] | select(.value.handlerHash != $b[0][.key].handlerHash) | .key] as $s |
    [to_entries[] | select(.value.naiveHash   != $b[0][.key].naiveHash)   | .key] as $n |
    "scoped: \($s | if length == 0 then "-" else join(",") end)   naive: \($n | if length == 0 then "-" else join(",") end)"'
}
demo "edit handlers/build.ts"              example/handlers/build.ts       's/"image.tar"/"image2.tar"/'
demo "edit versionFor() in lib/version.ts"   example/handlers/lib/version.ts 's/slice(0, 7)/slice(0, 8)/'
demo "edit releaseName() in lib/version.ts"   example/handlers/lib/version.ts 's/olve-pipelines-/olve-pl-/'
demo "add unused export to lib/version.ts" example/handlers/lib/version.ts '$a export const unused = 1;'
demo "comment-only edit in build.ts"       example/handlers/build.ts       '$a // just a comment'
demo "edit scripts/test.sh (file import)"  example/scripts/test.sh         '$a # changed'
demo "edit pipeline.ts (test image)"       example/pipeline.ts             's/name: "test", image: "alpine:latest"/name: "test", image: "alpine:3.20"/'
demo "edit models.ts (TestReport field)"   example/models.ts               's/failed: z.number().int() }/failed: z.number().int(), skipped: z.number().optional() }/'
demo "edit sdk/core.ts implement()"        sdk/core.ts                     's/return { kind: "handler", step: s, fn };/return { kind: "handler", step: s, fn, v: 2 } as Handler;/'
demo "edit sdk/ctx.ts (harness, not bundled)" sdk/ctx.ts                   's/olve-script-/olve-sh-/'
fresh; printf '%-44s' "rebuild with no edits"; hashes | jq -r --slurpfile b "$W/../base.$$.json" '[to_entries[] | select(.value.handlerHash != $b[0][.key].handlerHash) | .key] | "scoped changed: \(length)"'
rm -f "$W/../base.$$.json"
