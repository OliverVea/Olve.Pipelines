// The compose deployer: the one component with the Docker socket. Versioned HTTP API:
//
//   POST /v1/deploy   { service, image }   Authorization: Bearer <DEPLOYER_TOKEN>
//     → 200 { status: "deployed", service, image, previousImage, log }
//     → 502 { status: "rolled-back" | "failed", …, message, log }
//   GET  /v1/health   → 200
//
// It pulls the image, recreates the compose service's container with it (same config, mounts and
// networks), waits for the service's health URL, and puts the previous container back if that fails.
// Only services listed in DEPLOYER_SERVICES can be deployed.
//
// Env:
//   DEPLOYER_TOKEN              bearer token (required)
//   DEPLOYER_PROJECT            compose project name (com.docker.compose.project)
//   DEPLOYER_SERVICES           {"<service>": {"health": "<url>", "healthTimeoutSeconds": 180}}
//   DEPLOYER_REGISTRY_ALIASES   {"registry:5000": "localhost:5001"}: how the Docker daemon reaches a registry
//   DEPLOYER_REGISTRY_AUTH      {"localhost:5001": {"username": "…", "password": "…"}}
//   DEPLOYER_IMAGE_ENV          env var set on the container to the deployed image (default Deployment__Image)

const SOCKET = "/var/run/docker.sock";
const env = (name: string, fallback?: string): string => {
  const v = process.env[name] ?? fallback;
  if (v === undefined) throw new Error(`${name} is not set`);
  return v;
};
const TOKEN = env("DEPLOYER_TOKEN");
const PROJECT = env("DEPLOYER_PROJECT");
const SERVICES = JSON.parse(env("DEPLOYER_SERVICES", "{}")) as Record<string, { health: string; healthTimeoutSeconds?: number }>;
const ALIASES = JSON.parse(env("DEPLOYER_REGISTRY_ALIASES", "{}")) as Record<string, string>;
const REGISTRY_AUTH = JSON.parse(env("DEPLOYER_REGISTRY_AUTH", "{}")) as Record<string, { username: string; password: string }>;
const IMAGE_ENV = env("DEPLOYER_IMAGE_ENV", "Deployment__Image");

type Json = Record<string, unknown>;

async function docker(method: string, path: string, body?: unknown, headers: Record<string, string> = {}): Promise<Response> {
  return fetch(`http://docker${path}`, {
    method,
    unix: SOCKET,
    headers: { ...(body !== undefined ? { "content-type": "application/json" } : {}), ...headers },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  } as RequestInit);
}

async function ok(res: Response, what: string): Promise<Response> {
  if (res.ok || res.status === 304) return res;
  throw new Error(`${what}: Docker answered ${res.status}: ${(await res.text()).slice(0, 300)}`);
}

/** The reference the Docker daemon pulls: the registry host replaced by its alias, if any. */
export function pullRef(image: string): string {
  const [host, ...rest] = image.split("/");
  return host && ALIASES[host] ? [ALIASES[host], ...rest].join("/") : image;
}

/** Recreate body: the old container's config with the new image; settings Docker refuses with a shared network namespace dropped. */
export function recreateBody(old: Json, image: string, deployed: string): Json {
  const config = { ...(old.Config as Json) };
  const host = old.HostConfig as Json;
  config.Image = image;
  config.Env = [...((config.Env as string[] | undefined) ?? []).filter((e) => !e.startsWith(`${IMAGE_ENV}=`)), `${IMAGE_ENV}=${deployed}`];
  const sharedNetwork = String(host.NetworkMode ?? "").startsWith("container:");
  if (sharedNetwork) {
    delete config.Hostname;
    delete config.Domainname;
    delete config.ExposedPorts;
    delete config.MacAddress;
  }
  const networks = ((old.NetworkSettings as Json | undefined)?.Networks ?? {}) as Record<string, Json>;
  const endpoints = Object.fromEntries(Object.entries(networks).map(([name, n]) => [name, { Aliases: n.Aliases ?? null, IPAMConfig: n.IPAMConfig ?? null, Links: n.Links ?? null }]));
  return { ...config, HostConfig: host, ...(sharedNetwork ? {} : { NetworkingConfig: { EndpointsConfig: endpoints } }) };
}

async function pull(ref: string, log: (s: string) => void): Promise<void> {
  const host = ref.split("/")[0]!;
  const auth = REGISTRY_AUTH[host];
  const headers = auth ? { "X-Registry-Auth": Buffer.from(JSON.stringify({ ...auth, serveraddress: host })).toString("base64url") } : {};
  const res = await ok(await docker("POST", `/images/create?fromImage=${encodeURIComponent(ref)}`, undefined, headers), `pull ${ref}`);
  const text = await res.text();
  const error = text.split("\n").map((l) => (l ? (JSON.parse(l) as { error?: string }) : {})).find((l) => l.error);
  if (error) throw new Error(`pull ${ref}: ${error.error}`);
  log(`pulled ${ref}`);
}

