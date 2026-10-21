import { base, step, z } from "../fixtures2";

const s = step({ name: "s", image: "alpine", input: { tag: z.string() } });
base()
  // @ts-expect-error -- expect: Property 'slice' does not exist on type 'Ref<string>'
  .stage([s, from => ({ tag: from.input.sha.slice(0, 7) })]);
