// Build-time docs search index. Reads the raw setup guide (docs/setup/*.md — the same
// files the backend globs into wwwroot/docs) and emits public/docs-index.json, which Vite
// copies to the served root at /docs-index.json. The docs viewer loads it into MiniSearch
// for client-side full-text search. Runs via the `prebuild`/`predev` npm hooks.
//
// The index is version-locked to the deploy: it is generated from the same source on every
// build, so search always matches the docs actually shipped.

import { readdir, readFile, writeFile, mkdir } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const docsDir = join(here, '..', '..', 'docs', 'setup');
const outDir = join(here, '..', 'public');
const outFile = join(outDir, 'docs-index.json');

// Strip Markdown down to searchable prose. Crude but sufficient — MiniSearch tokenizes the
// result, so leftover noise costs little; we mainly want headings, links, and body words.
function toPlainText(md) {
  return md
    .replace(/```[\s\S]*?```/g, ' ') // fenced code blocks
    .replace(/^\s{0,3}#{1,6}\s+/gm, '') // heading markers
    .replace(/^\s*>\s?/gm, '') // blockquote markers
    .replace(/^\s*([-*+]|\d+\.)\s+/gm, '') // list markers
    .replace(/^\s*\|/gm, ' ') // table row leaders
    .replace(/[|]/g, ' ') // remaining table pipes
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1') // links -> label text
    .replace(/[*_`~#>]/g, '') // inline emphasis / leftover markers
    .replace(/\s+/g, ' ')
    .trim();
}

const files = (await readdir(docsDir)).filter((f) => f.endsWith('.md'));
const docs = [];

for (const file of files.sort()) {
  const slug = file.replace(/\.md$/, '');
  const raw = await readFile(join(docsDir, file), 'utf8');
  const h1 = raw.match(/^#\s+(.+)$/m);
  const title = h1 ? h1[1].trim() : slug;
  const headings = [...raw.matchAll(/^#{1,6}\s+(.+)$/gm)].map((m) => m[1].trim());
  const text = toPlainText(raw);
  // First chunk of prose, used as the result snippet.
  const excerpt = text.slice(0, 180) + (text.length > 180 ? '…' : '');
  docs.push({ slug, title, headings: headings.join(' '), text, excerpt });
}

await mkdir(outDir, { recursive: true });
await writeFile(outFile, JSON.stringify(docs));
console.log(`docs-index: indexed ${docs.length} pages -> ${outFile}`);
