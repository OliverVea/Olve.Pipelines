import { base } from "../fixtures";

base()
  // @ts-expect-error -- expect: Property 'sha' is missing
  .onPoll({ url: "https://x", valuePath: "sha", intervalSeconds: 60 }, value => ({ repo: value }));
