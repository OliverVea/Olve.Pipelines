# Replace the dedicated MinIO server with Garage

**Status:** design, not built · **Date:** 2026-09-26

## Why

MinIO's open-source server and `mc` are archived. dl.min.io returns 410, Docker Hub `minio/*` 404s and
quay.io `minio/*` 401s. Both controllers' MinIO pods (`apps`, `apps-beta`) run only because node
image caches still hold `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z`. This spec replaces the
server itself. Swapping the `mc mirror` helper in step pods for curl is a separate change (see
"Related").

## Choice: Garage v2.4.1

Considered: Garage, SeaweedFS, RustFS. RustFS is still `1.0.0-rc.3`. SeaweedFS targets
multi-node, small-file-heavy scale and is operationally heavier. Garage (AGPL, used unmodified)
fits a one-node, one-bucket store, and since v2.3 it bootstraps itself:

- `garage server --single-node` creates the layout on first boot. Restarts keep layout v1, so
  `Recreate` rollouts are safe. We verified this in the source (`src/garage/server.rs:200`) and by
  restarting a container that already held data.
- `--default-bucket` together with `GARAGE_DEFAULT_ACCESS_KEY` / `GARAGE_DEFAULT_SECRET_KEY` /
  `GARAGE_DEFAULT_BUCKET` creates the key and the bucket idempotently at startup. This replaces the
  bootstrap `mc mb` pod.
- `Key::import` accepts an access key of at least 8 characters from `[A-Za-z0-9._-]` and a
  printable secret of at least 16 characters. **The existing MinIO credentials qualify:** the user
  is 14 characters and the password is 48 hex characters, in both namespaces, and `pl bootstrap`
  generates the same shape. So the controller, the pipeline secrets and `publish-cli.sh` keep
  their credentials.

## Compatibility: verified locally (Docker, `dxflrs/garage:v2.4.1`, AWSSDK.S3 4.0.100.1)

| Operation | Result |
|---|---|
| `PutBucket` on an existing bucket | 409 `BucketAlreadyOwnedByYou`, which `S3BundleStore.cs:26` already catches |
| `PutObject` with the SDK defaults | **400 "Invalid payload signature"** |
| `PutObject` with `RequestChecksumCalculation = WHEN_REQUIRED` | OK, small JSON and 60 MB (byte-exact round trip) |
| `GetObject`, `ListObjectsV2`, missing key → `NoSuchKey` | OK |
| curl `--aws-sigv4 "aws:amz:us-east-1:s3"` + `UNSIGNED-PAYLOAD` PUT/GET/list | OK |
| Restart with data | OK |

**Required code change:** in `StorageConfiguration.cs`, set
`RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED` and
`ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED` on the `AmazonS3Config`.
The SDK's default aws-chunked upload with a checksum trailer is rejected by Garage. The setting is
harmless against MinIO, so it ships first, on its own.

The STS / `AssumeRoleWithWebIdentity` path (`S3Credentials.cs`) only works with MinIO. The static
credentials already take precedence in every environment. Delete it in the cleanup phase.

## Helm

New templates `garage-{deployment,service,pvc,configmap}.yaml` behind `garage.enabled`. They sit
**alongside** the MinIO templates during migration, and MinIO is removed afterwards.

- ConfigMap `garage.toml`:
  - `replication_factor = 1`
  - `db_engine = "sqlite"`
  - `metadata_dir` and `data_dir` on the PVC
  - `rpc_bind_addr [::]:3901`, `rpc_public_addr 127.0.0.1:3901`
  - `[s3_api] s3_region = "us-east-1"` (keeps the curl SigV4 scope unchanged), `api_bind_addr [::]:3900`
  - `[admin] api_bind_addr [::]:3903`
- Deployment:
  - args `server --single-node --default-bucket`, strategy `Recreate`
  - env from secret: `GARAGE_DEFAULT_ACCESS_KEY` ← `root-user`, `GARAGE_DEFAULT_SECRET_KEY` ←
    `root-password`, `GARAGE_RPC_SECRET` ← new key `rpc-secret` (32-byte hex)
  - `GARAGE_DEFAULT_BUCKET` ← `garage.bucket`
  - readiness and liveness probes: `GET :3903/health`
- Service `{release}-garage`, port 3900. PVC `{release}-garage-data`, `resource-policy: keep`,
  `local-path`, requested size 100Gi. local-path doesn't enforce the size, but the number should
  be honest; prod MinIO already holds 86 GB on its "10Gi" claim.
