import { base, checkout } from "../fixtures";

base()
  // @ts-expect-error -- expect: Property 'sha' is missing
  .stage([checkout, from => ({ repo: from.input.repo })]);
