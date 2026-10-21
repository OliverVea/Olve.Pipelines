import { base, checkout } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: duplicate step name 'checkout' (already used in an earlier stage)
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })]);
