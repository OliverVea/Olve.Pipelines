import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, apiPost, errorMessages } from '../api.js';
import type {
  Pipeline,
  PipelineBindingStatus,
  PipelineHealth,
  PipelineSecretInfo,
  StepHistoryItem,
} from '@olve/olve-pipelines-client/src/models/index.js';
import {
  sharedStyles,
  headerStyles,
  detailsOpen,
  rememberDetails,
  toneOf,
  statusLabel,
  navClick,
  runPath,
  runsPath,
  versionsPath,
  secretsPath,
  configPath,
  bindingPath,
  stepPath,
  stepLogsPath,
  isActiveStatus,
  shortSha,
  formatDuration,
} from '../v2/ui.js';
import { allSteps, type Manifest } from '../v2/manifest.js';
import {
  loadVersion,
  toActivation,
  currentVersion,
  type Activation,
  type LoadedVersion,
} from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import type { StepState } from './stage-graph.js';
import { sheetContentStyles } from './step-sheet.js';
import './relative-time.js';
import './stage-graph.js';
import './step-sheet.js';
import './trigger-panel.js';

const RECENT = 10;

/** A pipeline's steps with the latest attempt of each, a compact header and the selected step in a sheet. */
@customElement('pipeline-detail-view')
export class PipelineDetailView extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
    sheetContentStyles,
    css`
      .desc {
        margin: 0;
        color: var(--color-text-muted);
        font-size: 0.88rem;
        max-width: 75ch;
      }
      .graph {
        margin-top: 0.6rem;
      }
      .hist {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.25rem;
      }
      .hist a {
        display: inline-flex;
      }
      .cube {
        width: 1.15rem;
        height: 1.15rem;
        border-radius: 4px;
        border: 1px solid color-mix(in srgb, var(--tone) 55%, transparent);
        background: color-mix(in srgb, var(--tone) 22%, transparent);
      }
      .cube.t-ok {
        background: color-mix(in srgb, var(--tone) 70%, transparent);
      }
      .cube.t-fail {
        background: var(--tone);
      }
      .cube.t-skip,
      .cube.t-off,
      .cube.t-idle {
        background: transparent;
      }
      .cube.t-idle {
        border-style: dashed;
      }
      .facts {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.2rem 0.5rem;
        margin-top: 0.5rem;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .facts > :not(:last-child)::after {
        content: '·';
        margin-left: 0.5rem;
      }
      .warn {
        color: var(--color-warning);
      }
      .bad {
        color: var(--color-danger);
      }
      .problems {
        margin: 0.35rem 0 0;
        padding-left: 1.2rem;
        font-size: 0.8rem;
        color: var(--color-danger);
      }
      .retired {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.3rem 0.6rem;
        margin-top: 0.6rem;
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _pipeline: Pipeline | null;
  @state() declare private _binding: PipelineBindingStatus | null;
  @state() declare private _secrets: PipelineSecretInfo[];
  @state() declare private _health: PipelineHealth | null;
  @state() declare private _activation: Activation;
  @state() declare private _active: LoadedVersion | null;
  /** Recent attempts per step, newest first. */
  @state() declare private _history: Map<string, StepHistoryItem[]>;
  @state() declare private _blocked: Set<string>;
  @state() declare private _loaded: boolean;
  @state() declare private _error: string | null;
  @state() declare private _stepErrors: string[];
  @state() declare private _retrying: Set<string>;
  @state() declare private _triggerOpen: boolean;
  @state() declare private _titleHint: string;
  @state() declare private _step: string;
  @state() declare private _detailsOpen: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this._pipeline = null;
    this._binding = null;
    this._secrets = [];
    this._health = null;
    this._activation = { kind: 'none' };
    this._active = null;
    this._history = new Map();
    this._blocked = new Set();
    this._loaded = false;
    this._error = null;
    this._stepErrors = [];
    this._retrying = new Set();
    this._triggerOpen = new URLSearchParams(location.search).get('trigger') === '1';
    const st = history.state as { pipelineName?: string } | null;
    this._titleHint = st?.pipelineName ?? '';
    this._step = new URLSearchParams(location.search).get('step') ?? '';
    this._detailsOpen = detailsOpen();
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) {
      this._active = null;
      this._history = new Map();
      this._loaded = false;
      this.reload();
    }
  }

  async reload() {
    try {
      const p = client.api.pipelines.byId(this.pipelineId);
      const [pipeline, binding, health, secrets] = await Promise.all([
        p.get(),
        p.binding.status.get().catch(() => null),
        p.health.get().catch(() => null),
        p.secrets.get().catch(() => null), // needs auth
      ]);
      this._pipeline = pipeline ?? null;
      this._binding = binding ?? null;
      this._secrets = secrets ?? [];
      this._health = health ?? null;
      this._activation = toActivation(health?.activation);
      this._blocked = new Set(health?.blockedSteps ?? []);
      const cur = currentVersion(this._activation);
      if (cur !== null && this._active?.number !== cur)
        this._active = await loadVersion(this.pipelineId, cur);
      const names = this._active?.manifest
        ? allSteps(this._active.manifest.stages).map((s) => s.name)
        : [];
      const latest = await Promise.all(
        names.map((n) =>
          p.steps
            .byStep(n)
            .history.get({ queryParameters: { limit: RECENT } })
            .then((h): [string, StepHistoryItem[]] => [n, h ?? []])
            .catch((): [string, StepHistoryItem[]] => [n, []]),
        ),
      );
      this._history = new Map(latest.filter(([, h]) => h.length > 0));
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    } finally {
      this._loaded = true;
    }
  }

  private _states(): Map<string, StepState> {
    const held = new Map<string, number[]>();
    for (const r of this._health?.heldRuns ?? []) {
      if (r.heldAt) held.set(r.heldAt, [...(held.get(r.heldAt) ?? []), r.number ?? 0]);
    }
    const out = new Map<string, StepState>();
    for (const step of allSteps(this._active?.manifest?.stages ?? [])) {
      out.set(step.name, {
        status: 'not-run',
        blocked: this._blocked.has(step.name),
        heldRuns: held.get(step.name),
      });
    }
    for (const [name, [a]] of this._history) {
      out.set(name, {
        status: a.status ?? '',
        reason: a.reason,
        runNumber: a.runNumber,
        runHref: `${runPath(this.pipelineId, a.runNumber)}?step=${encodeURIComponent(name)}&attempt=${a.attemptNumber}`,
        logsHref: stepLogsPath(this.pipelineId, a.runNumber, name, a.attemptNumber),
        attemptCount: a.attemptNumber ?? 1,
        latestAttempt: a.attemptNumber,
        createdAt: a.createdAt,
        startedAt: a.startedAt,
        finishedAt: a.finishedAt,
        blocked: this._blocked.has(name),
        heldRuns: held.get(name),
        retryable:
          a.versionNumber === currentVersion(this._activation) &&
          !isActiveStatus(a.status) &&
          !this._blocked.has(name) &&
          !this._retrying.has(name),
        cancellable: isActiveStatus(a.status),
      });
    }
    return out;
  }

  render() {
    if (this._error && !this._pipeline) return html`<p class="error">${this._error}</p>`;
    const p = this._pipeline;
    const m = this._active?.manifest ?? null;
    const states = this._states();
    const sheet = !!m && !!this._step;
    const errors = this._stepErrors.length
      ? html`<div class="errors" role="alert">
          ${this._stepErrors.map((e) => html`<p>${e}</p>`)}
        </div>`
      : nothing;
    return html`
      <section>
        <div class="line">
          <h2>${p?.name ?? this._titleHint}</h2>
          ${this._health
            ? html`<span class="st t-${toneOf(this._health.status)}"
                ><span class="dot"></span>${statusLabel(this._health.status)}</span
              >`
            : nothing}
          <button
            class="more"
            aria-expanded=${this._detailsOpen}
            aria-controls="details"
            @click=${this._toggleDetails}
          >
            Details
          </button>
          <span class="spacer"></span>
          <button
            class="btn primary"
            ?disabled=${!m}
            @click=${() => (this._triggerOpen = !this._triggerOpen)}
          >
            ${this._triggerOpen ? 'Close' : 'Trigger'}
          </button>
          <i class="br"></i>
        </div>
        <div class="details" id="details" ?hidden=${!this._detailsOpen}>
          ${m?.description ? html`<p class="desc">${m.description}</p>` : nothing}
          ${this._renderFacts()} ${this._renderSync()}
        </div>
      </section>

      ${this._triggerOpen
        ? html`<section class="panel">
            <trigger-panel
              .pipelineId=${this.pipelineId}
              .manifest=${m}
              .versionNumber=${currentVersion(this._activation)}
              @close=${() => (this._triggerOpen = false)}
            ></trigger-panel>
          </section>`
        : nothing}

      <section class="graph">
        ${m
          ? html`<stage-graph
              .manifest=${m}
              .states=${states}
              .blocked=${this._blocked}
              .selected=${sheet ? this._step : ''}
              stepControls
              @block-toggle=${(e: CustomEvent<{ step: string; block: boolean }>) =>
                this._setBlocked(e.detail.step, e.detail.block)}
              @step-retry=${(e: CustomEvent<{ step: string }>) =>
                this._retry(e.detail.step)}
              @step-cancel=${(e: CustomEvent<{ step: string }>) =>
                this._cancel(e.detail.step)}
              @step-select=${(e: CustomEvent<{ step: string }>) =>
                this._select(e.detail.step)}
            ></stage-graph>`
          : this._loaded
            ? html`<p class="muted">
                No active version yet. One appears once reconcile has described the
                pipeline.
              </p>`
            : html`<p class="muted">Loading…</p>`}
        ${sheet ? nothing : errors} ${this._renderRetiredBlocks()}
      </section>
      ${sheet ? this._renderSheet(m, states.get(this._step), errors) : nothing}
    `;
  }

  private _renderSheet(m: Manifest, st: StepState | undefined, errors: unknown) {
    const step = this._step;
    const history = this._history.get(step) ?? [];
    const at = st?.finishedAt ?? st?.startedAt ?? st?.createdAt;
    return html`<step-sheet
      .pipelineId=${this.pipelineId}
      .manifest=${m}
      .step=${step}
      .state=${st}
      @close=${() => this._select('')}
      @step-select=${(e: CustomEvent<{ step: string }>) => this._select(e.detail.step)}
    >
      <span slot="actions" style="display:contents">
        ${st?.logsHref
          ? html`<a class="btn small" href=${st.logsHref} @click=${navClick}>Logs</a>`
          : nothing}
        ${st?.cancellable
          ? html`<button class="btn small danger" @click=${() => this._cancel(step)}>
              Cancel
            </button>`
          : nothing}
        ${st?.retryable
          ? html`<button class="btn small" @click=${() => this._retry(step)}>
              Rerun and continue
            </button>`
          : nothing}
        <button class="btn small" @click=${() => this._setBlocked(step, !st?.blocked)}>
          ${st?.blocked ? 'Unblock' : 'Block'}
        </button>
        <a class="btn small" href=${stepPath(this.pipelineId, step)} @click=${navClick}
          >Step page</a
        >
      </span>
      <div slot="attempt">
        <h4 class="sheet-h">Latest attempt</h4>
        <dl class="kv">
          <dt>Run</dt>
          <dd>
            ${st?.runNumber
              ? html`<a
                  href=${st.runHref ?? runPath(this.pipelineId, st.runNumber)}
                  @click=${navClick}
                  >#${st.runNumber}${(st.attemptCount ?? 0) > 1
                    ? `@${st.latestAttempt}`
                    : ''}</a
                >`
              : '—'}
          </dd>
          <dt>${st?.finishedAt ? 'Finished' : 'Started'}</dt>
          <dd><relative-time .value=${at}></relative-time></dd>
          <dt>Duration</dt>
          <dd>${st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '—'}</dd>
          <dt>Attempts</dt>
          <dd>${st?.attemptCount ?? 0}</dd>
        </dl>
        ${history.length
          ? html`<h4 class="sheet-h">Recent runs</h4>
              <div class="hist">
                ${history.map(
                  (h) =>
                    html`<a
                      href="${runPath(
                        this.pipelineId,
                        h.runNumber,
                      )}?step=${encodeURIComponent(step)}&attempt=${h.attemptNumber}"
                      title="#${h.runNumber}@${h.attemptNumber} ${statusLabel(h.status)}"
                      @click=${navClick}
                      ><span class="cube t-${toneOf(h.status)}"></span
                    ></a>`,
                )}
              </div>`
          : nothing}
      </div>
      ${errors}
    </step-sheet>`;
  }

  private _toggleDetails = () => {
    this._detailsOpen = !this._detailsOpen;
    rememberDetails(this._detailsOpen);
  };

  private _select(step: string) {
    this._step = step;
    const q = new URLSearchParams(location.search);
    if (step) q.set('step', step);
    else q.delete('step');
    history.replaceState(
      history.state,
      '',
      `${location.pathname}${q.size ? `?${q}` : ''}`,
    );
  }

  private _renderFacts() {
    const a = this._activation;
    const h = this._health;
    const last = h?.lastRun;
    return html`<div class="facts">
      ${a.kind === 'active'
        ? html`<span>version ${a.version}</span>`
        : a.kind === 'draining'
          ? html`<span class="warn">switching ${a.from} → ${a.to}</span>`
          : html`<span>no active version</span>`}
      ${last
        ? html`<span
            >last run
            <a href=${runPath(this.pipelineId, last.number)} @click=${navClick}
              >#${last.number}</a
            >
            <relative-time .value=${last.finishedAt ?? last.createdAt}></relative-time
          ></span>`
        : nothing}
      ${(h?.heldRuns ?? []).length
        ? html`<span class="warn">${h!.heldRuns!.length} held</span>`
        : nothing}
      <a href=${runsPath(this.pipelineId)} @click=${navClick}>Runs</a>
      <a href=${versionsPath(this.pipelineId)} @click=${navClick}>Versions</a>
      <a href=${configPath(this.pipelineId)} @click=${navClick}>Config</a>
      <a href=${secretsPath(this.pipelineId)} @click=${navClick}>Secrets</a>
      ${this._binding
        ? html`<a href=${bindingPath(this.pipelineId)} @click=${navClick}>Binding</a>`
        : nothing}
    </div>`;
  }

  private _renderSync() {
    const b = this._binding;
    if (!b) return nothing;
    const unset = this._secrets
      .filter((s) => s.isSet === false && (s.declaredBy?.length ?? 0) > 0)
      .map((s) => s.name);
    const where = `${b.repo}@${b.branch}`;
    return html`<div class="facts">
        ${b.reconcileInProgress
          ? html`<span>${where} reconciling ${shortSha(b.reconcileSha)}…</span>`
          : b.result === 2
            ? html`<a class="bad" href=${bindingPath(this.pipelineId)} @click=${navClick}
                >${where} reconcile failed at ${shortSha(b.reconcileSha)}</a
              >`
            : b.result === 1
              ? html`<span
                  >${where} synced at
                  <span class="mono">${shortSha(b.lastReconciledSha)}</span>
                  <relative-time .value=${b.lastSyncTime}></relative-time
                ></span>`
              : html`<span>${where} waiting for first sync</span>`}
        ${unset.length
          ? html`<a class="bad" href=${secretsPath(this.pipelineId)} @click=${navClick}
              >unset secrets: ${unset.join(', ')}</a
            >`
          : nothing}
        ${this._health?.pending?.version != null
          ? html`<a class="warn" href=${configPath(this.pipelineId)} @click=${navClick}
              >${this._health.pending.held ? 'held' : 'pending'}
              v${this._health.pending.version}:
              ${(this._health.pending.reasons ?? []).join('; ')}</a
            >`
          : nothing}
      </div>
      ${b.result === 2 && b.problems?.length
        ? html`<ul class="problems">
            ${b.problems.map((x) => html`<li>${x}</li>`)}
          </ul>`
        : nothing}`;
  }

  private _renderRetiredBlocks() {
    const m = this._active?.manifest;
    if (!m) return nothing;
    const names = new Set(allSteps(m.stages).map((s) => s.name));
    const retired = [...this._blocked].filter((s) => !names.has(s));
    if (!retired.length) return nothing;
    return html`<div class="retired">
      <span>Blocked steps not in this version:</span>
      ${retired.map(
        (s) =>
          html`<button class="btn small" @click=${() => this._setBlocked(s, false)}>
            Unblock ${s}
          </button>`,
      )}
    </div>`;
  }

  private async _cancel(step: string) {
    const run = this._history.get(step)?.[0]?.runNumber;
    if (
      !run ||
      !confirm(
        `Cancel ${step} in run #${run}? The run ends Cancelled once its stage settles.`,
      )
    )
      return;
    this._stepErrors = [];
    try {
      await apiPost(
        `/api/pipelines/${this.pipelineId}/runs/${run}/steps/${encodeURIComponent(step)}/cancel`,
      );
      await this.reload();
    } catch (err) {
      this._stepErrors = errorMessages(err);
    }
  }

  private async _retry(step: string) {
    const run = this._history.get(step)?.[0]?.runNumber;
    if (!run) return;
    this._stepErrors = [];
    this._retrying = new Set([...this._retrying, step]);
    try {
      await apiPost(
        `/api/pipelines/${this.pipelineId}/runs/${run}/steps/${encodeURIComponent(step)}/rerun`,
        { continue: true },
      );
      await this.reload();
    } catch (err) {
      this._stepErrors = errorMessages(err);
    } finally {
      this._retrying = new Set([...this._retrying].filter((s) => s !== step));
    }
  }

  private async _setBlocked(step: string, block: boolean) {
    this._stepErrors = [];
    try {
      const list = await apiPost<string[]>(
        `/api/pipelines/${this.pipelineId}/steps/${encodeURIComponent(step)}/${block ? 'block' : 'unblock'}`,
      );
      this._blocked = new Set(Array.isArray(list) ? list : []);
      this.reload();
    } catch (err) {
      this._stepErrors = errorMessages(err);
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-detail-view': PipelineDetailView;
  }
}
