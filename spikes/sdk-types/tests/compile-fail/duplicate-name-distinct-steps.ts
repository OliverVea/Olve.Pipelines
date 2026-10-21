import { base, steps, Source, checkout } from "../fixtures2";

// Two *different* step definitions sharing a name (identity collision).
const testA = steps.script({ name: "test", image: "a", script: "a.sh", input: { source: Source } });
const testB = steps.script({ name: "test", image: "b", script: "b.sh", input: { source: Source } });
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(testA)
  // @ts-expect-error -- expect: duplicate step name 'test'
  .stage(testB);
