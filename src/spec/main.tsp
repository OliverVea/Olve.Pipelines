// The API contract. One file per area is imported here as the areas move over (docs/design/spec-first.md).
import "@typespec/http";
import "./instance.tsp";

using Http;

@service(#{ title: "Olve.Pipelines" })
namespace Pipelines;
