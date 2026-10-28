import "@typespec/http";

using Http;

namespace Pipelines;

enum RunStatus {
  Running,
  Succeeded,
  Failed,
  Cancelled,
  Superseded,
}

enum RunTriggerKind {
  Push,
  Poll,
  Manual,
  Webhook,
}

/** A run as listed (no input, no layout). Links to other runs are by number. */
model RunSummary {
  @format("uuid") id: string;
  number: int32;
  status: RunStatus;
  triggerKind: RunTriggerKind;
  versionNumber: int32;
  versionStamp: string;
  createdAt: utcDateTime;
  finishedAt: utcDateTime | null;

  /** The blocked step holding the run, when it is held. */
  heldAt: string | null;

  supersededByNumber: int32 | null;
  rerunOfNumber: int32 | null;
  configSnapshot?: int32 | null;
}
