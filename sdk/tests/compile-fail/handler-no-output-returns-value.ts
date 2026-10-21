import { implement } from "@olve/pipelines";
import { summary } from "../fixtures";

// summary declares no output model: a handler may return nothing or skip, not a value.
// @ts-expect-error -- expect: is not assignable to type 'HandlerResult<
implement(summary, async () => ({ ok: true }));
