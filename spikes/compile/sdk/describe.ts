// `describe` mode: evaluates models + pipeline (never handlers) into a manifest.
import { z } from "zod";
import { type Model, type Node, type PipelineBuilder, type Step, type WaitStep, modelOf } from "./core";
import { type MapExpr, MapperError, recordMapper } from "./record";

export interface DescribeExtras {
  handlerHashes: Record<string, string>; // step name -> sha256:…
  handlerSteps: string[]; // steps with a registered handler
}

export class DescribeError extends Error {
  constructor(readonly problems: string[]) {
    super(`describe failed:\n  - ${problems.join("\n  - ")}`);
  }
}

type Avail = { model: Model; maybe: boolean };

export function describe(p: PipelineBuilder, extras: DescribeExtras) {
  const problems: string[] = [];
  const models = new Map<string, Model>();
  const jsonSchemas = new Map<string, any>();
  const secrets = new Map<string, string>();
  const seenSteps = new Set<string>();

  const useModel = (m: Model) => {
    const prev = models.get(m.name);
    if (prev && prev !== m) problems.push(`two different models are named '${m.name}'`);
    if (!prev) {
      models.set(m.name, m);
      // io:"input" so defaults appear as optional fields the trigger form may omit.
      const js = z.toJSONSchema(m.schema, { io: "input", unrepresentable: "throw" }) as any;
      delete js.$schema; // constant noise; the manifest's apiVersion pins the dialect
      jsonSchemas.set(m.name, js);
    }
    return m.name;
  };

  const inputModel = p.def.input;
  useModel(inputModel);

  // ---- path validation against JSON Schema ----
  const resolvePath = (path: string, avail: Map<string, Avail>, at: string): { maybe: boolean } | undefined => {
    const [root, ...rest] = path.split(".");
    const a = avail.get(root!);
    if (!a) {
      problems.push(`${at}: '${root}' is not available at this stage`);
      return;
    }
    let schema = jsonSchemas.get(a.model.name);
    for (const seg of rest) {
      schema = schema?.properties?.[seg];
      if (!schema) {
        problems.push(`${at}: '${path}' — model ${a.model.name} has no field '${rest.join(".")}'`);
        return;
      }
    }
    return { maybe: a.maybe };
  };
  const checkExpr = (e: MapExpr, avail: Map<string, Avail>, at: string, optionalTarget: boolean) => {
    if ("path" in e) {
      const r = resolvePath(e.path, avail, at);
      if (r?.maybe && !optionalTarget)
        problems.push(`${at}: '${e.path}' is only maybe-available (produced inside a when); declare the input optional(...)`);
    } else if ("object" in e) for (const [k, x] of Object.entries(e.object)) checkExpr(x, avail, `${at}.${k}`, optionalTarget);
  };

  // ---- walk ----
  const describeStep = (s: Step, mapper: ((r: any) => unknown) | undefined, avail: Map<string, Avail>) => {
    if (seenSteps.has(s.name)) problems.push(`duplicate step name '${s.name}'`);
    seenSteps.add(s.name);
    if (s.kind === "step" && !extras.handlerSteps.includes(s.name))
      problems.push(`step '${s.name}' has no handler (expected handlers/${s.name}.ts)`);

    let input: Record<string, MapExpr> = {};
    if (mapper) {
      try {
        input = recordMapper(mapper, new Set(avail.keys()));
      } catch (e) {
        if (e instanceof MapperError) problems.push(`step '${s.name}': ${e.message}`);
        else throw e;
      }
      for (const k of Object.keys(s.input))
        if (!(k in input)) problems.push(`step '${s.name}': mapper does not provide input '${k}'`);
      for (const k of Object.keys(input))
        if (!(k in s.input)) problems.push(`step '${s.name}': mapper provides unknown input '${k}'`);
    } else {
      // Match by model among everything available (pipeline input + earlier outputs).
      for (const [k, spec] of Object.entries(s.input)) {
        const m = modelOf(spec);
        const producers = [...avail.entries()].filter(([, a]) => a.model.name === m.name).map(([n]) => n);
        if (producers.length === 1) input[k] = { path: producers[0]! };
        else if (producers.length === 0) {
          if (spec.kind !== "optional") problems.push(`step '${s.name}': no producer of ${m.name} for input '${k}'`);
        } else problems.push(`step '${s.name}': input '${k}' is ambiguous (${m.name} produced by ${producers.join(", ")}) — add a mapper`);
      }
    }
    for (const [k, e] of Object.entries(input)) {
      const spec = s.input[k];
      checkExpr(e, avail, `step '${s.name}' input '${k}'`, spec?.kind === "optional");
    }

    for (const sec of s.secrets ?? []) secrets.set(sec.name, sec.description);
    const out: Record<string, unknown> = {
      name: s.name,
      kind: s.kind,
      image: s.image,
      inputModels: Object.fromEntries(
        Object.entries(s.input).map(([k, spec]) => [k, spec.kind === "optional" ? { model: useModel(spec.model), optional: true } : { model: useModel(spec) }]),
      ),
      input,
      output: useModel(s.output),
      secrets: (s.secrets ?? []).map((x) => x.name),
    };
    if (s.timeoutSeconds !== undefined) out.timeoutSeconds = s.timeoutSeconds;
    if (s.kind === "wait") {
      const w = s as WaitStep;
      out.resolveBy = {
        manual: !!w.resolveBy.manual,
        callback: !!w.resolveBy.callback,
        ...(w.resolveBy.check ? { check: { every: w.resolveBy.check.every } } : {}),
      };
      if (w.timeoutHours !== undefined) out.timeoutHours = w.timeoutHours;
    } else out.handlerHash = extras.handlerHashes[s.name] ?? null;
    return out;
  };

  const inputJson = jsonSchemas.get(inputModel.name);
  const walk = (nodes: Node[], avail: Map<string, Avail>): unknown[] => {
    const out: unknown[] = [];
    for (const n of nodes) {
      if (n.type === "stage") {
        const stepsOut = n.steps.map((ss) => describeStep(ss.step, ss.mapper, avail));
        // Outputs become available only after the barrier (same-stage siblings can't see each other).
        for (const ss of n.steps) avail.set(ss.step.name, { model: ss.step.output, maybe: false });
        out.push({ steps: stepsOut });
      } else {
        const cond: Record<string, unknown[]> = {};
        for (const [k, v] of Object.entries(n.cond)) {
          const vals = Array.isArray(v) ? v : [v];
          const field = inputJson?.properties?.[k];
          if (!field) problems.push(`when: unknown pipeline-input field '${k}'`);
          else {
            const literals: unknown[] | undefined = field.enum ?? (field.const !== undefined ? [field.const] : field.type === "boolean" ? [true, false] : undefined);
            if (!literals) problems.push(`when: field '${k}' is not literal-like (enum/boolean/literal)`);
            else for (const x of vals) if (!literals.includes(x)) problems.push(`when: '${String(x)}' is not a valid value of '${k}'`);
          }
          cond[k] = vals;
        }
        const inner = new Map(avail);
        const stages = walk(n.body, inner);
        // After the block, its outputs are maybe-available.
        for (const [name, a] of inner) if (!avail.has(name)) avail.set(name, { model: a.model, maybe: true });
        out.push({ when: cond, stages });
      }
    }
    return out;
  };

  const avail = new Map<string, Avail>([["input", { model: inputModel, maybe: false }]]);
  const stages = walk(p.nodes, avail);

  if (problems.length) throw new DescribeError(problems);

  return {
    apiVersion: "2.0-spike",
    name: p.def.name,
    ...(p.def.description ? { description: p.def.description } : {}),
    input: inputModel.name,
    models: Object.fromEntries(jsonSchemas),
    secrets: [...secrets].map(([name, description]) => ({ name, description })),
    // Trigger mappers are code: they run via `map-trigger` (see REPORT: proxies cannot record logic).
    triggers: p.triggers.map((t) => ({ kind: t.kind, mapping: "code", ...(t.config ?? {}) })),
    stages,
  };
}
