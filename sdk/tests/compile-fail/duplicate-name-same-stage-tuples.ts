import { base, checkout, test } from "../fixtures";

// For [step, mapper] entries the name error is reported on the mapper's return value (TS reports only the first failing argument of a call).
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(
    // @ts-expect-error -- expect: duplicate step name 'test' (used twice in this stage)
    [test, from => ({ source: from.checkout })],
    [test, from => ({ source: from.checkout })],
  );
