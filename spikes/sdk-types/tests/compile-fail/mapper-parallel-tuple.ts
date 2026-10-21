import { base, checkout, build, test } from "../fixtures";

// The error lands on the offending mapper of a parallel [step, mapper] stage.
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(
    [build, i => ({ source: i.checkout })],
    // @ts-expect-error -- expect: is not assignable to type 'Mappable<
    [test, i => ({ source: i.input })],
  );
