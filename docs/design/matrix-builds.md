# Matrix builds

A matrix step runs once per **cell**: one combination of its axes' values. It is designed, not
built; the UI mock is `src/frontend/mocks/matrix.html` (`npm run dev`).

## Agreed

- **Axes are a static list in code**, several at once, as a cross product with excludes
  (`{ os: ['linux', 'windows', 'macos'], arch: ['x64', 'arm64'] }` without `windows · arm64`).
- **One step in its stage.** The barrier waits for every cell. The step's status rolls up from
  its cells: a failed cell fails it, a cell still going keeps it going, and it succeeds when every
  cell did.
- **A cell is addressed** as `<pipeline>#<run>/<step>[os=linux,arch=x64]`, and reruns on its own.
- **What a failure does is the pipeline's choice**, as a declarative failure strategy, because the
  service never runs repo TypeScript:
  - `keepRunning()` (default): every cell runs to the end.
  - `cascadeFailure()`: the first failed cell cancels the cells still running or waiting.
  - More only as built-ins with parameters (e.g. allowed failures, cells that may fail) when a
    pipeline needs them.
- **The UI doesn't show the strategy.** A cancelled cell's reason names the failure that caused
  the cascade (`cancelled: windows · x64 failed`), and the step's reason is that failure.

## In the UI

- Chip: the cells finished (`2/5`); its hovercard shows the cells.
- Card: one pill per cell, or a grid for two axes with excluded cells hatched.
- Step sheet: a table of cells with status, attempt, time, logs and a rerun.

## Steps around a matrix step

- **Input**: every cell gets the step's input; the handler reads its typed cell from `ctx.cell`.
- **Output**: a later step that reads a matrix step gets a list of `{ cell, output }`, in cell
  order.
- **Concurrency**: each cell is its own Job and counts against `Jobs:MaxConcurrent`; a step may
  set `maxParallel` to hold heavy cells back.

## Contract between the SDK and the service

**Manifest.** A step descriptor (in `stages`, never a failure handler) may carry `matrix`:

```json
"matrix": {
  "axes": [
    { "name": "os", "values": ["linux", "windows", "macos"] },
    { "name": "arch", "values": ["x64", "arm64"] }
  ],
  "exclude": [{ "os": "windows", "arch": "arm64" }],
  "failure": { "kind": "keepRunning" },
  "maxParallel": 2
}
```

- `axes` is a list, because canonical JSON sorts object keys. 1 to 4 axes; an axis name matches
  `^[a-z][a-zA-Z0-9]{0,30}$`, a value `^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$`, values unique per axis.
- `exclude` (optional): each entry names one or more axes with one of their values; a cell matching
  every named axis is left out (`{ "os": "windows" }` drops every windows cell).
- `failure.kind`: `keepRunning` (default when the SDK omits nothing: it always writes it) or
  `cascadeFailure`.
- `maxParallel` (optional, ≥ 1): at most that many of the step's cells running at once in a run.
- The cells are the cross product in axis order (first axis outermost) minus the excludes: at
  least 1, at most 64.
- `axes`, `exclude`, `failure` and `maxParallel` are part of what the step declares (its version).

**Cell names.** A cell is the step `<step>[<axis>=<value>,…]` in axis order, e.g.
`build-cli[os=linux,arch=x64]`: its attempts, logs, files, reruns and addresses use that name. Its
label is the values joined with ` · ` (`linux · x64`).

**Reading a matrix step.** A later step's input entry of type
`{ "model": "BuiltBinary", "optional": false, "cells": true }` is mapped with a whole-step ref
(`{ "ref": "build-cli" }`, no field path) to a matrix step whose output is that model. Its value in
`input.json` is a list in cell order, one entry per cell whose latest successful attempt has an
output: `[{ "cell": { "os": "linux", "arch": "x64" }, "output": { … } }, …]`. A `cells` input can
only read a matrix step, and a matrix step can only be read through a `cells` input.

**The step container.** A cell's Job runs `/olve/step run <step>` (the matrix step's own name) with
`OLVE_CELL` set to the cell as compact JSON in axis order (`{"os":"linux","arch":"x64"}`). The
handler sees it as `ctx.cell`; a matrix step run without `OLVE_CELL` fails with exit code 65.

**Failure.** With `cascadeFailure`, a cell whose last allowed attempt failed (its `retries` used up)
cancels the step's other cells that are scheduled or running, each with the reason
`cancelled: <label> failed`. The matrix step's own reason is `<label> failed: <reason>`, or
`<n> cells failed. <label> failed: <reason>` when more than one did.
