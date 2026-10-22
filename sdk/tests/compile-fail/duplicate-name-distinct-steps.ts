import { base, steps, Source, checkout } from "../fixtures2";

// Two *different* step definitions sharing a name (identity collision).
const testA = steps.script({ name: "test", description: "", image: "a", script: "a.sh", input: { source: Source } });
const testB = steps.script({ name: "test", description: "", image: "b", script: "b.sh", input: { source: Source } });
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(testA)
  // @ts-expect-error -- expect: duplicate step name 'test'
  .stage(testB);
