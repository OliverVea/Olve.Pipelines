import { base, GITHUB_TOKEN } from "../fixtures";

base().onPoll(
  {
    url: "https://api.github.com/repos/x/y/commits/main",
    secrets: [GITHUB_TOKEN],
    // @ts-expect-error -- expect: secret 'GH_TOKEN' is not declared in this poll trigger's secrets
    headers: { Authorization: "$SECRET:GH_TOKEN" },
    valuePath: "sha",
    intervalSeconds: 300,
  },
  value => ({ sha: value }),
);
