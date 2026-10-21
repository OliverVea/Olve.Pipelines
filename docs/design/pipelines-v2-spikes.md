# Olve.Pipelines v2 — spike findings (2026-10-21)

Spike code lives under `spikes/` (`compile/`, `cluster/`, `sdk-types/`), with raw results in
`spikes/cluster/results/`. Bun 1.4.3, zod 4.6.7, k3s 1.34 + gVisor + the real runner NetworkPolicy.

## Spike 1 — harness in any image (cluster, gVisor) — PASS

A `bun build --compile` binary runs in **every** image tested — debian-slim, dotnet/sdk (glibc),
alpine (musl), kaniko executor (no libc), distroless/static (no libc, no shell) — when launched
through a **bundled loader invoked explicitly**:

```
/olve/lib/ld-linux-x86-64.so.2 --library-path /olve/lib /olve/step run <name>
```

| Variant | Result |
|---|---|
| native binary (image's loader) | only works in matching-libc images |
| **glibc binary via bundled ld-linux** (3.0 MB libs) | **works everywhere**, +~10 ms vs native |
| musl binary via bundled ld-musl + libstdc++ + libgcc_s (3.5–4.1 MB) | works everywhere, +~70 ms |
| patchelf interpreter/rpath | **segfaults** (breaks Bun's embedded payload) |

- Dependencies resolve only from `/olve/lib` (hermetic). Children spawned by the harness use the
  image's own libc (`dotnet`, `git`, `/kaniko/executor` all ran) because `--library-path` is
  per-process. **Never set `LD_LIBRARY_PATH`.**
- `process.execPath` / `/proc/self/exe` are the loader: re-exec self only via the loader command.
- kaniko has no `/tmp` and a shell only at `/busybox/sh` (PATH-dependent); distroless has no shell.
- Networking under the policy: npm, registry.ovea.pro (via Traefik), Garage in apps-beta OK; K8s API
  blocked (intended). Runs fine as non-root.
- Timings: harness image pull 1.3–2 s (first per node); init `cp` 0.05–0.12 s; ~1 s per extra init
  container under gVisor; Garage-style fetch + untar of 44 MB gz 0.93 s.
- glibc NSS caveat: images with exotic nsswitch modules / `ld.so.preload` could interfere with the
  bundled glibc (not observed). musl is immune — the validated fallback.

## Spike 2 — one binary, many modes — PASS

`describe`, `run <step>`, `check <step>`, `map-trigger <trigger>` in one executable.

- Size ~91 MB (glibc) / ~85 MB (musl); ~2 MB is pipeline code, the rest the Bun runtime.
  gzip ~38 MB, zstd ~32 MB. `describe` startup 21 ms (10 ms with `--bytecode`).
- **Deterministic**: manifest hash identical across runs, build dirs, glibc/musl, `env -i`, images;
  binaries byte-identical across builds. Needs canonical JSON (sorted keys), dropping `$schema`.
- **Handler hashes**: bundle each handler separately with the contract files (`models.ts`,
  `pipeline.ts`) **external**, hash the bundle output (tree-shaken → function-level granularity,
  includes embedded scripts). Bun's metafile import graph is incomplete — don't use it.
- **Recording proxies only catch coercion/calls/spread.** Ternaries, `===`, `&&`/`||`, `!` are
  silently mis-recorded; a source lint over `fn.toString()` catches them (harness must not be
  minified). TS `Ref<T>` types remain the primary guard.
- Gotchas: `in` is a reserved word (mapper param in the spec examples is a syntax error); computed
  dynamic imports load from disk at runtime (generate a literal-path handler registry); compiled
  binaries autoload `.env`/`bunfig.toml` from cwd; `--bytecode` defaults to CJS (pass
  `--format=esm`); scripts must be embedded via `import x from "./x.sh" with { type: "file" }` and
  copied out before execution (the repo isn't in the step container).

## Spike 3 — describe sandbox — PASS

In a gVisor pod under the NetworkPolicy: fetch public repo tarball (no token) 0.5 s, `bun install`
0.3 s, describe 0.07 s, `bun build --compile` 0.16 s — **1.34 s total** (+ first image pull ~4–5 s).

- **Must compile on a glibc Bun image (`oven/bun:<pinned>-slim`)**: on alpine, `bun-linux-x64`
  silently yields musl and glibc targets are rejected.
- Results out: **manifest on stdout between sentinels** (service already reads pod logs; termination
  message is capped at 4 KB). **Binary via pre-signed PUT to Garage.** No callbacks to the API.
- Runner pods can reach everything in apps/apps-beta (both Pipelines APIs, Garage) — all service
  endpoints must be authenticated; manifests/outputs are untrusted input (schema + size caps).

## Decisions taken into the plan

1. **glibc build + bundled glibc loader** is the default harness runtime (faster, libstdc++ static);
   musl is the documented fallback. Launch is always via the explicit loader; no patchelf, no
   `LD_LIBRARY_PATH`. `TMPDIR`/temp under `/olve/tmp`, outputs under `/olve/work`.
2. **Delivery = design B**: stable `harness-base` image (static busybox + `/olve/lib`, versioned with
   the service) as the single init container; it fetches the per-commit `step.tar.gz` and
   `input.json` from Garage via pre-signed GETs into the `/olve` emptyDir. (Validate one full
   busybox-wget pod early.)
3. **Build/describe Job** on `oven/bun:<pinned>-slim`: fetch tarball → `bun install --frozen-lockfile`
   → generate handler registry → compile with `--no-compile-autoload-dotenv
   --no-compile-autoload-bunfig --bytecode --format=esm --target=bun-linux-x64` → verify PT_INTERP →
   PUT `step.tar.gz` → `step describe` printed between sentinels.
4. **Handlers** live at `handlers/<step-name>.ts` (default export `implement(step, fn)`); missing
   handler = describe error. Contract files contain contracts only. Scripts are embedded with
   `with { type: "file" }`.
5. **`ctx.sh`** uses Bun Shell / spawn with shell detection (`/bin/sh`, `/busybox/sh`, `/bin/bash`);
   shell-less images get a clear error.
6. **Mappings** are tagged in the manifest: `{path}`, `{const}`, `{object}`. Trigger mappers are always
   code, run via `map-trigger` (never proxy-recorded).
7. **`run` contract**: `output.json` envelope `{status:"succeeded", output} | {status:"skipped",
   reason}`; exit codes 0 ok, 1 handler threw, 64 usage, 65 input invalid, 66 output invalid,
   78 config invalid.
8. Manifest carries `harness: { bun, sdkHash }`; a Bun/SDK bump legitimately bumps all handlers.
9. Store binaries compressed; later consider shared runtime + per-commit JS bundle (~1 MB).
