# Promotion Gate

[← Index](index.md) · [Subject Index](subjects.md)

The promotion gate is how you **pause or redrive** the artifact bundle as it advances into a
processing step. It is the one place where operational control overlaps with the pipeline
shape — and it is **state, not config**.

## What a promotion is

A **promotion** is the artifact bundle advancing **into** a processing step. Each processing
step has a gate with two controls:

- **Brake** — block / unblock promotion into the step. While blocked, the bundle does not
  advance into that step (and therefore not into any later step, since processing is
  sequential).
- **Re-promote** — redrive the same bundle into the step again (e.g. to retry a deploy without
  a fresh build). Unavailable where there's no bundle to redrive (the step has never run) or
  while promotion is blocked.

Production steps have **no** gate — only processing steps do.

## State, not config — and why that matters

The gate is **operational state**, deliberately kept *out* of GitOps config:

- It is **API/UI-mutable even on a git-bound pipeline.** Blocking a deploy is an operation, not
  a shape change, so the [git-only restriction](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints)
  does not apply to it.
- It is stored **separately from the step** (keyed on the step id; absence = enabled), so a
  **reconcile never erases an operator's brake.** You can pause a deploy and keep pushing code;
  the brake survives every reconcile until you lift it.
- It is **persisted** independently, so a braked step stays braked across a server restart.

## Orthogonal to the job result

`blocked` is **orthogonal** to the step's job status. A step can be `Done` *and* have its
promotion blocked — the brake controls whether the **next** bundle advances in, not whether the
last run succeeded. Don't read a green step as "promotion is open," or a blocked gate as "the
step failed." They're independent axes.

## Typical uses

- **Freeze production deploys** during an incident: brake the prod processing step. Builds and
  beta deploys keep running; nothing reaches prod until you unblock.
- **Retry a flaky deploy** without rebuilding: re-promote the last bundle into the step.
- **Stage a release**: brake prod, let beta validate, then unblock to let the same bundle flow
  through.

## How it relates to execution

Every path that would create a ProcessingJob consults the gate first — the automatic cascade
after a build, the manual processing trigger, configured `processing` triggers, and
re-promote. A blocked gate short-circuits them all, halting the chain at that step without
skipping ahead. See [Bundles & Execution](bundles-and-execution.md) for the job model.

## See also

- [Binding & Reconcile](binding-and-reconcile.md#git-only-there-are-no-config-mutation-endpoints) — why
  the gate is exempt from the git-only restriction
- [Bundles & Execution](bundles-and-execution.md) — the sequential processing chain the gate
  controls
