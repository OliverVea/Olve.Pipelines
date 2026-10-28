import { base, checkout, deployProd, aoeTriage } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: Type '"deploy-prod"' is not assignable to type '"checkout"'
  .onFailure(aoeTriage, { steps: [deployProd] });
