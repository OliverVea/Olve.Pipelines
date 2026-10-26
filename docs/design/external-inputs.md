# External inputs: steps that wait for the outside world

Status: design, 2026-10-26. Not built. Replaces the "gating steps" candidate in the v2 plan.

## 1. Why

Some steps can only run once something outside the pipeline has answered: a person approves a
deploy, a review tool accepts screenshots, a smoke check elsewhere reports back. Today the only way to hold a run is to block a step, which is an
operator hold, not an answer, and carries no value.

Olve.Pipelines owns the waiting, not the domain. It doesn't know what a screenshot baseline is: a
visual-regression tool does. The pipeline submits work to such a tool in one step and waits for the
tool's verdict in the next.

Pushes still go beta → prod on their own. External inputs are for the places where a decision or a
signal really belongs in the run; they don't make approvals routine.

## 2. The idea: an input that comes from outside

A step's inputs come from earlier steps or the pipeline input today. An **external input** is one
more source: a typed value, validated against a model, that arrives from outside the run.

```ts
const Approval = model("Approval", z.object({
  approved: z.boolean(),
  comment: z.string().optional(),
}));

export const approveProd = step({
  name: "approve-prod",
  description: "Waits for a person to approve the prod deploy.",
  image: "alpine:3.22",
  input: {
    release: M.Release,
    decision: external(Approval, {
      title: "Deploy to prod?",
      description: "Beta passed. Approve to deploy this release to prod.",
      from: [person()],
    }),
  },
});
// handlers/approve-prod.ts
export default implement(approveProd, async ({ decision }, ctx) => {
  if (!decision.approved) throw new Error(`rejected: ${decision.comment ?? "no reason given"}`);
});
```

The scheduler holds the step until the value arrives, then runs it with the value in `input.json`
like any other input. Consequences:

- **Handlers stay plain functions.** Nothing is replayed, no pod waits for days, and the
  service still never runs repo TypeScript: it only validates the value against the model's JSON
  Schema.
- **The value is data.** It can be a yes/no, a choice, a form (a target, a version, release notes),
  or whatever a tool sends back. Later steps can consume the step's output as usual.
- **The UI can render a form** for a person from the model's JSON Schema; `schema-form.ts` already
  renders input forms for manual triggers.

A library step covers the plain case: `steps.approval({ name, title, from? })` is the step above.

## 3. Where a value can come from (`from`)

An external input lists one or more sources; the first value that arrives wins.

| Source | Answered by | Use |
|---|---|---|
| `person()` | A signed-in user (allow-list) in the UI or `pl input <addr> --field …` | Approvals, choices, forms |
| `callback({ secret? })` | Another system: `POST /api/inputs/{token}` with the value as JSON. The token is a one-time URL handed to earlier steps (see below); `secret` adds an HMAC check like webhook triggers | Review tools (visual regression), CI elsewhere, chat bots |
| `poll({ url, valuePath, headers?, intervalSeconds })` | The service polls a URL until the value at `valuePath` validates against the model | External checks that expose a status endpoint |

Ideas, not planned until a pipeline needs them: `at(time)` (maintenance windows) and waiting for
another pipeline (more likely a trigger, `onRun`, than an input).

**Telling the outside world.** A step that runs earlier can include the wait's callback URL and a
link to it in what it sends out: `ctx.inputUrl("vr-verdict")` gives the URL for that external input
of this run, minted when the run starts, and `ctx.runUrl()` links the run. The visual-regression
flow (§6) uses this.

## 4. Lifecycle

A step with an external input gets an attempt as soon as everything else it needs is available. That
attempt starts in a new status:

```
Waiting(since, request) ──value──▶ Scheduled ─▶ InProgress ─▶ Succeeded / Failed
        │
        ├─ declined ─────▶ Failed ("declined by <user>: <comment>")
        ├─ newer run waits here too ─▶ Obsolete (the older run is superseded)
        └─ run cancelled ▶ Cancelled
```

- **Waiting holds the run at the stage**, like any unfinished step. It isn't the operator hold of a
  blocked step (`Run.HeldAt`); the UI shows "waiting for input" with the request.
- **Latest wins.** Waiting counts like Scheduled: a newer run that reaches the same step makes the
  older waiting attempt Obsolete and supersedes its run, so only the newest run waits.
- **Declining** is generic and needs no model support: a person (or a callback with
  `{ "$decline": "reason" }`) fails the attempt without running it.
- **The answer is recorded** on the attempt: the value, who or what sent it (user id and name, the
  source kind, the caller's IP for callbacks) and when.
- **No time limit** by default. An input can set `timeoutSeconds` with `onTimeout: "decline"` or a
  default value.
- **Blocked steps** still apply first: a blocked step doesn't start waiting.
- **Versions.** A waiting attempt can be answered on any version, like any running run continues on
  the version it pinned.
- **Reruns.** Rerunning a step with an external input asks again (a new Waiting attempt); `pl step
  rerun --reuse-input` reuses the last answer.

All of it goes through `StageScheduler.Advance` under `RunSchedulingLock`; answering moves an
existing attempt from Waiting to Scheduled and never creates attempts directly.

## 5. Surfaces

- **Manifest.** An input entry `{ external: { model, title, description, from: [...], timeout? } }`.
  Titles and descriptions can use mappings (`Deploy ${from.build.version}?`), recorded as tagged
  mappings and rendered by the service.
- **API.** `GET /api/inputs?status=waiting` (everything waiting, across pipelines);
  `POST /api/pipelines/{id}/runs/{n}/steps/{step}/input` (a person: value or `decline`);
  `POST /api/inputs/{token}` (callbacks, no login).
- **`pl`.** `pl input list`, `pl input <addr> --field k=v | --file value.json`, `pl input decline <addr>
  [--comment]`. `pl wait` returns with its own exit code when the run waits for input, so an agent
  hands the decision to the user instead of hanging. Agents never answer inputs themselves.
- **UI.** A "Waiting for you" list on the pipeline overview, and on the run page a form generated from
  the model, with the request's title, description, links and any files the earlier step produced.
- **Events.** `attempt` events carry `waiting`; a new `input` event when a value arrives. A future
  alerting listener (Slack, phone) only needs the stream.

## 6. Visual regression with an external tool

The tool owns baselines, diffs and its review UI. The pipeline only hands work over and waits:

```ts
.stage(test)                    // produces screenshots as files
.stage(submitReview)            // posts them to the tool with ctx.inputUrl("verdict")
.stage(reviewVerdict)           // input: verdict: external(Verdict, { from: [callback()] })
.stage(publish)
```

The tool compares against its baselines, auto-accepts when nothing changed (by calling the URL right
away), or lets a person review and then calls back with `{ "accepted": true }` or
`{ "$decline": "…" }`. "What counts as a difference" and "what about a first run without a
baseline" are the tool's rules.

## 7. Build order

Each step ships on its own.

1. **The primitive.** Waiting status, external inputs from `person()`, the API, `pl input`, UI form,
   events, `steps.approval`. Server first; the SDK (with a version bump) after the server is on prod.
2. **`callback()`** with one-time URLs and `ctx.inputUrl`.
3. **`poll()`**, reusing the poll-trigger machinery.

## 8. Open questions

- Should a person's answer be restricted to certain users per input (`person({ users })`), or is the
  instance allow-list enough for now?
