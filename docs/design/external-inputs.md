# External inputs: steps that wait for the outside world

Status: built 2026-10-26 (person, callback, poll); preflight built 2026-11-11. Not built: per-input timeouts, `--reuse-input` on reruns, mappings in titles, callback HMAC secrets, poll headers. Callback URLs are `{Webhooks:PublicBaseUrl}/api/hooks/inputs/{run}/{step}/{input}/{mac}` (HMAC under an instance key kept in storage), not minted tokens; beta-test waits for the test fixture's callback step (which asks for its callback in its preflight) and poll step, answered by a signal-test service deployed next to beta. Replaces the "gating steps" candidate in the v2 plan.

## 1. Why

Some steps can only run once something outside the pipeline has answered: a review tool accepts screenshots, a smoke check elsewhere reports back, a person
supplies a value only they know. Today the only way to hold a run is to block a step, which is an
operator hold, not an answer, and carries no value.

Olve.Pipelines owns the waiting, not the domain. It doesn't know what a screenshot baseline is: a
visual-regression tool does. The pipeline submits work to such a tool in one step and waits for the
tool's verdict in the next.

Pushes still go beta → prod on their own. External inputs are for the places where a signal really
belongs in the run. Manual promotion steps are not one of them: where promotion has to be manual,
release builds (e.g. nightly) and promote a chosen version with a run of its own.

## 2. The idea: an input that comes from outside

A step's inputs come from earlier steps or the pipeline input today. An **external input** is one
more source: a typed value, validated against a model, that arrives from outside the run.

```ts
const Verdict = model("Verdict", z.object({
  accepted: z.boolean(),
  comment: z.string().optional(),
}));

export const review = step({
  name: "review",
  description: "Waits for the review tool's verdict on this run's screenshots.",
  image: "alpine:3.22",
  input: {
    screenshots: M.Screenshots,
    verdict: external(Verdict, {
      title: "Screenshots accepted?",
      description: "The visual-regression tool compares them with its baselines.",
      from: [person()],
    }),
  },
});
export const reviewStep = implement(review, async ({ verdict }) => {
  if (!verdict.accepted) throw new Error(`rejected: ${verdict.comment ?? "no reason given"}`);
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

There is no library approval step: manual promotion inside a run is what this is not for.

## 3. Where a value can come from (`from`)

An external input lists one or more sources; the first value that arrives wins.

| Source | Answered by | Use |
|---|---|---|
| `person()` | A signed-in user (allow-list) in the UI or `pl input <addr> --field …` | Choices and forms only a person can give; testing |
| `callback({ secret? })` | Another system: `POST /api/inputs/{token}` with the value as JSON. The token is a one-time URL handed to earlier steps (see below); `secret` adds an HMAC check like webhook triggers | Review tools (visual regression), CI elsewhere, chat bots |
| `poll({ url, valuePath, headers?, intervalSeconds })` | The service polls a URL until the value at `valuePath` validates against the model | External checks that expose a status endpoint |

Ideas, not planned until a pipeline needs them: `at(time)` (maintenance windows) and waiting for
another pipeline (more likely a trigger, `onRun`, than an input).

**Telling the outside world.** The step's own preflight (below), or a step that runs earlier, includes
the wait's callback URL in what it sends out: `ctx.inputUrl(step, "verdict")` gives the URL for that
external input of this run (one URL per run, step and input). The visual-regression flow (§6) uses
this.

**Preflight.** A step with an external input may run code before it waits:
`implement(step, handler, { preflight: async (input, ctx) => … })`. The preflight gets the step's
regular inputs (the external values don't exist yet) and the handler's `ctx` without `output` and
`skip`; it hands the work and `ctx.inputUrl(...)` to the tool and can `ctx.link` to where the answer
is given. It runs as a short Job of its own (the harness's `preflight <step>` mode, no result upload:
its envelope, links only, comes from the log), after which the step waits with no pod up. The
manifest records `preflight: true` on the step (part of its version); `describe`, the types and
reconcile refuse one on a step without an external input.

**Linking to where the answer is given.** A waiting step's handler hasn't run, so it has no
`ctx.link` links yet. `link` on the external input names where its link comes from: one of the same
step's regular (non-external) inputs, and a dot path inside that input's value (no path = the value
itself is the URL). It is data in the manifest, so the service resolves it without running
TypeScript:

```ts
input: {
  review: M.Review,                         // { run, url } from the step that submitted the screenshots
  verdict: external(Verdict, {
    title: "Screenshots accepted?",
    from: [callback()],
    link: { input: "review", path: "url", label: "Review in VR" },   // label defaults to the title
  }),
},
```

When the Waiting attempt is created, the service reads `review.url` from its `input.json` and sets
it as the attempt's link, shown on the step's tile and in the step view while it waits. A value that
is not an absolute http(s) URL (at most 2048 characters) sets no link and logs a warning; the step
waits all the same. Once the handler has run, its own links follow the waiting ones (a URL already
there is not repeated). `describe` (typed: `input` must be one of the step's regular input names)
and reconcile reject a link to anything but a regular input of the step. The link is part of what
the step declares, so changing it bumps the step's version.

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

A step with a preflight starts one step earlier:

```
Scheduled ─▶ InProgress (the preflight Job) ──returns──▶ Waiting … as above
                       │                    └─ every input already answered ─▶ Scheduled
                       └─ throws ─▶ Failed ("step '<step>' preflight failed: …")
