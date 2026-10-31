import { config, z } from "@olivervea/pipelines";

// beta's signal-test companion (src/deploy/helm/values-v2-beta.yaml): answers the fixture's two external inputs.
export const SIGNAL_TEST = "http://olve-signal-test.apps-beta.svc.cluster.local:8080";
export const NOTIFY = config("notify", "Where the fixture's fake notification goes.", z.discriminatedUnion("channel", [
  z.object({ channel: z.literal("none") }),
  z.object({ channel: z.literal("log"), prefix: z.string().default("notify") }),
  z.object({ channel: z.literal("webhook"), url: z.url() }),
]).prefault({ channel: "log" }));
