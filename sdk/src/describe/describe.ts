// `describe`: evaluates the contracts (models + pipeline, never handlers) into the manifest and runs
// every describe-time check. All problems are collected and reported together.
import { z } from "zod";
import { CONFIG_NAME, isConfigEntry, SECRET_REF_KIND } from "../core/config.ts";
import { CONFIG_CASE_KEY, CONFIG_REF_KEY } from "../core/config-refs.ts";
import { configRefPath, configRefTarget, isConfigRef, unionAt } from "../core/config.ts";
import { isModel, isOptional, MODEL_NAME, type AnyModel } from "../core/models.ts";
import { isExternal } from "../core/external.ts";
import { SECRET_NAME, type Secret } from "../core/secrets.ts";
import { RESERVED_STEP_NAMES, STEP_NAME, isStep, type AnyStep } from "../core/steps.ts";
import { isImplementation } from "../core/handlers.ts";
import { recordConfigRefs } from "../core/config-refs.ts";
import { pipelineData, type MapperFunction, type StageNode, type StageStepNode } from "../builder/pipeline.ts";
import { FailureContext } from "../library/models.ts";
import { canonicalize, canonicalJson } from "../util/canonical.ts";
import { libraryHandlerHash, type HarnessInfo } from "../util/handler-hash.ts";
import { analyzeMapperSource } from "./mapper-source.ts";
import { MapperError, recordMapper, type Mapping } from "./record.ts";
import { API_VERSION, Manifest, type ManifestT, type ModelSchemasT, type Stage, type StepDescriptorT, type TriggerT } from "./manifest-schema.ts";

export class DescribeError extends Error {
  override name = "DescribeError";
  constructor(readonly problems: readonly string[]) {
    super(`describe found ${problems.length} problem(s):\n${problems.map((p) => `  - ${p}`).join("\n")}`);
  }
}

export interface DescribeOptions {
  /** Step names that have a handler (a `handlers/*.ts` default-exporting `implement(step, …)`). */
  handlerNames: Iterable<string>;
  /** Step name → handler hash (from the build; includes the harness identity). */
  handlerHashes: Readonly<Record<string, string>>;
  /** Kind → hash of the code that implements the steps of that kind which carry their implementation (from the build). */
  kindHashes?: Readonly<Record<string, string>>;
  harness: HarnessInfo;
}

/** Root name used in failure-handler mappings (the service supplies FailureContext under it). */
export const FAILURE_ROOT = "failure";
const RESERVED = new Set([...RESERVED_STEP_NAMES, FAILURE_ROOT]);

type JsonSchema = Record<string, unknown>;
interface Avail {
  model: string | null;
  maybe: boolean;
}

function stripStrict(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(stripStrict);
  if (v !== null && typeof v === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v)) if (!(k === "additionalProperties" && x === false)) out[k] = stripStrict(x);
    return out;
  }
  return v;
}

/** JSON Schema views of a zod schema. Throws if it is not representable (dates, transforms, …). */
export function schemaViews(schema: z.ZodType): ModelSchemasT {
  const view = (io: "input" | "output") => {
    const js = z.toJSONSchema(schema, { io, target: "draft-2020-12" }) as JsonSchema;
    delete js.$schema; // constant noise; apiVersion pins the dialect
    return canonicalize(js) as JsonSchema;
  };
  const output = view("output");
  const input = view("input");
  const same = JSON.stringify(stripStrict(output)) === JSON.stringify(input);
  return same ? { output } : { output, input };
}

/** Follows `segments` through a JSON Schema; undefined if the field doesn't exist. */
function resolveField(schema: JsonSchema | undefined, segments: readonly string[]): boolean {
  if (!schema) return false;
  if (segments.length === 0) return true;
  const [seg, ...rest] = segments as [string, ...string[]];
  if (Array.isArray(schema.anyOf)) return (schema.anyOf as JsonSchema[]).some((b) => resolveField(b, segments));
  if (Array.isArray(schema.oneOf)) return (schema.oneOf as JsonSchema[]).some((b) => resolveField(b, segments));
  if (Array.isArray(schema.allOf)) return (schema.allOf as JsonSchema[]).some((b) => resolveField(b, segments));
  const props = schema.properties as Record<string, JsonSchema> | undefined;
  if (props && Object.hasOwn(props, seg)) return resolveField(props[seg], rest);
  const extra = schema.additionalProperties;
  if (extra && typeof extra === "object") return resolveField(extra as JsonSchema, rest);
  // A schema without type/properties/anyOf accepts anything (z.unknown / z.any).
  return Object.keys(schema).every((k) => k === "description" || k === "title" || k === "default");
}

