import { base, checkout } from "../fixtures";

base()
  // @ts-expect-error -- expect: Property 'sha' is missing
  .stage([checkout, i => ({ repo: i.input.repo })]);
