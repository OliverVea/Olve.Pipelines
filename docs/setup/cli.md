# The `pl` CLI

[← Index](index.md)

`pl` drives an instance: pipelines, runs, steps, secrets, config, bindings, and local development.

## Installing

Every instance serves the `pl` it was deployed with. Install it from the instance you use:

```bash
curl -fsSL https://pipelines-v2-private.ovea.pro/install.sh | sh   # to ~/.local/bin/pl (PL_INSTALL_DIR overrides)
pl --version                                                       # the publishing run's version stamp
pl login --api-url https://pipelines-v2-private.ovea.pro           # profile 'default'; add --device over SSH
pl login --profile beta --api-url https://pipelines-v2-beta.ovea.pro
```

Windows: `irm https://pipelines-v2-private.ovea.pro/install.ps1 | iex`. The binaries alone:
`/download/pl-linux-x64` (Native AOT) and `/download/pl-win-x64.exe` (self-contained single-file).
`PL_API_URL` / `PL_INSTALL_DIR` override the script's defaults. From source:
`dotnet publish src/Olve.Pipelines.Cli -c Release -r linux-x64 -o ~/.local/opt/pl`.

`pl login` caches the token and the API URL in a **profile** of `~/.pl` (or `$PIPELINES_CONFIG`):
`default`, or the one named by `--profile <name>` / `PIPELINES_PROFILE`. Every command uses the
selected profile; `pl profile list` shows them. `--api-url` / `PIPELINES_API_URL` override the URL per
command, `--token` passes a bearer directly.

## Commands

Global flags: `--json`, `--verbose`/`-v`, `--help`/`-h`. `pl help`, `pl <noun> --help` and
`pl <command> --help` print the details.

```bash
# pipelines — <pipeline> is an id or a name (quote names with spaces)
pl pipeline list [--summaries]                         # health, last run, active version, running/held, blocked steps
pl pipeline get <pipeline>
pl pipeline delete <pipeline>                          # also deletes its binding, runs and secrets
pl pipeline health <pipeline>
pl pipeline versions <pipeline>                        # > marks the version runs start on
pl pipeline manifest <pipeline> [--version <n>] [--json]
pl pipeline switch <pipeline> <version> [--cancel-running]   # rollback = an older version

# runs. Addresses: <pipeline>#<run>[/<step>[@<attempt>]]
pl trigger <pipeline> [--input <json|@file|@->] [--field k=v ...]   # prints <pipeline>#<run>
pl run list <pipeline> [--page <n>] [--page-size <n>]
pl run get <pipeline>#<run>
pl run cancel <pipeline>#<run>
pl run rerun <pipeline>#<run>
pl run continue <pipeline>#<run> [--field k=v ...] [--input <json|@file|@->]

# steps of a run (without @attempt: the latest successful attempt; logs: the latest attempt)
pl step output <pipeline>#<run>/<step>[@<attempt>]
pl step files <pipeline>#<run>/<step>[@<attempt>]
pl step file <pipeline>#<run>/<step>[@<attempt>] <path> [-o <file>]
pl step logs <pipeline>#<run>/<step>[@<attempt>] [--follow] [--interval <seconds>]
pl step rerun <pipeline>#<run>/<step> [--continue]
pl step cancel <pipeline>#<run>/<step>                 # the run ends Cancelled once the stage settles
pl step history <pipeline>/<step> [--limit <n>]

# blocking steps (an operator hold; use sparingly)
pl step blocked <pipeline>
pl step block <pipeline>/<step>
pl step unblock <pipeline>/<step>

# what's running (scheduled/in-progress attempts across pipelines)
pl attempts

# live change events (GET /api/events) until Ctrl+C
pl watch [<pipeline>] [--json]

# block until something happens (re-checks on live events); exit 0 ok, 1 failed, 124 --timeout
pl wait run <pipeline>#<run>[/<step>] [--until finished|started|held] [--timeout 45m]
pl wait push <pipeline> <sha> [--until started|finished]   # prints the run's address: addr=$(pl wait push p <sha>)
pl wait reconcile <pipeline> <sha>                         # the binding applied that config commit

# secrets and bindings
pl secret list <pipeline>                              # declared-by and set/unset per secret
pl secret set <pipeline> <name> [--from-env <VAR> | --from-file <path>]   # default: stdin, verbatim
pl secret delete <pipeline> <name> [--force]           # --force: a secret the current config references
pl secret copy <source-pipeline>[/<name>] <target-pipeline> [--name <target-name>] [--overwrite]   # server-side; no /<name> = all
pl binding create <owner/repo> [--branch <b>] [--path <dir>] [--credentials-secret <key>]
pl binding get|status|reconcile <pipeline>
pl binding set-credentials <pipeline> [key]

# config (one YAML document per pipeline, saved as immutable snapshots)
pl config get <pipeline> [--snapshot N]                # the YAML as saved
pl config set <pipeline> -f app.yml                    # validate against the active version, save as config #N+1
pl config history <pipeline>
pl config rollback <pipeline> <N>                      # save snapshot N again as the newest
pl config check <pipeline> [--version V]               # problems, missing secrets, changed defaults; exit 1 if V can't activate

# local development (no service, except dev validate)
pl dev describe [--dir <path>] [--json]
pl dev validate [--dir <path>] [--json]
pl dev run <step> [--input k=v ...] [--input-file <f>] [--secret NAME=value ...] [--docker] [--json]
pl dev pipeline [--input k=v ...] [--until <step>] [--only a,b] [--secret NAME=value ...] [--docker] [--yes]

# installation
pl bootstrap / pl teardown                             # cold install / remove of an instance
```

`pl dev` calls the SDK's `olve-dev` / `olve-build` through Bun (`$OLVE_BUN`, then `PATH`, then
`~/.bun/bin/bun`; SDK override `$OLVE_SDK`) and writes to `<dir>/.olve/`. `pl dev run --from
<pipeline>#<run>` is parsed but not implemented yet. Details:
[Local Development](local-development.md).
