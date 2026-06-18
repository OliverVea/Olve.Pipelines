# Self-testing & GitOps deploy — working notes

Internal working notes (repo-only; **not** served at `/docs`, which is `docs/setup/**` only).
Captures the in-flight self-testing effort, the GitHub rate-limit learnings, and the design
direction for replacing polling with webhooks. This is a living memo, not user documentation —
prune or promote sections to `docs/setup/` once they stabilise.

Last updated: 2026-06-18.

---

## 1. Where the pipeline tests itself (status)

The CD pipeline tests itself through `.pipelines/config.yaml` instead of GitHub Actions (the
`.github/` workflow was deleted 2026-06-17). Two layers:

- **Code-only tests** → a `code-test` **production step** running in parallel with
  `build-and-package` (NOT inside the Docker build, so they don't serialise). Runs the unit suite
  via `scripts/test.sh` on the dotnet SDK image. A failure fails the production job group, which
  gates the whole processing cascade (verified: `JobGroupCompletionService` fires `OnGroupCompleted`
  only when every job in the group is `Done`). **Done and verified green in prod.**

- **Server-dependent tests** → run against the live **beta** instance over HTTP (Testcontainers
  dropped). The integration suite (`test/Olve.Pipelines.IntegrationTests`) now targets beta:
  `AppFixture` points at `BETA_BASE_URL` (default `https://pipelines-beta.ovea.pro`) and mints an
  Authentik client-credentials token when `BETA_OIDC_CLIENT_SECRET` is set. `BetaGuard.SkipIfNoBeta`
  skips these when no beta is configured so a plain local `dotnet test` doesn't fail.
  **Verified green against beta (14 passed, 1 skipped)** — the first-ever end-to-end run, including
  real K8s execution. The gating **beta-e2e processing step is not yet wired** into
  `.pipelines/config.yaml`.

### Step 2 remaining work (the gating step)

The user chose **gate prod**: the beta-e2e step goes BETWEEN `deploy-beta` and `deploy`, so a
failed e2e blocks the prod deploy. Three prerequisites, in order:

1. **Authenticate the 3 permanent prod-instance bindings** (`olve-pipelines`, `olve-homelab`,
   `questionbank`) via `PATCH /api/pipelines/{id}/binding` with `{"credentialsSecret":"GITHUB_TOKEN"}`.
   Moves their config fetches off the unauthenticated 60/hr limit onto the 5000/hr token bucket.
   The PATCH endpoint is new (commit `3f20576`) — needs to be live on the **prod** instance
   (`pipelines-private.ovea.pro`) first, since that's where these bindings live.
2. **Add the Authentik OIDC client secret** to the prod self-deploy pipeline's k8s secret via the
   secrets API (`PUT /api/pipelines/{id}/secrets/{name}`), so the e2e step (running as a K8s Job in
   `olve-runners`) can mint a beta token. Suggested key: `BETA_OIDC_CLIENT_SECRET` (what `AppFixture`
   reads). Secret value: see §4.
3. **Add the gating `beta-e2e` processing step** + a `scripts/beta-e2e.sh` that fetches the repo
   (via `olve_fetch_repo` from `olve-lib.sh`), exports `BETA_BASE_URL` + `BETA_OIDC_CLIENT_SECRET`,
   and runs `dotnet test --project test/Olve.Pipelines.IntegrationTests -p:RunIntegrationTests=true
   -p:RunUnitTests=false`. The step image needs the dotnet SDK.

Determinism guardrails (so a flaky gate doesn't re-create the "deploy silently doesn't land"
failure class step 1 killed): tests reconcile-now after binding (no 5-min poll wait — see §3),
clean up bindings in `finally` (see §2), mint-if-present token, bounded timeouts.

---

## 2. GitHub rate-limit learnings (60/hr/IP, the unauthenticated bucket)

This bit us repeatedly. Root causes, in order of impact:

1. **Leaked test bindings (the disease).** Two `GitOpsBindingTests` created pipelines bound to
   `olve-test/nonexistent-*` repos and never deleted them. Harmless under Testcontainers (container
   discarded), but on shared beta every leaked binding is polled **every cycle forever**, each poll a
   counted GitHub request. Several debug runs left ~12 leaked pipelines draining the quota
   continuously. **Fixed:** both tests now `DeletePipeline` in a `finally`. Rule of thumb: on a
   shared instance, any test that creates a binding MUST delete it, or it polls GitHub forever.
2. **The self-deploy's own build fetches.** `build.sh` pulls the repo tarball from GitHub on every
   deploy — counted requests against the same IP.
3. **Baseline poll load.** 3 permanent bindings × 1 branch-head check per 5-min cycle ≈ 36/hr,
   leaving only ~24/hr for everything else. Config fetches are a free 304 when unchanged; the full
   tree+blobs fetch only happens on a SHA change. So a *clean*, short-lived test binding costs only
   ~3 calls — the per-test math was fine; the leaks + repeated runs were not.

**Chicken-and-egg observed 2026-06-18:** the binding-auth fix (which frees the limit) is in commit
`3f20576`, but it couldn't deploy because the limit was exhausted — the deploy poll itself needs
GitHub. Resolution is just to wait for the hourly reset, let the deploy through once, then
authenticate the bindings so it stops recurring.

**Mitigations:** (a) test cleanup [done]; (b) authenticate the 3 permanent bindings [pending, §1.1];
(c) `Reconcile:PollIntervalSeconds` can be raised to cut baseline calls (config-only, slows deploy
responsiveness for all pipelines) — not chosen.

---

## 3. Reconcile is poll-only — and the reconcile-now endpoint

`DeployPollService` reconciles purely on a timer (`ExecuteAsync` → `Task.Delay(PollInterval)`,
default **5 min**, `ReconcileOptions.PollInterval`). There is **no on-create reconcile kick** and the
beta poll interval can't be reconfigured per-test. So a "bind then wait 90s for shape" test flakes
badly against a 5-min poll.

**Fix (commit `12aad80`):** `POST /api/pipelines/{id}/binding/reconcile` runs one reconcile+deploy
cycle immediately, off-schedule, reusing `DeployPollService.PollBindingAsync` (no behavioural drift).
`DeployPollService` is now registered as a singleton + hosted service so the endpoint and the loop
share the per-binding ETag cache (`ConcurrentDictionary` for the shared access). Tests call it right
after binding so shape materialises in seconds. Also useful operationally ("reconcile now" button).

---

## 4. Auth & secrets reality

- **Beta DOES enforce auth** on non-anonymous endpoints. Earlier assumption ("beta is effectively
  unauthenticated") was wrong — it generalised from an anonymous GET. Reality: **mutating endpoints**
  (`with-repo`, `binding/reconcile`, `binding` PATCH, `DeletePipeline`, set-secret) **require a valid
  token**; GET reads are `.AllowAnonymous()`. Verified: unauth `POST /api/pipelines/with-repo` → 401;
  unauth `GET /api/pipelines` → 200. So the token is **load-bearing** for the e2e suite.
- **Minting a beta token (client_credentials, verified working):** Authentik client
  `olve-pipelines-beta` (confidential; same client used for storage/OpenBao). Secret in k8s
  `infra-beta/authentik-oidc-secrets`, key `olve-pipelines-client-secret`. Request:
  `POST https://auth-beta.ovea.pro/application/o/token/` with `grant_type=client_credentials`,
  `client_id=olve-pipelines-beta`, `client_secret=…`, `scope=openid profile email`. Minted token has
  `aud=olve-pipelines-beta` (string, not array) and `iss=…/application/o/olve-pipelines/` — exactly
  what the API validates.
- **`UnauthenticatedRequest_Returns401` is `[Skip]`-marked** in `PipelineTests` with "Auth not
  enforced on GET /api/pipelines — needs investigation". That GET *is* anonymous by design, so the
  test as written can't pass; revisit whether it should target a mutating endpoint instead.
- **ACTION — rotate leaked secrets.** On 2026-06-17 the prod self-deploy pipeline secret
  (`olve-pipeline-0a97196c…`, ns `olve-runners`) had its **`GITHUB_TOKEN` and `SSH_PRIVATE_KEY`**
  values printed to an agent transcript. Treat both as compromised: rotate the GitHub PAT and the
  homelab SSH keypair. When querying k8s secrets, request key names only, never the raw `.data` blob.

---

## 5. Design direction: replace polling with GitHub webhooks (proposed, not started)

The whole rate-limit class of problems is a symptom of **poll-based, often-unauthenticated GitHub
fetching**. A push-based **GitHub webhook** would eliminate it: GitHub notifies the service on commit
instead of the service polling every 5 min.

What it would touch:
- A webhook receive endpoint (`POST /api/webhooks/github`) with **HMAC signature verification**
  (`X-Hub-Signature-256` against a shared secret) — reject unsigned/forged calls.
- Map the payload (repo + branch) to the bound pipeline(s) and trigger the **same** reconcile+deploy
  path the poll uses (`PollBindingAsync` / `ReconcileNowAsync` are already factored for this).
- Ingress + a webhook secret per environment; register the webhook on the GitHub repo(s).
- Likely **retire or demote `DeployPollService`** to a slow safety-net backstop (e.g. hourly) rather
  than the primary mechanism.

Benefits beyond rate limits: near-instant deploys (no up-to-5-min lag) and the reconcile-timing
problem disappears (shape materialises on push). The reconcile-now endpoint stays useful as a manual
trigger. **Treat this as its own focused effort** — it changes the live deploy mechanism and deserves
deliberate design, not a tack-on. Until then, the targeted mitigations in §2 keep things green.
