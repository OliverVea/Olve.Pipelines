import { base, checkout, test } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: duplicate step name 'test' (used twice in this stage)
  .stage(test, test);
