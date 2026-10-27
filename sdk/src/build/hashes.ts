// Per-handler hashes: bundle each handler on its own with the contract files and the SDK external
// (tree-shaken → function-level granularity; embedded scripts are bundle outputs too), then hash the
// outputs. Contract changes are versioned through the manifest's model/step entries, SDK changes
// through `harness`; both are combined in composeHandlerHash.
import { dirname, join, relative, resolve } from 'node:path';
import type { BunPlugin } from 'bun';
import { isImplementation } from '../core/handlers.ts';
import { originsOf } from '../core/origin.ts';
import { pipelineData } from '../builder/pipeline.ts';
import { allSteps } from '../harness/main.ts';
import { composeHandlerHash, type HarnessInfo } from '../util/handler-hash.ts';
import { sha256Hex } from '../util/hash.ts';
import type { Project } from './project.ts';

export interface HandlerHash {
  /** Hash of the bundle outputs alone. */
  bundleHash: string;
  /** composeHandlerHash(bundleHash, harness) — what the manifest carries. */
  handlerHash: string;
  /** Bundle output names (for diagnostics). */
  outputs: string[];
}

function externals(project: Project): BunPlugin {
  const contracts = new Set(project.contractFiles);
  return {
    name: 'olve-handler-hash-externals',
    setup(b) {
      b.onResolve({ filter: /^@olivervea\/pipelines(\/.*)?$/ }, (args) => ({
        path: args.path,
        external: true,
      }));
      b.onResolve({ filter: /^\.{1,2}\// }, (args) => {
        const base = resolve(dirname(args.importer), args.path);
        for (const cand of [base, `${base}.ts`, join(base, 'index.ts')])
          if (contracts.has(cand)) return { path: args.path, external: true };
        return undefined;
      });
    },
  };
}

export async function bundleHash(
  project: Project,
  entry: string,
): Promise<{ hash: string; outputs: string[] }> {
  const inside = !relative(project.dir, entry).startsWith('..');
  const r = await Bun.build({
    entrypoints: [entry],
    target: 'bun',
    format: 'esm',
    root: inside ? project.dir : dirname(entry),
    naming: {
      entry: '[dir]/[name].[ext]',
      chunk: '[dir]/[name].[ext]',
      asset: '[dir]/[name].[ext]',
    },
    plugins: [externals(project)],
    throw: false,
  });
  if (!r.success)
    throw new AggregateError(
      r.logs,
      `bundling ${entry} failed:\n${r.logs.map(String).join('\n')}`,
    );
  const parts: string[] = [];
  const outputs: string[] = [];
  for (const o of [...r.outputs].sort((a, b) =>
    a.path < b.path ? -1 : a.path > b.path ? 1 : 0,
  )) {
    const name = o.path.replace(/^\.\//, '');
    outputs.push(name);
    const bytes = new Uint8Array(await o.arrayBuffer());
    // Bun prefixes each module with a `// <path>` comment relative to the *process cwd*; drop those
    // so the hash depends only on code, not on where the repo or the build tool runs.
    const content = name.endsWith('.js')
      ? new TextDecoder().decode(bytes).replace(/^\/\/ \S+\n/gm, '')
      : bytes;
    parts.push(`${name}\0${sha256Hex(content)}`);
  }
  return { hash: 'sha256:' + sha256Hex(parts.join('\n')), outputs };
}

export async function handlerHashes(
  project: Project,
  harness: HarnessInfo,
): Promise<Record<string, HandlerHash>> {
  const out: Record<string, HandlerHash> = {};
  for (const [name, file] of project.handlers) {
    const b = await bundleHash(project, file);
    out[name] = {
      bundleHash: b.hash,
      handlerHash: composeHandlerHash(b.hash, harness),
      outputs: b.outputs,
    };
  }
  return out;
}

/**
 * Kind → hash of the modules that implement the steps of that kind which carry their implementation
 * (each module bundled like a handler: the SDK and contract files external).
 */
export async function kindHashes(
  project: Project,
  pipeline: unknown,
): Promise<Record<string, string>> {
  const origins = new Map<string, Set<string>>();
  for (const s of allSteps(pipelineData(pipeline))) {
    if (!isImplementation(s)) continue;
    const files = originsOf(s).filter((f) => !project.contractFiles.includes(f));
    if (files.length === 0)
      throw new Error(
        `step '${s.name}' carries its implementation but is defined in ${project.contractFiles.map((f) => relative(project.dir, f)).join(' or ')}; define and implement it in a module of its own (or a package)`,
      );
    const set = origins.get(s.kind) ?? new Set<string>();
    for (const f of files) set.add(f);
    origins.set(s.kind, set);
  }
  const out: Record<string, string> = {};
  for (const [kind, files] of [...origins].sort(([a], [b]) =>
    a < b ? -1 : a > b ? 1 : 0,
  )) {
    const parts: string[] = [];
    for (const f of [...files].sort()) parts.push((await bundleHash(project, f)).hash);
    out[kind] = 'sha256:' + sha256Hex(parts.join('\n'));
  }
  return out;
}
