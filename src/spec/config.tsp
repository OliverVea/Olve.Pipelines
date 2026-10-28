import "@typespec/http";
import "./errors.tsp";
import "./pipelines.tsp";

using Http;

namespace Pipelines;

/** One saved config document, without its content (`config #number`). */
model ConfigSnapshotSummary {
  number: int32;
  author: string;
  createdAt: utcDateTime;

  /** The snapshot this one restored, for a rollback. */
  restoredFrom: int32 | null;
}

/** The config document as saved: the YAML and its snapshot (null before the first save, with empty `yaml`). */
model ConfigDocument {
  snapshot: ConfigSnapshotSummary | null;
  yaml: string;
}

/** The config document checked against one version. */
model ConfigCheck {
  version: int32;
  valid: boolean;

  /** Valid, every referenced secret set, nothing unmet: the version can run on it. */
  satisfied: boolean;

  problems: string[];
  referencedSecrets: string[];
  missingSecrets: string[];
  changedDefaults: ChangedDefault[];

  /** The effective config (canonical JSON) when valid. */
  effective: string | null;

  /** Required entries without a value. */
  unmet: string[];
}

/** A save: the new snapshot, its checks against the active and the pending version, and the version it activated, if any. */
model ConfigSaveResult {
  snapshot: ConfigSnapshotSummary;
  active: ConfigCheck | null;
  pending: ConfigCheck | null;
  activatedVersion: int32 | null;
}

model SetConfig {
  yaml: string;
}

model RollbackConfig {
  /** The snapshot whose content is saved again as the newest. */
  snapshot: int32;
}

@tag("Config")
@route("/api/pipelines/{pipelineId}/config")
interface Config {
  /** The document as saved and its snapshot; `snapshot` gets an older one. */
  @get get(@path @format("uuid") pipelineId: string, @query snapshot?: int32): ConfigDocument | NotFound;

  /** Saves `yaml` as the next snapshot. 400 with each problem when the active version rejects it. */
  @put set(@path @format("uuid") pipelineId: string, @body body: SetConfig): ConfigSaveResult | NotFound | BadRequest | Conflict;

  /** Every snapshot, newest first. */
  @get @route("history") history(@path @format("uuid") pipelineId: string): ConfigSnapshotSummary[] | NotFound;

  /** Saves snapshot `snapshot`'s content again as the newest. */
  @post @route("rollback") rollback(@path @format("uuid") pipelineId: string, @body body: RollbackConfig): ConfigSaveResult | NotFound | BadRequest | Conflict;

  /** The current document against `version` (default: the pending version, else the active one). */
  @get @route("check") check(@path @format("uuid") pipelineId: string, @query version?: int32): ConfigCheck | NotFound | BadRequest;
}
