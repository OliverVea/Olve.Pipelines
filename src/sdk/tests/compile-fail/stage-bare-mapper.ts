import { base, checkout, build, deployBeta } from "../fixtures";

// Mappers are always given as [step, mapper] tuples; the old `.stage(step, mapper)` form is rejected.
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build)
  // @ts-expect-error -- expect: is not assignable to parameter of type 'AnyStep | readonly [AnyStep
  .stage(deployBeta, (from: any) => ({ artifact: from.build }));
