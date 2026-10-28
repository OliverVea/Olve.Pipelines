import { step, M } from "../fixtures2";

declare const name: string;
// @ts-expect-error -- expect: step name must be a string literal
const s = step({ name, image: "alpine", input: { req: M.DeployRequest } });
