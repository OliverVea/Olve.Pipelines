import "@typespec/http";

using Http;

namespace Pipelines;

/**
 * The body of every non-2xx answer except 401 and 403, as `application/json`. The status follows
 * from the primary error's code: `not-found` 404, `conflict` 409, `invalid` 400, `unavailable` 503
 * (with `Retry-After`), `internal` 500. 401 and 403 have no body.
 */
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

/** The error class; the problem's `type` is `urn:olve-pipelines:<code>` and its `status` follows from it. */
enum ApiProblemCode {
  NotFound: "not-found",
  Conflict: "conflict",
  Invalid: "invalid",
  Unavailable: "unavailable",
  Internal: "internal",
}

model ApiProblemError {
  code: ApiProblemCode;

  message: string;

  /** The request field the error is about (e.g. `input.sha`), if any. */
  field?: string;
}

model BadRequest {
  @statusCode statusCode: 400;
  @body body: ApiProblem;
}

model NotFound {
  @statusCode statusCode: 404;
  @body body: ApiProblem;
}

model Conflict {
  @statusCode statusCode: 409;
  @body body: ApiProblem;
}

/** A dependency (Kubernetes, storage, GitHub) is unavailable; retry shortly. */
model ServiceUnavailable {
  @statusCode statusCode: 503;
  @body body: ApiProblem;
}
