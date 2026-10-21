import { step, z } from "../fixtures2";

// A step's output must be a named model (outputs are nominal and listed in the manifest by name).
// @ts-expect-error -- expect: is missing the following properties from type 'Model<
step({ name: "s", image: "alpine", output: z.object({ ok: z.boolean() }) });
