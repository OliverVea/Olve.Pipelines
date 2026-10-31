// The build's code hash: everything the pipeline runs, bundled once with the SDK external (the
// harness identity stands for it).
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { BunPlugin } from 'bun';
import { sha256Hex } from '../util/hash.ts';
import { composeCodeHash, type HarnessInfo } from '../util/code-hash.ts';
import { relPosix, type Project } from './project.ts';

const sdkExternal: BunPlugin = {
  name: 'olve-code-hash-external',
  setup(b) {
    b.onResolve({ filter: /^@olivervea\/pipelines(\/.*)?$/ }, (args) => ({
      path: args.path,
      external: true,
    }));
  },
};

async function bundleHash(project: Project, entry: string): Promise<string> {
  const r = await Bun.build({
    entrypoints: [entry],
    target: 'bun',
    format: 'esm',
    root: project.dir,
    naming: {
      entry: '[dir]/[name].[ext]',
      chunk: '[dir]/[name].[ext]',
      asset: '[dir]/[name].[ext]',
    },
    plugins: [sdkExternal],
    throw: false,
  });
  if (!r.success)
    throw new AggregateError(
      r.logs,
      `bundling ${entry} failed:\n${r.logs.map(String).join('\n')}`,
    );
  const parts: string[] = [];
  for (const o of [...r.outputs].sort((a, b) =>
    a.path < b.path ? -1 : a.path > b.path ? 1 : 0,
  )) {
    const name = o.path.replace(/^\.\//, '');
    const bytes = new Uint8Array(await o.arrayBuffer());
    // Bun prefixes each module with a `// <path>` comment relative to the *process cwd*; drop those
    // so the hash depends only on code, not on where the repo or the build tool runs.
    const content = name.endsWith('.js')
      ? new TextDecoder().decode(bytes).replace(/^\/\/ \S+\n/gm, '')
      : bytes;
    parts.push(`${name}\0${sha256Hex(content)}`);
  }
  return 'sha256:' + sha256Hex(parts.join('\n'));
}

export async function codeHash(project: Project, harness: HarnessInfo): Promise<string> {
  const gen = join(project.dir, '.olve', 'gen');
  mkdirSync(gen, { recursive: true });
  const entry = join(gen, 'code.ts');
  const files = [project.pipelineFile, ...project.handlers.map(([, file]) => file)];
  writeFileSync(
    entry,
    files
      .map(
        (f, i) =>
          `export { default as m${i} } from ${JSON.stringify(relPosix(gen, f))};\n`,
      )
      .join(''),
  );
  return composeCodeHash(await bundleHash(project, entry), harness);
}
