import { checks } from "@olivervea/pipelines";
import { checkout, test } from "../fixtures";

checks()
  // @ts-expect-error -- expect: Property 'pr' does not exist
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.pr })]);

// @ts-expect-error -- expect: Property 'onPush' does not exist
checks().onPush(e => ({ repo: e.repository.full_name, sha: e.after }));

checks()
  // @ts-expect-error -- expect: Object literal may only specify known properties, and 'deployTo' does not exist
  .when({ deployTo: "prod" }, p => p);

checks()
  // @ts-expect-error -- expect: Source (input 'source' of step 'test') is not available at this stage
  .stage(test);
