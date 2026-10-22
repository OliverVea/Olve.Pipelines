import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, apiPost, errorMessages } from '../api.js';
import { navigate } from '../router.js';
import type {
  Pipeline,
  PipelineBindingStatus,
  PipelineHealth,
  PipelineSecretInfo,
  PipelineVersionSummary,
  RunSummary,
} from '@olve/olve-pipelines-client/src/models/index.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  runPath,
  stepHistoryPath,
} from '../v2/ui.js';
import { loadVersion, toActivation, currentVersion, type Activation, type LoadedVersion } from '../v2/versions.js';
import './refresh-control.js';
import './relative-time.js';
import './stage-graph.js';
import './versions-panel.js';
import './trigger-panel.js';

/**
 * A pipeline: health and activation, the active version's stage graph with
 * gate controls, the manual trigger form, recent runs and versions (diff +
 * switch). Binding/reconcile status stays at the top.
 */
@customElement('pipeline-detail-view')
export class PipelineDetailView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .header {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
      .header .st {
        font-size: 0.9rem;
      }
      .header .spacer {
        flex: 1;
      }
      .desc {
        margin: 0.3rem 0 0;
        color: var(--color-text-muted);
        font-size: 0.88rem;
      }
      .strip {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.4rem 0.5rem;
        margin: 0.75rem 0 0;
      }
      .pill.ok {
        color: var(--color-success);
        border-color: color-mix(in srgb, var(--color-success) 50%, transparent);
      }
      .pill.warn {
        color: var(--color-warning);
        border-color: color-mix(in srgb, var(--color-warning) 50%, transparent);
      }
      .pill.bad {
        color: var(--color-danger);
        border-color: color-mix(in srgb, var(--color-danger) 50%, transparent);
      }
      .pill a {
        color: inherit;
      }
      .branch {
        opacity: 0.65;
      }
      .problems {
        margin: 0.5rem 0 0;
        padding-left: 1.2rem;
        font-size: 0.82rem;
        color: var(--color-danger);
      }
      .section-head {
        display: flex;
        align-items: baseline;
        gap: 0.75rem;
        margin-bottom: 0.6rem;
      }
      .section-head h3 {
        margin: 0;
      }
      .section-head .hint {
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
      .gate-errors {
        margin-top: 0.5rem;
      }
      .links {
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
      .steps-list {
        display: flex;
        flex-wrap: wrap;
        gap: 0.3rem 0.8rem;
        font-size: 0.8rem;
        margin-top: 0.6rem;
      }
      .steps-list span {
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() private declare _pipeline: Pipeline | null;
  @state() private declare _binding: PipelineBindingStatus | null;
  @state() private declare _secrets: PipelineSecretInfo[];
  @state() private declare _health: PipelineHealth | null;
  @state() private declare _versions: PipelineVersionSummary[];
  @state() private declare _activation: Activation;
  @state() private declare _active: LoadedVersion | null;
  @state() private declare _runs: RunSummary[];
  @state() private declare _runsTotal: number;
  @state() private declare _blocked: Set<string>;
  @state() private declare _loading: boolean;
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
    this._versions = [];
    this._activation = { kind: 'none' };
    this._active = null;
    this._runs = [];
    this._runsTotal = 0;
    this._blocked = new Set();
    this._loading = true;
    this._error = null;
    this._gateErrors = [];
    this._triggerOpen = new URLSearchParams(location.search).get('trigger') === '1';
    const st = history.state as { pipelineName?: string } | null;
    this._titleHint = st?.pipelineName ?? '';
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this._load();
  }

  private _onRefresh(e: CustomEvent<{ silent: boolean }>) {
    this._load(e.detail.silent);
  }

  private async _load(silent = false) {
    if (!silent) {
      this._loading = true;
      this._error = null;
    }
    try {
      const p = client.api.pipelines.byId(this.pipelineId);
      const [pipeline, binding, health, versions, runs, secrets] = await Promise.all([
        p.get(),
        // Unbound (draft) pipelines have no binding — tolerate the 404.
        p.binding.status.get().catch(() => null),
        p.health.get().catch(() => null),
        p.versions.get().catch(() => null),
        p.runs.get({ queryParameters: { pageSize: 25 } }).catch(() => null),
        p.secrets.get().catch(() => null), // needs auth
      ]);
      this._pipeline = pipeline ?? null;
      this._binding = binding ?? null;
      this._secrets = secrets ?? [];
      this._health = health ?? null;
      this._versions = versions?.versions ?? [];
      this._activation = toActivation(versions?.activation ?? health?.activation);
      this._runs = runs?.items ?? [];
      this._runsTotal = runs?.totalCount ?? this._runs.length;
      this._blocked = new Set(health?.blockedSteps ?? []);
      const cur = currentVersion(this._activation);
      if (cur !== null && this._active?.number !== cur) this._active = await loadVersion(this.pipelineId, cur);
    } catch (e) {
      if (!silent) this._error = errorMessages(e).join(' ');
    } finally {
      if (!silent) this._loading = false;
    }
  }

  render() {
    if (this._error) return html`<p class="error">${this._error}</p>`;
    const p = this._pipeline;
    const h = this._health;
    const m = this._active?.manifest ?? null;
    return html`
      <div class="crumbs"><a href="/" @click=${navClick}>Pipelines</a></div>
      <section>
        <div class="header">
          <h2>${p?.name ?? this._titleHint}</h2>
          ${h ? html`<span class="st t-${toneOf(h.status)}"><span class="dot"></span>${statusLabel(h.status)}</span>` : nothing}
          <span class="spacer"></span>
          <button class="btn primary" ?disabled=${!m} @click=${() => (this._triggerOpen = !this._triggerOpen)}>
            ${this._triggerOpen ? 'Hide run form' : 'Run pipeline…'}
          </button>
          <refresh-control .loading=${this._loading} @refresh=${this._onRefresh}></refresh-control>
        </div>
        ${m?.description ? html`<p class="desc">${m.description}</p>` : nothing}
        ${this._renderStrip()}
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
        <div class="section-head">
          <h3>Stages${this._active ? html` <span class="muted">v${this._active.number}</span>` : nothing}</h3>
          <span class="hint">A closed gate holds runs before that step. Click a step for its history.</span>
        </div>
        ${m
          ? html`<stage-graph
              .manifest=${m}
              .stepVersions=${this._active?.stepVersions ?? null}
              .blocked=${this._blocked}
              gateControls
              @gate-toggle=${this._onGate}
              @step-select=${(e: CustomEvent<{ step: string }>) => navigate(stepHistoryPath(this.pipelineId, e.detail.step))}
            ></stage-graph>`
          : this._loading
            ? html`<p class="muted">Loading…</p>`
            : html`<p class="muted">No active version yet. Versions appear once reconcile has described the pipeline.</p>`}
        ${this._gateErrors.length
          ? html`<div class="errors gate-errors" role="alert">${this._gateErrors.map((e) => html`<p>${e}</p>`)}</div>`
          : nothing}
        ${this._renderRetiredGates(m)}
      </section>

      <section>
        <div class="section-head">
          <h3>Recent runs</h3>
          ${this._runsTotal > this._runs.length
            ? html`<span class="hint">Latest ${this._runs.length} of ${this._runsTotal}</span>`
            : nothing}
        </div>
        ${this._renderRuns()}
      </section>

      <section>
        <div class="section-head"><h3>Versions</h3></div>
        <versions-panel
          .pipelineId=${this.pipelineId}
          .versions=${this._versions}
          .activation=${this._activation}
          @switched=${() => this._load(true)}
        ></versions-panel>
      </section>
    `;
  }

  private _renderStrip() {
    const b = this._binding;
    const h = this._health;
    const a = this._activation;
    const result = b?.result ?? 0; // 0 NeverRun, 1 Success, 2 Error
    const problems = b?.problems ?? [];
    const unset = (this._secrets ?? []).filter((s) => s.isSet === false && (s.declaredBy?.length ?? 0) > 0).map((s) => s.name);
    const held = h?.heldRuns ?? [];
    return html`
      <div class="strip">
        ${a.kind === 'active'
          ? html`<span class="pill ok" title="New runs use this version">active v${a.version}</span>`
          : a.kind === 'draining'
            ? html`<span class="pill warn" title="The old version finishes its in-flight work, then the new one takes over"
                >switching v${a.from} → v${a.to}${a.cancelRunning ? ' (cancelling running work)' : ''}</span
              >`
            : html`<span class="pill">no active version</span>`}
        ${h && (h.runningRuns ?? 0) > 0 ? html`<span class="pill t-run"><span class="dot"></span>${h.runningRuns} running</span>` : nothing}
        ${held.length
          ? html`<span class="pill warn"
              >held:
              ${held.map(
                (r, i) => html`${i ? ', ' : ''}<a href=${runPath(this.pipelineId, r.number)} @click=${navClick}>#${r.number}</a
                  >${r.heldAt ? html` at ${r.heldAt}` : nothing}`,
              )}</span
            >`
          : nothing}
        ${this._blocked.size ? html`<span class="pill warn">gates closed: ${[...this._blocked].join(', ')}</span>` : nothing}
        ${b
          ? html`<span class="pill" title="GitOps source — configuration is git-only"
                >${b.repo}${b.branch ? html`<span class="branch">@${b.branch}</span>` : nothing}${b.path
                  ? html`<span class="branch">:${b.path}</span>`
                  : nothing}</span
              >
              ${result === 2
                ? html`<span class="pill bad">reconcile failed</span>`
                : result === 1
                  ? html`<span class="pill ok">config synced</span>`
                  : html`<span class="pill">waiting for first sync</span>`}
              ${unset.length ? html`<span class="pill bad" title="Set these via pl or the secrets API">unset secrets: ${unset.join(', ')}</span>` : nothing}`
          : nothing}
      </div>
      ${result === 2 && problems.length
        ? html`<ul class="problems">
            ${problems.map((x) => html`<li>${x}</li>`)}
          </ul>`
        : nothing}
    `;
  }

  /** Gates on steps the active version no longer has (still closed; can be opened). */
  private _renderRetiredGates(m: LoadedVersion['manifest']) {
    if (!m) return nothing;
    const names = new Set(Object.keys(this._active?.stepVersions ?? {}));
    const retired = [...this._blocked].filter((s) => !names.has(s));
    if (!retired.length) return nothing;
    return html`<div class="steps-list">
      <span>Closed gates on steps not in this version:</span>
      ${retired.map(
        (s) => html`<button class="btn small" @click=${() => this._setGate(s, false)}>Open ${s}</button>`,
      )}
    </div>`;
  }

  private _renderRuns() {
    if (!this._runs.length) return html`<p class="muted">No runs yet. Use Run pipeline to start one.</p>`;
    return html`<div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Run</th>
            <th>Status</th>
            <th>Version</th>
            <th>Trigger</th>
            <th>Started</th>
            <th class="num">Duration</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          ${this._runs.map(
            (r) => html`<tr>
              <td><a href=${runPath(this.pipelineId, r.number)} @click=${navClick}><b>#${r.number}</b></a></td>
              <td>
                <span class="st t-${toneOf(r.status)}"><span class="dot"></span>${statusLabel(r.status)}</span>
                ${r.heldAt && r.status === 'Running' ? html`<span class="pill warn">held at ${r.heldAt}</span>` : nothing}
              </td>
              <td class="mono">v${r.versionNumber} <span class="muted">${r.versionStamp}</span></td>
              <td>${(r.triggerKind ?? '').toLowerCase()}</td>
              <td><relative-time .value=${r.createdAt}></relative-time></td>
              <td class="num">${formatDuration(r.createdAt, r.finishedAt)}</td>
              <td class="links">
                ${r.rerunOfNumber
                  ? html`rerun of <a href=${runPath(this.pipelineId, r.rerunOfNumber)} @click=${navClick}>#${r.rerunOfNumber}</a>`
                  : nothing}
                ${r.supersededByNumber
                  ? html`superseded by
                      <a href=${runPath(this.pipelineId, r.supersededByNumber)} @click=${navClick}>#${r.supersededByNumber}</a>`
                  : nothing}
              </td>
            </tr>`,
          )}
        </tbody>
      </table>
    </div>`;
  }

  private _onGate = (e: CustomEvent<{ step: string; block: boolean }>) => this._setGate(e.detail.step, e.detail.block);

  private async _setGate(step: string, block: boolean) {
    this._gateErrors = [];
    try {
      const list = await apiPost<string[]>(
        `/api/pipelines/${this.pipelineId}/steps/${encodeURIComponent(step)}/${block ? 'block' : 'unblock'}`,
      );
      this._blocked = new Set(Array.isArray(list) ? list : []);
      this._load(true);
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
