// Test step for validating a design-B pod (not the real SDK harness).
// Reads $OLVE_DIR/input.json, spawns a child through the image's shell (if any), writes
// $OLVE_DIR/work/output.json, and prints the same JSON on stdout (so logs prove the result).
import { existsSync, readFileSync, writeFileSync } from "node:fs";

const dir = process.env.OLVE_DIR ?? "/olve";
const input = JSON.parse(readFileSync(`${dir}/input.json`, "utf8"));

const shell = ["/bin/sh", "/busybox/sh", "/bin/bash"].find((p) => existsSync(p));
let child: unknown = "no shell in image";
if (shell) {
  const p = Bun.spawn([shell, "-c", 'echo "child:$(uname -m):${LD_LIBRARY_PATH:-no-ld-path}"; head -1 /etc/os-release 2>/dev/null; exit 3'], {
    stdout: "pipe",
    stderr: "pipe",
  });
  const out = (await new Response(p.stdout).text()).trim().split("\n");
  child = { shell, exitCode: await p.exited, out };
}

let tmpOk: unknown;
try {
  writeFileSync(`${process.env.TMPDIR ?? "/tmp"}/probe`, "x");
  tmpOk = true;
} catch (e) {
  tmpOk = String(e);
}

const output = {
  status: "succeeded",
  output: { echo: input, bun: Bun.version, uid: process.getuid?.(), tmpOk, child },
};
writeFileSync(`${dir}/work/output.json`, JSON.stringify(output));
console.log(`OUTPUT ${JSON.stringify(output)}`);
