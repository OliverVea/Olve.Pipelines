// bun test compose/deployer — the pure parts of the deployer.
import { expect, test } from "bun:test";

process.env.DEPLOYER_TOKEN = "t";
process.env.DEPLOYER_PROJECT = "p";
process.env.DEPLOYER_REGISTRY_ALIASES = JSON.stringify({ "registry:5000": "localhost:5001" });
const { pullRef, recreateBody } = await import("./deployer.ts");

const digest = "sha256:" + "a".repeat(64);

test("the daemon pulls through the registry alias", () => {
  expect(pullRef(`registry:5000/olve-pipelines@${digest}`)).toBe(`localhost:5001/olve-pipelines@${digest}`);
  expect(pullRef(`ghcr.io/o/x@${digest}`)).toBe(`ghcr.io/o/x@${digest}`);
});

test("recreate keeps the config, sets the image and the deployed-image env, and drops what a shared network namespace forbids", () => {
  const old = {
    Config: { Image: "olve-pipelines-v2:local", Hostname: "abc", ExposedPorts: { "5000/tcp": {} }, Env: ["A=1", "Deployment__Image=old"], Labels: { "com.docker.compose.service": "pipelines" } },
    HostConfig: { NetworkMode: "container:edge-id", Mounts: [{ Type: "volume", Source: "k3s-sa", Target: "/sa" }] },
    NetworkSettings: { Networks: {} },
  };
  const body = recreateBody(old, `localhost:5001/olve-pipelines@${digest}`, `registry:5000/olve-pipelines@${digest}`) as Record<string, unknown>;
  expect(body.Image).toBe(`localhost:5001/olve-pipelines@${digest}`);
  expect(body.Env).toEqual(["A=1", `Deployment__Image=registry:5000/olve-pipelines@${digest}`]);
  expect(body.Hostname).toBeUndefined();
  expect(body.ExposedPorts).toBeUndefined();
  expect(body.HostConfig).toEqual(old.HostConfig);
  expect(body.NetworkingConfig).toBeUndefined();
  expect(body.Labels).toEqual(old.Config.Labels);
});

test("a container on its own networks keeps its aliases", () => {
  const old = {
    Config: { Image: "x", Hostname: "h" },
    HostConfig: { NetworkMode: "p_default" },
    NetworkSettings: { Networks: { p_default: { Aliases: ["registry"], IPAMConfig: { IPv4Address: "172.29.80.201" }, NetworkID: "n", IPAddress: "172.29.80.201" } } },
  };
  const body = recreateBody(old, "y", "y") as { Hostname?: string; NetworkingConfig: unknown };
  expect(body.Hostname).toBe("h");
  expect(body.NetworkingConfig).toEqual({ EndpointsConfig: { p_default: { Aliases: ["registry"], IPAMConfig: { IPv4Address: "172.29.80.201" }, Links: null } } });
});
