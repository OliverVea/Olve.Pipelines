import { base, checkout } from "../fixtures";

base()
  // @ts-expect-error -- expect: Type 'number' is not assignable to type 'Mappable<string>'
  .stage([checkout, from => ({ repo: from.input.repo, sha: 42 })]);
