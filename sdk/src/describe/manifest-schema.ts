// The manifest format, defined once in zod. `manifest.schema.json` is generated from this
// (bun scripts/gen-manifest-schema.ts) and describe validates every manifest against it.
import { z } from "zod";

export const API_VERSION = "2.0";

const stepName = z.string().regex(/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/).describe("Step name: lower-case kebab, 1-63 chars. The step's identity.");
const modelName = z.string().regex(/^[A-Za-z][A-Za-z0-9_]*$/).describe("Model name (key of `models`).");
const secretName = z.string().regex(/^[A-Z_][A-Z0-9_]*$/).describe("Secret name; also the env var name in the step container.");
const configName = z.string().regex(/^[a-z][a-zA-Z0-9]*$/).describe("Config entry name; a key of the pipeline's config document.");
const hash = z.string().regex(/^sha256:[0-9a-f]{64}$/);
const Json = z.json().meta({ id: "Json", description: "Any JSON value." });
const jsonSchema = z.record(z.string(), z.unknown()).describe("A JSON Schema (draft 2020-12) object, canonical (sorted keys, no $schema).");

export const RefMapping = z
  .strictObject({
    ref: z
      .string()
      .regex(/^(?:input|failure|[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.[^.]+)*$/)
      .describe("'<root>' or '<root>.<field>.<field>…'. Root: 'input' (pipeline input), a step name (that step's output) or 'failure' (FailureContext, failure handlers only)."),
  })
  .describe("Value taken from the run pool. A ref whose root step was skipped yields no value (only allowed for optional inputs).");
export const ConstMapping = z.strictObject({ const: Json }).describe("A JSON constant.");

export type Mapping = { ref: string } | { const: unknown } | { [field: string]: Mapping };
export const Mapping: z.ZodType<Mapping> = z
  .union([
    RefMapping,
    ConstMapping,
    z.record(z.string(), z.lazy(() => Mapping)).describe(
      "An object built field by field. Discrimination: an object whose only key is 'ref' (string value) is a ref, whose only key is 'const' is a constant, anything else is a nested object (describe never emits a nested object whose only key is 'ref' or 'const').",
    ),
  ])
  .meta({ id: "Mapping" });

export const ModelSchemas = z
  .strictObject({
    output: jsonSchema.describe("z.output view: validates step outputs, trigger results and the pool. Every object has additionalProperties: false."),
    input: jsonSchema.optional().describe("z.input view (defaults make fields optional; `default` values are listed). Present only when it differs from `output`."),
  })
  .meta({ id: "ModelSchemas" });

export const InputType = z
  .union([
    z.strictObject({ model: modelName, optional: z.boolean().describe("optional(Model): may be absent because its producer was skipped.") }),
    z.strictObject({ schema: ModelSchemas.describe("A plain (unnamed) zod schema. Such inputs always have an explicit mapping.") }),
  ])
  .meta({ id: "InputType" });

export const StepDescriptor = z
  .strictObject({
    name: stepName,
    description: z.string().describe("What the step does, shown in the UI (may be empty)."),
    kind: z.string().min(1).describe("'custom' = handlers/<name>.ts; others are SDK library steps. A label: the service accepts any kind."),
    image: z.string().min(1).describe("Container image the step runs in."),
    inputTypes: z.record(z.string(), InputType).describe("Declared input entries, keyed like input.json."),
    input: z.record(z.string(), Mapping).describe("How the service assembles input.json: one mapping per input key (same keys as inputTypes)."),
    output: modelName.nullable().describe("Output model, or null if the step produces no output."),
    secrets: z.array(secretName).describe("Declared secrets (sorted). Mount exactly these, as env vars of the same name."),
    handlerHash: hash.describe("Implementation identity: handler bundle (contract files excluded) + harness (bun + sdkHash); for library steps kind + config + harness."),
    timeoutSeconds: z.number().int().positive().optional(),
    retries: z.number().int().nonnegative().optional(),
    config: z
      .record(z.string(), Json)
      .optional()
      .describe("Library-step configuration (display only; already covered by handlerHash). A config reference is recorded as {\"$config\": \"<entry>.<field>…\"}; the entry is in configEntries."),
    configEntries: z
      .array(configName)
      .optional()
      .describe("Config entries the step receives (sorted; omitted when none). The service delivers them in input.json under '$config'."),
    clusterAccess: z
      .literal(true)
      .optional()
      .describe("The step pod gets the instance's step ServiceAccount token (Kubernetes:StepServiceAccount), if the instance grants one. Omitted = no token."),
  })
  .meta({ id: "Step" });

export type Stage =
  | { steps: z.infer<typeof StepDescriptor>[] }
  | { when: Record<string, (string | number | boolean)[]>; stages: Stage[] }
  | { select: string; discriminator: string; cases: Record<string, Stage[]> };
export const Stage: z.ZodType<Stage> = z
  .union([
    z.strictObject({ steps: z.array(StepDescriptor).min(1).describe("Steps run in parallel; the stage is a barrier.") }),
    z.strictObject({
      when: z.record(z.string(), z.array(z.union([z.string(), z.number(), z.boolean()])).min(1)).describe(
        "Pipeline-input field → accepted values (all fields must match). Empty object = always.",
      ),
      stages: z.array(z.lazy(() => Stage)),
    }),
    z.strictObject({
      select: z.string().min(1).describe("Config reference (`<entry>.<field>…`) to a discriminated union; the run's config snapshot picks the case."),
      discriminator: z.string().min(1).describe("The union's literal field whose value names the case."),
      cases: z
        .record(z.string(), z.array(z.lazy(() => Stage)))
        .describe("Discriminator value → stages. Exactly one case runs; a step name may repeat across cases, nowhere else."),
    }),
  ])
  .meta({ id: "Stage" });

export const Trigger = z
  .discriminatedUnion("kind", [
    z.strictObject({ kind: z.literal("push"), name: z.literal("push") }).describe("GitHub push. Event = the webhook payload."),
    z
      .strictObject({
        kind: z.literal("poll"),
        name: z.string().regex(/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/),
        url: z.string(),
        headers: z.record(z.string(), z.string()).describe("Values may contain '$SECRET:NAME' (NAME ∈ secrets)."),
        secrets: z.array(secretName),
        valuePath: z.string().describe("Dot path into the JSON response; a change triggers map-trigger with {\"value\": <string>}."),
        intervalSeconds: z.number().int().positive(),
      })
      .describe("Background poller."),
    z
      .strictObject({
        kind: z.literal("webhook"),
        name: z.string().regex(/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/),
        verify: z.discriminatedUnion("scheme", [
          z.strictObject({
            scheme: z.literal("hmac-sha256"),
            secret: secretName,
            header: z.string().min(1),
            prefix: z.string().describe("Text before the hex digest in the header, e.g. 'sha256='."),
          }),
          z.strictObject({ scheme: z.literal("bearer"), secret: secretName }),
        ]),
      })
      .describe("Inbound HTTP delivery to /api/hooks/<pipeline id>/<name>. Event = {headers, body}; a null mapping ignores it."),
  ])
  .meta({ id: "Trigger" });

export const Manifest = z
  .strictObject({
    apiVersion: z.literal(API_VERSION),
    name: z.string().min(1),
    description: z.string().optional(),
    input: modelName.describe("Pipeline input model."),
    harness: z
      .strictObject({
        bun: z.string().describe("Bun runtime version compiled into the binary."),
        sdk: z.string().describe("@olivervea/pipelines package version."),
        sdkHash: hash.describe("Hash of the SDK runtime sources + zod version."),
      })
      .describe("Harness identity. Already folded into every handlerHash."),
    models: z.record(modelName, ModelSchemas).describe("Every model referenced by the pipeline (input, step inputs/outputs)."),
    secrets: z.array(z.strictObject({ name: secretName, description: z.string() })).describe("All declared secrets (steps + poll triggers), sorted by name."),
    config: z
      .array(
        z.strictObject({
          name: configName,
          description: z.string(),
          required: z.boolean().describe("False when the schema accepts a missing value (optional, or a default)."),
          schema: jsonSchema.describe("z.input view: validates the config document's value for this entry (defaults listed)."),
        }),
      )
      .optional()
      .describe("Config entries used by steps, sorted by name (omitted when none). Fields of kind secretRef ({secret: NAME}) name a secret."),
    triggers: z.array(Trigger),
    stages: z.array(Stage),
    failureHandlers: z.array(
      z.strictObject({
        step: StepDescriptor.describe("The handler step. Not part of any stage; its inputs are all FailureContext ({ref: 'failure'})."),
        steps: z.array(stepName).nullable().describe("Steps whose failure triggers the handler (null = any step)."),
      }),
    ),
  })
  .meta({ id: "Manifest", title: "Olve.Pipelines v2 manifest" });

export type ManifestT = z.infer<typeof Manifest>;
export type StepDescriptorT = z.infer<typeof StepDescriptor>;
export type TriggerT = z.infer<typeof Trigger>;
export type ModelSchemasT = z.infer<typeof ModelSchemas>;

/** The JSON Schema document written to manifest.schema.json. */
export function manifestJsonSchema(): Record<string, unknown> {
  const js = z.toJSONSchema(Manifest, { target: "draft-2020-12" }) as Record<string, unknown>;
  return { ...js, $id: "https://olve.dev/schemas/pipelines/manifest-2.0.json" };
}
