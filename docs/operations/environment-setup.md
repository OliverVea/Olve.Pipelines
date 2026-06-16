# Environment setup (bootstrapping a self-deployable instance)

This is the interim, manual runbook to get an Olve.Pipelines environment into a state where it
can self-deploy. It will be subsumed by the `pl` CLI self-installation
(`docs/superpowers/specs/2026-06-15-pl-cli-self-installation-design.md`). Everything except the
MinIO credentials Secret and the initial bucket is in the helm chart and applied by the normal
deploy.

K8s access is `ssh oliver@bulwark-m2` then `kubectl` (k3s on the host).

## Storage: dedicated in-cluster MinIO

Each environment runs its own MinIO (separate from the shared `minio.ovea.pro`), deployed by
the helm chart (`minio.enabled`), with **static credentials** from a k8s Secret. The controller
and the runner Jobs both talk to it over plain HTTP inside the cluster
(`http://olve-pipelines-minio.<ns>:9000`).

| Environment | Namespace  | Bucket                | Endpoint                                   |
|-------------|------------|-----------------------|--------------------------------------------|
| beta        | `apps-beta`| `olve-pipelines-beta` | `http://olve-pipelines-minio.apps-beta:9000` |
| prod        | `apps`     | `olve-pipelines`      | `http://olve-pipelines-minio.apps:9000`    |

### 1. Create the MinIO credentials Secret (before the first deploy)

The chart references an external Secret `olve-pipelines-minio` with keys `root-user` /
`root-password`. MinIO reads them as its root user; the controller reads them as
`Storage__AccessKey` / `Storage__SecretKey`. Create it idempotently (set `NS` accordingly):

```sh
NS=apps-beta
USER=olve-pipelines
PASS=$(openssl rand -hex 24)
ssh oliver@bulwark-m2 "kubectl create secret generic olve-pipelines-minio -n $NS \
  --from-literal=root-user=$USER \
  --from-literal=root-password=$PASS \
  --dry-run=client -o yaml | kubectl apply -f -"
```

Record `PASS` somewhere safe (it is the storage root credential). Losing it just means
recreating the Secret and restarting MinIO + the controller.

### 2. Deploy

Push the helm change (or run the pipeline). The deploy step `helm upgrade --install`s the
chart, which brings up the MinIO Deployment/Service/PVC and points the controller at it. The
controller crashloops by design until MinIO is up and the bucket exists — that's expected until
step 3.

### 3. Create the bucket

The official `minio/minio` image does **not** auto-create buckets. Create it once after MinIO
is running (the controller's `/api/ready` stays 503 until this is done):

```sh
NS=apps-beta
BUCKET=olve-pipelines-beta
ssh oliver@bulwark-m2 "kubectl run minio-mb -n $NS --rm -i --restart=Never \
  --image=minio/mc:latest --env=MC_HOST_local=http://\$(kubectl get secret olve-pipelines-minio -n $NS -o jsonpath='{.data.root-user}' | base64 -d):\$(kubectl get secret olve-pipelines-minio -n $NS -o jsonpath='{.data.root-password}' | base64 -d)@olve-pipelines-minio.$NS:9000 \
  -- mc mb --ignore-existing local/$BUCKET"
```

(Or `kubectl exec` into the MinIO pod and use `mc`, or `mc` from any host with cluster
access.) Once the bucket exists, the controller loads FirstRun and `/api/ready` returns 200.

### 4. Verify

```sh
# beta
curl -skf https://pipelines-beta.ovea.pro/api/ready && echo READY
curl -sk  https://pipelines-beta.ovea.pro/api/pipelines     # should respond (empty list is fine)
```

Then re-bind the self-deploy pipeline if needed (see the `setup-pipeline` skill) and run a
deploy to confirm a bundle round-trips through the new MinIO.

## Notes

- **Cutover is a FirstRun reset.** A fresh MinIO is empty, so config self-heals from
  `.pipelines/config.yaml` via GitOps reconcile, but bundles and job history are lost. This is
  accepted (history is disposable).
- **The MinIO PVC is the precious volume.** It holds snapshots + bundles. Back up the host dir
  under k3s local-path storage (`/var/lib/rancher/k3s/storage/...`).
- **STS storage auth is gone but the OIDC Secret stays.** `Storage__AuthUrl/ClientId/
  ClientSecret` are still required because `KubernetesConfiguration` reuses them for OpenBao
  auth to reach the cluster API. The static MinIO creds simply take precedence for storage.
- **Pin the MinIO image** (`minio.image` in `values.yaml`) — currently
  `RELEASE.2025-09-07T16-13-09Z`.
