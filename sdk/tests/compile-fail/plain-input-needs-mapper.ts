import { base, checkout } from "../fixtures";

base()
  // @ts-expect-error -- expect: input 'repo' of step 'checkout' is a plain schema, not a model
  .stage(checkout);
