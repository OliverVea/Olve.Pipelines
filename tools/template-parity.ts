#!/usr/bin/env bun
// Compares this repo with the olve-api template's feature manifest (template-features.json): what is
// missing, what is adopted and what is opted out, per .template.json. Reports only; exit 0.
//
//   bun tools/template-parity.ts                    the latest manifest from GitHub main
//   bun tools/template-parity.ts --manifest <path>  a local manifest (a template checkout's)
//   bun tools/template-parity.ts --repo <dir>       check another checkout (default: this one)
//   bun tools/template-parity.ts --strict           exit 1 when a feature is missing
//
// In the template repo itself (template-features.json at its root) it checks the template against its
// own manifest, so a feature the template doesn't ship yet shows as missing there too.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

// Split so `dotnet new` (sourceName) doesn't rename the template's own repo in generated services.
const MANIFEST_URL = `https://raw.githubusercontent.com/OliverVea/Olve.Template${'.Api'}/main/template-features.json`;

type Check =
  | { kind: 'file'; glob: string }
  | { kind: 'contains' | 'absent'; glob: string; pattern: string }
  | { kind: 'package'; name: string; atLeast?: string }
  | { kind: 'manual' }
  | { kind: 'all' | 'any'; checks: Check[] };
type Feature = {
  id: string;
  description: string;
  since: string | null;
  origin?: string;
  check: Check;
};
type Manifest = { template: string; features: Feature[] };
type Record = {
  template?: string;
  version?: string | null;
  features?: { [id: string]: string };
};

const args = process.argv.slice(2);
const opt = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const strict = args.includes('--strict');
const repo = resolve(opt('--repo') ?? join(import.meta.dir, '..'));
const templateMode =
  !opt('--manifest') && existsSync(join(repo, 'template-features.json'));

function fail(message: string): never {
  // A child must never fail its checks on the template being unreachable or malformed.
  console.log(`template-parity: ${message}`);
  process.exit(templateMode || strict ? 2 : 0);
}

async function loadManifest(): Promise<Manifest> {
  const source =
    opt('--manifest') ??
    (templateMode ? join(repo, 'template-features.json') : MANIFEST_URL);
  let text: string;
  try {
    if (/^https?:/.test(source)) {
      const res = await fetch(source, { signal: AbortSignal.timeout(10_000) });
      if (!res.ok) fail(`could not fetch ${source}: HTTP ${res.status}`);
      text = await res.text();
    } else text = readFileSync(source, 'utf8');
  } catch (e) {
    fail(`could not read ${source}: ${(e as Error).message}`);
  }
  let manifest: Manifest;
  try {
    manifest = JSON.parse(text) as Manifest;
  } catch (e) {
    fail(`${source} is not valid JSON: ${(e as Error).message}`);
  }
  const ids = new Set<string>();
  for (const f of manifest.features ?? []) {
    if (!f.id || ids.has(f.id))
      fail(`${source}: missing or duplicate feature id '${f.id}'`);
    ids.add(f.id);
    validate(f.check, f.id);
  }
  console.log(`Manifest: ${source}`);
  return manifest;
}

function validate(c: Check, id: string) {
  switch (c?.kind) {
    case 'file':
      if (c.glob) return;
      break;
    case 'contains':
    case 'absent':
      if (c.glob && c.pattern) {
        new RegExp(c.pattern, 'm');
        return;
      }
      break;
    case 'package':
      if (c.name) return;
      break;
    case 'manual':
      return;
    case 'all':
    case 'any':
      if (c.checks?.length) {
        c.checks.forEach((x) => validate(x, id));
        return;
      }
      break;
  }
  fail(`feature '${id}': invalid check ${JSON.stringify(c)}`);
}

// The repo's files as git sees them (tracked and untracked, not ignored), or a walk outside git.
function listFiles(): string[] {
  const git = Bun.spawnSync(
    ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'],
    { cwd: repo },
  );
  if (git.exitCode === 0) {
    return git.stdout
      .toString()
      .split('\0')
      .filter((f) => f && existsSync(join(repo, f)));
  }
  const skip = new Set([
    '.git',
    'node_modules',
    'bin',
    'obj',
    'artifacts',
    'dist',
    'TestResults',
  ]);
  const out: string[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) {
        if (!skip.has(name)) walk(full);
      } else out.push(relative(repo, full).replaceAll('\\', '/'));
    }
  };
  walk(repo);
  return out;
}

const files = listFiles();
const matching = (glob: string) => {
  const g = new Bun.Glob(glob);
  return files.filter((f) => g.match(f));
};
const read = (f: string) => readFileSync(join(repo, f), 'utf8');

