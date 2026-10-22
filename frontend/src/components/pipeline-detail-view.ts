import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, apiPost, errorMessages } from '../api.js';
import { navigate } from '../router.js';
import type {
  Pipeline,
  PipelineBindingStatus,
  PipelineHealth,
  PipelineSecretInfo,
  StepHistoryItem,
} from '@olve/olve-pipelines-client/src/models/index.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  navClick,
  runPath,
  runsPath,
  versionsPath,
  stepHistoryPath,
  shortSha,
} from '../v2/ui.js';
import { allSteps } from '../v2/manifest.js';
import { loadVersion, toActivation, currentVersion, type Activation, type LoadedVersion } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import type { StepState } from './stage-graph.js';
import './relative-time.js';
import './stage-graph.js';
import './trigger-panel.js';

/** A pipeline's steps with the latest attempt of each, plus a compact header. */
@customElement('pipeline-detail-view')
export class PipelineDetailView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .header {
        display: flex;
        align-items: center;
        gap: 0.5rem 0.9rem;
        flex-wrap: wrap;
      }
      .header .spacer {
        flex: 1;
      }
      .desc {
        margin: 0.3rem 0 0;
        color: var(--color-text-muted);
        font-size: 0.88rem;
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

  @state() private declare _pipeline: Pipeline | null;
  @state() private declare _binding: PipelineBindingStatus | null;
  @state() private declare _secrets: PipelineSecretInfo[];
  @state() private declare _health: PipelineHealth | null;
  @state() private declare _activation: Activation;
  @state() private declare _active: LoadedVersion | null;
  @state() private declare _latest: Map<string, StepHistoryItem>;
  @state() private declare _blocked: Set<string>;
  @state() private declare _loaded: boolean;
  @state() private declare _error: string | null;
  @state() private declare _gateErrors: string[];
  @state() private declare _triggerOpen: boolean;
  @state() private declare _titleHint: string;

  constructor() {
    super();
    this.pipelineId = '';
    this._pipeline = null;
    this._binding = null;
    this._secrets = [];
    this._health = null;
    this._activation = { kind: 'none' };
    this._active = null;
    this._latest = new Map();
    this._blocked = new Set();
    this._loaded = false;
    this._error = null;
    this._gateErrors = [];
    this._triggerOpen = new URLSearchParams(location.search).get('trigger') === '1';
    const st = history.state as { pipelineName?: string } | null;
    this._titleHint = st?.pipelineName ?? '';
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) {
      this._active = null;
      this._latest = new Map();
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
      if (cur !== null && this._active?.number !== cur) this._active = await loadVersion(this.pipelineId, cur);
      const names = this._active?.manifest ? allSteps(this._active.manifest.stages).map((s) => s.name) : [];
      const latest = await Promise.all(
        names.map((n) =>
          p.steps
            .byStep(n)
            .history.get({ queryParameters: { limit: 1 } })
            .then((h) => [n, h?.[0]] as const)
            .catch(() => [n, undefined] as const),
        ),
      );
      this._latest = new Map(latest.filter((x): x is readonly [string, StepHistoryItem] => !!x[1]));
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
      out.set(step.name, { status: 'not-run', blocked: this._blocked.has(step.name), heldRuns: held.get(step.name) });
    }
    for (const [name, a] of this._latest) {
      out.set(name, {
        status: a.status ?? '',
        reason: a.reason,
        runNumber: a.runNumber,
        attemptCount: a.attemptNumber ?? 1,
        latestAttempt: a.attemptNumber,
        createdAt: a.createdAt,
        startedAt: a.startedAt,
        finishedAt: a.finishedAt,
        blocked: this._blocked.has(name),
        heldRuns: held.get(name),
      });
    }
    return out;
  }

  render() {
    if (this._error && !this._pipeline) return html`<p class="error">${this._error}</p>`;
    const p = this._pipeline;
    const m = this._active?.manifest ?? null;
    return html`
      <div class="crumbs"><a href="/" @click=${navClick}>Pipelines</a></div>
      <section>
        <div class="header">
          <h2>${p?.name ?? this._titleHint}</h2>
          ${this._health
            ? html`<span class="st t-${toneOf(this._health.status)}"><span class="dot"></span>${statusLabel(this._health.status)}</span>`
            : nothing}
          <span class="spacer"></span>
          <button class="btn primary" ?disabled=${!m} @click=${() => (this._triggerOpen = !this._triggerOpen)}>
            ${this._triggerOpen ? 'Close' : 'Trigger'}
          </button>
        </div>
        ${m?.description ? html`<p class="desc">${m.description}</p>` : nothing}
        ${this._renderFacts()} ${this._renderSync()}
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

      <section>
        ${m
          ? html`<stage-graph
              .manifest=${m}
              .states=${this._states()}
              .blocked=${this._blocked}
              gateControls
              @gate-toggle=${(e: CustomEvent<{ step: string; block: boolean }>) => this._setGate(e.detail.step, e.detail.block)}
              @step-select=${(e: CustomEvent<{ step: string }>) => this._open(e.detail.step)}
            ></stage-graph>`
          : this._loaded
            ? html`<p class="muted">No active version yet. One appears once reconcile has described the pipeline.</p>`
            : html`<p class="muted">Loading…</p>`}
        ${this._gateErrors.length
          ? html`<div class="errors" role="alert">${this._gateErrors.map((e) => html`<p>${e}</p>`)}</div>`
          : nothing}
        ${this._renderRetiredGates()}
      </section>
    `;
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
            >last run <a href=${runPath(this.pipelineId, last.number)} @click=${navClick}>#${last.number}</a>
            <relative-time .value=${last.finishedAt ?? last.createdAt}></relative-time
          ></span>`
        : nothing}
      ${(h?.heldRuns ?? []).length ? html`<span class="warn">${h!.heldRuns!.length} held</span>` : nothing}
      <a href=${runsPath(this.pipelineId)} @click=${navClick}>Runs</a>
      <a href=${versionsPath(this.pipelineId)} @click=${navClick}>Versions</a>
    </div>`;
  }

  private _renderSync() {
    const b = this._binding;
    if (!b) return nothing;
    const unset = this._secrets.filter((s) => s.isSet === false && (s.declaredBy?.length ?? 0) > 0).map((s) => s.name);
    const where = `${b.repo}@${b.branch}`;
    return html`<div class="facts">
        ${b.reconcileInProgress
          ? html`<span>${where} reconciling ${shortSha(b.reconcileSha)}…</span>`
          : b.result === 2
            ? html`<span class="bad">${where} reconcile failed at ${shortSha(b.reconcileSha)}</span>`
            : b.result === 1
              ? html`<span title=${b.lastReconciledSha ?? ''}
                  >${where} synced at <span class="mono">${shortSha(b.lastReconciledSha)}</span>
                  <relative-time .value=${b.lastSyncTime}></relative-time
                ></span>`
              : html`<span>${where} waiting for first sync</span>`}
        ${unset.length ? html`<span class="bad">unset secrets: ${unset.join(', ')}</span>` : nothing}
      </div>
      ${b.result === 2 && b.problems?.length ? html`<ul class="problems">${b.problems.map((x) => html`<li>${x}</li>`)}</ul>` : nothing}`;
  }

  private _renderRetiredGates() {
    const m = this._active?.manifest;
    if (!m) return nothing;
    const names = new Set(allSteps(m.stages).map((s) => s.name));
    const retired = [...this._blocked].filter((s) => !names.has(s));
    if (!retired.length) return nothing;
    return html`<div class="retired">
      <span>Blocked steps not in this version:</span>
      ${retired.map((s) => html`<button class="btn small" @click=${() => this._setGate(s, false)}>Unblock ${s}</button>`)}
    </div>`;
  }

  private _open(step: string) {
    const a = this._latest.get(step);
    if (!a) return navigate(stepHistoryPath(this.pipelineId, step));
    navigate(`${runPath(this.pipelineId, a.runNumber)}?step=${encodeURIComponent(step)}&attempt=${a.attemptNumber}`);
  }

  private async _setGate(step: string, block: boolean) {
    this._gateErrors = [];
    try {
      const list = await apiPost<string[]>(
        `/api/pipelines/${this.pipelineId}/steps/${encodeURIComponent(step)}/${block ? 'block' : 'unblock'}`,
      );
      this._blocked = new Set(Array.isArray(list) ? list : []);
      this.reload();
    } catch (err) {
      this._gateErrors = errorMessages(err);
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-detail-view': PipelineDetailView;
  }
}
