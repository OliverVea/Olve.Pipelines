import { z } from "zod";
import type { ModelBrand } from "./brands.ts";

/** A named, nominal zod schema. The name is its identity in the manifest (one schema per name). */
export interface Model<N extends string = string, S extends z.ZodType = z.ZodType> {
  readonly kind: "model";
  readonly name: N;
  readonly schema: S;
}
export type AnyModel = Model<string, z.ZodType>;

/** Output view of a model (`z.output`), nominally branded with the model name. The brand is optional, so plain literals still assign. */
export type Infer<M extends AnyModel> = z.output<M["schema"]> & { readonly [ModelBrand]?: M["name"] };
/** Input view of a model (`z.input`: fields with defaults are optional). What trigger mappers produce. */
export type InferInput<M extends AnyModel> = z.input<M["schema"]>;
/** Output view without the nominal brand. */
export type Plain<M extends AnyModel> = z.output<M["schema"]>;

/** Model names: identifier-like, so they are safe as JSON keys, C# names and URL segments. */
export const MODEL_NAME = /^[A-Za-z][A-Za-z0-9_]*$/;

export function model<const N extends string, S extends z.ZodType>(name: N, schema: S): Model<N, S> {
  return Object.freeze({ kind: "model" as const, name, schema });
}

export function isModel(v: unknown): v is AnyModel {
  return typeof v === "object" && v !== null && (v as { kind?: unknown }).kind === "model" && (v as { schema?: unknown }).schema instanceof z.ZodType;
}

// ─── File references ─────────────────────────────────────────────────────────────────────────

/**
 * One path segment: no `/`, `\` or NUL, and not `.` / `..`. A path is segments joined by `/`;
 * a trailing `/` marks a directory reference (stored as a tar archive, see README).
 */
const SEGMENT = String.raw`(?!\.\.?(?:/|$))[^/\\\x00]+`;
export const FILE_PATH_PATTERN = `^(?:${SEGMENT}/)*${SEGMENT}/?$`;
export const FILE_PATH = new RegExp(FILE_PATH_PATTERN);

const FileRefSchema = z
  .object({
    run: z.number().int().nonnegative(),
    step: z.string().min(1),
    attempt: z.number().int().positive(),
    path: z.string().regex(FILE_PATH),
    sha256: z.string().regex(/^[0-9a-f]{64}$/),
  })
  .brand<"FileRef">()
  .meta({ "x-olve-kind": "fileRef" });

/** A file or directory in some step attempt's output (`runs/<p>/<run>/<step>/<attempt>/files/<path>`). Branded: only `ctx.output.*` or a mapper ref produce one. */
export type FileRef = z.output<typeof FileRefSchema>;
/** Schema for a file reference field inside a model. Marked `"x-olve-kind": "fileRef"` in JSON Schema. */
export const fileRef = () => FileRefSchema;
export const fileRefSchema = FileRefSchema;

// ─── optional() ──────────────────────────────────────────────────────────────────────────────

/** A step input that may be missing because its producer was *maybe skipped* (inside a `when`). There must still be a producer. */
export interface Optional<M extends AnyModel = AnyModel> {
  readonly kind: "optional";
  readonly model: M;
}
export function optional<M extends AnyModel>(model: M): Optional<M> {
  return Object.freeze({ kind: "optional" as const, model });
}
export function isOptional(v: unknown): v is Optional {
  return typeof v === "object" && v !== null && (v as { kind?: unknown }).kind === "optional" && isModel((v as { model?: unknown }).model);
}
