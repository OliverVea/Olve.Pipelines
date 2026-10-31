/** Harness exit codes (sysexits-style). Part of the service contract. */
export const EXIT = {
  ok: 0,
  /** The handler (or trigger mapper) threw. */
  handlerFailed: 1,
  /** Bad command line: unknown mode, step or trigger. */
  usage: 64,
  /** input.json missing, not JSON, or not valid for the step's inputs (or the trigger event). */
  inputInvalid: 65,
  /** The handler's return value (or the mapped trigger input) failed strict validation. */
  outputInvalid: 66,
  /** Uploading result.tar.gz to OLVE_RESULT_URL failed (after retries). The envelope was still printed. */
  uploadFailed: 74,
  /** The pipeline is invalid: describe problems, a step without an implementation. */
  configInvalid: 78,
  /** Cancelled by SIGINT / SIGTERM (128 + signal). */
  sigint: 130,
  sigterm: 143,
} as const;
