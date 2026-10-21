import { base, checkout } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: duplicate step name 'checkout' (already used in an earlier stage)
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })]);