function packageVersion(name: string): string | null | undefined {
  const esc = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const xml = new RegExp(
    `<Package(?:Version|Reference)\\s+Include="${esc}"(?:\\s+Version="([^"]*)")?`,
    'i',
  );
  let found: string | null | undefined;
  for (const f of matching('**/{Directory.Packages.props,*.csproj}')) {
    const m = read(f).match(xml);
    if (m) found = m[1] ?? found ?? null;
  }
  for (const f of matching('**/package.json')) {
    let pkg;
    try {
      pkg = JSON.parse(read(f));
    } catch {
      continue;
    }
    const v = pkg?.dependencies?.[name] ?? pkg?.devDependencies?.[name];
    if (v) found = String(v).replace(/^[\^~>=]+/, '');
  }
  return found; // undefined: not referenced; null: referenced without a version
}

function compare(a: string, b: string): number {
  const pa = a.split(/[.-]/).map(Number);
  const pb = b.split(/[.-]/).map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}
const isVersion = (v: unknown): v is string =>
  typeof v === 'string' && /^\d+(\.\d+)+$/.test(v);

function passes(c: Check): boolean {
  switch (c.kind) {
    case 'file':
      return matching(c.glob).length > 0;
    case 'contains':
      return matching(c.glob).some((f) => new RegExp(c.pattern, 'm').test(read(f)));
    case 'absent':
      return !matching(c.glob).some((f) => new RegExp(c.pattern, 'm').test(read(f)));
    case 'package': {
      const v = packageVersion(c.name);
      if (v === undefined) return false;
      return !c.atLeast || (v !== null && isVersion(v) && compare(v, c.atLeast) >= 0);
    }
    case 'manual':
      return false;
    case 'all':
      return c.checks.every(passes);
    case 'any':
      return c.checks.some(passes);
  }
}

const manifest = await loadManifest();
const recordPath = join(repo, '.template.json');
// The template's own .template.json is the one new services get (with `dotnet new` conditionals);
// the template itself declares nothing.
let record: Record = {};
if (!templateMode && existsSync(recordPath)) {
  try {
    record = JSON.parse(readFileSync(recordPath, 'utf8')) ?? {};
  } catch (e) {
    fail(`.template.json is not valid JSON: ${(e as Error).message}`);
  }
}
if (!existsSync(recordPath) && !templateMode)
  console.log('No .template.json: every feature is judged by its check alone.');
const synced = isVersion(record.version) ? record.version : null;
console.log(`Repo: ${repo}${synced ? ` (synced with template ${synced})` : ''}\n`);

const missing: string[] = [],
  adopted: string[] = [],
  optedOut: string[] = [];
const known = new Set(manifest.features.map((f) => f.id));
for (const f of manifest.features) {
  const declared = record.features?.[f.id];
  const tags = [
    f.since === null
      ? templateMode
        ? 'expected, not built yet'
        : 'not in the template yet'
      : synced && compare(f.since, synced) > 0
        ? `new since ${synced}`
        : '',
    f.origin ? `from ${f.origin}` : '',
  ].filter(Boolean);
  const line = (note?: string) =>
    `  ${f.id.padEnd(28)} ${note ?? f.description}${tags.length ? ` [${tags.join('; ')}]` : ''}`;
  if (declared && /^(declined|n\/a)\b/.test(declared)) {
    optedOut.push(line(declared));
    continue;
  }
  const ok = passes(f.check);
  if (ok || (f.check.kind === 'manual' && declared === 'adopted')) adopted.push(line());
  else if (declared === 'adopted')
    missing.push(line(`declared adopted, but its check fails: ${f.description}`));
  else
    missing.push(
      line(
        f.check.kind === 'manual'
          ? `${f.description} (manual: declare it in .template.json)`
          : undefined,
      ),
    );
}
const unknown = Object.keys(record.features ?? {}).filter((id) => !known.has(id));

const section = (title: string, lines: string[]) => {
  if (lines.length) console.log(`${title} (${lines.length})\n${lines.join('\n')}\n`);
};
section('Missing', missing);
section('Adopted', adopted);
section('Opted out', optedOut);
if (unknown.length)
  console.log(
    `Unknown ids in .template.json (not in the manifest): ${unknown.join(', ')}\n`,
  );
console.log(
  `${missing.length} missing, ${adopted.length} adopted, ${optedOut.length} opted out.`,
);
process.exit(strict && missing.length ? 1 : 0);
