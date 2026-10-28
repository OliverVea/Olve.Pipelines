// The API contract. One file per area is imported here as the areas move over (docs/design/spec-first.md).
import "@typespec/http";
import "./errors.tsp";
import "./instance.tsp";
import "./pipelines.tsp";
import "./runs.tsp";
import "./secrets.tsp";
import "./versions.tsp";

using Http;

@service(#{ title: "Olve.Pipelines" })
namespace Pipelines;
