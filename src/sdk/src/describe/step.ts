import { z } from 'zod';
import { isModel, isOptional } from '../core/models.ts';
import { isExternal } from '../core/external.ts';
import {
  AXIS_NAME,
  AXIS_VALUE,
  MAX_AXES,
  MAX_CELLS,
  isCells,
  matrixCells,
  type Matrix,
} from '../core/matrix.ts';
import type { AnyStep } from '../core/steps.ts';
import { isImplementation } from '../core/handlers.ts';
import { recordConfigRefs } from '../core/config-refs.ts';
import type { MapperFunction, StageStepNode } from '../builder/pipeline.ts';
import { FailureContext } from '../library/models.ts';
import { canonicalize } from '../util/canonical.ts';
import { analyzeMapperSource } from './mapper-source.ts';
import { MapperError, recordMapper, type Mapping } from './record.ts';
import type { StepDescriptorT } from './manifest-schema.ts';
import { recordedConfigRefs, secretRefsOf, subSchemas } from './config-schema.ts';
import { resolveField, schemaViews } from './schemas.ts';
import {
  FAILURE_ROOT,
  type Avail,
  type CaseOf,
  type DescribeContext,
} from './context.ts';

type InputTypes = StepDescriptorT['inputTypes'];
type MatrixT = NonNullable<StepDescriptorT['matrix']>;
type ExternalInputs = NonNullable<StepDescriptorT['externalInputs']>;
type InputEntries = [string, unknown][];

export function describeStep(
  ctx: DescribeContext,
  node: StageStepNode,
  avail: Map<string, Avail>,
  failureHandler: boolean,
  cases: readonly CaseOf[] = [],
): StepDescriptorT {
  const problems = ctx.problems;
  const s = node.step;
  const at = `step '${s.name}'`;
  if (typeof s.image !== 'string' || s.image.trim() === '')
    problems.push(`${at}: image must be a non-empty string`);

  const allEntries = Object.entries(s.input as Record<string, unknown>);
  const externalInputs = describeExternalInputs(ctx, at, allEntries, failureHandler);
  const entries = allEntries.filter(([, e]) => !isExternal(e));
  const inputTypes = describeInputTypes(ctx, at, entries);

  let input: Record<string, Mapping>;
  if (failureHandler) input = failureHandlerInput(ctx, at, entries, node);
  else if (node.mapper) input = mapStep(ctx, at, s, node.mapper, avail);
  else input = matchInputsByModel(ctx, at, entries, avail);

  for (const key of Object.keys(s.input as Record<string, unknown>))
    if (key.startsWith('$'))
      problems.push(`${at}: input '${key}': keys starting with '$' are reserved`);
  const secretNames = s.secrets.map((x) => ctx.useSecret(x, at)).sort();
  const configNames = [
    ...new Set(
      (s.config ?? [])
        .map((c) => ctx.useConfig(c, at))
        .filter((n): n is string => n !== undefined),
    ),
  ].sort();
  const carried = isImplementation(s);
  const { settings, settingsProblem } = recordSettings(s, carried);
  const refs = settings ? recordedConfigRefs(settings) : [];
  checkConfigSecrets(ctx, at, configNames, refs, secretNames);
  if (settingsProblem !== undefined)
    problems.push(`${at}: invalid ${s.kind} settings (${settingsProblem})`);
  if (typeof s.kind !== 'string' || s.kind === '')
    problems.push(`${at}: kind must be a non-empty string`);
  checkConfigRefs(ctx, at, refs, configNames, cases);
  if (!carried)
    problems.push(`${at} has no implementation (stage implement(step({...}), fn))`);
  if (typeof s.description !== 'string')
    problems.push(`${at}: description must be a string (it may be empty)`);
  if (
    s.timeoutSeconds !== undefined &&
    !(Number.isInteger(s.timeoutSeconds) && s.timeoutSeconds > 0)
  )
    problems.push(`${at}: timeoutSeconds must be a positive integer`);
  if (s.retries !== undefined && !(Number.isInteger(s.retries) && s.retries >= 0))
    problems.push(`${at}: retries must be a non-negative integer`);
  if (s.matrix !== undefined && failureHandler)
    problems.push(`${at}: a failure handler cannot be a matrix step`);
  const matrix = s.matrix !== undefined ? describeMatrix(ctx, at, s.matrix) : undefined;

  const out: StepDescriptorT = {
    name: s.name,
    description: typeof s.description === 'string' ? s.description : '',
    kind: s.kind,
    image: s.image,
    inputTypes,
    input,
    output: s.output ? ctx.useModel(s.output, `${at} output`) : null,
    secrets: secretNames,
    ...(s.timeoutSeconds !== undefined ? { timeoutSeconds: s.timeoutSeconds } : {}),
    ...(s.retries !== undefined ? { retries: s.retries } : {}),
    ...(matrix !== undefined ? { matrix } : {}),
    ...(settings !== undefined
      ? { config: settings as Record<string, z.core.util.JSONType> }
      : {}),
    ...(configNames.length ? { configEntries: configNames } : {}),
    ...(s.clusterAccess === true ? { clusterAccess: true as const } : {}),
    ...(Object.keys(externalInputs).length ? { externalInputs } : {}),
  };
  if (s.clusterAccess !== undefined && typeof s.clusterAccess !== 'boolean')
    problems.push(`${at}: clusterAccess must be a boolean`);
  if (s.output && !isModel(s.output)) problems.push(`${at}: output must be a model`);
  return out;
}

