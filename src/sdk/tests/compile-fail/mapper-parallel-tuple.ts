import { base, checkout, build, test } from "../fixtures";

// The error lands on the offending mapper of a parallel [step, mapper] stage.
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(
    [build, from => ({ source: from.checkout })],
    // @ts-expect-error -- expect: is not assignable to type 'Mappable<
    [test, from => ({ source: from.input })],
  );
