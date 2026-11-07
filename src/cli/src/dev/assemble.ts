import {
  findStep,
  type DevManifest,
  type DevStep,
  type Json,
  type JsonObject,
} from './manifest';
import { cellName, cellsOf } from './matrix';
import type { Pool, PoolEntry } from './pool';

export type Assembly = {
  input: JsonObject;
  problems: string[];
  skippedBecause: string[];
};

type Resolved = { present: true; value: Json } | { present: false };
const absent: Resolved = { present: false };

/** The step's input.json from its tagged mappings over the local pool, as the scheduler builds it. */
/** `pl dev run build-cli --cell os=linux,arch=x64` for a cell's name. */
const runCommand = (name: string) => {
  const m = /^([^[]+)\[(.*)\]$/.exec(name);
  return m ? `pl dev run ${m[1]} --cell ${m[2]}` : `pl dev run ${name}`;
};

export function assemble(step: DevStep, pool: Pool, manifest?: DevManifest): Assembly {
  const input: JsonObject = {};
  const problems: string[] = [];
  const skippedBecause: string[] = [];
  for (const [key, mapping] of Object.entries(step.input)) {
    const unavailable: { root: string; entry?: PoolEntry }[] = [];
    const resolved = step.cellsInputs[key]
      ? resolveCells(mapping, pool, manifest, unavailable, problems)
      : resolveMapping(mapping, pool, unavailable, problems);
    if (unavailable.length === 0) {
      if (resolved.present) input[key] = resolved.value;
      continue;
    }
    if (step.optionalInputs[key]) continue; // a maybe-skipped input: leave the key out
    const seen = new Set<string>();
    for (const { root, entry } of unavailable) {
      if (seen.has(root)) continue;
      seen.add(root);
      if (entry?.status === 'skipped')
        skippedBecause.push(`needs '${root}', which was skipped (${entry.reason})`);
      else if (entry?.status === 'failed')
        problems.push(
          `input '${key}' needs the output of '${root}', which failed (${entry.reason}); rerun \`${runCommand(root)}\``,
        );
      else if (pool.source)
        problems.push(
          `input '${key}' needs the output of '${root}', which has none in ${pool.source.pipeline}#${pool.source.run}; run \`pl dev run ${root}\` first (or pass --input-file)`,
        );
      else
        problems.push(
          `input '${key}' needs the output of '${root}', which is not in the local pool; run \`pl dev run ${root}\` first (or pass --input-file)`,
        );
    }
  }
  return { input, problems, skippedBecause };
}

/** A `cells` input: `[{cell, output}]` in cell order, from the cells' pool entries. */
function resolveCells(
  mapping: Json,
  pool: Pool,
  manifest: DevManifest | undefined,
  unavailable: { root: string; entry?: PoolEntry }[],
  problems: string[],
): Resolved {
  const root =
    mapping && typeof mapping === 'object' && !Array.isArray(mapping)
      ? mapping.ref
      : undefined;
  const matrix =
    typeof root === 'string' && manifest ? findStep(manifest, root)?.matrix : undefined;
  if (typeof root !== 'string' || !matrix) {
    problems.push(`a cells input must map a matrix step, got ${JSON.stringify(mapping)}`);
    return absent;
  }
  const whole = pool.steps.get(root);
  if (whole?.status === 'skipped') {
    unavailable.push({ root, entry: whole });
    return absent;
  }
  const value: Json[] = [];
  let ran = false;
  for (const cell of cellsOf(matrix)) {
    const name = cellName(root, cell);
    const entry = pool.steps.get(name);
    if (entry) ran = true;
    if (entry?.status === 'failed') unavailable.push({ root: name, entry });
    if (entry?.status === 'succeeded')
      value.push({ cell, output: structuredClone(entry.output ?? null) });
  }
  if (!ran) unavailable.push({ root });
  return { present: true, value };
}

function resolveMapping(
  mapping: Json,
  pool: Pool,
  unavailable: { root: string; entry?: PoolEntry }[],
  problems: string[],
): Resolved {
  if (mapping && typeof mapping === 'object' && !Array.isArray(mapping)) {
    const keys = Object.keys(mapping);
    if (keys.length === 1 && typeof mapping.ref === 'string')
      return resolveRef(mapping.ref, pool, unavailable, problems);
    if (keys.length === 1 && 'const' in mapping)
      return { present: true, value: structuredClone(mapping.const ?? null) };
    const nested: JsonObject = {};
    for (const [key, child] of Object.entries(mapping)) {
      const r = resolveMapping(child, pool, unavailable, problems);
      if (r.present) nested[key] = r.value;
    }
    return { present: true, value: nested };
  }
  // Not a tag (manifests always tag); pass literals through.
  return { present: true, value: structuredClone(mapping) };
}

function resolveRef(
  reference: string,
  pool: Pool,
  unavailable: { root: string; entry?: PoolEntry }[],
  problems: string[],
): Resolved {
  const [root, ...path] = reference.split('.');
  let current: Json | undefined;
  if (root === 'input') {
    current = pool.input ?? null;
    if (path.length && !(pool.input && path[0]! in pool.input)) {
      problems.push(
        `pipeline input field '${path[0]}' is not set; pass --field ${path[0]}=…`,
      );
      return absent;
    }
  } else if (root === 'failure') {
    problems.push(
      'failure-handler steps take a FailureContext; pass it with --input-file',
    );
    return absent;
  } else {
    const entry = pool.steps.get(root!);
    if (entry?.status !== 'succeeded') {
      unavailable.push({ root: root!, entry });
      return absent;
    }
    current = entry.output;
  }
  for (const segment of path) {
    if (
      !current ||
      typeof current !== 'object' ||
      Array.isArray(current) ||
      !(segment in current)
    )
      return absent; // an absent optional field
    current = current[segment];
  }
  return { present: true, value: structuredClone(current ?? null) };
}
