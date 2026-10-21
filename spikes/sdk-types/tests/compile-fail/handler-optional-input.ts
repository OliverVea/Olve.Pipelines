import { implement } from "@olve/pipelines";
import { summary } from "../fixtures";

implement(summary, ({ prod }, ctx) => {
  // @ts-expect-error -- expect: 'prod' is possibly 'undefined'
  ctx.log(prod.url);
});
