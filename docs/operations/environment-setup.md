# Environment setup (bootstrapping a self-deployable instance)

This is the manual runbook for the homelab environments (beta, prod). A standalone install on
any other cluster is `pl bootstrap -n <ns>` (and `pl teardown`), which automates the same steps
with the chart's minimal profile. Everything except the storage credentials Secret is in the
helm chart and applied by the normal deploy.

K8s access is `ssh oliver@bulwark-m2` then `kubectl` (k3s on the host).

## Storage: dedicated in-cluster Garage

Each environment runs its own single-node [Garage](https://garagehq.deuxfleurs.fr/) S3 store
(separate from the shared `minio.ovea.pro`), deployed by the helm chart (`garage.enabled`), with
**static credentials** from a k8s Secret. The controller and the runner Jobs both talk to it over
plain HTTP inside the cluster (`http://olve-pipelines-garage.<ns>:3900`). Garage replaced the
per-environment MinIO in 2026-09 (MinIO's open-source images were archived); see
`docs/superpowers/specs/2026-09-26-garage-storage-design.md`.

| Environment | Namespace  | Bucket                | Endpoint                                      |
|-------------|------------|-----------------------|-----------------------------------------------|
| beta        | `apps-beta`| `olve-pipelines-beta` | `http://olve-pipelines-garage.apps-beta:3900` |
| prod        | `apps`     | `olve-pipelines`      | `http://olve-pipelines-garage.apps:3900`      |

Storage is split across two PVCs: `olve-pipelines-garage-meta` (Garage's sqlite metadata, node
key and layout; `local-path` SSD) and `olve-pipelines-garage-data` (object data: snapshots,
bundles, job logs, `pl` binaries; the `bulk` HDD). Both carry `helm.sh/resource-policy: keep`.

### 1. Create the storage credentials Secret (before the first deploy)

The chart references an external Secret `olve-pipelines-minio` (the name predates Garage) with
keys `root-user` / `root-password` / `rpc-secret`. Garage imports `root-user`/`root-password` as
its S3 access key at startup; the controller reads them as `Storage__AccessKey` /
`Storage__SecretKey`; `rpc-secret` is Garage's internal cluster secret. Create it idempotently
(set `NS` accordingly):

```sh
NS=apps-beta
ssh oliver@bulwark-m2 "kubectl create secret generic olve-pipelines-minio -n $NS \
  --from-literal=root-user=olve-pipelines \
  --from-literal=root-password=$(openssl rand -hex 24) \
  --from-literal=rpc-secret=$(openssl rand -hex 32) \
  --dry-run=client -o yaml | kubectl apply -f -"
```

Garage requires the access key to be at least 8 characters of `[A-Za-z0-9._-]` and the secret at
least 16 printable characters. Once Garage has imported a key, its secret can't be changed in
place (Garage refuses to start with a different `GARAGE_DEFAULT_SECRET_KEY`), so don't rotate
`root-password` without also resetting Garage's metadata.

### 2. Deploy

Push the helm change (or run the pipeline). The deploy step `helm upgrade --install`s the chart,
which brings up Garage and points the controller at it. Garage runs `server --single-node
--default-bucket`: on first boot it lays out a one-node cluster and creates the access key and
the bucket (`garage.bucket`), so there is no separate bucket step. The controller crashloops
briefly by design until Garage answers (it refuses to start rather than risk overwriting state).

### 3. Verify

```sh
# beta
curl -skf https://pipelines-beta.ovea.pro/api/ready && echo READY
curl -sk  https://pipelines-beta.ovea.pro/api/pipelines     # should respond (empty list is fine)
ssh oliver@bulwark-m2 "kubectl -n apps-beta exec deploy/olve-pipelines-garage -- /garage bucket list"
```

Then re-bind the self-deploy pipeline if needed (see the `setup-pipeline` skill) and run a
deploy to confirm a bundle round-trips through Garage.

## Migrating from MinIO

An install that still stores its data in MinIO (`olve-pipelines-minio` Deployment, no
`olve-pipelines-garage`) must be migrated rather than re-bootstrapped; `pl bootstrap` refuses to
run over it. This is how beta and prod were moved (2026-09-26/27):

1. Add `rpc-secret` to the existing credentials Secret (the existing `root-user`/`root-password`
   are valid Garage keys and are reused as-is).
2. Deploy the chart with `garage.enabled=true` while `Storage__Endpoint` still points at MinIO,
   so both run side by side.
3. Bulk pre-copy while live: a one-shot pod running `rclone sync src:<bucket> dst:<bucket>` with
   both remotes configured through env (`RCLONE_CONFIG_SRC_*`, provider `Minio`, endpoint
   `http://olve-pipelines-minio.<ns>:9000`; `RCLONE_CONFIG_DST_*`, provider `Other`, region
   `us-east-1`, path-style, endpoint `http://olve-pipelines-garage.<ns>:3900`), credentials via
   `secretKeyRef` from the Secret.
4. Hold the gate (prod: `pl processing block 212505e7-8e30-4e3c-94e3-be8433e2c4d6`) and wait for
   in-flight jobs on **all** pipelines to finish; running step pods have the old endpoint baked in.
5. `kubectl scale deploy/olve-pipelines --replicas=0`, run the `rclone sync` again (the delta),
   then `rclone check` for 0 differences.
6. `helm upgrade --reuse-values --set config.Storage__Endpoint=http://olve-pipelines-garage.<ns>:3900`
   by hand (the controller can't deploy its own storage swap), and commit the same value.
7. Verify state loaded (`Loaded configuration: N pipelines…` in the controller log), a restart,
   and a full pipeline run; then `pl processing unblock …` **and** `pl production trigger …`
   (unblock alone doesn't promote, #34).

Rollback until MinIO is removed: scale the controller to 0, point `Storage__Endpoint` back at
MinIO, scale up. Anything written only to Garage since the cutover is lost.

## Notes

- **The Garage data PVC is the precious volume.** It holds snapshots (all pipeline config and
  state) + bundles. Back up the host dir under the `bulk` storage (`/mnt/minio-data/bulk/...`).
  The metadata PVC is needed too: without it Garage can't find its objects.
- **STS storage auth is gone but the OIDC Secret stays.** `Storage__AuthUrl/ClientId/
  ClientSecret` are still required because `KubernetesConfiguration` reuses them for OpenBao
  auth to reach the cluster API. The static storage creds take precedence for storage.
- **Pin the Garage image** (`garage.image` in `values.yaml`), currently `dxflrs/garage:v2.4.1`.
  Upgrades follow Garage's release notes; minor versions have been drop-in.
- **Step pods move bundles with curl**, not `mc`: the `s3-download`/`s3-upload` helpers run
  `S3SyncScript` on `Kubernetes__S3HelperImage` (`curlimages/curl`).
