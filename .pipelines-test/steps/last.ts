import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";

export const last = implement(step({
  name: "last",
  description: "Summarises the greeting and its line count.",
  image: "alpine:3.22",
  input: { greeting: M.Greeting, count: M.LineCount },
  output: M.Summary,
  timeoutSeconds: 300,
}), ({ greeting, count }) => ({
  summary: `${greeting.message}: ${count.lines} lines`,
}));
