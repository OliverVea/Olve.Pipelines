import "@typespec/http";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

namespace Pipelines;

/** First event of every connection: the id a reconnect should resume from. */
model StreamHello {
  type: "hello";
  lastEventId: string | null;
}

/** The client reconnected with a stale `Last-Event-ID` (or across a restart): refetch everything. */
model StreamResync {
  type: "resync";
  reason: string;
}

/** Keep-alive while idle. */
model Ping {
  type: "ping";
}

/** A run was created, changed (status, held, resumed) or deleted (`status: "Deleted"`). */
model RunChanged {
  type: "run";
  @format("uuid") pipelineId: string;
  run: int32;
  status: string;
}

/** An attempt was created or changed status. */
model AttemptChanged {
  type: "attempt";
  @format("uuid") pipelineId: string;
  run: int32;
  step: string;
  attempt: int32;
  status: string;
}

/** `added`, `active` (switched, or a switch finished), `draining` (a switch to `version` started), `deleted`, `none`. */
model VersionChanged {
  type: "version";
  @format("uuid") pipelineId: string;
  version: int32 | null;
  change: string;
}

/** A step was blocked or unblocked: the full blocked list. */
model BlockedStepsChanged {
  type: "blocked";
  @format("uuid") pipelineId: string;
  blockedSteps: string[];
}

/** The binding changed: `reconciling`, then `Success` or `Error`; `removed`. */
model BindingChanged {
  type: "binding";
  @format("uuid") pipelineId: string;
  state: string;
}

/** `added`, `updated`, `removed`. */
model PipelineChanged {
  type: "pipeline";
  @format("uuid") pipelineId: string;
  change: string;
}

/** A config snapshot was saved. */
model ConfigChanged {
  type: "config";
  @format("uuid") pipelineId: string;
  snapshot: int32;
}

/** A version started waiting for valid config, or none waits any more (`null`). */
model PendingChanged {
  type: "pending";
  @format("uuid") pipelineId: string;
  version: int32 | null;
}

/** Run starts are queued during a switch with `--cancel-running`, or the queue emptied. */
model QueuedChanged {
  type: "queued";
  @format("uuid") pipelineId: string;
  queued: boolean;
}

/** A pipeline secret was set, deleted or copied in (names only, never values). */
model SecretsChanged {
  type: "secrets";
  @format("uuid") pipelineId: string;
}

/** A poll trigger saw a new value. */
model PollChanged {
  type: "poll";
  @format("uuid") pipelineId: string;
}

/** A change notice: the event name says what changed, the data just enough to know what to reload. */
@TypeSpec.Events.events
union ChangeEvent {
  @TypeSpec.Events.contentType("application/json") hello: StreamHello,
  @TypeSpec.Events.contentType("application/json") resync: StreamResync,
  @TypeSpec.Events.contentType("application/json") ping: Ping,
  @TypeSpec.Events.contentType("application/json") run: RunChanged,
  @TypeSpec.Events.contentType("application/json") attempt: AttemptChanged,
  @TypeSpec.Events.contentType("application/json") version: VersionChanged,
  @TypeSpec.Events.contentType("application/json") blocked: BlockedStepsChanged,
  @TypeSpec.Events.contentType("application/json") binding: BindingChanged,
  @TypeSpec.Events.contentType("application/json") pipeline: PipelineChanged,
  @TypeSpec.Events.contentType("application/json") config: ConfigChanged,
  @TypeSpec.Events.contentType("application/json") pending: PendingChanged,
  @TypeSpec.Events.contentType("application/json") queued: QueuedChanged,
  @TypeSpec.Events.contentType("application/json") secrets: SecretsChanged,
  @TypeSpec.Events.contentType("application/json") poll: PollChanged,
}

/** Log text to append; with `reset` the text replaces what was shown. */
model LogChunk {
  type: "log";
  text: string;
  reset: boolean;
}

/** The attempt finished: its final status. */
model LogStreamEnd {
  type: "end";
  status: string;
}

/** Keep-alive while the attempt is quiet. */
model LogPing {
  type: "ping";
}

/** An attempt's log as it grows, ending with `end` once the attempt is terminal. */
@TypeSpec.Events.events
union LogEvent {
  @TypeSpec.Events.contentType("application/json") log: LogChunk,
  @TypeSpec.Events.contentType("application/json") end: LogStreamEnd,
  @TypeSpec.Events.contentType("application/json") ping: LogPing,
}

@tag("Events")
@route("/api/events")
interface Events {
  /**
   * Change notifications for one pipeline (`pipeline`) or all, as server-sent events. Ids are `<boot>-<seq>`;
   * there is no replay: a reconnect with a stale `Last-Event-ID` gets `resync`. A client that falls 256 events
   * behind is disconnected. Streams end when the service shuts down.
   */
  @get stream(
    @query @format("uuid") pipeline?: string,
    @header("Last-Event-ID") lastEventId?: string,
  ): SSEStream<ChangeEvent>;
}
