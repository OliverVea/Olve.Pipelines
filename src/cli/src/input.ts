import { versionsGet, versionsList } from '@olve/api';
import type { Client } from '@olve/api/client';
import { call } from './api';
import { currentVersion } from './commands/pipeline';
import { CliError, describe } from './errors';

export type Schema = Record<string, unknown>;

const properties = (schema: Schema | undefined): Record<string, Schema> | undefined =>
  schema && typeof schema.properties === 'object' && schema.properties
    ? (schema.properties as Record<string, Schema>)
    : undefined;

/** The text of a `--input`/`--value` option: inline JSON, `@file`, or `@-` for stdin. */
export async function readInputText(
  option: string,
  stdin: () => Promise<string>,
): Promise<string> {
  if (!option.startsWith('@')) return option;
  const path = option.slice(1);
  try {
    return path === '-' ? await stdin() : await Bun.file(path).text();
  } catch (error) {
    throw new CliError(`Could not read --input file '${path}': ${describe(error)}`);
  }
}

/**
 * The input object: `--input` (a whole JSON object) with each `--field k=v` set over it, typed
 * by the model's schema when known (`sha=0123` stays a string, `count=3` becomes a number).
 */
export async function buildInput(
  inputOption: string | undefined,
  fields: readonly string[],
  schema: Schema | undefined,
  stdin: () => Promise<string>,
  optionName = '--input',
): Promise<Record<string, unknown>> {
  let input: Record<string, unknown> = {};
  if (inputOption !== undefined) {
    const text = await readInputText(inputOption, stdin);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      throw new CliError(`${optionName} is not valid JSON: ${describe(error)}`);
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
      throw new CliError(`${optionName} must be a JSON object (the pipeline input).`);
    input = parsed as Record<string, unknown>;
  }

  const props = properties(schema);
  const problems: string[] = [];
  for (const field of fields) {
    const eq = field.indexOf('=');
    if (eq <= 0) {
      problems.push(`--field expects key=value, got '${field}'.`);
      continue;
    }
    const key = field.slice(0, eq);
    if (props && !(key in props)) {
      problems.push(
        `Unknown pipeline input field '${key}' (known: ${Object.keys(props).join(', ')}).`,
      );
      continue;
    }
    try {
      input[key] = coerce(key, field.slice(eq + 1), props?.[key]);
    } catch (error) {
      if (error instanceof CliError) problems.push(...error.problems);
      else throw error;
    }
  }
  if (problems.length) throw new CliError(problems);
  return input;
}

/** One `--field` value as the JSON its schema property asks for. */
export function coerce(key: string, raw: string, property: Schema | undefined): unknown {
  const type = typeof property?.type === 'string' ? property.type : undefined;
  switch (type) {
    case 'string':
      return raw;
    case 'boolean':
      if (raw === 'true') return true;
      if (raw === 'false') return false;
      throw new CliError(
        `Pipeline input '${key}' is a boolean: use ${key}=true or ${key}=false (got '${raw}').`,
      );
    case 'integer':
      if (/^-?\d+$/.test(raw)) return Number(raw);
      throw new CliError(`Pipeline input '${key}' is an integer (got '${raw}').`);
    case 'number': {
      const n = Number(raw);
      if (raw.trim() !== '' && !Number.isNaN(n)) return n;
      throw new CliError(`Pipeline input '${key}' is a number (got '${raw}').`);
    }
  }

  // An untyped enum: the member whose string form or JSON matches.
  if (type === undefined && Array.isArray(property?.enum)) {
    for (const member of property.enum as unknown[]) {
      if (member === raw || JSON.stringify(member) === raw) return member;
    }
  }

  if (type === 'object' || type === 'array') {
    try {
      return JSON.parse(raw);
    } catch (error) {
      throw new CliError(`Pipeline input '${key}' is a JSON ${type}: ${describe(error)}`);
    }
  }

  // Unions and unknown fields: JSON if it parses, else the raw string.
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

/** The pipeline input model's `z.input` view (defaults optional) from a manifest's text. */
export function manifestInputSchema(manifestText: string): Schema | undefined {
  try {
    const manifest = JSON.parse(manifestText) as {
      input?: unknown;
      models?: Record<string, { input?: Schema; output?: Schema }>;
    };
    if (typeof manifest.input !== 'string') return undefined;
    const model = manifest.models?.[manifest.input];
    return model?.input ?? model?.output;
  } catch {
    return undefined;
  }
}

/** The input schema of a pipeline version (default: the version runs start on). Best-effort: undefined when unknown. */
export async function pipelineInputSchema(
  io: { api: Client; log(message: string): void },
  pipelineId: string,
  version?: number,
): Promise<Schema | undefined> {
  try {
    if (version === undefined) {
      const versions = await call(versionsList({ client: io.api, path: { pipelineId } }));
      version = currentVersion(versions.activation);
      if (version === undefined) return undefined;
    }
    const pv = await call(
      versionsGet({ client: io.api, path: { pipelineId, number: version } }),
    );
    return manifestInputSchema(pv.manifest);
  } catch (error) {
    io.log(`input schema unavailable (fields sent untyped): ${describe(error)}`);
    return undefined;
  }
}
