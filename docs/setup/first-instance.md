# A First Real Instance

[← Index](index.md) · [Running an Instance](self-hosting.md)

From a Kubernetes cluster to an instance people log in to with GitHub, behind an Ingress with TLS,
that hears about pushes and pull requests from GitHub webhooks. Check the cluster against the
[requirements](self-hosting.md#requirements) first. The examples use the namespace `pipelines`, the
release `olve-pipelines` and the host `pipelines.example.com`.

## 1. A GitHub OAuth app for Dex

The instance runs its own [Dex](self-hosting.md#bundled-dex) with GitHub as the upstream. In GitHub,
under **Settings → Developer settings → OAuth Apps → New OAuth App**, set:

| Field | Value |
|---|---|
| Homepage URL | `https://pipelines.example.com` |
| Authorization callback URL | `https://pipelines.example.com/dex/callback` |

Keep its client id and generate a client secret.

## 2. The Secrets the chart reads

```bash
kubectl create namespace pipelines

# Garage: its access key, secret key and cluster RPC secret (32 bytes, hex)
kubectl -n pipelines create secret generic olve-pipelines-minio \
  --from-literal=root-user=olve-pipelines \
  --from-literal=root-password="$(openssl rand -hex 24)" \
  --from-literal=rpc-secret="$(openssl rand -hex 32)"

# Dex: the OAuth app from step 1
kubectl -n pipelines create secret generic olve-pipelines-dex \
  --from-literal=github-client-id=<client id> \
  --from-literal=github-client-secret=<client secret>
```

## 3. `values.yaml`

```yaml
image:
  tag: "2026.11.17.297"            # a release: its version without the leading v

auth:
  mode: dex
  issuer: https://pipelines.example.com/dex     # the same as dex.issuer
  clientId: olve-pipelines                      # the same as dex.clientId (its default)
  allow:
    users: [your-github-login]                  # and/or orgs, subjects, emails

dex:
  enabled: true
  issuer: https://pipelines.example.com/dex

ci:
  authors: [your-github-login]                  # whose pull requests get CI

ingress:
  enabled: true
  host: pipelines.example.com
  clusterIssuer: letsencrypt                    # a cert-manager ClusterIssuer
  tls:
    enabled: true
    secretName: olve-pipelines-tls

config:
  Webhooks__PublicBaseUrl: "https://pipelines.example.com"
  Links__BaseUrl: "https://pipelines.example.com"
  # Kubernetes__RuntimeClassName: "gvisor"      # only if the cluster has this RuntimeClass

# Dex is served under /dex of the same host. The chart's Ingress routes the host to the server
# only, so this one routes /dex to the Dex Service (<release>-dex, port 5556).
extraObjects:
  - apiVersion: networking.k8s.io/v1
    kind: Ingress
    metadata:
      name: olve-pipelines-dex
    spec:
      tls:
        - secretName: olve-pipelines-tls
          hosts: [pipelines.example.com]
      rules:
        - host: pipelines.example.com
          http:
            paths:
              - path: /dex
                pathType: Prefix
                backend:
                  service:
                    name: olve-pipelines-dex
                    port:
                      number: 5556
```

- **Allow-list.** GitHub lets anyone sign in to Dex, so the chart refuses `dex` without
  `auth.allow`. `users` are GitHub logins, `orgs` are orgs or `org:team` (GitHub then asks each user
  for `read:org`). A login can be renamed and taken by someone else; subjects can't
  ([`Auth:Allow:*`](self-hosting.md#server-configuration)).
- **Ingress.** The chart's Ingress names no IngressClass, so the cluster's default class serves it;
  set another through `ingress.annotations`. It always carries the
  `external-dns.alpha.kubernetes.io/target` annotation, from `ingress.externalDnsTarget` (empty by
  default). Without cert-manager, leave `clusterIssuer` empty and create the TLS Secret
  `olve-pipelines-tls` yourself (`kubectl -n pipelines create secret tls …`). The extra Ingress
  carries no issuer annotation: it reuses the certificate of the chart's.
- **Names.** `extraObjects` is not templated: with another release name, change `olve-pipelines-dex`
  to `<release>-dex`.

## 4. Webhooks or polling

The service polls every binding's branch every 15 minutes (`Reconcile:PollIntervalSeconds`), so
pushes deploy without webhooks, only later. Pull requests need webhooks: without them, a binding
reads its pull requests only when the server starts and when the binding is created or moves to
another branch, so pull request CI, the merge queue and previews don't follow GitHub.

- **The host is reachable from the internet:** `Webhooks__PublicBaseUrl` is the host, as above.
  Each binding whose token may manage webhooks gets a hook for `push` and `pull_request` events,
  delivered to `/api/webhooks/binding/<binding>/github`.
- **The host is private:** turn on the chart's public Ingress, which routes only the webhook
  receivers (`/api/webhooks/binding` and `/api/hooks`), and point `Webhooks__PublicBaseUrl` at it.
  `Links__BaseUrl` stays the private host, so commit statuses link to the UI:

  ```yaml
  ingress:
    public:
      enabled: true
      host: pipelines-hooks.example.com
      clusterIssuer: letsencrypt
      ingressClassName: ""               # the internet-facing IngressClass, if not the default
  config:
    Webhooks__PublicBaseUrl: "https://pipelines-hooks.example.com"
    Links__BaseUrl: "https://pipelines.example.com"
  ```

- **Polling only:** leave `Webhooks__PublicBaseUrl` out.

## 5. Install and log in

Each [release](https://github.com/OliverVea/Olve.Pipelines/releases) publishes its chart, which runs
that release's server. Its version is the release's in SemVer: release `2026.11.17.297` is chart
version `20261117.297.0`.

```bash
helm upgrade --install olve-pipelines oci://ghcr.io/olivervea/charts/olve-pipelines \
  --version <chart version> -n pipelines -f values.yaml
kubectl -n pipelines rollout status deploy/olve-pipelines
```

Install `pl` from the [GitHub release](cli.md#installing), then log in through Dex:

```bash
pl login --api-url https://pipelines.example.com      # add --device on a box without a browser
pl pipeline list
```

A login outside the allow-list gets 403, and the server logs a warning with its login and subject.

## 6. A first pipeline

[Getting Started](getting-started.md) puts a repository under CD. Give its binding a GitHub token
with the [permissions](binding-and-reconcile.md#the-github-token) for what it uses: webhooks,
commit statuses and the merge queue each need more than reading the repository.

Retention runs in dry-run mode until `Retention__DryRun` is `"false"`
([Retention](runs-and-operations.md#retention)).
