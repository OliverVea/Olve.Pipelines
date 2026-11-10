import { external, implement, model, pipeline, step, z, type Infer } from "@olivervea/pipelines";
import { M } from "../fixtures";
import type { Equal, Expect } from "../type-assert";

const Choice = model("Choice", z.object({ target: z.enum(["beta", "prod"]) }));
const pick = step({
  name: "pick",
  description: "",
  image: "alpine",
  input: { req: M.DeployRequest, choice: external(Choice, { title: "Where to?" }) },
});

// The handler gets the answered value typed by its model, like any input.
implement(pick, ({ choice, req }) => {
  type _choice = Expect<Equal<typeof choice, Infer<typeof Choice>>>;
  void req;
});

// Matched by model: the external input needs no producer.
pipeline({ name: "a", input: M.DeployRequest }).stage(pick);

// A mapper provides only the mapped inputs.
pipeline({ name: "b", input: M.DeployRequest }).stage([pick, (from) => ({ req: from.input })]);


// The waiting link names one of the step's regular inputs.
const Review = model("Review", z.object({ run: z.string(), url: z.string() }));
const review = step({
  name: "review",
  description: "",
  image: "alpine",
  input: {
    review: Review,
    verdict: external(Choice, { title: "Accepted?", link: { input: "review", path: "url", label: "Review" } }),
  },
});
implement(review, ({ review: r, verdict }) => {
  type _verdict = Expect<Equal<typeof verdict, Infer<typeof Choice>>>;
  void r;
});
