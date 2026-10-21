import { base, checkout, deploy, aoeTriage } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: Type '"deploy"' is not assignable to type '"checkout"'
  .onFailure(aoeTriage, { steps: [deploy] });
