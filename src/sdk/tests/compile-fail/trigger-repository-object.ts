import { base } from "../fixtures";

// The spec's `repo: e.repository` — in a real GitHub push payload `repository` is an object.
base()
  // @ts-expect-error -- expect: is not assignable to type 'string'
  .onPush(e => ({ repo: e.repository, sha: e.after }));
