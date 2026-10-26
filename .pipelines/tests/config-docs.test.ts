// The config documents kept in the repo are valid for this pipeline, and render the helm values they claim to.
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { AUTH, BUILD, PUBLISH, SOURCE, TARGET, TELEMETRY } from "../config";

const repo = join(import.meta.dir, "../..");
const load = (path: string) => {
  const doc = Bun.YAML.parse(readFileSync(join(repo, path), "utf8")) as Record<string, unknown>;
  return {
    target: TARGET.schema.parse(doc.target),
    auth: AUTH.schema.parse(doc.auth),
    telemetry: TELEMETRY.schema.parse(doc.telemetry),
    build: BUILD.schema.parse(doc.build),
    publish: PUBLISH.schema.parse(doc.publish),
    source: SOURCE.schema.parse(doc.source),
  };
};

describe("config documents", () => {
  test("prod example: today's targets, Authentik, no telemetry, builds staged on registry.ovea.pro and released to ghcr and npm", () => {
    const c = load("docs/design/prod-config.example.yaml");
    expect(c.target.beta).toMatchObject({ kind: "ssh", host: "oliver@bulwark-m2", namespace: "apps-beta", release: "olve-pipelines-v2", values: ["values-beta.yaml", "values-v2-beta.yaml"], set: { "slo.enabled": "false" } });
    expect(c.target.prod).toMatchObject({ kind: "ssh", host: "oliver@bulwark-m2", namespace: "apps", release: "olve-pipelines-v2", values: ["values-v2-prod.yaml"], set: {} });
    // What deploy-prod renders into its values files (beta layers target.beta's auth/telemetry on top).
    expect({ auth: c.auth, telemetry: c.telemetry }).toEqual({
      auth: {
        mode: "authentik",
        authority: "https://auth.ovea.pro/application/o/olve-pipelines/",
        audience: "olve-pipelines",
        frontendAuthority: "https://auth.ovea.pro/application/o/olve-pipelines-frontend/",
        frontendAudience: "olve-pipelines-frontend",
        anonymousReads: false,
      },
      telemetry: { exporter: "none" },
    });
    const beta = c.target.beta;
    if (beta.kind === "none") throw new Error("the prod example has a beta");
    expect(beta.auth).toMatchObject({ mode: "authentik", audience: "olve-pipelines-beta", anonymousReads: true });
    expect(beta.telemetry).toBeUndefined();
    const ghcr = { username: "OliverVea", password: { secret: "GHCR_TOKEN" } };
    expect(c.publish).toMatchObject({ server: { repository: "registry.ovea.pro/olve-pipelines-v2", release: [{ repository: "ghcr.io/olivervea/olve-pipelines", auth: ghcr }] }, harness: { push: true, repository: "registry.ovea.pro/olve-pipelines-v2/harness-base", release: [{ repository: "ghcr.io/olivervea/olve-pipelines-harness-base", auth: ghcr }] }, npm: { enabled: true, prereleaseTag: "next", releaseTag: "beta" }, imageTags: { prerelease: "next", release: "latest" } });
    expect(c.source.apiUrl).toBe("https://api.github.com");
  });

  test("local self-deploy: one instance redeployed by the compose deployer, no beta, nothing published to npm", () => {
    const c = load("compose/selfdeploy.config.yaml");
    expect(c.target.beta).toEqual({ kind: "none" });
    expect(c.target.prod).toMatchObject({ kind: "compose", deployerUrl: "http://deployer:8080", service: "pipelines", token: { secret: "DEPLOYER_TOKEN" } });
    expect(c.auth).toEqual({ mode: "none" });
    expect(c.publish.server.repository.startsWith("registry:5000/")).toBe(true);
    expect(c.build.runtime).toBe("aot");
    expect(c.publish.npm.enabled).toBe(false);
  });

  test("local in-cluster variant: two k3s namespaces, the compose registry, nothing published to npm", () => {
    const c = load("compose/selfdeploy-incluster.config.yaml");
    expect(c.target.beta).toMatchObject({ kind: "inCluster", namespace: "selfdeploy-beta", release: "olve-pipelines" });
    expect(c.target.prod).toMatchObject({ kind: "inCluster", namespace: "selfdeploy-prod", release: "olve-pipelines" });
    expect(c.auth).toEqual({ mode: "none" });
    expect(c.telemetry).toEqual({ exporter: "none" });
    expect(c.publish.server.repository.startsWith("registry:5000/")).toBe(true);
    expect(c.publish.harness).toMatchObject({ push: true, repository: "registry:5000/olve-pipelines-harness-base" });
    expect(c.publish.npm.enabled).toBe(false);
  });

  test("auth: the commented Dex variant of the prod example parses, allow lists default to empty", () => {
    const text = readFileSync(join(repo, "docs/design/prod-config.example.yaml"), "utf8");
    const lines = text.split(/\r?\n/);
    const start = lines.findIndex((l) => l.startsWith("# After the switch to Dex")) + 1;
    const end = lines.findIndex((l, i) => i >= start && !l.startsWith("#"));
    const dex = lines.slice(start, end).map((l) => l.replace(/^# ?/, "")).join("\n");
    const auth = AUTH.schema.parse((Bun.YAML.parse(dex) as { auth: unknown }).auth);
    expect(auth).toMatchObject({ mode: "dex", allow: { users: ["OliverVea"], orgs: [], emails: [] } });
  });

  test("prod from local: ssh to bulwark-m2 by IP, today's namespaces and values, pinned harness, no npm", () => {
    const c = load("compose/prod-from-local.config.yaml");
    const pin = { "config.Harness__HarnessBaseImage": "registry.ovea.pro/olve-pipelines-v2/harness-base:0c4808c152bf" };
    expect(c.target.beta).toMatchObject({ kind: "ssh", host: "oliver@100.100.117.17", namespace: "apps-beta", release: "olve-pipelines-v2", values: ["values-beta.yaml", "values-v2-beta.yaml"], set: { "slo.enabled": "false", ...pin }, url: "https://pipelines-beta.ovea.pro" });
    expect(c.target.prod).toMatchObject({ kind: "ssh", host: "oliver@100.100.117.17", namespace: "apps", release: "olve-pipelines-v2", values: ["values-v2-prod.yaml"], set: { ...pin, "config.Distribution__CliPromotedBy": "" }, url: "https://pipelines-private.ovea.pro" });
    expect(c.publish).toMatchObject({ server: { repository: "registry.ovea.pro/olve-pipelines-v2" }, harness: { push: false }, npm: { enabled: false } });
    const prodExample = load("docs/design/prod-config.example.yaml");
    expect({ auth: c.auth, telemetry: c.telemetry }).toEqual({ auth: prodExample.auth, telemetry: prodExample.telemetry });
  });

  test("publishing is off unless the document turns it on", () => {
    expect(PUBLISH.schema.parse({ server: { repository: "r/s" }, harness: { repository: "r/h" } }).npm.enabled).toBe(false);
  });
});