```

- **The API shows the preflight as the attempt running**: `scheduled`, then `in-progress`, then
  `waiting`; no new status. Its Job is `olve-<attempt>-preflight` (the attempt's own Job keeps
  `olve-<attempt>`), and its log starts the attempt's log.
- **Its links** join the waiting links (deduplicated by URL), and the handler's follow them.
- **An answer during the preflight** (a tool that answers at once) is kept on the attempt and
  applied when the preflight succeeds. A decline during it gets 409.
- **Superseding.** A step runs one attempt at a time, so a newer run's preflight starts once an
  older run's preflight has ended. An attempt contends for its step when it is added and again when
  it starts waiting, so the older attempt becomes Obsolete as soon as its preflight succeeds. What
  the older preflight handed over is the tool's to cancel (VR's `--supersede`).
- **Reruns** run the preflight again; the callback URL is the run's, so it is the same URL.
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

- **Manifest.** An input entry `{ external: { model, title, description, from: [...], link?, timeout? } }`,
  `link` being `{ input, path?, label? }`; `preflight: true` on a step that runs a preflight.
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
  Waiting has a colour of its own (violet) on the step's tile, its status and the "waiting for input"
  chip in the pipeline list; the waiting link is on the tile and in the step view.
- **Events.** `attempt` events carry `waiting`; a new `input` event when a value arrives. A future
  alerting listener (Slack, phone) only needs the stream.

## 6. Visual regression with an external tool

The tool owns baselines, diffs and its review UI. The pipeline only hands work over and waits, in one
step:

```ts
.stage(test)                    // produces screenshots as files
.stage(visualReview)            // input: screenshots, verdict: external(Verdict, { from: [callback()] });
                                // its preflight posts the screenshots with ctx.inputUrl(...) and links the review
.stage(publish)
```

The tool compares against its baselines, auto-accepts when nothing changed (by calling the URL right
away), or lets a person review and then calls back with `{ "accepted": true }` or
`{ "$decline": "…" }`. "What counts as a difference" and "what about a first run without a
baseline" are the tool's rules.

## 7. Build order

Each step ships on its own.

1. **The primitive.** Waiting status, external inputs from `person()`, the API, `pl input`, UI form,
   events. (Built 2026-10-26.)
2. **`callback()`** with one-time URLs and `ctx.inputUrl`.
3. **`poll()`**, reusing the poll-trigger machinery.

## 8. Open questions

- Should a person's answer be restricted to certain users per input (`person({ users })`), or is the
  instance allow-list enough for now?
