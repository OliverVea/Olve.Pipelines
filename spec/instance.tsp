import "@typespec/http";

using Http;

namespace Pipelines;

/** Liveness. */
model HealthResponse {
  status: string;

  /** The image this instance runs, when its deployer says (`Deployment:Image`). */
  image?: string;

  /** The server's informational version. */
  version?: string;
}

/** The login settings the web UI and `pl login` use. */
model FrontendConfig {
  oidcAuthority?: string;
  oidcClientId?: string;

  /** Space-separated scopes to request; `groups` only when `Auth:Allow:Orgs` needs the claim. */
  oidcScopes: string;
}

/** Persisted state is not loaded yet. */
model NotReady {
  @statusCode statusCode: 503;
}

@tag("Instance")
@route("/api")
interface Instance {
  /** Liveness: `status`, `image` (`Deployment:Image`, set by the compose deployer) and the server `version`. */
  @get @route("health") health(): HealthResponse;

  /** Readiness: 200 once persisted state is loaded, 503 before. */
  @get @route("ready") ready(): OkResponse | NotReady;

  /** The OIDC authority, client id and scopes the web UI and `pl login` use. */
  @get @route("config/frontend") frontendConfig(): FrontendConfig;
}
