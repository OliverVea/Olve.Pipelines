import { checks } from "@olivervea/pipelines";
import { GITHUB_TOKEN, checkout, test } from "../fixtures";

export const ci = checks({ description: "Checks a pull request.", secrets: [GITHUB_TOKEN] })
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(test);
