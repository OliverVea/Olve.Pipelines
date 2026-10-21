import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: Type '"staging"' is not assignable to type '"beta" | "prod"'
  .when({ deployTo: ["beta", "staging"] }, p => p);
