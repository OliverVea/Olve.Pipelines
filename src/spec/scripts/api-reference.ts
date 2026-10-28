// The API reference page: api-reference.header.md (hand-written: auth, errors, live events, the
// routes outside the contract) followed by every operation and schema of the compiled contract.
//
//   bun scripts/api-reference.ts    # artifacts/spec/openapi.json → artifacts/spec/api-reference.md
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

type Schema = Record<string, any>;
type Operation = { tag: string; method: string; path: string; op: Schema };

const spec = join(import.meta.dir, "..");
const artifacts = join(spec, "../../artifacts/spec");
const doc = JSON.parse(readFileSync(join(artifacts, "openapi.json"), "utf8"));
const schemas: Record<string, Schema> = doc.components?.schemas ?? {};
const tagOrder = ["Pipelines", "Bindings", "Versions", "Config", "Secrets", "Runs", "Steps", "Attempts", "Events", "Instance"];

const ops: Operation[] = [];
for (const [path, item] of Object.entries<Schema>(doc.paths ?? {}))
  for (const [method, op] of Object.entries<Schema>(item))
    if (op && typeof op === "object" && "responses" in op)
      ops.push({ tag: op.tags?.[0] ?? "Other", method: method.toUpperCase(), path, op });
const tags = [...tagOrder, ...[...new Set(ops.map((o) => o.tag))].filter((t) => !tagOrder.includes(t)).sort()].filter((t) =>
  ops.some((o) => o.tag === t),
);

let md = readFileSync(join(spec, "api-reference.header.md"), "utf8").trimEnd() + "\n\n## Endpoints\n\n";
for (const tag of tags) {
  md += `### ${tag}\n\n`;
  for (const { method, path, op } of ops.filter((o) => o.tag === tag)) {
    md += `#### ${str(op.summary) ?? str(op.operationId) ?? `${method} ${path}`}\n\n\`${method} ${path}\`\n\n`;
    if (str(op.description)) md += `${op.description.trim()}\n\n`;
    const parameters: Schema[] = op.parameters ?? [];
    if (parameters.length) {
      md += "| Parameter | In | Type | Required |\n|---|---|---|---|\n";
      for (const p of parameters) md += `| \`${p.name}\` | ${p.in} | ${type(p.schema)} | ${p.required ? "yes" : ""} |\n`;
      md += "\n";
    }
    const body: Record<string, Schema> | undefined = op.requestBody?.content;
    if (body) {
      const [mediaType, content] = Object.entries(body)[0]!;
      md += `**Body** (\`${mediaType}\`): ${type(content.schema)}\n\n`;
    }
    const successes = Object.entries<Schema>(op.responses ?? {}).filter(([code]) => code.startsWith("2"));
    if (successes.length) {
      md += "**Returns:**";
      for (const [code, response] of successes) {
        const [mediaType, content] = Object.entries<Schema>(response.content ?? {})[0] ?? [];
        md += ` \`${code}\`${content?.schema ? ` ${type(content.schema)}` : ""}${mediaType && mediaType !== "application/json" ? ` (\`${mediaType}\`)` : ""}`;
      }
      md += "\n\n";
    }
  }
}

md += "## Schemas\n\n";
for (const [name, schema] of Object.entries(schemas).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
  if (!documented(schema)) continue;
  md += `### ${name}\n\n`;
  if (str(schema.description)) md += `${schema.description.trim()}\n\n`;
  const fields = properties(schema);
  if (fields.length) {
    md += "| Field | Type | |\n|---|---|---|\n";
    for (const [field, fieldType, required] of fields) md += `| \`${field}\` | ${fieldType} | ${required ? "required" : ""} |\n`;
    md += "\n";
  } else md += `${type(schema)}\n\n`;
}

mkdirSync(artifacts, { recursive: true });
writeFileSync(join(artifacts, "api-reference.md"), md.trimEnd() + "\n");
console.log("wrote artifacts/spec/api-reference.md");

function str(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** Objects and unions of them get their own section; primitives, enums and ids are written inline. */
function documented(schema: Schema): boolean {
  return !!(schema.properties || schema.allOf || schema.oneOf || schema.anyOf);
}

function properties(schema: Schema): [string, string, boolean][] {
  const out: [string, string, boolean][] = [];
  for (const part of schema.allOf ?? []) if (!part.$ref) out.push(...properties(part));
  const required = new Set<string>(schema.required ?? []);
  for (const [field, prop] of Object.entries<Schema>(schema.properties ?? {})) out.push([field, type(prop), required.has(field)]);
  return out;
}

function type(s: Schema | undefined): string {
  if (!s) return "any";
  const nullable = s.nullable === true ? " \\| null" : "";
  if (typeof s.$ref === "string") {
    const name = s.$ref.slice(s.$ref.lastIndexOf("/") + 1);
    const target = schemas[name];
    return (target && !documented(target) ? type(target) : `[${name}](#${name.toLowerCase()})`) + nullable;
  }
  if (Array.isArray(s.enum)) return s.enum.map((v: unknown) => `\`${JSON.stringify(v)}\``).join(" \\| ") + nullable;
  const union: Schema[] | undefined = s.oneOf ?? s.anyOf;
  if (union) return [...new Set(union.map(type))].join(" \\| ") + nullable;
  if (Array.isArray(s.allOf)) {
    const refs = s.allOf.filter((p: Schema) => p.$ref).map(type);
    return (refs.length && !s.properties && refs.length === s.allOf.length ? refs.join(" & ") : "object") + nullable;
  }
  switch (s.type) {
    case "array":
      return `${type(s.items)}[]` + nullable;
    case "object":
      return (s.additionalProperties && typeof s.additionalProperties === "object" ? `Record<${type(s.additionalProperties)}>` : "object") + nullable;
    case "integer":
    case "number":
      return (s.format ?? s.type) + nullable;
    case "string":
      return (s.format === "uuid" || s.format === "date-time" ? s.format : "string") + nullable;
    case undefined:
      return "any";
    default:
      return s.type + nullable;
  }
}
