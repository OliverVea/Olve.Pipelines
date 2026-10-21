import { base, step, M } from "../fixtures2";

const input = step({ name: "input", image: "alpine", input: { req: M.DeployRequest } });
base()
  // @ts-expect-error -- expect: 'input' is reserved for the pipeline input
  .stage(input);
