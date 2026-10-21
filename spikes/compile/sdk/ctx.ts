import { mkdirSync, writeFileSync, chmodSync, mkdtempSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { type Ctx, type ShOptions, notYet, skip } from "./core";

export function createCtx(dirs: { olveDir: string; workDir: string }): Ctx {
  const filesDir = join(dirs.olveDir, "files");

  // `{ script }` paths come from `import x from "./x.sh" with { type: "file" }`. In a compiled
  // binary they live in the embedded /$bunfs filesystem, which a child process cannot see,
  // so copy the script out to a real temp file first.
  const materialize = async (cmd: string | { script: string }): Promise<string[]> => {
    if (typeof cmd === "string") return ["sh", "-c", cmd];
    // Not os.tmpdir(): libc-less images (kaniko) have no /tmp. $OLVE_DIR is a writable volume.
    mkdirSync(join(dirs.olveDir, "tmp"), { recursive: true });
    const tmp = mkdtempSync(join(dirs.olveDir, "tmp", "script-"));
    const p = join(tmp, cmd.script.split("/").pop()!);
    await Bun.write(p, Bun.file(cmd.script));
    chmodSync(p, 0o755);
    return ["sh", p];
  };

  const spawn = async (cmd: string | { script: string }, opts: ShOptions, capture: boolean) => {
    const argv = [...(await materialize(cmd)), ...(opts.args ?? [])];
    const proc = Bun.spawn(argv, {
      cwd: opts.cwd ?? dirs.workDir,
      env: { ...process.env, ...opts.env },
      stdout: capture ? "pipe" : "inherit",
      stderr: "inherit",
    });
    const text = capture ? await new Response(proc.stdout).text() : "";
    const code = await proc.exited;
    if (code === 127 || code === null) throw new Error(`could not execute ${argv[0]} (exit ${code}) — does the image have a shell?`);
    if (code !== 0) throw new Error(`command failed with exit code ${code}: ${typeof cmd === "string" ? cmd : cmd.script}`);
    return text;
  };

  const sh = Object.assign((c: string | { script: string }, o: ShOptions = {}) => spawn(c, o, false).then(() => {}), {
    text: (c: string | { script: string }, o: ShOptions = {}) => spawn(c, o, true).then((t) => t.trim()),
  });

  return {
    sh,
    readText: (p) => Bun.file(resolve(dirs.workDir, p)).text(),
    readJson: (p) => Bun.file(resolve(dirs.workDir, p)).json(),
    output: {
      async file(name, content) {
        const p = join(filesDir, name);
        mkdirSync(dirname(p), { recursive: true });
        writeFileSync(p, content);
        return { path: name };
      },
    },
    log: (m) => console.error(`[olve] ${m}`),
    skip,
    notYet,
    run: {
      id: process.env.OLVE_RUN_ID ?? "local",
      number: Number(process.env.OLVE_RUN_NUMBER ?? 0),
      version: process.env.OLVE_RUN_VERSION ?? "0.0.0-local",
    },
  };
}
