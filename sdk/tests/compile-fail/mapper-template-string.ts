import { base, step, z } from "../fixtures2";

const s = step({ name: "s", image: "alpine", input: { tag: z.string() } });
base()
  // @ts-expect-error -- expect: Implicit conversion of a 'symbol' to a 'string' will fail at runtime
  .stage([s, from => ({ tag: `v-${from.input.sha}` })]);
