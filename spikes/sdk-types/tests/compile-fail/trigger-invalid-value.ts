import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: Type '"prd"' is not assignable to type '"beta" | "prod" | undefined'
  .onPush(e => ({ sha: e.after, deployTo: "prd" }));
