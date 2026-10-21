import { base, summary } from "../fixtures";

// optional() means "maybe skipped", not "may never exist": no producer at all is still an error.
base()
  // @ts-expect-error -- expect: is not available at this stage
  .stage(summary);
