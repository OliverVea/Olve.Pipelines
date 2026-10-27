// Regenerates manifest.schema.json from src/describe/manifest-schema.ts.
import { join } from 'node:path';
import { manifestJsonSchema } from '../src/describe/manifest-schema.ts';
import { canonicalJson } from '../src/util/canonical.ts';

const out = join(import.meta.dir, '..', 'manifest.schema.json');
await Bun.write(out, canonicalJson(manifestJsonSchema()));
console.log(`wrote ${out}`);
