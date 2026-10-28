import "@typespec/http";
import "./errors.tsp";

using Http;

namespace Pipelines;

/** A secret as the API knows it: its name, who declares it and whether it is set. Values are never returned. */
model PipelineSecret {
  name: string;
  description: string | null;

  /** What declares it: steps, failure handlers, poll triggers or the binding. Empty for a set but unused secret. */
  declaredBy: string[];

  /** Null when Kubernetes can't be read. */
  isSet: boolean | null;

  /** False when a declared secret is optional for the current config. */
  needed?: boolean | null;
}

model SetSecret {
  value: string;
}

/** Copy secrets server-side from another pipeline on this instance. */
model CopySecrets {
  @format("uuid") sourcePipelineId: string;

  /** The names to copy; all by default. */
  names?: string[] | null;

  /** Renames a single copied secret. */
  targetName?: string | null;

  /** Replace existing names; refused without. */
  overwrite?: boolean;
}

/** The target names written (never values). */
model CopiedSecrets {
  copied: string[];
}

@tag("Secrets")
@route("/api/pipelines/{pipelineId}/secrets")
interface Secrets {
  /** The secrets the active version or the binding uses, plus set but unused ones. */
  @get list(@path @format("uuid") pipelineId: string): PipelineSecret[] | NotFound;

  /** Sets the value and re-checks a pending version. */
  @put @route("{name}") set(
    @path @format("uuid") pipelineId: string,
    @path name: string,
    @body body: SetSecret,
  ): OkResponse | NotFound | BadRequest | ServiceUnavailable;

  /** Deletes the value and re-checks a pending version. A secret the current config references needs `force`; new runs then wait until it is set again. */
  @delete @route("{name}") delete(
    @path @format("uuid") pipelineId: string,
    @path name: string,
    @query force?: boolean,
  ): OkResponse | NotFound | BadRequest | Conflict | ServiceUnavailable;

  /** Copies values server-side from `sourcePipelineId`. Whoever can write this pipeline can read the copied values from its steps. */
  @post @route("copy") copy(
    @path @format("uuid") pipelineId: string,
    @body body: CopySecrets,
  ): CopiedSecrets | NotFound | BadRequest | Conflict | ServiceUnavailable;
}
