import { base, fakeProd, needsBeta } from "../fixtures";

// ProdDeployment and BetaDeployment have the same shape but are distinct models.
base()
  .stage(fakeProd)
  // @ts-expect-error -- expect: is not assignable to type 'Mappable<
  .stage([needsBeta, i => ({ beta: i["fake-prod"] })]);
