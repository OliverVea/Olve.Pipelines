# AGENTS.md — mocks

Guidance for working on the UI mocks in `docs/mocks/`. These are static
HTML/CSS/JS prototypes of the Olve.Pipelines web UI, used to design and iterate
on screens *fast* before touching the real Lit + TypeScript client in
`frontend/`. See the repo-root `CLAUDE.md` for project-wide rules.

The pattern is borrowed from the QuestionBank project's `docs/mocks/`, adapted
for this app: a desktop developer/admin tool (not a phone-first PWA).

## Treat `mocks.css` as a small CSS library — don't copy-paste

`mocks.css` is the **shared stylesheet** for every mock. Before adding styles
for a new screen:

1. **Read the existing CSS first** — `mocks.css` and every per-screen `*.css`
   already present. Know what's there before writing.
2. **Reuse what exists.** If a component (status badge, pill, CI strip, commit
   badge, button, icon button, card) already fits, use its class — don't
   restyle from scratch.
3. **Lift reusable components up.** When two or more mocks need the same visual
   pattern, **promote it into `mocks.css`** under a documented class. Keep only
   genuinely screen-specific layout in the per-screen file.
4. **Use the design tokens.** Colors come from the `:root` variables in
   `mocks.css`. Don't hard-code hex values that duplicate a token.

## The mocks have their own palette (light theme)

The mocks use a **light theme** built on a navy-blue primary ramp with warm
gold and coral secondaries — deliberately distinct from the real frontend's
dark `frontend/src/styles/theme.css`.

Crucially, the **semantic token names match the real app's** (`--color-bg`,
`--color-surface`, `--color-primary`, `--color-text`, `--color-success`,
`--color-danger`, `--color-warning`, …). Only the raw hex behind each token
differs. That keeps a mock → Lit-component port near-mechanical: the markup and
class names translate directly; only the palette values change.

- Raw ramps (`--blue-*`, `--gold-*`, `--coral-*`) are the source palette.
  **Don't reach for them in screen CSS** — map through the semantic tokens so a
  re-tint is a one-line change in `mocks.css`.
- Status colors mirror the job statuses in `frontend/src/components/step-node.ts`:
  `idle / scheduled / running / done / failed / cancelled / obsolete`.

## What currently lives in `mocks.css` (shared)

- **Design tokens** — raw ramps + semantic tokens, radii, fonts, shadow.
- **Base reset** + base typography.
- **App shell chrome** — `.app`, `.app-header`, `.user-info`.
- **Buttons** — `.btn`, `.btn-primary`, `.btn-delete`, `.icon-btn` (+ `.spin`).
- **Pill** — `.pill` (+ `.ok` / `.error` / `.warn`) for the GitOps binding bar.
- **CI strip** — `.ci-strip` / `.ci-box` (status-colored) / `.ci-divider`,
  the at-a-glance step-health row on a pipeline card.
- **Commit badge** — `.commit-badge` (+ outcome classes), a mono short-hash
  chip linking to a GitHub commit.
- **Status badge** — `.status-badge` (+ status classes), the per-step job
  status pill on the flow diagram.
- **Load-in animation** — `.animate-in` + `.anim-*`, switched via `?anim=`.

Screen-specific layout (the card grid, the flow diagram, the log console)
lives in its per-screen stylesheet. When you reach for one of those on a
*second* screen, that's the signal to lift it into `mocks.css`.

## JS: keep it inline and disposable

These are mocks — most JS is per-screen demo data and throwaway stubs
(`alert('→ create pipeline')`). Real interaction logic belongs in the actual
client, not a polished mock helper. So keep mock JS **inline and disposable**;
don't build a shared interactivity library pre-emptively. The one shared script
is `footer.js` (the `?anim=` load-in switch), because it repeats on every page.
If another genuinely cross-page interactive pattern shows up, lift it the same
way; otherwise leave it inline.

## Screens & navigation

The mocks form a navigable flow with plain `window.location.href` /
`<a href>` links:

- **`index.html`** — the pipeline list. A responsive card grid (2 columns
  desktop, 1 on narrow). Each card: name + GitHub repo link, a CI-strip of
  step-health boxes, and a short commit-history of hash badges. An
  `+ Add pipeline` dashed card sits in the grid. A card's name links to →
- **`pipeline.html?id=…`** — one pipeline: the GitOps binding bar + the
  horizontal flow diagram (production parallel → artifact bundle → processing
  sequential), each step showing the run it last ran (numbered, newest-first).
  The connector INTO a processing step is the **promotion arrow**, carrying the
  brake + re-promote badges. A finished step's status badge links to →
- **`step.html?id=…&step=…`** — one step: status/timing, the promotion gate
  (brake + re-promote, processing only), the StepConfiguration (image, script,
  env), run history (newest-first), and a lazy-loaded tail of the most recent
  log. "View full logs" and each run-history row link to →
- **`logs.html?id=…&step=…&status=…&run=…&outcome=…`** — a job's full logs
  (hardcoded sample block; the real view streams + ANSI-renders). Back returns
  to the step when reached from `step.html`, else to the pipeline (the commit
  badges on `index.html` link here directly as `?id&run&outcome&repo`).

Wire new screens into this flow the same way.

### Promotion model (state, not config)

A **promotion** is the artifact bundle advancing INTO a processing step. Each
processing step has an `enabled` flag — the promotion gate. This is operational
**STATE**, not GitOps config: it is UI/API-mutable and present even on a bound
pipeline (config is git-owned; operations like enable/disable + re-promote are
API-allowed). `enabled` is **orthogonal** to the step's job result — a step can
be `done` AND have its promotion blocked. Two controls, shown as the
`.arrow-badge` pair (in `mocks.css`): a **brake** (block/unblock; `.on` = blocked)
and a **re-promote** (redrive the same bundle; hidden/disabled where there is no
bundle to redrive, i.e. `lastRun == null` or promotion blocked). Production steps
have no gate.

## Served by a tiny static server

Mocks are served over http, **not** opened from `file://`. From `frontend/`:

```
npm run mocks    # serves ../docs/mocks at http://localhost:4173 (+ LAN via --host)
```

(`sirv-cli`, a zero-config static server, added as a frontend devDependency.)

## Adding a new mock

1. Create `<name>.html`; link `mocks.css` first, then a `<name>.css` for
   screen-specific layout.
2. **Skeleton first:** ship the bare, navigable screen wired into the flow
   before any real interaction logic.
3. Reuse shared components; lift anything reusable into `mocks.css`.
4. Add `<script src="footer.js" defer></script>` before `</body>`.
5. Run `npm run mocks` and check it before calling it done.
