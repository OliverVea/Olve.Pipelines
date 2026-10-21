import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: 'sha' is not a literal-like field (enum, boolean or literal); when cannot match on it
  .when({ sha: "abc" }, p => p);
