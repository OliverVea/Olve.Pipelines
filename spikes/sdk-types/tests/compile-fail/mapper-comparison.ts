import { base, step, z } from "../fixtures2";

const s = step({ name: "s", image: "alpine", input: { tag: z.string() } });
base()
  // @ts-expect-error -- expect: This comparison appears to be unintentional
  .stage([s, i => ({ tag: i.input.deployTo === "prod" ? "latest" : "beta" })]);
