import { base, checkout, build } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: is not assignable to type 'Mappable<
  .stage([build, i => ({ source: i.input })]);
