import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: 'branch' is not a field of the pipeline input
  .onPush(e => ({ sha: e.after, branch: e.ref }));