- Image `dxflrs/garage:v2.4.1`, **mirrored to `registry.ovea.pro:5000`** and referenced from there,
  so a Docker Hub change can't strand us the way quay did.
- Runner NetworkPolicy (Olve.Homelab `runner-hardening.yaml`, rule 4) allows the `apps`/`apps-beta`
  namespaces without a port restriction, so port 3900 needs no change.

## Data migration

Prod bucket: about 1 MB of state JSON (`configuration.json`, `jobs.json`, `promotion-state.json`,
hooks), 2.1 GB `cli/`, 84 GB `bundles/`. Beta: 404 KB.

**Copy everything, bundles included.** Re-promote redrives each step's last bundle, `JobLogService`
reads logs from under `bundles/`, and pending cascades carry bundle prefixes. Deciding which
bundles are safe to drop is its own project (retention, below). The node has 376 GB free, so
a second 86 GB copy fits.

Tool: a one-shot in-cluster pod running `rclone sync` (pinned `rclone/rclone` image, also
mirrored), with both endpoints configured through env, S3 provider `Other`, path-style. `mc` is
not an option.

## Cutover (per environment, beta first)

A pipeline deploy can't flip the endpoint, because the controller would be orchestrating its own
storage swap. Any write between the copy and the flip would be lost. So cutover is manual:

1. Deploy the chart with `garage.enabled=true` and MinIO still in use. Wait until Garage is ready
   and the key and bucket exist.
2. `rclone sync` MinIO → Garage (bulk pre-copy while live).
3. Hold the promotion gate (prod: `pl processing block 212505e7-8e30-4e3c-94e3-be8433e2c4d6`).
   Wait for in-flight jobs to finish; running step pods have the old endpoint baked in.
4. `kubectl scale deploy/olve-pipelines --replicas=0`.
5. Final `rclone sync` (small delta), then a `check` of counts and sizes.
6. `helm upgrade` by hand with `Storage__Endpoint=http://olve-pipelines-garage.<ns>:3900`, and
   commit the same value to `values*.yaml` so the next pipeline deploy is a no-op.
7. Verify:
   - pipelines, promotion state and job history are intact
   - a full pipeline run works: bundle in, logs out, `pl` download
   - a controller restart keeps everything
8. Prod only: `pl processing unblock …` **and** `pl production trigger 0a97196c-…`. Unblock alone
   doesn't promote (#34).

Rollback until cleanup: scale to 0, point `Storage__Endpoint` back at MinIO, scale up. MinIO is
untouched from step 2 on, so anything written to Garage after cutover is lost on rollback.

**Prod doesn't start until beta has run a full pipeline on Garage and survived a restart.**

## Other touch points

- `pl bootstrap`:
  - generate `rpc-secret` into the credentials secret; generate-if-absent, and also add the key to
    an existing secret that lacks it
  - wait for `{release}-garage`
  - drop `EnsureBucket` and the `mc` image
  - endpoint `http://{release}-garage.{ns}:3900`
- `pl teardown --purge-data` removes the Garage PVC.
- `values-minimal.yaml`: comment update (`garage.bucket`).
- `.pipelines/scripts/publish-cli.sh`: endpoint → `olve-pipelines-garage.apps.svc.cluster.local:3900`.
  Rename `MINIO_*` secrets in cleanup.
- Remove the unused `Testcontainers.Minio` package reference.
- Docs: `docs/operations/environment-setup.md`, README, and a superseded note on
  `2026-06-15-dedicated-minio-storage-design.md`.

## Cleanup (after both environments have run on Garage for about a week)

- Delete the MinIO templates and the `minio:` values. Delete the MinIO PVCs by hand once they're
  confirmed unneeded.
- Rename the secret `olve-pipelines-minio` → `olve-pipelines-storage` and the pipeline `MINIO_*`
  secrets.
- Delete the STS credentials path.

## Out of scope / related

- **`mc mirror` helper in step pods** (`KubernetesClient.cs:332,348`): separate change → a curl
  image with a list-then-loop SigV4 script. Until that lands, step pods still depend on the cached
  `quay.io/minio/mc` image; mirroring it to `registry.ovea.pro` is the stopgap.
- **Bundle retention**: nothing deletes bundles (84 GB and growing). Garage supports lifecycle
  expiration, but a blanket age rule would expire the "last bundle" that re-promote needs, so
  retention has to be app-driven.
- The homelab MinIO at `minio.ovea.pro` and `olve-trains-minio`.
