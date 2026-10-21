import { base, checkout, checkout2, build } from "../fixtures";

base()
  .stage(
    [checkout, from => ({ repo: from.input.repo, sha: from.input.sha })],
    [checkout2, from => ({ repo: from.input.repo, sha: from.input.sha })],
  )
  // @ts-expect-error -- expect: Source (input 'source' of step 'build') has several producers; use a mapper to pick one
  .stage(build);
