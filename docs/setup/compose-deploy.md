# Deploying to Docker Compose

[← Index](index.md)

A pipeline can deploy to a Docker Compose project instead of a Kubernetes cluster. Two parts do the
work:

- **The deployer** runs beside your compose project. It is the only component with the Docker socket.
  Given a service and an image, it pulls the image, recreates the service's container with it, waits
  for the service to be healthy, and puts the previous container back if it isn't.
- **`steps.composeDeploy`** is the pipeline step. It makes one authenticated HTTP call to the deployer
  and reports the answer. It needs no Docker access, no SSH key and no cluster access.

The step stays thin on purpose: pulling, recreating, health checks and rollback live in the deployer,
behind a versioned API, so they are fixed there without editing pipelines.

## Running the deployer

The deployer is `compose/deployer` in the Olve.Pipelines repo (`deployer.ts`, a Bun server, and its
`Dockerfile`). Add it to your compose project:

```yaml
services:
  app:
    image: registry.example.com/app:latest
    # …

  deployer:
    build: ./deployer                # or an image you built from compose/deployer
    restart: unless-stopped
    environment:
      DEPLOYER_TOKEN: "${DEPLOYER_TOKEN}"
      DEPLOYER_PROJECT: myproject     # the compose project name
      DEPLOYER_SERVICES: '{"app": {"health": "http://app:8080/health", "healthTimeoutSeconds": 180}}'
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

| Variable | Meaning |
|---|---|
| `DEPLOYER_TOKEN` | Bearer token every deploy request must carry. Required. |
| `DEPLOYER_PROJECT` | The compose project (`com.docker.compose.project` label) whose services it deploys. Required. |
| `DEPLOYER_SERVICES` | The deployable services: `{"<service>": {"health": "<url>", "healthTimeoutSeconds": 180}}`. A service not listed here is refused. `health` is checked from the deployer's container. |
| `DEPLOYER_REGISTRY_ALIASES` | How the Docker daemon reaches a registry, when that differs from the name in the image (`{"registry:5000": "localhost:5001"}`). |
| `DEPLOYER_REGISTRY_AUTH` | Logins for private registries, keyed by the host the daemon pulls from: `{"<host>": {"username": "…", "password": "…"}}`. |
| `DEPLOYER_IMAGE_ENV` | Env var set on the recreated container to the deployed image (default `Deployment__Image`; Olve.Pipelines reports it in `/api/health`). |
| `PORT` | Listen port (default `8080`). |

**Access to the Docker socket is root on the host.** Keep the deployer on a private network, reachable
only by the step pods that deploy through it, and use a long random token.

**Step pods must reach the deployer.** `deployerUrl` is dialled from the step pod, so it has to resolve
and be allowed by the runner namespace's network policy.

## What a deploy does

`POST /v1/deploy` with `{ "service": "app", "image": "<repository>@sha256:<digest>" }` and
`Authorization: Bearer <token>`:

1. Refuses a service not in `DEPLOYER_SERVICES` and an image not pinned by digest (400), and a service
   that doesn't have exactly one container (409).
2. Pulls the image.
3. Renames the running container to `<name>-previous` and stops it.
4. Creates a container with the old one's config, mounts and networks, the new image, and
   `DEPLOYER_IMAGE_ENV` set to the deployed image, then starts it.
5. Waits until the service's `health` URL answers 2xx (up to `healthTimeoutSeconds`; it fails at once
   if the container exits).
6. Healthy: removes the previous container and answers **200** `{ status: "deployed", service, image,
   previousImage, log }`. Not healthy: removes the new container, renames and restarts the previous
   one, and answers **502** `{ status: "rolled-back", …, message, log }`.

Deploys are handled one at a time. `GET /v1/health` answers 200 when the deployer is up.

The compose file is not edited: it still names the image it was started with, so a later
`docker compose up` can bring that image back.

## The deploy target

Use `deployTarget()` as the schema of your target config entry. It is a union on `kind`, so you can
`select` on it: `inCluster` and `ssh` deploy with helm, and `compose` deploys through a deployer.
`optional: true` adds `kind: none`, a target that isn't deployed (for example an optional beta).

```ts
// .pipelines/config.ts
export const DEPLOYER_TOKEN = secret("DEPLOYER_TOKEN", "Bearer token of the compose deployer.");

export const TARGET = config("target", "Where beta and prod are deployed.", z.object({
  beta: deployTarget({ release: "app", composeToken: DEPLOYER_TOKEN, optional: true }),
  prod: deployTarget({ release: "app", composeToken: DEPLOYER_TOKEN }),
}));
```

`deployTarget` options: `namespace?` and `release?` (defaults for the helm kinds), `extend?` (fields
added to every kind but `none`), `composeToken` (the secret a compose target's `token` names) and
`optional?`. `helmTarget()` is the helm-only form (`inCluster | ssh`).

A compose target in the config document:

```yaml
target:
  beta:
    kind: none
  prod:
    kind: compose
    deployerUrl: http://deployer:8080
    service: app
    token: { secret: DEPLOYER_TOKEN }
    healthUrl: https://app.example.com/health   # optional
```

Set the token with `printf '%s' "$TOKEN" | pl secret set <pipeline> DEPLOYER_TOKEN`.

## The step

```ts
.select(TARGET.prod, {
  ssh: (p, prod) => p.stage([steps.helmDeployOverSsh({ name: "deploy-prod", description: "…", target: prod, sshKey: SSH_PRIVATE_KEY, … }), from => ({ … })]),
  inCluster: (p, prod) => p.stage([steps.helmDeployInCluster({ name: "deploy-prod", description: "…", target: prod, … }), from => ({ … })]),
  compose: (p, prod) => p.stage([
    steps.composeDeploy({ name: "deploy-prod", description: "Deploys prod.", target: prod, token: DEPLOYER_TOKEN, timeoutSeconds: 900 }),
    from => ({ image: from["build-server"] }),
  ]),
})
```

- **Input:** `{ image: PushedImage }`, an image pushed to a registry (`steps.imageBuild` with
  `push: true`). The deployer gets it as `<repository>@<digest>`.
- **Config:** `target` (a `select` case's reference to the compose variant), `token` (the declared
  secret; it must be the one the target's `token` names), `timeoutSeconds?`. Give `timeoutSeconds`
  room for the pull and the health wait.
- **Output:** a `HelmRelease` with the service as `release`, `compose` as `namespace` and the deployed
  image, so it can share a step name (`deploy-prod`) with the helm deploys.
- The step fails unless the deployer answers `deployed`, and logs the deployer's own log lines. With
  the target's `healthUrl`, it then GETs that URL until 2xx (12 × 5 s).
- It declares only the token: the SSH key and cluster access of the helm cases aren't needed when
  `compose` is selected.

## A full example

Olve.Pipelines deploys itself this way in its local compose stack: `compose/selfdeploy.config.yaml`
(no beta, prod `kind: compose`) with the `deployer` service in `docker-compose.yml`. See the repo
README, "Watch it deploy itself".