function describeExternalInputs(
  ctx: DescribeContext,
  at: string,
  allEntries: InputEntries,
  failureHandler: boolean,
): ExternalInputs {
  const problems = ctx.problems;
  const externalInputs: ExternalInputs = {};
  for (const [key, e] of allEntries) {
    if (!isExternal(e)) continue;
    if (failureHandler)
      problems.push(
        `${at}: failure handlers run unattended and cannot wait for input ('${key}')`,
      );
    if (typeof e.title !== 'string' || e.title.trim() === '')
      problems.push(`${at} external input '${key}': title must not be empty`);
    if (e.from.length === 0)
      problems.push(`${at} external input '${key}': from must list at least one source`);
    if (new Set(e.from.map((f) => f.kind)).size !== e.from.length)
      problems.push(`${at} external input '${key}': each source kind may be listed once`);
    if (e.from.some((f) => f.kind === 'callback') && !/^[A-Za-z0-9_-]+$/.test(key))
      problems.push(
        `${at} external input '${key}': a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)`,
      );
    for (const f of e.from) {
      if (f.kind !== 'poll') continue;
      if (!/^https?:\/\//.test(f.url))
        problems.push(
          `${at} external input '${key}': poll url must be an absolute http(s) URL`,
        );
      if (!Number.isInteger(f.intervalSeconds) || f.intervalSeconds < 10)
        problems.push(
          `${at} external input '${key}': poll intervalSeconds must be an integer of at least 10`,
        );
      if (f.valuePath.trim() === '')
        problems.push(`${at} external input '${key}': poll valuePath must not be empty`);
    }
    externalInputs[key] = {
      model: ctx.useModel(e.model, `${at} external input '${key}'`),
      title: e.title,
      description: e.description,
      from: e.from.map((f) => ({ ...f })),
    };
  }
  return externalInputs;
}

function describeInputTypes(
  ctx: DescribeContext,
  at: string,
  entries: InputEntries,
): InputTypes {
  const inputTypes: InputTypes = {};
  for (const [key, e] of entries) {
    if (isCells(e))
      inputTypes[key] = {
        model: ctx.useModel(e.model, `${at} input '${key}'`),
        optional: false,
        cells: true,
      };
    else if (isOptional(e))
      inputTypes[key] = {
        model: ctx.useModel(e.model, `${at} input '${key}'`),
        optional: true,
      };
    else if (isModel(e))
      inputTypes[key] = {
        model: ctx.useModel(e, `${at} input '${key}'`),
        optional: false,
      };
    else if (e instanceof z.ZodType) {
      try {
        inputTypes[key] = { schema: schemaViews(e) };
      } catch (err) {
        ctx.problems.push(
          `${at} input '${key}': schema is not representable as JSON Schema (${(err as Error).message})`,
        );
      }
    } else
      ctx.problems.push(
        `${at} input '${key}': must be a model, optional(model), cells(model) or a zod schema`,
      );
  }
  return inputTypes;
}

function failureHandlerInput(
  ctx: DescribeContext,
  at: string,
  entries: InputEntries,
  node: StageStepNode,
): Record<string, Mapping> {
  const input: Record<string, Mapping> = {};
  for (const [key, e] of entries) {
    if (e !== FailureContext && !(isModel(e) && e.name === FailureContext.name))
      ctx.problems.push(
        `${at}: a failure handler's inputs must all be FailureContext (input '${key}' is not)`,
      );
    input[key] = { ref: FAILURE_ROOT };
  }
  if (node.mapper) ctx.problems.push(`${at}: failure handlers take no mapper`);
  return input;
}

function matchInputsByModel(
  ctx: DescribeContext,
  at: string,
  entries: InputEntries,
  avail: Map<string, Avail>,
): Record<string, Mapping> {
  const problems = ctx.problems;
  const input: Record<string, Mapping> = {};
  for (const [key, e] of entries) {
    if (isCells(e)) {
      const producers = [...avail].filter(
        ([, a]) => a.matrix && a.model === e.model.name,
      );
      if (producers.length === 1) {
        const [producer, a] = producers[0]!;
        if (a.maybe) problems.push(maybeCells(at, e.model.name, key));
        input[key] = { ref: producer };
      } else
        problems.push(
          producers.length === 0
            ? `${at}: ${e.model.name} (input '${key}') has no matrix step producing it at this stage; cells() reads a matrix step`
            : `${at}: ${e.model.name} (input '${key}') has several matrix steps producing it (${producers
                .map(([n]) => n)
                .sort()
                .join(', ')}); use a mapper to pick one`,
        );
      continue;
    }
    const optional = isOptional(e);
    const m = optional ? e.model : isModel(e) ? e : undefined;
    if (!m) {
      problems.push(
        `${at}: input '${key}' is a plain schema, not a model, so it cannot be matched by model; add a mapper ([step, from => ({ ... })])`,
      );
      continue;
    }
    const producers = [...avail].filter(([, a]) => !a.matrix && a.model === m.name);
    const matrixProducer = [...avail].find(([, a]) => a.matrix && a.model === m.name);
    if (producers.length === 0 && matrixProducer) {
      problems.push(
        `${at}: ${m.name} (input '${key}') is produced by matrix step '${matrixProducer[0]}'; read it with cells(${m.name})`,
      );
    } else if (producers.length === 0) {
      problems.push(
        `${at}: ${m.name} (input '${key}') is not available at this stage${optional ? ' (optional() means maybe-skipped; there must still be an earlier producer)' : ''}`,
      );
    } else if (producers.length > 1) {
      problems.push(
        `${at}: ${m.name} (input '${key}') has several producers (${producers
          .map(([n]) => n)
          .sort()
          .join(', ')}); use a mapper to pick one`,
      );
    } else {
      const [producer, a] = producers[0]!;
      if (a.maybe && !optional)
        problems.push(
          `${at}: ${m.name} (input '${key}') is only maybe-available (produced inside a when block); declare it optional(${m.name})`,
        );
      input[key] = { ref: producer };
    }
  }
  return input;
}

function mapStep(
  ctx: DescribeContext,
  at: string,
  s: AnyStep,
  mapper: MapperFunction,
  avail: Map<string, Avail>,
): Record<string, Mapping> {
  const problems = ctx.problems;
  try {
    analyzeMapperSource(Function.prototype.toString.call(mapper));
  } catch (e) {
    if (e instanceof MapperError) problems.push(`${at}: ${e.message}`);
    else throw e;
    return {};
  }
  const roots = new Set([...avail].filter(([, a]) => a.model !== null).map(([n]) => n));
  let recorded: Record<string, Mapping>;
  try {
    recorded = recordMapper(mapper, roots);
  } catch (e) {
    if (e instanceof MapperError) problems.push(`${at}: ${e.message}`);
    else problems.push(`${at}: mapper threw: ${(e as Error)?.message ?? String(e)}`);
    return {};
  }
  const declared = Object.fromEntries(
    Object.entries(s.input as Record<string, unknown>).filter(([, e]) => !isExternal(e)),
  );
  for (const k of Object.keys(declared))
    if (!(k in recorded)) problems.push(`${at}: mapper does not provide input '${k}'`);
  for (const k of Object.keys(recorded))
    if (!(k in declared))
      problems.push(`${at}: mapper provides '${k}', which is not an input of the step`);
  for (const [k, m] of Object.entries(recorded)) {
    const e = declared[k];
    if (isCells(e)) checkCellsMapping(ctx, `${at} input '${k}'`, m, avail, e.model.name);
    else checkMapping(ctx, `${at} input '${k}'`, m, avail, isOptional(e), true);
  }
  return recorded;
}

const maybeCells = (at: string, model: string, key: string) =>
  `${at}: ${model} (input '${key}') is only maybe-available (produced inside a when block); cells() cannot read a maybe-skipped matrix step`;

function checkCellsMapping(
  ctx: DescribeContext,
  at: string,
  m: Mapping,
  avail: Map<string, Avail>,
  model: string,
): void {
  const problems = ctx.problems;
  const ref =
    Object.keys(m).length === 1 && typeof (m as { ref?: unknown }).ref === 'string'
      ? (m as { ref: string }).ref
      : undefined;
  if (ref === undefined || ref.includes('.'))
    return void problems.push(
      `${at}: a cells() input takes a whole matrix step (from['<step>']), not a field or an object`,
    );
  const a = avail.get(ref);
  if (!a) return void problems.push(`${at}: '${ref}' is not available at this stage`);
  if (!a.matrix)
    return void problems.push(
      `${at}: '${ref}' is not a matrix step; cells() reads a matrix step`,
    );
  if (a.model !== model)
    problems.push(`${at}: '${ref}' outputs ${a.model ?? 'nothing'}, not ${model}`);
  if (a.maybe)
    problems.push(
      `${at}: '${ref}' is only maybe-available (produced inside a when block); cells() cannot read a maybe-skipped matrix step`,
    );
}

function checkMapping(
  ctx: DescribeContext,
  at: string,
  m: Mapping,
  avail: Map<string, Avail>,
  optionalTarget: boolean,
  topLevel: boolean,
): void {
  const problems = ctx.problems;
  const keys = Object.keys(m);
  if (
    keys.length === 1 &&
    keys[0] === 'ref' &&
    typeof (m as { ref: unknown }).ref === 'string'
  ) {
    const [root, ...path] = (m as { ref: string }).ref.split('.') as [
      string,
      ...string[],
    ];
    const a = avail.get(root);
    if (!a) return void problems.push(`${at}: '${root}' is not available at this stage`);
    if (a.matrix)
      return void problems.push(
        `${at}: '${(m as { ref: string }).ref}' — '${root}' is a matrix step; read it whole through a cells() input`,
      );
    if (!resolveField(ctx.outputViewOf(a.model), path))
      problems.push(
        `${at}: '${(m as { ref: string }).ref}' — model ${a.model} has no field '${path.join('.')}'`,
      );
    if (a.maybe && !(optionalTarget && topLevel))
      problems.push(
        `${at}: '${(m as { ref: string }).ref}' is only maybe-available (produced inside a when block); map it whole into an optional(...) input`,
      );
    return;
  }
  if (keys.length === 1 && keys[0] === 'const') return;
  for (const [k, x] of Object.entries(m as Record<string, Mapping>))
    checkMapping(ctx, `${at}.${k}`, x, avail, optionalTarget, false);
}

function recordSettings(
  s: AnyStep,
  carried: boolean,
): { settings?: Record<string, unknown>; settingsProblem?: string } {
  if (s.settings === undefined && !carried) return {};
  try {
    return {
      settings: canonicalize(recordConfigRefs(s.settings ?? {})) as Record<
        string,
        unknown
      >,
    };
  } catch (e) {
    return { settings: {}, settingsProblem: (e as Error).message };
  }
}

type ConfigRefUse = { ref: string; case?: string };

// A step whose settings reference parts of an entry needs the secrets those parts name; any other
// step receiving an entry needs every secret the entry names.
function checkConfigSecrets(
  ctx: DescribeContext,
  at: string,
  configNames: readonly string[],
  refs: readonly ConfigRefUse[],
  secretNames: readonly string[],
): void {
  for (const name of configNames) {
    const into = refs.filter((r) => r.ref.split('.')[0] === name);
    const needed =
      into.length === 0
        ? (ctx.configs.get(name)?.secretRefs ?? [])
        : [
            ...new Set(
              into.flatMap((r) =>
                secretRefsOf(
                  subSchemas(
                    ctx.configs.get(name)?.schema,
                    r.ref.split('.').slice(1),
                    r.case,
                    name,
                  ),
                ),
              ),
            ),
          ].sort();
    for (const ref of needed)
      if (!secretNames.includes(ref))
        ctx.problems.push(
          `${at}: config '${name}' references secret '${ref}' (secretRef), so the step must declare it in secrets`,
        );
  }
}

function checkConfigRefs(
  ctx: DescribeContext,
  at: string,
  refs: readonly ConfigRefUse[],
  configNames: readonly string[],
  cases: readonly CaseOf[],
): void {
  const problems = ctx.problems;
  for (const { ref, case: kase } of refs) {
    const [entry, ...path] = ref.split('.') as [string, ...string[]];
    if (kase !== undefined && !cases.some((c) => c.cond === kase))
      problems.push(
        `${at}: config reference '${ref}' is narrowed to the select case '${kase}' and can only be used inside it`,
      );
    if (!configNames.includes(entry))
      problems.push(
        `${at}: config reference '${ref}': the step does not receive config entry '${entry}'`,
      );
    else if (!resolveField(ctx.configs.get(entry)?.schema, path))
      problems.push(
        `${at}: config reference '${ref}': config '${entry}' has no field '${path.join('.')}'`,
      );
  }
}

function describeMatrix(
  ctx: DescribeContext,
  at: string,
  m: Matrix,
): MatrixT | undefined {
  const problems = ctx.problems;
  const before = problems.length;
  const axes = Object.entries(
    typeof m.axes === 'object' && m.axes !== null ? m.axes : {},
  ) as [string, unknown][];
  if (axes.length < 1 || axes.length > MAX_AXES)
    problems.push(
      `${at}: a matrix has 1 to ${MAX_AXES} axes (this one has ${axes.length})`,
    );
  for (const [name, values] of axes) {
    if (!AXIS_NAME.test(name))
      problems.push(`${at}: matrix axis name '${name}' must match ${AXIS_NAME}`);
    if (!Array.isArray(values) || values.some((v) => typeof v !== 'string')) {
      problems.push(`${at}: matrix axis '${name}' must be a list of strings`);
      continue;
    }
    if (values.length === 0) problems.push(`${at}: matrix axis '${name}' has no values`);
    for (const v of values as string[])
      if (!AXIS_VALUE.test(v))
        problems.push(
          `${at}: matrix axis '${name}': value '${v}' must match ${AXIS_VALUE}`,
        );
    for (const v of new Set(
      (values as string[]).filter((v, i) => values.indexOf(v) !== i),
    ))
      problems.push(`${at}: matrix axis '${name}': value '${v}' is listed twice`);
  }
  const known = new Map(axes);
  m.exclude.forEach((x, i) => {
    const where = `${at}: matrix exclude ${i + 1}`;
    if (Object.keys(x).length === 0)
      problems.push(`${where} must name at least one axis`);
    for (const [k, v] of Object.entries(x)) {
      const values = known.get(k);
      if (!values) problems.push(`${where}: '${k}' is not an axis`);
      else if (!(Array.isArray(values) && values.includes(v)))
        problems.push(`${where}: '${String(v)}' is not a value of axis '${k}'`);
    }
  });
  const kind = (m.failure as { kind?: unknown } | undefined)?.kind;
  if (kind !== 'keepRunning' && kind !== 'cascadeFailure')
    problems.push(`${at}: matrix failure must be keepRunning() or cascadeFailure()`);
  if (
    m.maxParallel !== undefined &&
    !(Number.isInteger(m.maxParallel) && m.maxParallel >= 1)
  )
    problems.push(`${at}: matrix maxParallel must be a positive integer`);
  if (problems.length > before) return undefined;
  const count = matrixCells(m).length;
  if (count === 0) problems.push(`${at}: the matrix excludes every cell`);
  else if (count > MAX_CELLS)
    problems.push(`${at}: the matrix has ${count} cells (at most ${MAX_CELLS})`);
  return {
    axes: axes.map(([name, values]) => ({ name, values: [...(values as string[])] })),
    ...(m.exclude.length ? { exclude: m.exclude.map((x) => ({ ...x })) } : {}),
    failure: { kind: kind as MatrixT['failure']['kind'] },
    ...(m.maxParallel !== undefined ? { maxParallel: m.maxParallel } : {}),
  };
}
