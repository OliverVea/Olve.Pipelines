import { base, deployBeta } from "../fixtures";

base()
  // @ts-expect-error -- expect: Object literal may only specify known properties, and 'environment' does not exist
  .when({ environment: "prod" }, p => p);
