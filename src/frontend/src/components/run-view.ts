import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import {
  pipelinesGet,
  runsAttempts,
  runsCancel,
  runsContinue,
  runsGet,
  runsRerun,
  runStepsCancel,
  runStepsFiles,
  runStepsRerun,
  type Attempt,
  type Run,
  type StepFile,
  type AttemptState,
} from '@olve/api';
import { api, errorMessages } from '../api/client.js';
import { downloadFile } from '../api/files.js';
import { navigate } from '../router.js';
import {
  sharedStyles,
  headerStyles,
  detailsOpen,
  rememberDetails,
  toneOf,
  statusLabel,
  formatDuration,
  formatSize,
  shortSha,
  navClick,
  pipelinePath,
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
  inputSchemaOf,
} from '../model/manifest.js';
import type { StepState } from './stage-graph.js';
import type { FileRefValue } from './typed-value.js';
import type { InputForm } from './input-form.js';
import { sheetContentStyles } from './step-sheet.js';
import './stage-graph.js';
import './step-sheet.js';
import './typed-value.js';
import './input-form.js';
import './log-pane.js';
import './external-input-panel.js';
import { renderAnswers } from './external-input-panel.js';
import './relative-time.js';
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
 * One run: its input, the pinned version's stage graph coloured by step status,
 * and per step the attempts with typed input/output, files and logs. Actions:
 * cancel, rerun the run, continue with changed input, cancel a step, rerun a step (optionally
 * continuing the run).
 */
