import { implement } from "@olivervea/pipelines";
import { summary } from "../fixtures";

implement(summary.step, ({ prod }, ctx) => {
  // @ts-expect-error -- expect: 'prod' is possibly 'undefined'
  ctx.log(prod.url);
});