function literalValues(field: JsonSchema | undefined): unknown[] | undefined {
  if (!field) return undefined;
  if (Array.isArray(field.enum)) return field.enum as unknown[];
  if (field.const !== undefined) return [field.const];
  if (field.type === "boolean") return [true, false];
  const alts = (field.anyOf ?? field.oneOf) as JsonSchema[] | undefined;
  if (Array.isArray(alts)) {
    const all: unknown[] = [];
    for (const a of alts) {
      const l = literalValues(a);
      if (!l) return undefined;
      all.push(...l);
    }
    return all;
  }
  return undefined;
}

export function describePipeline(pipeline: unknown, opts: DescribeOptions): ManifestT {
  const data = pipelineData(pipeline);
  const problems: string[] = [...data.problems];
  const handlerNames = new Set(opts.handlerNames);

  // ── models ──
  const models = new Map<string, { schemas: ModelSchemasT; key: string }>();
  const useModel = (m: AnyModel, at: string): string => {
    if (!MODEL_NAME.test(m.name)) problems.push(`${at}: model name '${m.name}' must match ${MODEL_NAME}`);
    let schemas: ModelSchemasT;
    try {
      schemas = schemaViews(m.schema);
    } catch (e) {
      problems.push(`${at}: model '${m.name}' is not representable as JSON Schema (${(e as Error).message})`);
      return m.name;
    }
    const key = JSON.stringify(schemas);
    const prev = models.get(m.name);
    if (!prev) models.set(m.name, { schemas, key });
    else if (prev.key !== key) problems.push(`two different schemas are registered under the model name '${m.name}' (one schema per model name)`);
    return m.name;
  };

  // ── secrets ──
  const secrets = new Map<string, string>();
  const useSecret = (s: Secret, at: string): string => {
    if (!SECRET_NAME.test(s.name)) problems.push(`${at}: secret name '${s.name}' must match ${SECRET_NAME}`);
    const prev = secrets.get(s.name);
    if (prev === undefined) secrets.set(s.name, s.description);
    else if (prev !== s.description) problems.push(`secret '${s.name}' is declared twice with different descriptions`);
    return s.name;
  };

  // ── config entries ──
  const configs = new Map<string, { description: string; required: boolean; schema: JsonSchema; key: string; secretRefs: string[] }>();
  const useConfig = (e: unknown, at: string): string | undefined => {
    if (!isConfigEntry(e)) return void problems.push(`${at}: config entries must be config("name", "description", schema)`);
    if (!CONFIG_NAME.test(e.name)) problems.push(`${at}: config name '${e.name}' must match ${CONFIG_NAME}`);
    let schema: JsonSchema;
    try {
      schema = z.toJSONSchema(e.schema, { io: "input", target: "draft-2020-12" }) as JsonSchema;
      delete schema.$schema;
      schema = canonicalize(schema) as JsonSchema;
    } catch (err) {
      problems.push(`${at}: config '${e.name}' is not representable as JSON Schema (${(err as Error).message})`);
      return e.name;
    }
    const key = JSON.stringify([e.description, schema]);
    const prev = configs.get(e.name);
    if (!prev) configs.set(e.name, { description: e.description, required: !e.schema.safeParse(undefined).success, schema, key, secretRefs: secretRefsOf(schema) });
    else if (prev.key !== key) problems.push(`two different config entries are named '${e.name}' (one description and schema per config name)`);
    return e.name;
  };

  // ── step identity ──
  const steps = new Map<string, AnyStep>();
  const placements = new Map<string, { step: AnyStep; cases: readonly CaseOf[] }[]>();
  // Two places are mutually exclusive when they sit in different cases of the same select.
  const exclusive = (a: readonly CaseOf[], b: readonly CaseOf[]) => a.some((x) => b.some((y) => y.select === x.select && y.value !== x.value));
  const registerStep = (s: AnyStep, where: string, cases: readonly CaseOf[] = []) => {
    if (typeof s.name !== "string" || !STEP_NAME.test(s.name))
      problems.push(`${where}: step name '${String(s.name)}' must be lower-case kebab (${STEP_NAME})`);
    if (RESERVED.has(s.name)) problems.push(`${where}: '${s.name}' is reserved and cannot be a step name`);
    const prev = placements.get(s.name) ?? [];
    if (prev.some((p) => !exclusive(p.cases, cases))) problems.push(`duplicate step name '${s.name}'`);
    else if (prev.length) {
      if (prev.some((p) => p.step !== s && !isImplementation(p.step) && !isImplementation(s)))
        problems.push(`step '${s.name}': two different steps with handler files share the name across select cases; handlers pair by name, so use one step or different names`);
      if (prev.some((p) => (p.step.output?.name ?? null) !== (s.output?.name ?? null)))
        problems.push(`step '${s.name}': its select cases must give it the same output model`);
    }
    placements.set(s.name, [...prev, { step: s, cases }]);
    if (!steps.has(s.name)) steps.set(s.name, s);
  };

  const inputModel = data.input;
  if (!isModel(inputModel)) throw new DescribeError(["pipeline input must be a model (model(\"Name\", z.object({...})))"]);
  useModel(inputModel, "pipeline input");
  const inputOutputView = (): JsonSchema | undefined => models.get(inputModel.name)?.schemas.output;
  const outputViewOf = (modelName: string | null): JsonSchema | undefined => (modelName ? models.get(modelName)?.schemas.output : undefined);

  // ── one step ──
  const describeStep = (node: StageStepNode, avail: Map<string, Avail>, failureHandler: boolean, cases: readonly CaseOf[] = []): StepDescriptorT => {
    const s = node.step;
    const at = `step '${s.name}'`;
    if (typeof s.image !== "string" || s.image.trim() === "") problems.push(`${at}: image must be a non-empty string`);

    const inputTypes: StepDescriptorT["inputTypes"] = {};
    const externalInputs: NonNullable<StepDescriptorT["externalInputs"]> = {};
    const allEntries = Object.entries(s.input as Record<string, unknown>);
    for (const [key, e] of allEntries) {
      if (!isExternal(e)) continue;
      if (failureHandler) problems.push(`${at}: failure handlers run unattended and cannot wait for input ('${key}')`);
      if (typeof e.title !== "string" || e.title.trim() === "") problems.push(`${at} external input '${key}': title must not be empty`);
      if (e.from.length === 0) problems.push(`${at} external input '${key}': from must list at least one source`);
      if (new Set(e.from.map((f) => f.kind)).size !== e.from.length) problems.push(`${at} external input '${key}': each source kind may be listed once`);
      if (e.from.some((f) => f.kind === "callback") && !/^[A-Za-z0-9_-]+$/.test(key))
        problems.push(`${at} external input '${key}': a callback input's key must be letters, digits, '_' or '-' (it is part of the URL)`);
      for (const f of e.from) {
        if (f.kind !== "poll") continue;
        if (!/^https?:\/\//.test(f.url)) problems.push(`${at} external input '${key}': poll url must be an absolute http(s) URL`);
        if (!Number.isInteger(f.intervalSeconds) || f.intervalSeconds < 10) problems.push(`${at} external input '${key}': poll intervalSeconds must be an integer of at least 10`);
        if (f.valuePath.trim() === "") problems.push(`${at} external input '${key}': poll valuePath must not be empty`);
      }
      externalInputs[key] = {
        model: useModel(e.model, `${at} external input '${key}'`),
        title: e.title,
        description: e.description,
        from: e.from.map((f) => ({ ...f })),
      };
    }
    const entries = allEntries.filter(([, e]) => !isExternal(e));
    for (const [key, e] of entries) {
      if (isOptional(e)) inputTypes[key] = { model: useModel(e.model, `${at} input '${key}'`), optional: true };
      else if (isModel(e)) inputTypes[key] = { model: useModel(e, `${at} input '${key}'`), optional: false };
      else if (e instanceof z.ZodType) {
        try {
          inputTypes[key] = { schema: schemaViews(e) };
        } catch (err) {
          problems.push(`${at} input '${key}': schema is not representable as JSON Schema (${(err as Error).message})`);
        }
      } else problems.push(`${at} input '${key}': must be a model, optional(model) or a zod schema`);
    }

    let input: Record<string, Mapping> = {};
    if (failureHandler) {
      for (const [key, e] of entries) {
        if (e !== FailureContext && !(isModel(e) && e.name === FailureContext.name))
          problems.push(`${at}: a failure handler's inputs must all be FailureContext (input '${key}' is not)`);
        input[key] = { ref: FAILURE_ROOT };
      }
      if (node.mapper) problems.push(`${at}: failure handlers take no mapper`);
    } else if (node.mapper) {
      input = mapStep(at, s, node.mapper, avail);
    } else {
      for (const [key, e] of entries) {
        const optional = isOptional(e);
        const m = optional ? e.model : isModel(e) ? e : undefined;
        if (!m) {
          problems.push(`${at}: input '${key}' is a plain schema, not a model, so it cannot be matched by model; add a mapper ([step, from => ({ ... })])`);
          continue;
        }
        const producers = [...avail].filter(([, a]) => a.model === m.name);
        if (producers.length === 0) {
          problems.push(`${at}: ${m.name} (input '${key}') is not available at this stage${optional ? " (optional() means maybe-skipped; there must still be an earlier producer)" : ""}`);
        } else if (producers.length > 1) {
          problems.push(`${at}: ${m.name} (input '${key}') has several producers (${producers.map(([n]) => n).sort().join(", ")}); use a mapper to pick one`);
        } else {
          const [producer, a] = producers[0]!;
          if (a.maybe && !optional) problems.push(`${at}: ${m.name} (input '${key}') is only maybe-available (produced inside a when block); declare it optional(${m.name})`);
          input[key] = { ref: producer };
        }
      }
    }

    for (const key of Object.keys(s.input as Record<string, unknown>))
      if (key.startsWith("$")) problems.push(`${at}: input '${key}': keys starting with '$' are reserved`);
    const secretNames = s.secrets.map((x) => useSecret(x, at)).sort();
    const configNames = [...new Set((s.config ?? []).map((c) => useConfig(c, at)).filter((n): n is string => n !== undefined))].sort();
    const carried = isImplementation(s);
    let settings: Record<string, unknown> | undefined;
    let settingsProblem: string | undefined;
    if (s.settings !== undefined || carried) {
      try {
        settings = canonicalize(recordConfigRefs(s.settings ?? {})) as Record<string, unknown>;
      } catch (e) {
        settingsProblem = (e as Error).message;
        settings = {};
      }
    }
    const refs = settings ? recordedConfigRefs(settings) : [];
    // A step whose settings reference parts of an entry needs the secrets those parts name; any other
    // step receiving an entry needs every secret the entry names.
    for (const name of configNames) {
      const into = refs.filter((r) => r.ref.split(".")[0] === name);
      const needed = into.length === 0
        ? configs.get(name)?.secretRefs ?? []
        : [...new Set(into.flatMap((r) => secretRefsOf(subSchemas(configs.get(name)?.schema, r.ref.split(".").slice(1), r.case, name))))].sort();
      for (const ref of needed)
        if (!secretNames.includes(ref)) problems.push(`${at}: config '${name}' references secret '${ref}' (secretRef), so the step must declare it in secrets`);
    }
    let handlerHash: string;
    if (settingsProblem !== undefined) problems.push(`${at}: invalid ${s.kind} settings (${settingsProblem})`);
    if (typeof s.kind !== "string" || s.kind === "") problems.push(`${at}: kind must be a non-empty string`);
    if (carried && s.kind === "custom") problems.push(`${at} carries its implementation, so it needs a kind (step({ kind: "…", … })); a step with kind custom has its handler in handlers/`);
    for (const { ref, case: kase } of refs) {
      const [entry, ...path] = ref.split(".") as [string, ...string[]];
      if (kase !== undefined && !cases.some((c) => c.cond === kase))
        problems.push(`${at}: config reference '${ref}' is narrowed to the select case '${kase}' and can only be used inside it`);
      if (!configNames.includes(entry)) problems.push(`${at}: config reference '${ref}': the step does not receive config entry '${entry}'`);
      else if (!resolveField(configs.get(entry)?.schema, path)) problems.push(`${at}: config reference '${ref}': config '${entry}' has no field '${path.join(".")}'`);
    }
    if (carried) {
      const code = opts.kindHashes?.[s.kind];
      if (opts.kindHashes && !code) problems.push(`${at}: no hash was computed for the code of kind '${s.kind}'`);
      handlerHash = libraryHandlerHash(s.kind, settings, opts.harness, code);
    } else {
      if (!handlerNames.has(s.name)) problems.push(`${at} has no handler (add a handlers/*.ts default-exporting implement(${camel(s.name)}, …))`);
      handlerHash = opts.handlerHashes[s.name] ?? "";
      if (handlerNames.has(s.name) && !handlerHash) problems.push(`${at}: no handler hash was computed`);
    }
    if (typeof s.description !== "string") problems.push(`${at}: description must be a string (it may be empty)`);
    if (s.timeoutSeconds !== undefined && !(Number.isInteger(s.timeoutSeconds) && s.timeoutSeconds > 0)) problems.push(`${at}: timeoutSeconds must be a positive integer`);
    if (s.retries !== undefined && !(Number.isInteger(s.retries) && s.retries >= 0)) problems.push(`${at}: retries must be a non-negative integer`);

    const out: StepDescriptorT = {
      name: s.name,
      description: typeof s.description === "string" ? s.description : "",
      kind: s.kind,
      image: s.image,
      inputTypes,
      input,
      output: s.output ? useModel(s.output, `${at} output`) : null,
      secrets: secretNames,
      handlerHash: handlerHash || "sha256:" + "0".repeat(64),
      ...(s.timeoutSeconds !== undefined ? { timeoutSeconds: s.timeoutSeconds } : {}),
      ...(s.retries !== undefined ? { retries: s.retries } : {}),
      ...(settings !== undefined ? { config: settings as Record<string, z.core.util.JSONType> } : {}),
      ...(configNames.length ? { configEntries: configNames } : {}),
      ...(s.clusterAccess === true ? { clusterAccess: true as const } : {}),
      ...(Object.keys(externalInputs).length ? { externalInputs } : {}),
    };
    if (s.clusterAccess !== undefined && typeof s.clusterAccess !== "boolean") problems.push(`${at}: clusterAccess must be a boolean`);
    if (s.output && !isModel(s.output)) problems.push(`${at}: output must be a model`);
    return out;
  };

  const mapStep = (at: string, s: AnyStep, mapper: MapperFunction, avail: Map<string, Avail>): Record<string, Mapping> => {
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
    const declared = Object.fromEntries(Object.entries(s.input as Record<string, unknown>).filter(([, e]) => !isExternal(e)));
    for (const k of Object.keys(declared)) if (!(k in recorded)) problems.push(`${at}: mapper does not provide input '${k}'`);
    for (const k of Object.keys(recorded)) if (!(k in declared)) problems.push(`${at}: mapper provides '${k}', which is not an input of the step`);
    for (const [k, m] of Object.entries(recorded)) checkMapping(`${at} input '${k}'`, m, avail, isOptional(declared[k]), true);
    return recorded;
  };

  const checkMapping = (at: string, m: Mapping, avail: Map<string, Avail>, optionalTarget: boolean, topLevel: boolean) => {
    const keys = Object.keys(m);
    if (keys.length === 1 && keys[0] === "ref" && typeof (m as { ref: unknown }).ref === "string") {
      const [root, ...path] = (m as { ref: string }).ref.split(".") as [string, ...string[]];
      const a = avail.get(root);
      if (!a) return problems.push(`${at}: '${root}' is not available at this stage`);
      if (!resolveField(outputViewOf(a.model), path)) problems.push(`${at}: '${(m as { ref: string }).ref}' — model ${a.model} has no field '${path.join(".")}'`);
      if (a.maybe && !(optionalTarget && topLevel))
        problems.push(`${at}: '${(m as { ref: string }).ref}' is only maybe-available (produced inside a when block); map it whole into an optional(...) input`);
      return;
    }
    if (keys.length === 1 && keys[0] === "const") return;
    for (const [k, x] of Object.entries(m as Record<string, Mapping>)) checkMapping(`${at}.${k}`, x, avail, optionalTarget, false);
  };

  // ── stages ──
  let selects = 0;
  const walk = (nodes: readonly StageNode[], avail: Map<string, Avail>, cases: readonly CaseOf[] = []): Stage[] => {
    const out: Stage[] = [];
    for (const n of nodes) {
      if (n.kind === "select") {
        out.push(describeSelect(n, avail, cases, ++selects));
        continue;
      }
      if (n.kind === "stage") {
        const descs = n.steps.map((ss) => {
          if (!isStep(ss.step)) {
            problems.push("a stage entry is not a step");
            return undefined;
          }
          registerStep(ss.step, "stage", cases);
          return describeStep(ss, avail, false, cases);
        }).filter((d): d is StepDescriptorT => d !== undefined);
        // Outputs become available only after the barrier (same-stage siblings can't see each other).
        for (const ss of n.steps) if (isStep(ss.step)) avail.set(ss.step.name, { model: ss.step.output?.name ?? null, maybe: false });
        if (descs.length) out.push({ steps: descs });
      } else {
        const when: Record<string, (string | number | boolean)[]> = {};
        const props = (inputOutputView()?.properties ?? {}) as Record<string, JsonSchema>;
        for (const [field, raw] of Object.entries(n.cond)) {
          const values = (Array.isArray(raw) ? raw : [raw]) as (string | number | boolean)[];
          if (!Object.hasOwn(props, field)) problems.push(`when: '${field}' is not a field of the pipeline input ${inputModel.name}`);
          else {
            const literals = literalValues(props[field]);
            if (!literals) problems.push(`when: '${field}' is not a literal-like field (enum, boolean or literal)`);
            else for (const v of values) if (!literals.includes(v)) problems.push(`when: ${JSON.stringify(v)} is not a valid value of '${field}' (${literals.map((l) => JSON.stringify(l)).join(", ")})`);
          }
          if (values.length === 0) problems.push(`when: '${field}' has no values`);
          when[field] = values;
        }
        const inner = new Map(avail);
        const stages = walk(n.nodes, inner, cases);
        for (const [name, a] of inner) if (!avail.has(name)) avail.set(name, { model: a.model, maybe: true });
        out.push({ when, stages });
      }
    }
    return out;
  };
  const describeSelect = (n: Extract<StageNode, { kind: "select" }>, avail: Map<string, Avail>, cases: readonly CaseOf[], id: number): Stage => {
    if (!isConfigRef(n.on)) {
      problems.push("select: the first argument must be a config reference");
      return { select: "", discriminator: "", cases: {} };
    }
    const ref = configRefPath(n.on);
    const { entry, case: kase } = configRefTarget(n.on);
    useConfig(entry, `select '${ref}'`);
    if (kase !== undefined && !cases.some((c) => c.cond === kase))
      problems.push(`select '${ref}': the reference is narrowed to the case '${kase}' and can only be used inside it`);
    const union = unionAt(n.on);
    if (!union) problems.push(`select '${ref}': it must reference a z.discriminatedUnion in config '${entry.name}'`);
    else {
      for (const v of union.values) if (!Object.hasOwn(n.cases, v)) problems.push(`select '${ref}': no case for ${union.discriminator} '${v}'`);
      for (const v of Object.keys(n.cases)) if (!union.values.includes(v)) problems.push(`select '${ref}': '${v}' is not a value of ${union.discriminator} (${union.values.join(", ")})`);
    }
    const discriminator = union?.discriminator ?? "";
    const branches = Object.entries(n.cases).map(([value, nodes]) => {
      const inner = new Map(avail);
      const stages = walk(nodes, inner, [...cases, { select: id, value, cond: `${ref}.${discriminator}=${value}` }]);
      return { value, inner, stages };
    });
    // A step every case has is as available as in its cases; one only some cases have is maybe-available.
    const added = new Set(branches.flatMap((b) => [...b.inner.keys()].filter((k) => !avail.has(k))));
    for (const name of added) {
      const present = branches.map((b) => b.inner.get(name)).filter((a): a is Avail => a !== undefined);
      avail.set(name, { model: present[0]!.model, maybe: present.length < branches.length || present.some((a) => a.maybe) });
    }
    return { select: ref, discriminator, cases: Object.fromEntries(branches.map((b) => [b.value, b.stages])) };
  };
  const stages = walk(data.nodes, new Map([["input", { model: inputModel.name, maybe: false }]]));

  // ── failure handlers ──
  const stageStepNames = new Set(steps.keys());
  const seenHandlers = new Map<string, AnyStep>();
  const failureHandlers = data.failureHandlers.map((fh) => {
    if (!isStep(fh.step)) {
      problems.push("onFailure: handler is not a step");
      return undefined;
    }
    if (seenHandlers.get(fh.step.name) !== fh.step) registerStep(fh.step, "onFailure");
    seenHandlers.set(fh.step.name, fh.step);
    const scope = fh.steps === null ? null : fh.steps.map((x) => x.name);
    for (const n of scope ?? []) if (!stageStepNames.has(n)) problems.push(`onFailure(${fh.step.name}): '${n}' is not a step of this pipeline`);
    return { step: describeStep({ step: fh.step }, new Map(), true), steps: scope };
  }).filter((x) => x !== undefined);

  // ── handlers without a step ──
  for (const h of handlerNames) {
    const s = steps.get(h);
    if (!s) problems.push(`a handler implements step '${h}', which is not in the pipeline`);
    else if (isImplementation(s)) problems.push(`a handler implements '${h}', a ${s.kind} step that carries its own implementation`);
  }

  // ── triggers ──
  const triggerNames = new Set<string>();
  const triggers: TriggerT[] = data.triggers.map((t) => {
    if (triggerNames.has(t.name)) problems.push(`duplicate trigger '${t.name}'${t.kind === "push" ? " (onPush may be used once)" : " (give each onPoll and onWebhook a distinct name)"}`);
    triggerNames.add(t.name);
    if (t.kind === "push") return { kind: "push", name: "push" };
    if (t.kind === "webhook") {
      if (!STEP_NAME.test(t.name)) problems.push(`webhook trigger name '${t.name}' must be lower-case kebab`);
      const secret = useSecret(t.verify.secret, `webhook '${t.name}'`);
      if (t.verify.scheme === "bearer") return { kind: "webhook", name: t.name, verify: { scheme: "bearer", secret } };
      if (!t.verify.header) problems.push(`webhook '${t.name}': the signature header must not be empty`);
      return { kind: "webhook", name: t.name, verify: { scheme: "hmac-sha256", secret, header: t.verify.header, prefix: t.verify.prefix } };
    }
    if (!STEP_NAME.test(t.name)) problems.push(`poll trigger name '${t.name}' must be lower-case kebab`);
    if (!(Number.isInteger(t.intervalSeconds) && t.intervalSeconds > 0)) problems.push(`poll '${t.name}': intervalSeconds must be a positive integer`);
    const declared = new Set(t.secrets.map((s) => useSecret(s, `poll '${t.name}'`)));
    for (const [h, v] of Object.entries(t.headers)) {
      for (const m of v.matchAll(/\$SECRET:([A-Za-z0-9_]+)/g))
        if (!declared.has(m[1]!)) problems.push(`poll '${t.name}': header '${h}' references secret '${m[1]}', which is not in the poll's secrets`);
    }
    return { kind: "poll", name: t.name, url: t.url, headers: { ...t.headers }, secrets: [...declared].sort(), valuePath: t.valuePath, intervalSeconds: t.intervalSeconds };
  });

  // ── retention ──
  const retention: Record<string, number> = {};
  for (const key of ["keepLatestRuns", "minRunAgeDays", "buildGraceDays"] as const) {
    const v = data.retention?.[key];
    if (v === undefined) continue;
    if (!(Number.isInteger(v) && v >= 0)) problems.push(`retention.${key} must be a non-negative integer`);
    else retention[key] = v;
  }

  if (problems.length) throw new DescribeError(problems);

  const manifest = canonicalize({
    apiVersion: API_VERSION,
    name: data.name,
    description: data.description,
    input: inputModel.name,
    ...(Object.keys(retention).length ? { retention } : {}),
    harness: opts.harness,
    models: Object.fromEntries([...models].map(([k, v]) => [k, v.schemas])),
    secrets: [...secrets].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([name, description]) => ({ name, description })),
    ...(configs.size
      ? {
          config: [...configs]
            .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
            .map(([name, c]) => ({ name, description: c.description, required: c.required, schema: c.schema })),
        }
      : {}),
    triggers,
    stages,
    failureHandlers,
  });
  const checked = Manifest.safeParse(manifest);
  if (!checked.success) throw new DescribeError([`internal: manifest does not match its schema: ${z.prettifyError(checked.error)}`]);
  return manifest as ManifestT;
}

