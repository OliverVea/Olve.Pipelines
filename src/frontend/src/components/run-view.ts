import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  pipelinesGet,
  runsAttempts,
  runsCancel,
  runsChanges,
  runsCriticalPath,
  runsGet,
  runStepsCancel,
  runStepsRerun,
  type Attempt,
  type Run,
  type RunChanges,
  type AttemptState,
} from '@olve/api';
import { api, errorMessages } from '../api/client.js';
import { downloadFile, downloadName } from '../api/files.js';
import './attempt-files.js';
import {
  sharedStyles,
  headerStyles,
  statusCardStyles,
  toneOf,
  statusLabel,
  formatDuration,
  formatSeconds,
  median,
  shortSha,
  navClick,
  pipelinePath,
  criticalPathPath,
  versionsPath,
  runPath,
  stepPath,
  stepLogsPath,
  isLiveStatus,
} from '../styles/ui.js';
import {
  loadEffective,
  loadVersion,
  type EffectivePipeline,
  type LoadedVersion,
} from '../model/versions.js';
import {
  type Manifest,
  stepsByName,
  stepInputSchema,
  outputSchemaOf,
} from '../model/manifest.js';
import type { StepState } from './stage-graph.js';
import type { FileRefValue } from './typed-value.js';
import { sheetContentStyles } from './step-sheet.js';
import './stage-graph.js';
import './step-sheet.js';
import './typed-value.js';
import './log-pane.js';
import './external-input-panel.js';
import { renderAnswers } from './external-input-panel.js';
import './relative-time.js';
import './matrix-cells.js';
import './run-changes.js';
import {
  type Cell,
  type CellState,
  cellAddress,
  matrixOf,
  stepOf,
} from '../model/matrix.js';
import { LiveController } from '../api/events.js';

type AttemptStatusFields = {
  type?: AttemptState;
  startedAt?: string | null;
  finishedAt?: string | null;
  cancelledAt?: string | null;
  reason?: string | null;
};

function attemptStatus(a: Attempt): AttemptStatusFields {
  return a.status as AttemptStatusFields;
}

function parseJson(s: string | null | undefined): unknown {
  if (!s) return undefined;
  try {
    return JSON.parse(s);
  } catch {
    return s;
  }
}

/**
 * One run: a card of its version, trigger, start, critical path and input, what it changes since the
 * newest older run that succeeded, the pinned version's stage graph coloured by step status, and per
 * step the attempts with typed input/output, files and logs. Actions: cancel, trigger again (on the
 * pipeline page), cancel a step, rerun a step (optionally continuing the run).
 */
