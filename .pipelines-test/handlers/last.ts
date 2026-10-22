import { implement } from "@olivervea/pipelines";
import { last } from "../pipeline";

export default implement(last, ({ greeting, count }) => ({
  summary: `${greeting.message}: ${count.lines} lines`,
}));
