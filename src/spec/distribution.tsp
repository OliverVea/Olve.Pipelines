import "@typespec/http";

using Http;

namespace Pipelines;

/** A `pl` binary, streamed from the latest successful `publish-cli` attempt (or the instance's local build). */
model CliBinary {
  @header contentType: "application/octet-stream";
  @header contentDisposition: string;
  @header contentLength?: int64;

  /** The run of the `publish-cli` attempt the binary comes from; absent for a local build. */
  @header("X-Olve-Pl-Run") run?: int32;

  @body body: bytes;
}

model CliBinaryHead {
  @header contentType: "application/octet-stream";
  @header contentDisposition: string;
  @header contentLength: int64;
  @header("X-Olve-Pl-Run") run?: int32;
}

/** Not an asset, or not published yet. */
model CliNotFound {
  @statusCode statusCode: 404;
}

/** `pl` is still being built on this instance, or storage is not configured. */
model CliUnavailable {
  @statusCode statusCode: 503;
  @header("Retry-After") retryAfter?: int32;
  @header contentType: "text/plain; charset=utf-8";
  @body message: string;
}

model CliUnavailableHead {
  @statusCode statusCode: 503;
  @header("Retry-After") retryAfter?: int32;
}

model ShellScript {
  @header contentType: "text/x-shellscript; charset=utf-8";
  @body script: string;
}

model PowerShellScript {
  @header contentType: "text/plain; charset=utf-8";
  @body script: string;
}

/** Downloads of the `pl` CLI, so an operator can install it with one `curl` before they have a token. */
@tag("Distribution")
@route("/download")
interface CliDownloads {
  #suppress "@typespec/openapi3/path-reserved-expansion" "Served as a catch-all, so nothing under /download/ falls through to the web UI"
  /** `pl-linux-x64` or `pl-win-x64.exe`; any other name is a 404. */
  @summary("Download pl")
  @useAuth(NoAuth)
  @get @route("{+asset}") download(@path asset: string): CliBinary | CliNotFound | CliUnavailable;

  #suppress "@typespec/openapi3/path-reserved-expansion" "Served as a catch-all, so nothing under /download/ falls through to the web UI"
  /** The binary's headers (size, run), without its content. */
  @summary("Check a pl download")
  @useAuth(NoAuth)
  @head @route("{+asset}") head(@path asset: string): CliBinaryHead | CliNotFound | CliUnavailableHead;
}

/** The install scripts, with this instance as their default (`Links:BaseUrl`, else the request's origin). While `pl` is still being built they are a script that says so and exits 1. */
@tag("Distribution")
interface CliInstall {
  /** `curl -fsSL <instance>/install.sh | sh` installs this instance's `pl`. */
  @summary("Get the install script")
  @useAuth(NoAuth)
  @get @route("/install.sh") shell(): ShellScript | CliNotFound;

  /** `irm <instance>/install.ps1 | iex` installs this instance's `pl` on Windows. */
  @summary("Get the Windows install script")
  @useAuth(NoAuth)
  @get @route("/install.ps1") powerShell(): PowerShellScript | CliNotFound;
}
