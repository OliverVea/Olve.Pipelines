// Fixture: the shape of Olve.Pipelines' own self-deploy (build → test → beta → prod → publish CLI).
// Steps are offline fakes so the SDK tests can build and run every step locally.
import { pipeline, githubSignature } from '@olivervea/pipelines';
import * as M from './models';
import { GITHUB_TOKEN, RELEASE_HOOK_SECRET } from './secrets';
import { aoeTriage } from './steps/aoe-triage';
import { build } from './steps/build';
import { checkout } from './steps/checkout';
import { deployBeta } from './steps/deploy-beta';
import { deployProd } from './steps/deploy-prod';
import { publishCli } from './steps/publish-cli';
import { summary } from './steps/summary';
import { test } from './steps/test';

export default pipeline({
  name: 'Olve.Pipelines',
  description: 'Build, deploy beta then prod, publish the pl CLI.',
  input: M.DeployRequest,
})
  .onPush((e) => ({
    repo: e.repository.full_name,
    sha: e.after,
    deployTo: e.ref === 'refs/heads/main' ? 'prod' : 'beta',
  }))
  .onPoll(
    {
      url: 'https://api.github.com/repos/OliverVea/Olve.Pipelines/commits/main',
      secrets: [GITHUB_TOKEN],
      headers: { Authorization: '$SECRET:GITHUB_TOKEN' },
      valuePath: 'sha',
      intervalSeconds: 300,
    },
    (value) => ({ sha: value }), // z.input view: repo/deployTo take their defaults
  )
  .onWebhook(
    { name: 'release', verify: githubSignature(RELEASE_HOOK_SECRET) },
    ({ headers, body }) =>
      // null ignores the delivery
      headers['x-github-event'] === 'release'
        ? {
            sha: (body as { release: { target_commitish: string } }).release
              .target_commitish,
          }
        : null,
  )
  .stage([checkout, (from) => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test) // both matched by model (Source)
  .stage(deployBeta) // BuildArtifact from build
  .when({ deployTo: 'prod' }, (p) =>
    p.stage(deployProd).when({ publishCli: true }, (p) => p.stage(publishCli)),
  ) // ProdDeployment is definite inside the block
  .stage(summary) // prod/cli maybe-available → optional()
  .onFailure(aoeTriage, { steps: [deployProd, deployBeta] });
