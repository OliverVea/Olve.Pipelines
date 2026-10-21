import { base, step, z } from "../fixtures2";

const s = step({ name: "s", image: "alpine", input: { tag: z.string() } });
base()
  // @ts-expect-error -- expect: The '+' operator cannot be applied to type 'symbol'
  .stage([s, from => ({ tag: "v-" + from.input.sha })]);