async function healthy(url: string, timeoutSeconds: number, containerId: string): Promise<boolean> {
  const deadline = Date.now() + timeoutSeconds * 1000;
  while (Date.now() < deadline) {
    const state = (await (await docker("GET", `/containers/${containerId}/json`)).json()) as { State?: { Running?: boolean } };
    if (state.State?.Running === false) return false;
    try {
      const res = await fetch(url, { signal: AbortSignal.timeout(5000) });
      if (res.ok) return true;
    } catch {
      // not up yet
    }
    await Bun.sleep(3000);
  }
  return false;
}

let busy: Promise<unknown> = Promise.resolve();

async function deploy(service: string, image: string): Promise<{ code: number; body: Json }> {
  const lines: string[] = [];
  const log = (s: string) => {
    lines.push(s);
    console.log(`[deploy ${service}] ${s}`);
  };
  const spec = SERVICES[service];
  if (!spec) return { code: 400, body: { status: "failed", service, image, message: `service '${service}' is not deployable here (DEPLOYER_SERVICES)`, log: lines } };
  if (!/@sha256:[0-9a-f]{64}$/.test(image)) return { code: 400, body: { status: "failed", service, image, message: "image must be <repository>@sha256:<digest>", log: lines } };

  const ref = pullRef(image);
  await pull(ref, log);
  const filters = encodeURIComponent(JSON.stringify({ label: [`com.docker.compose.project=${PROJECT}`, `com.docker.compose.service=${service}`] }));
  const found = (await (await ok(await docker("GET", `/containers/json?all=1&filters=${filters}`), "list containers")).json()) as { Id: string }[];
  if (found.length !== 1) return { code: 409, body: { status: "failed", service, image, message: `expected one container of service '${service}', found ${found.length}`, log: lines } };
  const old = (await (await ok(await docker("GET", `/containers/${found[0]!.Id}/json`), "inspect")).json()) as Json;
  const name = String(old.Name).replace(/^\//, "");
  const previousImage = (((old.Config as Json).Env as string[] | undefined) ?? []).find((e) => e.startsWith(`${IMAGE_ENV}=`))?.slice(IMAGE_ENV.length + 1) ?? String((old.Config as Json).Image);
  log(`replacing ${name} (${previousImage})`);

  const backup = `${name}-previous`;
  await ok(await docker("POST", `/containers/${old.Id}/rename?name=${encodeURIComponent(backup)}`), "rename the old container");
  await ok(await docker("POST", `/containers/${old.Id}/stop?t=30`), "stop the old container");
  let createdId: string | undefined;
  try {
    const created = (await (await ok(await docker("POST", `/containers/create?name=${encodeURIComponent(name)}`, recreateBody(old, ref, image)), "create")).json()) as { Id: string };
    createdId = created.Id;
    await ok(await docker("POST", `/containers/${created.Id}/start`), "start");
    log(`started ${name} with ${image}; waiting for ${spec.health}`);
    if (await healthy(spec.health, spec.healthTimeoutSeconds ?? 180, created.Id)) {
      await docker("DELETE", `/containers/${old.Id}?v=false`);
      log("healthy; removed the previous container");
      return { code: 200, body: { status: "deployed", service, image, previousImage, log: lines } };
    }
    throw new Error(`${spec.health} did not answer 2xx in time`);
  } catch (e) {
    log(`failed: ${(e as Error).message}; rolling back`);
    if (createdId) await docker("DELETE", `/containers/${createdId}?force=true`);
    await ok(await docker("POST", `/containers/${old.Id}/rename?name=${encodeURIComponent(name)}`), "rename the old container back");
    await ok(await docker("POST", `/containers/${old.Id}/start`), "restart the old container");
    log(`rolled back to ${previousImage}`);
    return { code: 502, body: { status: "rolled-back", service, image, previousImage, message: (e as Error).message, log: lines } };
  }
}

if (import.meta.main) {
  Bun.serve({
    port: Number(process.env.PORT ?? 8080),
    idleTimeout: 0,
    async fetch(req) {
      const url = new URL(req.url);
      if (req.method === "GET" && url.pathname === "/v1/health") return Response.json({ status: "ok" });
      if (req.method !== "POST" || url.pathname !== "/v1/deploy") return Response.json({ message: "not found" }, { status: 404 });
      if (req.headers.get("authorization") !== `Bearer ${TOKEN}`) return Response.json({ message: "unauthorized" }, { status: 401 });
      const body = (await req.json().catch(() => ({}))) as { service?: unknown; image?: unknown };
      if (typeof body.service !== "string" || typeof body.image !== "string") return Response.json({ message: "expected { service, image }" }, { status: 400 });
      const run = busy.then(() => deploy(body.service as string, body.image as string)).catch((e: Error) => ({
        code: 500, body: { status: "failed", service: body.service, image: body.image, message: e.message },
      }));
      busy = run;
      const { code, body: result } = await run;
      return Response.json(result, { status: code });
    },
  });
  console.log(`deployer: project ${PROJECT}, services ${Object.keys(SERVICES).join(", ") || "(none)"}`);
}
