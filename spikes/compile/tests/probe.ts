// Prints how a compiled binary sees itself (used to test loader-invoked execution).
import { readlinkSync } from "node:fs";
console.log(JSON.stringify({ execPath: process.execPath, argv: process.argv, main: Bun.main,
  procSelfExe: readlinkSync("/proc/self/exe"), ldlp: process.env.LD_LIBRARY_PATH ?? null }));
if (process.argv.includes("--shell")) {
  console.log("Bun.$ builtin echo:", (await Bun.$`echo hi from bun shell`.text()).trim());
  try { Bun.spawnSync(["sh", "-c", "echo x"]); console.log("sh ok"); } catch (e: any) { console.log("spawn sh:", e.message); }
}
