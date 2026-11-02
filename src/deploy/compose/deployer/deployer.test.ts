// bun test src/deploy/compose/deployer — against a fake Docker daemon on a unix socket.
import { beforeEach, expect, test } from "bun:test";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const health = Bun.serve({ port: 0, fetch: () => new Response("ok") });
const socket = join(mkdtempSync(join(tmpdir(), "deployer-")), "docker.sock");

process.env.DEPLOYER_TOKEN = "t";
process.env.DEPLOYER_PROJECT = "p";
process.env.DEPLOYER_DOCKER_SOCKET = socket;
process.env.DEPLOYER_SERVICES = JSON.stringify({ web: { health: `http://localhost:${health.port}/` } });
process.env.DEPLOYER_REGISTRY_ALIASES = JSON.stringify({ "registry:5000": "localhost:5001" });
const { deploy, pullRef, recreateBody } = await import("./deployer.ts");

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

const OLD = "old-id";
const NEW = "new-id";
let calls: string[];
let refuse: Record<string, Response>;
let newContainerRuns: boolean;

Bun.serve({
  unix: socket,
  fetch(req) {
    const url = new URL(req.url);
    const path = url.pathname.replace(/^\/v[\d.]+/, "");
    const call = `${req.method} ${path}${url.search}`;
    if (req.method === "GET" && path === "/containers/json") return Response.json([{ Id: OLD }]);
    if (req.method === "GET" && path === `/containers/${OLD}/json`)
      return Response.json({ Id: OLD, Name: "/p-web-1", Config: { Image: "web:1", Env: [] }, HostConfig: { NetworkMode: "p_default" } });
    if (req.method === "GET" && path === `/containers/${NEW}/json`) return Response.json({ State: { Running: newContainerRuns } });
    if (req.method === "POST" && path === "/images/create") return new Response("{}\n");
    calls.push(call);
    const refused = Object.entries(refuse).find(([prefix]) => call.startsWith(prefix));
    if (refused) return refused[1];
    return path === "/containers/create" ? Response.json({ Id: NEW }) : new Response(null, { status: 204 });
  },
});

beforeEach(() => {
  calls = [];
  refuse = {};
  newContainerRuns = true;
});

const image = `ghcr.io/o/web@${digest}`;
const swap = [
  `POST /containers/${OLD}/rename?name=p-web-1-previous`,
  `POST /containers/${OLD}/stop?t=30`,
  "POST /containers/create?name=p-web-1",
];
const restore = [`POST /containers/${OLD}/rename?name=p-web-1`, `POST /containers/${OLD}/start`];

test("a healthy deploy removes the previous container", async () => {
  const { code, body } = await deploy("web", image);

  expect({ code, status: body.status, previousImage: body.previousImage }).toEqual({ code: 200, status: "deployed", previousImage: "web:1" });
  expect(calls).toEqual([...swap, `POST /containers/${NEW}/start`, `DELETE /containers/${OLD}?v=false`]);
});

test("a container that stops running is removed and the previous one put back", async () => {
  newContainerRuns = false;

  const { code, body } = await deploy("web", image);

  expect({ code, status: body.status, previousImage: body.previousImage }).toEqual({ code: 502, status: "rolled-back", previousImage: "web:1" });
  expect(body.message).toBe(`http://localhost:${health.port}/ did not answer 2xx in time`);
  expect(calls).toEqual([...swap, `POST /containers/${NEW}/start`, `DELETE /containers/${NEW}?force=true`, ...restore]);
  expect(body.log).toContain("rolled back to web:1");
});

test("a container that won't start is removed and the previous one put back", async () => {
  refuse[`POST /containers/${NEW}/start`] = new Response("no such image", { status: 500 });

  const { code, body } = await deploy("web", image);

  expect({ code, status: body.status }).toEqual({ code: 502, status: "rolled-back" });
  expect(body.message).toBe("start: Docker answered 500: no such image");
  expect(calls).toEqual([...swap, `POST /containers/${NEW}/start`, `DELETE /containers/${NEW}?force=true`, ...restore]);
});

test("a refused create leaves nothing to remove before the previous container is put back", async () => {
  refuse["POST /containers/create"] = new Response("name in use", { status: 409 });

  const { code, body } = await deploy("web", image);

  expect({ code, status: body.status }).toEqual({ code: 502, status: "rolled-back" });
  expect(body.message).toBe("create: Docker answered 409: name in use");
  expect(calls).toEqual([...swap, ...restore]);
});

test("a rollback the daemon refuses reports the failure, not a rollback", async () => {
  newContainerRuns = false;
  refuse[`POST /containers/${OLD}/start`] = new Response("port is taken", { status: 500 });

  await expect(deploy("web", image)).rejects.toThrow("restart the old container: Docker answered 500: port is taken");
});

test("a service outside DEPLOYER_SERVICES and a tag instead of a digest touch nothing", async () => {
  const unknown = await deploy("db", image);
  const tagged = await deploy("web", "ghcr.io/o/web:latest");

  expect({ code: unknown.code, message: unknown.body.message }).toEqual({ code: 400, message: "service 'db' is not deployable here (DEPLOYER_SERVICES)" });
  expect({ code: tagged.code, message: tagged.body.message }).toEqual({ code: 400, message: "image must be <repository>@sha256:<digest>" });
  expect(calls).toEqual([]);
});
