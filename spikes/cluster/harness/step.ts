// Stand-in harness for spike 1: `/olve/step run <name>`.
// Reads /olve/input.json, spawns child processes through the image's shell (ctx.sh-style),
// probes DNS/TLS egress, writes /olve/work/output.json and prints the same JSON to stdout.
import { existsSync, readFileSync, readlinkSync, writeFileSync } from "node:fs";

// --- startup latency: exec -> first JS line (clock ticks from /proc/self/stat vs /proc/uptime)
function startupMs(): number | string {
  try {
    const stat = readFileSync("/proc/self/stat", "utf8");
    const fields = stat.slice(stat.lastIndexOf(")") + 2).split(" ");
    const startTicks = Number(fields[19]); // field 22 overall
    const uptime = Number(readFileSync("/proc/uptime", "utf8").split(" ")[0]);
    return Math.round((uptime - startTicks / 100) * 1000);
  } catch (e) {
    return String(e);
  }
}
const execToJsMs = startupMs();
const t0 = performance.now();

const [mode, name] = process.argv.slice(2);
const inPath = process.env.OLVE_INPUT ?? "/olve/input.json";
const outPath = process.env.OLVE_OUTPUT ?? "/olve/work/output.json";
if (mode !== "run") {
  console.error(`usage: step run <name> (got ${mode})`);
  process.exit(2);
}

let input: unknown = null;
try { input = JSON.parse(readFileSync(inPath, "utf8")); } catch (e) { input = { error: String(e) }; }

const exe = {
  execPath: process.execPath,
  procSelfExe: (() => { try { return readlinkSync("/proc/self/exe"); } catch (e) { return String(e); } })(),
  argv0: process.argv[0],
};

async function sh(shell: string, script: string, env?: Record<string, string>) {
  const c0 = performance.now();
  try {
    const p = Bun.spawn([shell, "-c", script], { stdout: "pipe", stderr: "pipe", env: env ?? process.env });
    const [out, err] = await Promise.all([new Response(p.stdout).text(), new Response(p.stderr).text()]);
    const code = await p.exited;
    return { code, out: out.trim().split("\n").slice(0, 6), err: err.trim().slice(0, 300) || undefined, ms: Math.round(performance.now() - c0) };
  } catch (e) {
    return { error: String(e), ms: Math.round(performance.now() - c0) };
  }
}

const shell = ["/bin/sh", "/busybox/sh", "/bin/bash"].find((p) => existsSync(p));
const children: Record<string, unknown> = {};
if (shell) {
  children.shell = shell;
  // non-zero exit code must propagate
  children.basic = await sh(shell, "echo hello-from-child; uname -m; head -1 /etc/os-release 2>/dev/null; exit 3");
  // pipes + env passing to the child (ctx.sh-style)
  children.pipeEnv = await sh(shell, "echo $OLVE_STEP | tr a-z A-Z", { ...process.env, OLVE_STEP: name } as Record<string, string>);
  // children must see the IMAGE's libc, not ours (no LD_LIBRARY_PATH leak)
  children.ldEnv = await sh(shell, "echo LD_LIBRARY_PATH=${LD_LIBRARY_PATH:-unset}");
  if (process.env.OLVE_CHILD) children.custom = await sh(shell, process.env.OLVE_CHILD);
  // 20 sequential spawns -> per-spawn overhead under gVisor
  const s0 = performance.now();
  for (let i = 0; i < 20; i++) await (Bun.spawn([shell, "-c", "true"]).exited);
  children.spawn20AvgMs = Math.round((performance.now() - s0) / 20 * 10) / 10;
} else {
  children.skipped = "no shell";
  // still prove direct exec of a non-shell program works: exec ourselves? no - execPath is the loader.
}

// --- DNS + TLS egress (Garage upload stand-in). OLVE_FETCH = comma-separated URLs.
const net: Record<string, unknown>[] = [];
for (const url of (process.env.OLVE_FETCH ?? "").split(",").filter(Boolean)) {
  const f0 = performance.now();
  try {
    const r = await fetch(url, { method: "GET", signal: AbortSignal.timeout(15000) });
    await r.arrayBuffer();
    net.push({ url, status: r.status, ms: Math.round(performance.now() - f0) });
  } catch (e) {
    net.push({ url, error: String(e).slice(0, 200), ms: Math.round(performance.now() - f0) });
  }
}

const result = {
  step: name, bun: Bun.version, execToJsMs, jsMs: Math.round(performance.now() - t0),
  exe, input, children, net,
};
console.log(JSON.stringify(result));
writeFileSync(outPath, JSON.stringify(result));
