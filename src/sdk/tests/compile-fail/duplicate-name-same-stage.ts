import { base, checkout, test } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: duplicate step name 'test' (used twice in this stage)
  .stage(test, test);
