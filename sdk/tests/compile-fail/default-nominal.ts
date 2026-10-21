import { base, fakeProd, needsBeta } from "../fixtures";

base()
  .stage(fakeProd)
  // @ts-expect-error -- expect: BetaDeployment (input 'beta' of step 'needs-beta') is not available at this stage
  .stage(needsBeta);