/** Canonical manifest text (what `describe` prints and the service hashes). */
export const manifestText = (m: ManifestT): string => canonicalJson(m);

/**
 * The schemas at `path` inside an entry's schema: every union branch that has the field, except where
 * `kase` (`<entry>.<path>.<discriminator>=<value>`) narrows a union to one branch.
 */
export function subSchemas(schema: unknown, path: readonly string[], kase: string | undefined, entry: string): unknown[] {
  let narrow: { at: string; discriminator: string; value: string } | undefined;
  if (kase) {
    const eq = kase.lastIndexOf("=");
    const left = kase.slice(0, eq);
    const dot = left.lastIndexOf(".");
    narrow = { at: left.slice(0, dot), discriminator: left.slice(dot + 1), value: kase.slice(eq + 1) };
  }
  const walk = (node: unknown, rest: readonly string[], here: string): unknown[] => {
    if (node === null || typeof node !== "object") return [];
    const o = node as JsonSchema;
    const branches = (o.oneOf ?? o.anyOf) as JsonSchema[] | undefined;
    if (Array.isArray(branches)) {
      const chosen = narrow && narrow.at === here
        ? branches.filter((b) => ((b.properties as Record<string, JsonSchema> | undefined)?.[narrow!.discriminator] as JsonSchema | undefined)?.const === narrow!.value)
        : branches;
      return chosen.flatMap((b) => walk(b, rest, here));
    }
    if (rest.length === 0) return [node];
    const props = o.properties as Record<string, JsonSchema> | undefined;
    if (props && Object.hasOwn(props, rest[0]!)) return walk(props[rest[0]!], rest.slice(1), `${here}.${rest[0]}`);
    return [];
  };
  return walk(schema, path, entry);
}

