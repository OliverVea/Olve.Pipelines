import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: Type '"prd"' is not assignable to type
  .when({ deployTo: "prd" }, p => p);
