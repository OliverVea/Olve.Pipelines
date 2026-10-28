// The API contract (docs/design/spec-first.md): one file per area. Every operation needs a bearer token
// unless it says `@useAuth(NoAuth)`; the OpenAPI document an instance serves at /docs/openapi.json is
// compiled from these files.
import "@typespec/http";
import "./bindings.tsp";
import "./config.tsp";
import "./errors.tsp";
import "./events.tsp";
import "./instance.tsp";
import "./pipelines.tsp";
import "./runs.tsp";
import "./secrets.tsp";
import "./steps.tsp";
import "./versions.tsp";

using Http;

@service(#{ title: "Olve.Pipelines" })
@useAuth(BearerAuth)
namespace Pipelines;