@customElement('run-view')
export class RunView extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
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
      .continue-help {
        font-size: 0.82rem;
        color: var(--color-text-muted);
        margin: 0 0 0.8rem;
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
  @state() declare private _files: StepFile[] | null;
  @state() declare private _filesError: string | null;
  @state() declare private _continueErrors: string[];
  @state() declare private _detailsOpen: boolean;

  @query('#continue-dialog') declare private _continueDialog: HTMLDialogElement;
  @query('#continue-form') declare private _continueForm: InputForm;

  private _loading = false;
  private _filesKey = '';

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
    this._files = null;
    this._filesError = null;
    this._continueErrors = [];
    this._detailsOpen = detailsOpen();
  }

  reload() {
    return this._load();
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('run')) {
      this._detail = null;
      this._version = null;
      this._actionErrors = [];
      this._filesKey = '';
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
      this._loadFiles();
    } catch (e) {
      if (current()) this._error = errorMessages(e).join(' ');
    } finally {
      this._loading = false;
    }
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

  private async _loadFiles() {
    const a = this._selectedAttempt();
    const st = a ? attemptStatus(a).type : null;
    const key = a ? `${a.id}:${st}` : '';
    if (key === this._filesKey) return;
    this._filesKey = key;
    this._files = null;
    this._filesError = null;
    if (!a || st !== 'succeeded') return;
    try {
      const res = await runStepsFiles({
        client: api,
        path: { ...this._path, step: a.stepName },
        query: { attempt: a.number },
      });
      if (this._filesKey === key) this._files = res.data?.files ?? [];
    } catch (e) {
      if (this._filesKey === key) this._filesError = errorMessages(e).join(' ');
    }
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
    this._loadFiles();
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
        states.set(s.name ?? '', {
          status: s.status ?? '',
          reason: s.reason,
          attemptCount: s.attemptCount ?? 0,
          latestAttempt: s.latestAttempt,
          startedAt: s.startedAt,
          finishedAt: s.finishedAt,
          blocked: s.blocked ?? false,
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
          <button
            class="more"
            aria-expanded=${this._detailsOpen}
            aria-controls="details"
            @click=${this._toggleDetails}
          >
            Details
          </button>
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
          <i class="br"></i>
        </div>
        <div class="details" id="details" ?hidden=${!this._detailsOpen}>
          <div class="actions">
            <button class="btn small" ?disabled=${this._busy} @click=${this._rerunRun}>
              Rerun run
            </button>
            <button
              class="btn primary small"
              ?disabled=${this._busy || !d.continuable}
              @click=${this._openContinue}
            >
              Continue…
            </button>
          </div>
          <div class="meta">
            <span>Version <b>v${r.versionNumber}</b></span>
            <span>Stamp <b class="mono">${r.versionStamp}</b></span>
            <span>Trigger <b>${(r.triggerKind ?? '').toLowerCase()}</b></span>
            ${d.commitSha
              ? html`<span
                  >Pipeline commit <b class="mono">${shortSha(d.commitSha)}</b></span
                >`
              : nothing}
            <span
              >Started <b><relative-time .value=${r.createdAt}></relative-time></b
            ></span>
            ${r.heldAt ? html`<span>Held at <b>${r.heldAt}</b></span>` : nothing}
            ${r.rerunOfNumber
              ? html`<span
                  >Rerun of
                  <a href=${runPath(this.pipelineId, r.rerunOfNumber)} @click=${navClick}
                    >#${r.rerunOfNumber}</a
                  ></span
                >`
              : nothing}
            ${r.supersededByNumber
              ? html`<span
                  >Superseded by
                  <a
                    href=${runPath(this.pipelineId, r.supersededByNumber)}
                    @click=${navClick}
                    >#${r.supersededByNumber}</a
                  ></span
                >`
              : nothing}
          </div>
          ${!d.continuable && r.status !== 'Superseded'
            ? html`<p class="note">
                This run's version (v${r.versionNumber}) is no longer active, so it can't
                be continued. Trigger a new run on the current version instead.
              </p>`
            : nothing}
          <h4 class="sheet-h">Input</h4>
          <typed-value
            .value=${input}
            .schema=${m ? outputSchemaOf(m, m.input) : null}
          ></typed-value>
        </div>
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

      <section class="graph">
        <h3>Stages</h3>
        ${m
          ? html`<stage-graph
              .manifest=${m}
              .cases=${this._effective?.ok ? this._effective.cases : null}
              .states=${states}
              .selected=${sheet ? this._step : ''}
              @step-select=${(e: CustomEvent<{ step: string }>) =>
                this._selectStep(e.detail.step)}
            ></stage-graph>`
          : html`<p class="muted">Loading the pinned version…</p>`}
      </section>

      ${sheet ? this._renderSheet(m, states.get(this._step), errors) : nothing}
      ${this._renderContinueDialog(input)}
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

  private _toggleDetails = () => {
    this._detailsOpen = !this._detailsOpen;
    rememberDetails(this._detailsOpen);
  };

  private _renderSheet(m: Manifest, st: StepState | undefined, errors: unknown) {
    const step = stepsByName(m).get(this._step);
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
          @click=${() => this._rerunStep(false)}
        >
          Rerun step
        </button>
        <button
          class="btn small"
          ?disabled=${this._busy || !continuable}
          @click=${() => this._rerunStep(true)}
        >
          Rerun and continue
        </button>
        <a
          class="btn small"
          href=${stepPath(this.pipelineId, this._step)}
          @click=${navClick}
          >Step page</a
        >
      </span>
      <div slot="attempt">
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
                  <span class="dot t-${toneOf(t)}"></span>@${x.number} ${statusLabel(t)}
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
                        ${formatDuration(as.startedAt, as.finishedAt ?? as.cancelledAt)}
                      </dd>`
                  : nothing}
                ${a.inputHash
                  ? html`<dt>Input hash</dt>
                      <dd class="mono">${a.inputHash.slice(7, 17)}</dd>`
                  : nothing}
              </dl>
              ${as.reason && as.reason !== st?.reason
                ? html`<p class="reason">
                    <b>${as.type === 'skipped' ? 'Skipped' : 'Reason'}:</b> ${as.reason}
                  </p>`
                : nothing}
              ${renderAnswers(a)}`
          : nothing}
      </div>
      ${errors}
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
              ${this._renderFiles(a)}
              <h4 class="sheet-h">Logs</h4>
              <log-pane
                .address=${{ ...this._path, step: this._step, attempt: a.number }}
                ?live=${isLiveStatus(as.type)}
              ></log-pane>`
          : nothing}
    </step-sheet>`;
  }

  private _renderFiles(a: Attempt) {
    if (attemptStatus(a).type !== 'succeeded') return nothing;
    return html`<div>
      <h4 class="sheet-h">Files</h4>
      ${this._filesError
        ? html`<p class="error">${this._filesError}</p>`
        : this._files === null
          ? html`<span class="muted">Loading…</span>`
          : this._files.length === 0
            ? html`<span class="muted">No files.</span>`
            : html`<div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Path</th>
                      <th class="num">Size</th>
                      <th>Modified</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    ${this._files.map(
                      (f) =>
                        html`<tr>
                          <td class="mono">${f.path}</td>
                          <td class="num">${formatSize(f.size)}</td>
                          <td>
                            ${f.lastModified
                              ? html`<relative-time
                                  .value=${f.lastModified}
                                ></relative-time>`
                              : ''}
                          </td>
                          <td>
                            <button
                              class="btn small"
                              @click=${() =>
                                this._download(
                                  a.stepName ?? '',
                                  a.number ?? 0,
                                  f.path ?? '',
                                )}
                            >
                              Download
                            </button>
                          </td>
                        </tr>`,
                    )}
                  </tbody>
                </table>
              </div>`}
    </div>`;
  }

  private _renderContinueDialog(input: Record<string, unknown>) {
    const m = this._runManifest();
    return html`<dialog id="continue-dialog" @close=${() => (this._continueErrors = [])}>
      <h3>Continue run #${this.run}</h3>
      <p class="continue-help">
        Completed stages are kept; the rest re-evaluates its <code>when</code> conditions
        with the new input. Only fields no completed step has read can change.
      </p>
      ${m
        ? html`<input-form
            id="continue-form"
            .schema=${inputSchemaOf(m, m.input)}
            .initial=${input}
          ></input-form>`
        : nothing}
      ${this._continueErrors.length
        ? html`<div class="errors" role="alert">
            ${this._continueErrors.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      <div class="dialog-actions">
        <button class="btn" @click=${() => this._continueDialog.close()}>Close</button>
        <button class="btn primary" ?disabled=${this._busy} @click=${this._continue}>
          Continue run
        </button>
      </div>
    </dialog>`;
  }

  private _openContinue() {
    this._continueErrors = [];
    this._continueDialog.showModal();
  }

  private async _act(fn: () => Promise<void>, errors: 'action' | 'continue' = 'action') {
    this._busy = true;
    if (errors === 'action') this._actionErrors = [];
    else this._continueErrors = [];
    try {
      await fn();
    } catch (e) {
      if (errors === 'action') this._actionErrors = errorMessages(e);
      else this._continueErrors = errorMessages(e);
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

  private _rerunRun = () =>
    this._act(async () => {
      const { data: res } = await runsRerun({ client: api, path: this._path });
      if (res?.run?.number) navigate(runPath(this.pipelineId, res.run.number));
      else this._actionErrors = [`Rerun: ${res?.outcome ?? 'no run started'}.`];
    });

  private _continue = () =>
    this._act(async () => {
      const r = this._continueForm?.collect();
      if (!r || !r.ok) {
        this._continueErrors = ['Fix the highlighted fields.'];
        return;
      }
      await runsContinue({ client: api, path: this._path, body: { input: r.value } });
      this._continueDialog.close();
      await this._load();
    }, 'continue');

  private _rerunStep = (cont: boolean) =>
    this._act(async () => {
      const step = this._step;
      const { data: a } = await runStepsRerun({
        client: api,
        path: { ...this._path, step },
        body: { continue: cont },
      });
      await this._load();
      this._selectStep(step, a?.number ?? null);
    });

  private _download(step: string, attempt: number, path: string) {
    const name = path.endsWith('/')
      ? `${path.slice(0, -1).split('/').pop()}.tar`
      : (path.split('/').pop() ?? path);
    const file = { ...this._path, step, attempt, path };
    return this._act(() => downloadFile(file, name));
  }

  private _onFileRef = (e: CustomEvent<{ ref: FileRefValue }>) => {
    const ref = e.detail.ref;
    const name = ref.path.endsWith('/')
      ? `${ref.path.slice(0, -1).split('/').pop()}.tar`
      : (ref.path.split('/').pop() ?? ref.path);
    const { run: number, step, attempt, path } = ref;
    const file = { pipelineId: this.pipelineId, number, step, attempt, path };
    this._act(() => downloadFile(file, name));
  };
}

declare global {
  interface HTMLElementTagNameMap {
    'run-view': RunView;
  }
}
