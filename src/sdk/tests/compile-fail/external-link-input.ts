import { external, model, step, z } from "@olivervea/pipelines";

const Review = model("Review", z.object({ url: z.string() }));
const Verdict = model("Verdict", z.object({ accepted: z.boolean() }));

step({
  name: "review",
  description: "",
  image: "alpine",
  input: {
    review: Review,
    // @ts-expect-error -- expect: Type '"reviews"' is not assignable to type '"review"'
    verdict: external(Verdict, { title: "Accepted?", link: { input: "reviews", path: "url" } }),
  },
});

step({
  name: "review-twice",
  description: "",
  image: "alpine",
  input: {
    review: Review,
    first: external(Verdict, { title: "First?" }),
    // @ts-expect-error -- expect: Type '"first"' is not assignable to type '"review"'
    second: external(Verdict, { title: "Second?", link: { input: "first" } }),
  },
});
