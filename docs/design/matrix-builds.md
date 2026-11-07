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

## Open

- What a later step gets from a matrix step: every cell's output, keyed by cell.
- How a handler learns its cell (`ctx.cell`), and whether a cell's input can depend on it.
- Concurrency: whether cells count against `Jobs:MaxConcurrent` one by one, and a per-step limit.
