import { base, checkout, test } from "../fixtures";

// For [step, mapper] entries the name error is reported on the mapper's return value (TS reports only the first failing argument of a call).
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(
    // @ts-expect-error -- expect: duplicate step name 'test' (used twice in this stage)
    [test, i => ({ source: i.checkout })],
    [test, i => ({ source: i.checkout })],
  );
