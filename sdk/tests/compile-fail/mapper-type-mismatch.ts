import { base, checkout, build } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: is not assignable to type 'Mappable<
  .stage([build, from => ({ source: from.input })]);
