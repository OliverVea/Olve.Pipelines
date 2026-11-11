import { external, implement, model, step, z } from "@olivervea/pipelines";

const Review = model("Review", z.object({ url: z.string() }));
const Verdict = model("Verdict", z.object({ accepted: z.boolean() }));

const plain = step({ name: "plain", description: "", image: "alpine", input: { review: Review } });
// @ts-expect-error -- expect: is not assignable to parameter of type '"❌ a preflight needs a step with an external(...) input"'
implement(plain, () => {}, { preflight: () => {} });

const waits = step({
  name: "waits",
  description: "",
  image: "alpine",
  input: { review: Review, verdict: external(Verdict, { title: "Accepted?" }) },
});
implement(waits, () => {}, {
  preflight: (input) => {
    // @ts-expect-error -- expect: Property 'verdict' does not exist
    void input.verdict;
  },
});
implement(waits, () => {}, {
  preflight: (_input, ctx) => {
    // @ts-expect-error -- expect: Property 'skip' does not exist
    ctx.skip("no");
  },
});
