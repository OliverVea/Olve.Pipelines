#!/usr/bin/env bun
// olve-build <pipelineDir> --out <dir> [--target bun-linux-x64|bun-linux-x64-musl] [--upload-url <url>] [--sentinels] [--hash-only]
import { parseArgs } from "node:util";
import { canonicalJson } from "../util/canonical.ts";
import { build, BuildError, type Target } from "./build.ts";
import { handlerHashes } from "./hashes.ts";
import { harnessInfo, loadProject, ProjectError } from "./project.ts";

const USAGE = "usage: olve-build <pipelineDir> --out <dir> [--target bun-linux-x64|bun-linux-x64-musl] [--upload-url <url>] [--sentinels]\n       olve-build <pipelineDir> --hash-only";

async function main(): Promise<number> {
  let parsed;
  try {
    parsed = parseArgs({
      allowPositionals: true,
      options: {
        out: { type: "string" },
        target: { type: "string" },
        "upload-url": { type: "string" },
        sentinels: { type: "boolean", default: false },
        "hash-only": { type: "boolean", default: false },
        help: { type: "boolean", short: "h", default: false },
      },
    });
  } catch (e) {
    console.error(`${(e as Error).message}\n${USAGE}`);
    return 64;
  }
  const { values, positionals } = parsed;
  if (values.help) return console.log(USAGE), 0;
  if (positionals.length !== 1) return console.error(USAGE), 64;
  const dir = positionals[0]!;
  try {
    if (values["hash-only"]) {
      const h = await handlerHashes(await loadProject(dir), harnessInfo());
      await Bun.write(Bun.stdout, canonicalJson({ harness: harnessInfo(), handlers: h }));
      return 0;
    }
    if (!values.out) return console.error(`--out is required\n${USAGE}`), 64;
    const target = (values.target ?? "bun-linux-x64") as Target;
    if (target !== "bun-linux-x64" && target !== "bun-linux-x64-musl") return console.error(`unsupported target ${target}`), 64;
    await build(dir, { out: values.out, target, uploadUrl: values["upload-url"], sentinels: values.sentinels });
    return 0;
  } catch (e) {
    if (e instanceof BuildError) return console.error(e.message), e.exitCode;
    if (e instanceof ProjectError) return console.error(e.message), 78;
    console.error((e as Error)?.stack ?? String(e));
    return 1;
  }
}

if (import.meta.main) process.exit(await main());
