// Verifies that every `// @ts-expect-error -- expect: <substring>` in tests/compile-fail/*.ts fails
// for the *right* reason: strips the directives, runs tsc once over the stripped copies, and checks
// that each marked line has an error containing <substring> and that no unmarked line errors.
//   bun scripts/check-compile-fail.ts [--print]   (--print dumps the tsc messages for REPORT.md)
import { readdirSync, readFileSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";

const root = join(import.meta.dir, "..");
const src = join(root, "tests/compile-fail");
const tmp = join(root, "tests/.tmp");
const print = process.argv.includes("--print");
rmSync(tmp, { recursive: true, force: true });
mkdirSync(tmp);

type Expect = { file: string; line: number; substr: string };
const expects: Expect[] = [];
const files = readdirSync(src).filter((f) => f.endsWith(".ts")).sort();
for (const f of files) {
  const lines = readFileSync(join(src, f), "utf8").split("\n");
  lines.forEach((l, i) => {
    const m = l.match(/\/\/ @ts-expect-error(?: -- expect: (.*))?$/);
    if (!m) return;
    if (!m[1]) throw new Error(`${f}:${i + 1}: @ts-expect-error without '-- expect: <substring>'`);
    expects.push({ file: f, line: i + 2, substr: m[1].trim() });
    lines[i] = l.replace(/\/\/ @ts-expect-error.*/, "// (directive stripped)");
  });
  writeFileSync(join(tmp, f), lines.join("\n"));
}
writeFileSync(
  join(tmp, "tsconfig.json"),
  JSON.stringify({ extends: "../../tsconfig.json", include: ["*.ts"], exclude: [], compilerOptions: { noErrorTruncation: false } }),
);

const tsc = process.env.TSC ?? join(root, "node_modules/.bin/tsc"); // TSC=/path/to/tsc to try another version
const proc = Bun.spawnSync([tsc, "--noEmit", "--pretty", "false", "-p", tmp], { cwd: root });
const out = proc.stdout.toString();
if (process.argv.includes("--raw")) console.log(out);
// group "file(line,col): error TSxxxx: msg" + indented continuation lines
const errors: { file: string; line: number; text: string }[] = [];
for (const raw of out.split("\n")) {
  const m = raw.match(/^tests\/\.tmp\/([^(]+)\((\d+),\d+\): (error TS\d+: .*)$/);
  if (m) errors.push({ file: m[1]!, line: +m[2]!, text: m[3]! });
  else if (raw.startsWith(" ") && errors.length) errors[errors.length - 1]!.text += "\n" + raw;
  else if (raw.trim()) errors.push({ file: "?", line: 0, text: raw });
}

let failed = 0;
for (const e of expects) {
  const hits = errors.filter((x) => x.file === e.file && x.line === e.line);
  const ok = hits.some((h) => h.text.includes(e.substr));
  if (!ok) {
    failed++;
    console.log(`FAIL ${e.file}:${e.line} expected error containing: ${e.substr}`);
    for (const h of hits) console.log("     got: " + h.text.split("\n")[0]);
    if (!hits.length) console.log("     got: (no error on that line)");
  } else if (print) {
    console.log(`── ${e.file}:${e.line}`);
    for (const h of hits) console.log(h.text);
  }
}
for (const x of errors) {
  if (!expects.some((e) => e.file === x.file && e.line === x.line)) {
    failed++;
    console.log(`UNEXPECTED ${x.file}:${x.line} ${x.text.split("\n")[0]}`);
  }
}
console.log(`${expects.length - (failed > 0 ? 0 : 0)} expectations in ${files.length} files; ${failed} problem(s)`);
rmSync(tmp, { recursive: true, force: true });
process.exit(failed ? 1 : 0);
