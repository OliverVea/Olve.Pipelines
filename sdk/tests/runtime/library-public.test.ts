// The library is built on the public API only, so a step package outside the SDK can do all it does.
import { describe, expect, test } from 'bun:test';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { SDK } from './helpers.ts';

const src = join(SDK, 'src');
const exported = (file: string) =>
  new Set(
    [...readFileSync(file, 'utf8').matchAll(/export (?:type )?\{([^}]*)\}/g)]
      .flatMap((m) => m[1]!.split(','))
      .map((n) =>
        n
          .trim()
          .replace(/^type /, '')
          .split(/\s+as\s+/)
          .at(-1)!,
      )
      .filter(Boolean),
  );

describe('library steps use the public API', () => {
  const publicNames = exported(join(src, 'index.ts'));
  const library = join(src, 'library');
  for (const f of readdirSync(library).filter((f) => f.endsWith('.ts'))) {
    test(f, () => {
      const text = readFileSync(join(library, f), 'utf8');
      const offending: string[] = [];
      for (const m of text.matchAll(
        /import (?:type )?\{([^}]*)\} from "(\.\.\/[^"]+)"/g,
      )) {
        if (m[2]!.startsWith('../util/')) continue;
        for (const raw of m[1]!.split(',')) {
          const name = raw
            .trim()
            .replace(/^type /, '')
            .split(/\s+as\s+/)[0]!;
          if (name && !publicNames.has(name)) offending.push(`${name} (${m[2]})`);
        }
      }
      expect(offending).toEqual([]);
    });
  }
});
