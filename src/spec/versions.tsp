import "@typespec/http";
import "./errors.tsp";
import "./pipelines.tsp";

using Http;

namespace Pipelines;

/** A version without its manifest. */
model PipelineVersionSummary {
  @format("uuid") id: string;
  number: int32;
  manifestHash: string;
  commitSha: string | null;

  /** Step name → the step's version (bumped when its content changes). */
  stepVersions: Record<int32>;

  /** Model name → the model's version. */
  modelVersions: Record<int32>;

  createdAt: utcDateTime;
}

/** One version in full; `manifest` is the canonical manifest JSON text. */
model PipelineVersion {
  ...PipelineVersionSummary;
  @format("uuid") pipelineId: string;
  manifest: string;
}

/** The pipeline's activation state (null = no version yet) and its versions, newest first. */
model PipelineVersions {
  activation: PipelineActivation | null;
  versions: PipelineVersionSummary[];
}

/** A version as a config snapshot makes it run: each `select` replaced by the case the config picks. */
model EffectivePipeline {
  version: int32;

  /** The config snapshot it was resolved with (null = the pipeline has no config yet). */
  configSnapshot: int32 | null;

  /** Each select's reference → the case picked. */
  selected: Record<string>;

  /** The effective manifest (canonical JSON): the version's, with selects resolved. */
  manifest: string;

  /** Step name → the select cases it was picked by (`target.beta.kind=inCluster`); steps outside selects are absent. */
  cases: Record<string[]>;
}

model SwitchVersion {
  version: int32;

  /** Cancel the other versions' runs and drain first. */
  cancelRunning?: boolean;
}

@tag("Versions")
@route("/api/pipelines/{pipelineId}")
interface Versions {
  /** Every version (commit, step and model versions) and the current activation. */
  @get @route("versions") list(@path @format("uuid") pipelineId: string): PipelineVersions | NotFound;

  /** One version, including its canonical manifest. */
  @get @route("versions/{number}") get(@path @format("uuid") pipelineId: string, @path number: int32): PipelineVersion | NotFound;

  /** The version with each `select` resolved by a config snapshot (`snapshot`; default the latest): the steps a run of it would have. */
  @get @route("versions/{number}/effective") effective(
    @path @format("uuid") pipelineId: string,
    @path number: int32,
    @query snapshot?: int32,
  ): EffectivePipeline | NotFound | BadRequest | Conflict;

  /** New runs start on `version`. 409, with the reasons, when the config document or a secret it references doesn't satisfy that version. */
  @post @route("switch") switch(@path @format("uuid") pipelineId: string, @body body: SwitchVersion): PipelineActivation | NotFound | BadRequest | Conflict;
}
