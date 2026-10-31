#!/usr/bin/env bun
// olve-dev — run a pipeline from source through the same harness path as the compiled binary.
//   olve-dev describe <dir> [--sentinels]
//   olve-dev run <dir> <step> [--input-file f] [--config-file f] [--olve-dir d] [--secret NAME=value]...
//   olve-dev map-trigger <dir> <trigger> --event-file f [--olve-dir d]
// $OLVE_DIR defaults to <dir>/.olve; outputs land in <dir>/.olve/work/.
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { runHarness, type HarnessConfig } from '../harness/main.ts';
import { EXIT } from '../harness/exit.ts';
import { codeHash } from '../build/hashes.ts';
import {
  harnessInfo,
  loadPipeline,
  loadProject,
  ProjectError,
} from '../build/project.ts';

const USAGE = `usage: olve-dev describe <dir> [--sentinels]
       olve-dev run <dir> <step> [--input-file f] [--config-file f] [--olve-dir d] [--secret NAME=value]...
       olve-dev map-trigger <dir> <trigger> --event-file f [--olve-dir d]`;

export async function devConfig(
  dir: string,
  withHashes: boolean,
): Promise<HarnessConfig> {
  const project = await loadProject(dir);
  const pipeline = await loadPipeline(project);
  const harness = harnessInfo();
  return {
    pipeline,
    handlers: Object.fromEntries(
      project.handlers.map(([name, file]) => [
        name,
        () => import(file) as Promise<{ default: unknown }>,
      ]),
    ),
    ...(withHashes ? { codeHash: await codeHash(project, harness) } : {}),
    harness,
  };
}

export async function devMain(argv: string[]): Promise<number> {
  let parsed;
  try {
    parsed = parseArgs({
      args: argv,
      allowPositionals: true,
      options: {
        'input-file': { type: 'string' },
        'config-file': { type: 'string' },
        'event-file': { type: 'string' },
        'olve-dir': { type: 'string' },
        secret: { type: 'string', multiple: true },
        sentinels: { type: 'boolean', default: false },
        help: { type: 'boolean', short: 'h', default: false },
      },
    });
  } catch (e) {
    console.error(`${(e as Error).message}\n${USAGE}`);
    return EXIT.usage;
  }
  const { values, positionals } = parsed;
  if (values.help) return (console.log(USAGE), 0);
  const [mode, dir, name] = positionals;
  if (!mode || !dir) return (console.error(USAGE), EXIT.usage);
  try {
    if (mode === 'describe')
      return await runHarness(await devConfig(dir, true), [
        'describe',
        ...(values.sentinels ? ['--sentinels'] : []),
      ]);
    if (mode !== 'run' && mode !== 'map-trigger')
      return (console.error(USAGE), EXIT.usage);
    if (!name) return (console.error(USAGE), EXIT.usage);
    const file = mode === 'run' ? values['input-file'] : values['event-file'];
    if (mode === 'map-trigger' && !file)
      return (console.error('--event-file is required'), EXIT.usage);
    const olveDir = resolve(values['olve-dir'] ?? join(dir, '.olve'));
    mkdirSync(olveDir, { recursive: true });
    process.env.OLVE_DIR = olveDir;
    process.env.TMPDIR ??= join(olveDir, 'tmp');
    for (const s of values.secret ?? []) {
      const i = s.indexOf('=');
      if (i <= 0)
        return (console.error(`--secret expects NAME=value, got '${s}'`), EXIT.usage);
      process.env[s.slice(0, i)] = s.slice(i + 1);
    }
    if (file) copyFileSync(resolve(file), join(olveDir, 'input.json'));
    if (mode === 'run' && values['config-file']) {
      const inputFile = join(olveDir, 'input.json');
      const input = (
        existsSync(inputFile) ? JSON.parse(readFileSync(inputFile, 'utf8')) : {}
      ) as Record<string, unknown>;
      input.$config = JSON.parse(readFileSync(resolve(values['config-file']), 'utf8'));
      writeFileSync(inputFile, JSON.stringify(input));
    }
    return await runHarness(await devConfig(dir, false), [mode, name]);
  } catch (e) {
    if (e instanceof ProjectError) return (console.error(e.message), EXIT.configInvalid);
    console.error((e as Error)?.stack ?? String(e));
    return EXIT.configInvalid;
  }
}

if (import.meta.main) process.exit(await devMain(process.argv.slice(2)));