@customElement('run-view')
export class RunView extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
    statusCardStyles,
    sheetContentStyles,
    css`
      .dur {
        font-size: 0.85rem;
        font-variant-numeric: tabular-nums;
      }
      .actions {
        display: flex;
        gap: 0.4rem;
        flex-wrap: wrap;
      }
      .meta {
        display: flex;
        flex-wrap: wrap;
        gap: 0.3rem 1.2rem;
        margin: 0.6rem 0 0;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .meta b {
        font-weight: 500;
        color: var(--color-text);
      }
      .note {
        margin: 0.6rem 0 0;
        font-size: 0.82rem;
        padding: 0.4rem 0.7rem;
        border-left: 3px solid var(--color-warning);
        background: color-mix(in srgb, var(--color-warning) 8%, transparent);
        border-radius: 0 var(--radius) var(--radius) 0;
      }
      .graph {
        margin-top: 1rem;
      }
      .graph h3 {
        margin-bottom: 0.4rem;
      }
      .attempts {
        display: flex;
        gap: 0.3rem;
        flex-wrap: wrap;
        margin-bottom: 0.6rem;
      }
      .attempt-tab {
        display: inline-flex;
        align-items: center;
        gap: 0.35rem;
        padding: 0.2rem 0.6rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: transparent;
        color: var(--color-text);
        font-size: 0.8rem;
      }
      .attempt-tab[aria-selected='true'] {
        border-color: var(--color-primary);
        background: var(--color-surface-hover);
      }
      .io {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: 0 1.5rem;
      }
      @media (max-width: 760px) {
        .io {
          grid-template-columns: 1fr;
        }
      }
      .sheet-h code {
        font-weight: 500;
      }
      .reason {
        font-size: 0.82rem;
        margin: 0.5rem 0 0;
      }
      log-pane {
        --log-max-height: 22rem;
      }
      .links {
        font-size: 0.8rem;
      }
      .status button.value {
        padding: 0;
        border: 0;
        background: transparent;
        text-align: left;
        color: var(--color-primary);
      }
      .status button.value::after {
        content: ' ▸';
        font-size: 0.75rem;
        color: var(--color-text-muted);
      }
      .status button.value[aria-expanded='true']::after {
        content: ' ▾';
      }
      .status button.fields {
        display: inline-flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0 0.35rem;
        font-weight: 500;
        color: var(--color-text);
      }
      .fields .muted {
        font-weight: 400;
        font-size: 0.85rem;
      }
      .panel.more-info {
        margin-top: 0.5rem;
        box-shadow: var(--shadow);
        font-size: 0.85rem;
      }
      .more-info .sheet-h {
        margin-top: 0;
      }
      .more-info pre {
        margin: 0;
        padding: 0.6rem 0.8rem;
        font-size: 0.78rem;
        background: var(--color-code-bg);
        border-radius: var(--radius);
        overflow-x: auto;
      }
      .event {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: 0.8rem 1.5rem;
      }
      @media (max-width: 760px) {
        .event {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      run-changes {
        margin-top: 1rem;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare run: string;

  @state() declare private _detail: Run | null;
  @state() declare private _attempts: Attempt[];
  @state() declare private _version: LoadedVersion | null;
  /** The run's pinned version as its pinned config snapshot ran it. */
  @state() declare private _effective: EffectivePipeline | null;
  @state() declare private _pipelineName: string;
  @state() declare private _error: string | null;
  @state() declare private _actionErrors: string[];
  @state() declare private _busy: boolean;
  @state() declare private _step: string;
  @state() declare private _attempt: number | null;
  @state() declare private _changes: RunChanges | null;
  /** This run's critical path and the pipeline's median, in seconds. */
  @state() declare private _critical: { run: number | null; median: number | null };
  @state() declare private _panel: '' | 'trigger' | 'input';

  private _loading = false;
  /** Set while a click selects a step, so the same click doesn't count as a click on nothing. */
  private _picked = false;

  constructor() {
    super();
    new LiveController(
      this,
      () => {
        if (!this._loading) this.reload();
      },
      (n) =>
        n.pipelineId === this.pipelineId &&
        (((n.type === 'run' || n.type === 'attempt') && String(n.run) === this.run) ||
          n.type === 'blocked' ||
          n.type === 'version'),
    );
    this.pipelineId = '';
    this.run = '';
    this._detail = null;
    this._attempts = [];
    this._version = null;
    this._pipelineName = '';
    this._error = null;
    this._actionErrors = [];
    this._busy = false;
    const q = new URLSearchParams(location.search);
    this._step = q.get('step') ?? '';
    this._attempt = q.get('attempt') ? Number(q.get('attempt')) : null;
    this._changes = null;
    this._critical = { run: null, median: null };
    this._panel = '';
    this.addEventListener('click', this._clickAway);
  }

  /** A click on nothing (no control, no code, not the sheet) closes the step sheet. */
  private _clickAway = (e: Event) => {
    if (this._picked) {
      this._picked = false;
      return;
    }
    if (!this._step) return;
    const hit = e
      .composedPath()
      .some(
        (n) =>
          n instanceof Element &&
          (n.localName === 'step-sheet' ||
            n.matches('a, button, input, select, textarea, pre, dialog, label')),
      );
    if (!hit) this._selectStep('');
  };

  reload() {
    return this._load();
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('run')) {
      this._detail = null;
      this._version = null;
      this._changes = null;
      this._critical = { run: null, median: null };
      this._panel = '';
      this._actionErrors = [];
      const q = new URLSearchParams(location.search);
      this._step = q.get('step') ?? '';
      this._attempt = q.get('attempt') ? Number(q.get('attempt')) : null;
      this._load();
      pipelinesGet({ client: api, path: { pipelineId: this.pipelineId } })
        .then((r) => (this._pipelineName = r.data?.name ?? ''))
        .catch(() => {});
    }
  }

  private get _path() {
    return { pipelineId: this.pipelineId, number: Number(this.run) };
  }

  private async _load() {
    // A response for a run we've navigated away from must not overwrite the current one.
    const key = `${this.pipelineId}#${this.run}`;
    const current = () => key === `${this.pipelineId}#${this.run}`;
    this._loading = true;
    try {
      const [detail, attempts] = await Promise.all([
        runsGet({ client: api, path: this._path }).then((r) => r.data),
        runsAttempts({ client: api, path: this._path }).then((r) => r.data),
      ]);
      if (!current()) return;
      this._detail = detail ?? null;
      this._attempts = attempts ?? [];
      this._error = null;
      if (detail) this._loadAround(detail, current);
      const vn = detail?.versionNumber;
      if (vn !== undefined && vn !== null && this._version?.number !== vn) {
        const version = await loadVersion(this.pipelineId, vn);
        const effective = version
          ? await loadEffective(this.pipelineId, version, detail?.configSnapshot)
          : null;
        if (!current()) return;
        this._version = version;
        this._effective = effective;
      }
    } catch (e) {
      if (current()) this._error = errorMessages(e).join(' ');
    } finally {
      this._loading = false;
    }
  }

  /** What the run changes and its critical path: extras that never hold the page up. */
  private async _loadAround(detail: Run, current: () => boolean) {
    const n = detail.number;
    const finished = detail.status !== 'Running';
    const [changes, own, recent] = await Promise.all([
      runsChanges({
        client: api,
        path: { pipelineId: this.pipelineId },
        query: { from: n, to: n },
      })
        .then((r) => r.data?.items?.[0] ?? null)
        .catch(() => null),
      finished
        ? runsCriticalPath({
            client: api,
            path: { pipelineId: this.pipelineId },
            query: { from: n, to: n, includeUnsuccessful: true },
          })
            .then((r) => r.data?.runs?.[0]?.seconds ?? null)
            .catch(() => null)
        : null,
      runsCriticalPath({ client: api, path: { pipelineId: this.pipelineId } })
        .then((r) => r.data?.runs ?? [])
        .catch(() => []),
    ]);
    if (!current()) return;
    this._changes = changes;
    this._critical = {
      run: own,
      median: recent.length ? median(recent.map((x) => x.seconds ?? 0)) : null,
    };
  }

  private _stepAttempts(step: string): Attempt[] {
    return this._attempts
      .filter((a) => a.stepName === step)
      .sort((a, b) => (a.number ?? 0) - (b.number ?? 0));
  }

  private _selectedAttempt(): Attempt | null {
    const list = this._stepAttempts(this._step);
    if (!list.length) return null;
    return list.find((a) => a.number === this._attempt) ?? list[list.length - 1];
  }

  /** The run's pipeline: its pinned version with selects resolved by its pinned config; the version itself if that fails. */
  private _runManifest(): Manifest | null {
    return this._effective?.ok
      ? this._effective.manifest
      : (this._version?.manifest ?? null);
  }

  private _selectStep(step: string, attempt: number | null = null) {
    this._step = step;
    this._attempt = attempt;
    const q = new URLSearchParams();
    if (step) q.set('step', step);
    if (step && attempt) q.set('attempt', String(attempt));
    history.replaceState(
      history.state,
      '',
      `${location.pathname}${q.size ? `?${q}` : ''}`,
    );
  }

  render() {
    if (this._error && !this._detail) return html`<p class="error">${this._error}</p>`;
    const d = this._detail;
    if (!d) return html`<p class="muted">Loading run…</p>`;
    const r = d;
    const input = d.input;
    const m = this._runManifest();
    const states = new Map<string, StepState>();
    for (const stage of d.stages ?? []) {
      for (const s of stage.steps ?? []) {
        const cells: CellState[] | undefined = s.cells?.map((c) => ({
          cell: c.cell,
          status: c.status,
          reason: c.reason,
          attemptCount: c.attemptCount,
          latestAttempt: c.latestAttempt,
          startedAt: c.startedAt,
          finishedAt: c.finishedAt,
          logsHref: c.latestAttempt
            ? stepLogsPath(this.pipelineId, r.number, c.name, c.latestAttempt)
            : undefined,
        }));
        states.set(s.name ?? '', {
          status: s.status ?? '',
          reason: s.reason,
          attemptCount: s.attemptCount ?? 0,
          latestAttempt: s.latestAttempt,
          startedAt: s.startedAt,
          finishedAt: s.finishedAt,
          blocked: s.blocked ?? false,
          links: s.links,
          ...(cells ? { cells } : {}),
        });
        for (const c of s.cells ?? [])
          states.set(c.name, {
            status: c.status,
            reason: c.reason,
            attemptCount: c.attemptCount,
            latestAttempt: c.latestAttempt,
            startedAt: c.startedAt,
            finishedAt: c.finishedAt,
            blocked: s.blocked ?? false,
            links: c.links,
          });
      }
    }
    const running = r.status === 'Running';
    const sheet = !!m && !!this._step;
    const errors = this._actionErrors.length
      ? html`<div class="errors" role="alert">
          ${this._actionErrors.map((e) => html`<p>${e}</p>`)}
        </div>`
      : nothing;
    return html`
      <section>
        <div class="line">
          <h2>
            <a class="crumb" href=${pipelinePath(this.pipelineId)} @click=${navClick}
              >${this._pipelineName || 'Pipeline'}</a
            ><span class="sep">/</span>Run #${r.number}
          </h2>
          <span class="st t-${toneOf(r.status)}"
            ><span class="dot"></span>${statusLabel(r.status)}</span
          >
          <span class="dur muted">${formatDuration(r.createdAt, r.finishedAt)}</span>
          ${this._renderLinks(r)}
          <span class="spacer"></span>
          ${running
            ? html`<button
                class="btn danger small"
                ?disabled=${this._busy}
                @click=${this._cancel}
              >
                Cancel run
              </button>`
            : nothing}
          <a
            class="btn small"
            href="${pipelinePath(this.pipelineId)}?rerun=${r.number}"
            @click=${navClick}
            >Trigger again</a
          >
          <i class="br"></i>
        </div>
        ${this._renderCard(r, input)}
        ${this._panel === 'trigger'
          ? this._renderTrigger(r)
          : this._panel === 'input'
            ? this._renderInput(input, m)
            : nothing}
        ${r.heldAt && running
          ? html`<p class="note">
              Held before <b>${r.heldAt}</b>: the step is blocked. Unblock it on the
              <a href=${pipelinePath(this.pipelineId)} @click=${navClick}
                >pipeline page</a
              >
              to let the run continue.
            </p>`
          : nothing}
        ${this._renderWaiting(states, m)} ${sheet ? nothing : errors}
      </section>

      <run-changes
        .pipelineId=${this.pipelineId}
        .changes=${this._changes}
        .versionNumber=${r.versionNumber}
        .configSnapshot=${r.configSnapshot ?? null}
      ></run-changes>

      <section class="graph">
        <h3>Stages</h3>
        ${m
          ? html`<stage-graph
              .manifest=${m}
              .cases=${this._effective?.ok ? this._effective.cases : null}
              .states=${states}
              .selected=${sheet ? stepOf(this._step) : ''}
              @step-select=${(e: CustomEvent<{ step: string; cell?: Cell }>) => {
                this._picked = true;
                this._selectStep(
                  e.detail.cell
                    ? cellAddress(e.detail.step, e.detail.cell)
                    : e.detail.step,
                );
              }}
            ></stage-graph>`
          : html`<p class="muted">Loading the pinned version…</p>`}
      </section>

      ${sheet ? this._renderSheet(m, states.get(this._step), errors) : nothing}
    `;
  }

  /** A form per step that waits for input, on the page itself so it's seen first. */
  private _renderWaiting(states: Map<string, StepState>, m: Manifest | null) {
    if (!m) return nothing;
    return [...states]
      .filter(([, s]) => s.status === 'waiting')
      .map(([step]) => {
        const list = this._stepAttempts(step);
        return html`<external-input-panel
          .pipelineId=${this.pipelineId}
          .run=${this.run}
          .step=${step}
          .manifest=${m}
          .attempt=${list[list.length - 1] ?? null}
          @answered=${() => this._load()}
        ></external-input-panel>`;
      });
  }

  /** Rerun of, superseded by and held at, next to the status. */
  private _renderLinks(r: Run) {
    const link = (n: number) =>
      html`<a href=${runPath(this.pipelineId, n)} @click=${navClick}>#${n}</a>`;
    return [
      r.rerunOfNumber
        ? html`<span class="links muted">rerun of ${link(r.rerunOfNumber)}</span>`
        : nothing,
      r.supersededByNumber
        ? html`<span class="links muted"
            >superseded by ${link(r.supersededByNumber)}</span
          >`
        : nothing,
      r.heldAt && r.status === 'Running'
        ? html`<span class="links muted">held at <b>${r.heldAt}</b></span>`
        : nothing,
    ];
  }

  private _togglePanel(panel: 'trigger' | 'input') {
    this._panel = this._panel === panel ? '' : panel;
  }

  private _renderCard(r: Run, input: Record<string, unknown>) {
    const o = r.origin;
    const fields = Object.entries(input ?? {});
    const critical = this._critical;
    return html`<div class="status">
      <div class="cell">
        <span class="label">Version</span>
        <a class="value" href=${versionsPath(this.pipelineId)} @click=${navClick}
          >v${r.versionNumber}</a
        >
        <span class="sub"
          >${r.commitSha
            ? html`built from <span class="mono">${shortSha(r.commitSha)}</span>`
            : nothing}${r.commitSha && r.configSnapshot
            ? ' · '
            : nothing}${r.configSnapshot ? `config #${r.configSnapshot}` : nothing}</span
        >
      </div>
      <div class="cell">
        <span class="label">Trigger</span>
        <button
          class="value"
          aria-expanded=${this._panel === 'trigger'}
          @click=${() => this._togglePanel('trigger')}
        >
          ${o?.trigger ?? (r.rerunOfNumber ? 'rerun' : 'manual')}
        </button>
        <span class="sub"
          >${o?.via ? `via ${o.via}` : o?.startedBy ? `by ${o.startedBy}` : nothing}</span
        >
      </div>
      <div class="cell">
        <span class="label">Started</span>
        <span class="value"><relative-time .value=${r.createdAt}></relative-time></span>
        <span class="sub mono">${r.versionStamp}</span>
      </div>
      <div class="cell">
        <span class="label">Critical path</span>
        <a class="value" href=${criticalPathPath(this.pipelineId)} @click=${navClick}
          >${critical.run === null ? '—' : formatSeconds(critical.run)}</a
        >
        <span class="sub"
          >${critical.median === null
            ? nothing
            : `median ${formatSeconds(critical.median)}`}</span
        >
      </div>
      <div class="cell grow">
        <span class="label">Input</span>
        <button
          class="value fields"
          aria-expanded=${this._panel === 'input'}
          @click=${() => this._togglePanel('input')}
        >
          ${fields.length
            ? fields
                .slice(0, 4)
                .map(([k, v]) => html`<span class="muted">${k}</span> ${brief(v)}`)
            : html`<span class="muted">none</span>`}
        </button>
        <span class="sub">${fields.length} field${fields.length === 1 ? '' : 's'}</span>
      </div>
    </div>`;
  }

  /** The event the trigger received and how it reached the run, or who started it. */
  private _renderTrigger(r: Run) {
    const o = r.origin;
    if (!o?.trigger)
      return html`<div class="panel more-info">
        <p class="muted" style="margin:0">
          Started by <b>${o?.startedBy ?? 'unknown'}</b>${r.rerunOfNumber
            ? html` as a rerun of
                <a href=${runPath(this.pipelineId, r.rerunOfNumber)} @click=${navClick}
                  >#${r.rerunOfNumber}</a
                >`
            : nothing}.
        </p>
      </div>`;
    const firstStep = this._attempts
      .map((a) => attemptStatus(a).startedAt)
      .filter((t): t is string => !!t)
      .sort()[0];
    const marks: [string, string | null | undefined][] = [
      ['Pushed', o.pushedAt],
      [o.via === 'poll' ? 'Poll checked' : 'Webhook received', o.noticedAt],
      ['Config ready', o.configReadyAt],
      ['Mapped', o.mapFinishedAt],
      ['First step started', firstStep],
    ];
    let previous: number | null = null;
    return html`<div class="panel more-info">
      <div class="event">
        <div>
          <h4 class="sheet-h">Event</h4>
          ${o.event === null || o.event === undefined
            ? html`<p class="muted">Not kept: it was larger than 64 KB.</p>`
            : html`<pre class="mono">${JSON.stringify(o.event, null, 2)}</pre>`}
        </div>
        <div>
          <h4 class="sheet-h">Timeline</h4>
          <dl class="kv">
            ${marks
              .filter(([, t]) => !!t)
              .map(([label, t]) => {
                const at = new Date(t!).getTime();
                const gap =
                  previous === null ? '' : `+${formatSeconds((at - previous) / 1000)}`;
                previous = at;
                return html`<dt>${label}</dt>
                  <dd>
                    <span class="mono">${new Date(at).toLocaleTimeString()}</span>
                    <span class="muted">${gap}</span>
                  </dd>`;
              })}
          </dl>
        </div>
      </div>
    </div>`;
  }

  private _renderInput(input: Record<string, unknown>, m: Manifest | null) {
    return html`<div class="panel more-info">
      <h4 class="sheet-h">Input</h4>
      <typed-value
        .value=${input}
        .schema=${m ? outputSchemaOf(m, m.input) : null}
      ></typed-value>
    </div>`;
  }

  private _renderSheet(m: Manifest, st: StepState | undefined, errors: unknown) {
    const step = stepsByName(m).get(stepOf(this._step));
    const attempts = this._stepAttempts(this._step);
    const a = this._selectedAttempt();
    const as = a ? attemptStatus(a) : null;
    const continuable = this._detail?.continuable ?? false;
    const active = attempts.some((x) =>
      ['waiting', 'scheduled', 'in-progress'].includes(attemptStatus(x).type ?? ''),
    );
    return html`<step-sheet
      .pipelineId=${this.pipelineId}
      .manifest=${m}
      .step=${this._step}
      .state=${st}
      @close=${() => this._selectStep('')}
      @step-select=${(e: CustomEvent<{ step: string }>) =>
        this._selectStep(e.detail.step)}
    >
      <span slot="actions" style="display:contents">
        ${a
          ? html`<a
              class="btn small"
              href=${stepLogsPath(
                this.pipelineId,
                Number(this.run),
                this._step,
                a.number,
              )}
              @click=${navClick}
              >Logs</a
            >`
          : nothing}
        ${active
          ? html`<button
              class="btn small danger"
              ?disabled=${this._busy}
              @click=${this._cancelStep}
            >
              Cancel step
            </button>`
          : nothing}
        <button
          class="btn small"
          ?disabled=${this._busy || !continuable}
          @click=${this._rerunStep}
        >
          Rerun
        </button>
        <a
          class="btn small"
          href=${stepPath(this.pipelineId, stepOf(this._step))}
          @click=${navClick}
          >Step page</a
        >
      </span>
      ${step && matrixOf(step) && step.name === this._step
        ? nothing
        : html`<div slot="attempt">
            <h4 class="sheet-h">Attempts</h4>
            ${attempts.length
              ? html`<div class="attempts" role="tablist" aria-label="Attempts">
                  ${attempts.map((x) => {
                    const t = attemptStatus(x).type ?? '';
                    return html`<button
                      class="attempt-tab"
                      role="tab"
                      aria-selected=${x.id === a?.id}
                      @click=${() => this._selectStep(this._step, x.number ?? null)}
                    >
                      <span class="dot t-${toneOf(t)}"></span>@${x.number}
                      ${statusLabel(t)}
                    </button>`;
                  })}
                </div>`
              : html`<p class="muted">
                  No attempts${st ? ` (${statusLabel(st.status)})` : ''}.
                </p>`}
            ${a && as
              ? html`<dl class="kv">
                    <dt>Created</dt>
                    <dd><relative-time .value=${a.createdAt}></relative-time></dd>
                    ${as.startedAt
                      ? html`<dt>Ran</dt>
                          <dd>
                            ${formatDuration(
                              as.startedAt,
                              as.finishedAt ?? as.cancelledAt,
                            )}
                          </dd>`
                      : nothing}
                    ${a.inputHash
                      ? html`<dt>Input hash</dt>
                          <dd class="mono">${a.inputHash.slice(7, 17)}</dd>`
                      : nothing}
                  </dl>
                  ${as.reason && as.reason !== st?.reason
                    ? html`<p class="reason">
                        <b>${as.type === 'skipped' ? 'Skipped' : 'Reason'}:</b>
                        ${as.reason}
                      </p>`
                    : nothing}
                  ${renderAnswers(a)}`
              : nothing}
          </div>`}
      ${this._renderCells(m, st)} ${errors}
      ${a && as?.type === 'waiting'
        ? html`<p class="reason">
            Waiting for input: answer it in the form above the stages.
          </p>`
        : a && as
          ? html`<div class="io">
                <div>
                  <h4 class="sheet-h">Input</h4>
                  ${a.input
                    ? html`<typed-value
                        .value=${parseJson(a.input)}
                        .schema=${step ? stepInputSchema(m, step) : null}
                        @file-download=${this._onFileRef}
                      ></typed-value>`
                    : html`<span class="muted">Not assembled.</span>`}
                </div>
                <div>
                  <h4 class="sheet-h">
                    Output${step?.output ? html` <code>${step.output}</code>` : nothing}
                  </h4>
                  ${a.output?.json
                    ? html`<typed-value
                        .value=${parseJson(a.output.json)}
                        .schema=${outputSchemaOf(m, step?.output)}
                        @file-download=${this._onFileRef}
                      ></typed-value>`
                    : html`<span class="muted"
                        >${step && !step.output
                          ? 'This step declares no output.'
                          : 'No output.'}</span
                      >`}
                </div>
              </div>
              ${as.type === 'succeeded'
                ? html`<attempt-files
                    .pipelineId=${this.pipelineId}
                    .run=${Number(this.run)}
                    .step=${a.stepName ?? ''}
                    .attempt=${a.number ?? 0}
                  ></attempt-files>`
                : nothing}
              <h4 class="sheet-h">Logs</h4>
              <log-pane
                .address=${{ ...this._path, step: this._step, attempt: a.number }}
                ?live=${isLiveStatus(as.type)}
              ></log-pane>`
          : nothing}
    </step-sheet>`;
  }

  private async _act(fn: () => Promise<void>) {
    this._busy = true;
    this._actionErrors = [];
    try {
      await fn();
    } catch (e) {
      this._actionErrors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }

  private _cancel = () =>
    this._act(async () => {
      if (!confirm(`Cancel run #${this.run}? Running steps are stopped.`)) return;
      await runsCancel({ client: api, path: this._path });
      await this._load();
    });

  private _cancelStep = () =>
    this._act(async () => {
      const step = this._step;
      if (
        !confirm(
          `Cancel step ${step} in run #${this.run}? The run ends Cancelled once its stage settles.`,
        )
      )
        return;
      await runStepsCancel({ client: api, path: { ...this._path, step } });
      await this._load();
      this._selectStep(step, null);
    });

  /** A matrix step's sheet lists its cells; picking one opens the cell's own sheet. */
  private _renderCells(m: Manifest, st: StepState | undefined) {
    const def = stepsByName(m).get(this._step);
    const matrix = def && matrixOf(def);
    if (!matrix) return nothing;
    return html`<h4 class="sheet-h">Cells</h4>
      <matrix-cells
        .matrix=${matrix}
        .cells=${st?.cells ?? null}
        .step=${this._step}
        @cell-select=${(e: CustomEvent<{ cell: Cell }>) =>
          this._selectStep(cellAddress(this._step, e.detail.cell))}
        @cell-rerun=${(e: CustomEvent<{ cell: Cell }>) => {
          this._step = cellAddress(this._step, e.detail.cell);
          this._rerunStep();
        }}
      ></matrix-cells>`;
  }

  /** A rerun in a failed or cancelled run continues the run; in a succeeded one it reruns the step only. */
  private _rerunStep = () =>
    this._act(async () => {
      const step = this._step;
      const status = this._detail?.status;
      const { data: a } = await runStepsRerun({
        client: api,
        path: { ...this._path, step },
        body: { continue: status === 'Failed' || status === 'Cancelled' },
      });
      await this._load();
      this._selectStep(step, a?.number ?? null);
    });

  private _onFileRef = (e: CustomEvent<{ ref: FileRefValue }>) => {
    const { run: number, step, attempt, path } = e.detail.ref;
    const file = { pipelineId: this.pipelineId, number, step, attempt, path };
    this._act(() => downloadFile(file, downloadName(path)));
  };
}

/** A value short enough for the card: a commit hash shortened, long text cut. */
function brief(v: unknown): string {
  if (typeof v === 'string')
    return /^[0-9a-f]{40}$/.test(v)
      ? v.slice(0, 7)
      : v.length > 24
        ? `${v.slice(0, 23)}…`
        : v;
  if (v === null || typeof v !== 'object') return String(v);
  return Array.isArray(v) ? `[${v.length}]` : '{…}';
}

declare global {
  interface HTMLElementTagNameMap {
    'run-view': RunView;
  }
}
