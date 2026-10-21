// Runs scripts/check-compile-fail.ts: every tests/compile-fail case must fail type-checking for the
// expected reason, and tests/compile-pass must type-check (it is part of the tsconfig project).
import { expect, test } from "bun:test";
import { join } from "node:path";

test("compile-fail cases fail for the right reason", () => {
  const p = Bun.spawnSync([process.execPath, join(import.meta.dir, "../../scripts/check-compile-fail.ts")]);
  const out = p.stdout.toString() + p.stderr.toString();
  expect(out).toMatch(/expectations in \d+ files; 0 problem\(s\)/);
  expect(p.exitCode).toBe(0);
}, 60000);
