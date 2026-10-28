#!/bin/sh
# Builds pl from the checkout for /download (pl-linux-x64, pl-win-x64.exe), the way the build-cli step
# does. Works on a copy so the checkout's node_modules and artifacts/ stay untouched.
set -eu
rm -rf /tmp/src
mkdir -p /tmp/src
cd /repo
tar --exclude='*/node_modules' -cf - .prettierrc src/spec src/cli | tar -xf - -C /tmp/src
cd /tmp/src/src/spec
bun install --frozen-lockfile
bun run build
cd /tmp/src/src/cli
bun install --frozen-lockfile
for target in linux-x64:pl-linux-x64 windows-x64:pl-win-x64.exe; do
  bun build src/main.ts --compile --minify --target="bun-${target%%:*}" \
    --define 'process.env.PL_VERSION="0.0.0-local"' --outfile "/dist/${target#*:}"
done
/dist/pl-linux-x64 --version
echo "pl built into /dist"
