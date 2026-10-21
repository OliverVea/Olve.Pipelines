import { implement } from "@olve/pipelines";
import { last } from "../pipeline";

export default implement(last, ({ greeting, count }) => ({
  summary: `${greeting.message}: ${count.lines} lines`,
}));
