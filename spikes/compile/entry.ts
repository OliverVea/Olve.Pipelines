// Harness entrypoint compiled into the single executable.
// Imports models + pipeline eagerly; handlers only via the lazy registry (describe never loads them).
import pipeline from "./example/pipeline";
import { handlers, handlerHashes } from "./gen/handlers";
import { harness } from "./sdk/main";

await harness({ pipeline, handlers: handlers as any, handlerHashes });
