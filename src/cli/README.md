# pl

The operator CLI of Olve.Pipelines: Bun + TypeScript on the generated client, compiled to one
binary per platform (Linux and Windows x64). How to use it is in [docs/setup/cli.md](../../docs/setup/cli.md)
(served at `/docs/cli`); this is how it is built.

## Commands

The generated client lives in the repo-root `artifacts/` (gitignored), which `tsconfig.json`'s
`paths` point at: run `cd src/spec && bun run build` first, and again after the contract changes.

```bash
bun install
bun run src/main.ts <command> …     # pl from source
bun run typecheck                   # tsc over src/, test/ and the shared client
bun test                            # the command tests
bun run format / format:check       # prettier over src/, test/ and ../client/src
bun run build                       # ../../artifacts/cli/pl (build:linux-x64, build:windows-x64 for the release targets)
```

The deploy's `build-cli` step runs `format:check`, `typecheck` and `bun test`, then compiles both
targets with `--define process.env.PL_VERSION=<run version>` (`pl --version`; `dev` from source).

## Layout

```
src/cli/
├── src/
│   ├── main.ts           # Entry: real stdio, signals, the browser opener → cli.run
│   ├── cli.ts            # One invocation: parse, profile and token, the API client, dispatch, exit code
│   ├── registry.ts       # Command and CommandContext
│   ├── commands/         # One file per noun (run, step, wait, …); index.ts lists them in `pl help` order
│   ├── api.ts            # The client for an invocation (verbose request log) and call(): data, or a CliError
│   ├── events.ts         # readChanges: the change events, as CliErrors on failure
│   ├── address.ts        # <pipeline>#<run>/<step>[@<attempt>] parsing, pipeline lookup by name
│   ├── args.ts, help.ts  # Flags, options, operands; help text
│   ├── output.ts         # --json or the human rendering; tables
│   ├── profiles.ts       # ~/.pl: profiles, logins, token refresh
│   ├── auth/             # OIDC for `pl login` (loopback, device grant), terminal QR codes
│   ├── dev/              # `pl dev`: describe, plan, run steps locally (toolchain, manifest, pool, input assembly)
│   ├── wait.ts           # `pl wait`: change events nudge, the API decides
│   ├── process.ts        # The child-process runner (bun, docker)
│   └── errors.ts         # CliError, UsageError, exit codes
└── test/                 # bun test: helpers.ts (pl, fakeFetch), one file per area, fixtures/
```

The client setup and the typed event streams are shared with the frontend in `src/client/`
(`@olve/client`). Every server call is a generated operation with `client: ctx.api`, unwrapped by
`call()`; there are no hand-built `/api/` URLs (`tools/check.sh` rejects them).

## Adding a command

1. Add a `Command` (`noun`, `verb`, `summary`, `detail` for `--help`, `flags` / `options` /
   `aliases`, `operands`, `run(ctx)`) to its noun's file in `commands/`, or a new file listed in
   `commands/index.ts`.
2. In `run`, call the generated operation (`await call(runsGet({ client: ctx.api, path }))`) and
   print with `ctx.out.emit(data, () => ctx.out.line(…))`, so `--json` prints the payload as is.
   Problems are `CliError`s (exit 1), bad invocations `UsageError`s (exit 2). Diagnostics go to
   `ctx.log` (stderr, `--verbose` only), warnings to `ctx.stderr`.
3. Everything outside goes through the context so tests can fake it: HTTP through the client
   (`ctx.fetch` underneath), child processes through `ctx.run`, time through `ctx.now` and
   `ctx.sleep`, stdin through `ctx.stdin` / `ctx.prompt`.
4. Test it with `pl([...], { fetch })` from `test/helpers.ts`: `fakeFetch` records each request and
   answers from a function (`stream` for server-sent events). A recorded `path` includes the query
   string.
