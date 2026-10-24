// The config documents kept in the repo are valid for this pipeline, and render the helm values they claim to.
import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { AUTH, PUBLISH, SOURCE, TARGET, TELEMETRY } from "../config";

const repo = join(import.meta.dir, "../..");
const load = (path: string) => {
  const doc = Bun.YAML.parse(readFileSync(join(repo, path), "utf8")) as Record<string, unknown>;
  return {
    target: TARGET.schema.parse(doc.target),
    auth: AUTH.schema.parse(doc.auth),
    telemetry: TELEMETRY.schema.parse(doc.telemetry),
    publish: PUBLISH.schema.parse(doc.publish),
    source: SOURCE.schema.parse(doc.source),
  };
};

describe("config documents", () => {
  test("prod example: today's targets, Authentik, OTLP, and the real registries", () => {
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
      telemetry: {
        exporter: "otlp", endpoint: "https://otel.ovea.pro", protocol: "http",
        auth: { type: "oauth2", tokenUrl: "https://auth.ovea.pro/application/o/token/", clientId: "otel", clientSecret: { kubernetesSecret: { name: "authentik-oidc-secrets", key: "otel-client-secret" } } },
      },
    });
    expect(c.target.beta.auth).toMatchObject({ mode: "authentik", audience: "olve-pipelines-beta", anonymousReads: true });
    expect(c.target.beta.telemetry).toEqual({ exporter: "otlp", endpoint: "http://otel-beta.ovea.pro", protocol: "http", auth: { type: "none" } });
    expect(c.publish).toMatchObject({ npm: { enabled: true, prereleaseTag: "next", releaseTag: "beta" }, imageTags: { prerelease: "next", release: "latest" } });
    expect(c.source.apiUrl).toBe("https://api.github.com");
  });

  test("local self-deploy: in-cluster namespaces, the compose registry, nothing published to npm", () => {
    const c = load("compose/selfdeploy.config.yaml");
    expect(c.target.beta).toMatchObject({ kind: "inCluster", namespace: "selfdeploy-beta", release: "olve-pipelines" });
    expect(c.target.prod).toMatchObject({ kind: "inCluster", namespace: "selfdeploy-prod", release: "olve-pipelines" });
    expect(c.auth).toEqual({ mode: "none" });
    expect(c.telemetry).toEqual({ exporter: "none" });
    expect(c.publish.server.repository.startsWith("registry:5000/")).toBe(true);
    expect(c.publish.harness).toMatchObject({ push: false, repository: "registry:5000/olve-pipelines-harness-base" });
    expect(c.publish.npm.enabled).toBe(false);
  });

  test("publishing is off unless the document turns it on", () => {
    expect(PUBLISH.schema.parse({ server: { repository: "r/s" }, harness: { repository: "r/h" } }).npm.enabled).toBe(false);
  });
});
