// Models of the v2 test fixture pipeline (contract file: contracts only).
import { fileRef, model, z } from "@olivervea/pipelines";

export const TestInput = model("TestInput", z.object({
  sha: z.string().regex(/^[0-9a-f]{7,40}$/),
  /**
   * "skip-last" leaves out the conditional last stage (exercises `when` skips); "fail" makes `echo`
   * throw, failing the run (exercises onFailure).
   */
  mode: z.enum(["normal", "skip-last", "fail"]).default("normal"),
}));

export const Greeting = model("Greeting", z.object({
  message: z.string(),
  file: fileRef(),
}));

export const LineCount = model("LineCount", z.object({
  lines: z.number().int(),
}));

export const Summary = model("Summary", z.object({
  summary: z.string(),
}));