/** Secret names of every secretRef inside a config entry's JSON Schema. */
export function secretRefsOf(schema: unknown): string[] {
  const out = new Set<string>();
  const walk = (v: unknown) => {
    if (Array.isArray(v)) return v.forEach(walk);
    if (v === null || typeof v !== "object") return;
    const o = v as JsonSchema;
    if (o["x-olve-kind"] === SECRET_REF_KIND) {
      const name = (o.properties as { secret?: { const?: unknown } } | undefined)?.secret?.const;
      if (typeof name === "string") out.add(name);
    }
    Object.values(o).forEach(walk);
  };
  walk(schema);
  return [...out].sort();
}

/** The `{"$config": "<entry>.<path>"[, "$case": …]}` references in a step's recorded settings. */
export function recordedConfigRefs(v: unknown, out: { ref: string; case?: string }[] = []): { ref: string; case?: string }[] {
  if (Array.isArray(v)) v.forEach((x) => recordedConfigRefs(x, out));
  else if (v !== null && typeof v === "object") {
    const o = v as Record<string, unknown>;
    const keys = Object.keys(o).sort();
    const isRef = typeof o[CONFIG_REF_KEY] === "string"
      && (keys.length === 1 ? keys[0] === CONFIG_REF_KEY : keys.length === 2 && keys[0] === CONFIG_CASE_KEY && keys[1] === CONFIG_REF_KEY && typeof o[CONFIG_CASE_KEY] === "string");
    if (isRef) out.push({ ref: o[CONFIG_REF_KEY] as string, ...(keys.length === 2 ? { case: o[CONFIG_CASE_KEY] as string } : {}) });
    else Object.values(o).forEach((x) => recordedConfigRefs(x, out));
  }
  return out;
}

/** A select case a step sits in: which select (numbered in describe order), which value, and its condition text. */
interface CaseOf {
  select: number;
  value: string;
  cond: string;
}

function camel(name: string): string {
  return name.replace(/-([a-z0-9])/g, (_, c: string) => c.toUpperCase());
}
