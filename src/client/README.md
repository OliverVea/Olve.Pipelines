# @olve/client

The setup the frontend and `pl` share over `@olve/api`, the Hey API client generated from the
contract (`src/spec/`, `cd src/spec && bun run build` → `artifacts/clients/ts`). It doesn't wrap the
generated operations: callers pass the client as `client` to each one.

- `createApiClient({ baseUrl, token, fetch?, headers?, throwOnError? })`: the bearer per request and
  an injected `fetch` (the CLI tests fake it). With `throwOnError` a non-2xx answer throws
  `ApiError` (the frontend); without it, it is the call's `error` (`pl` unwraps with `call()`).
- `ApiError`: status, the parsed body and its problems (`field: message`), from `serverProblems`.
- `changeEvents(client, signal, pipeline?)` and `logEvents(client, signal, path, attempt?)`: one
  connection of an SSE operation, typed as the contract's `ChangeEvent` / `LogEvent`. A refused
  stream throws `ApiError`, a lost connection its error; reconnecting is the caller's. `reloadsAll`
  is true for `hello` and `resync` (nothing is replayed: reload what is shown), `isNotice` for the
  rest but `ping`.

It is no package of its own: `@olve/client` is a path alias in the frontend's and `src/cli`'s
`tsconfig.json` (and the frontend's `vite.config.ts`), like `@olve/api`. `src/cli`'s prettier and
type-check cover it, and its behaviour is tested through `pl` (`src/cli/test/watch.test.ts`).
