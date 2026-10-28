import "@typespec/http";

using Http;

namespace Pipelines;

/** The body of every non-2xx answer except 401 and 403 (RFC 9457 style). */
@error
model ApiProblem {
  /** A URI naming the error class, e.g. `urn:olve-pipelines:not-found`. */
  type: string;

  title: string;
  status: int32;

  /** The primary error's message. */
  detail?: string;

  errors: ApiProblemError[];

  /** Matches the `TraceId` of this request's log lines. */
  traceId?: string;
}

model ApiProblemError {
  /** `not-found`, `conflict`, `invalid`, `unavailable` or `internal`. */
  code: string;

  message: string;

  /** The request field the error is about (e.g. `input.sha`), if any. */
  field?: string;
}

model NotFound {
  @statusCode statusCode: 404;
  @body body: ApiProblem;
}
