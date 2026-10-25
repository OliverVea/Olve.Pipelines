import { base, step, Source, checkout } from "../fixtures2";

// Two *different* step definitions sharing a name (identity collision).
const testA = step({ name: "test", description: "", image: "a", input: { source: Source } });
const testB = step({ name: "test", description: "", image: "b", input: { source: Source } });
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(testA)
  // @ts-expect-error -- expect: duplicate step name 'test'
  .stage(testB);
