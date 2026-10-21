import { base, checkout, checkout2, build } from "../fixtures";

base()
  .stage(
    [checkout, i => ({ repo: i.input.repo, sha: i.input.sha })],
    [checkout2, i => ({ repo: i.input.repo, sha: i.input.sha })],
  )
  // @ts-expect-error -- expect: Source (input 'source' of step 'build') has several producers; use a mapper to pick one
  .stage(build);
