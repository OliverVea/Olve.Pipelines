import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { api } from '../api.js';
import {
  attemptsList,
  bindingsStatus,
  pipelinesGet,
  runsCriticalPath,
  pipelinesHealth,
  secretsList,
  type PipelineBindingStatus,
  type PipelineHealth,
  type PipelineSecret,
  type ActiveAttempt,
  type CriticalPathReport,
} from '@olve/api';
import {
  sharedStyles,
  headerStyles,
  tabStyles,
  statusCardStyles,
  pipelineTabs,
  type PipelineTab,
  toneOf,
  statusLabel,
  navClick,
  runPath,
  runsPath,
  versionsPath,
  secretsPath,
  configPath,
  bindingPath,
  shortSha,
  criticalPathPath,
  formatSeconds,
  median,
} from '../v2/ui.js';
import {
  loadVersion,
  toActivation,
  currentVersion,
  type Activation,
} from '../v2/versions.js';
import { LiveController, forPipeline } from '../v2/events.js';
import './clamped-text.js';
import './relative-time.js';

/** The header of every pipeline page: name, health, description and tabs; the overview adds a status card. Actions go in the `actions` slot. */
@customElement('pipeline-header')
export class PipelineHeader extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
    tabStyles,
    statusCardStyles,
    css`
      .desc {
        margin: 0.25rem 0 0;
        color: var(--color-text-muted);
        font-size: 0.88rem;
        max-width: 75ch;
      }
      .mono {
        font-family: var(--font-mono);
      }
      .muted {
        color: var(--color-text-muted);
      }
      .warn {
        color: var(--color-warning);
      }
      .bad {
        color: var(--color-danger);
      }
      .ask {
        color: var(--color-primary);
      }
      .alerts {
        display: flex;
        flex-direction: column;
        gap: 0.35rem;
        margin-top: 0.5rem;
      }
      .alert {
        padding: 0.4rem 0.75rem;
        border-radius: var(--radius);
        border-left: 3px solid currentColor;
        background: color-mix(in srgb, currentColor 8%, transparent);
        font-size: 0.82rem;
        text-decoration: none;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare tab: PipelineTab;

  @state() declare private _name: string;
  @state() declare private _description: string;
  @state() declare private _binding: PipelineBindingStatus | null;
  @state() declare private _secrets: PipelineSecret[];
  @state() declare private _health: PipelineHealth | null;
  @state() declare private _activation: Activation;
  @state() declare private _critical: CriticalPathReport | null;
  @state() declare private _waiting: ActiveAttempt[];

  constructor() {
    super();
    this.pipelineId = '';
    this.tab = 'overview';
    const st = history.state as { pipelineName?: string } | null;
    this._name = st?.pipelineName ?? '';
    this._description = '';
    this._binding = null;
    this._secrets = [];
    this._health = null;
    this._activation = { kind: 'none' };
    this._critical = null;
    this._waiting = [];
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') && this.pipelineId) this.reload();
  }

  async reload() {
    const path = { pipelineId: this.pipelineId };
    const overview = this.tab === 'overview';
    const [pipeline, binding, health, secrets, critical, waiting] = await Promise.all([
      pipelinesGet({ client: api, path })
        .then((r) => r.data)
        .catch(() => null),
      bindingsStatus({ client: api, path })
        .then((r) => r.data)
        .catch(() => null),
      pipelinesHealth({ client: api, path })
        .then((r) => r.data)
        .catch(() => null),
      overview
        ? secretsList({ client: api, path })
            .then((r) => r.data)
            .catch(() => null)
        : null, // needs auth
      overview
        ? runsCriticalPath({ client: api, path })
            .then((r) => r.data)
            .catch(() => null)
        : null,
      overview
        ? attemptsList({ client: api, query: { status: 'waiting' } })
            .then((r) => r.data)
            .catch(() => null)
        : null,
    ]);
    this._waiting = (waiting ?? []).filter(
      (a) => String(a.pipelineId) === this.pipelineId,
    );
    this._critical = critical ?? null;
    if (pipeline?.name) this._name = pipeline.name;
    this._binding = binding ?? null;
    this._secrets = secrets ?? [];
    this._health = health ?? null;
    this._activation = toActivation(health?.activation);
    const cur = currentVersion(this._activation);
    const version =
      cur === null ? null : await loadVersion(this.pipelineId, cur).catch(() => null);
    this._description = version?.manifest?.description ?? '';
  }

  render() {
    return html`<div class="line">
        <h2>${this._name}</h2>
        ${this._health
          ? html`<span class="st t-${toneOf(this._health.status)}"
              ><span class="dot"></span>${statusLabel(this._health.status)}</span
            >`
          : nothing}
        <span class="spacer"></span>
        <slot name="actions"></slot>
        <i class="br"></i>
      </div>
      ${this._description
        ? html`<clamped-text class="desc" .text=${this._description}></clamped-text>`
        : nothing}
      ${pipelineTabs(this.pipelineId, this.tab, !!this._binding)}
      ${this.tab === 'overview' ? this._renderStatus() : nothing}`;
  }

  private _renderStatus() {
    const a = this._activation;
    const h = this._health;
    const b = this._binding;
    const last = h?.lastRun;
    const running = h?.runningRuns ?? 0;
    const held = h?.heldRuns?.length ?? 0;
    const unset = this._secrets
      .filter(
        (s) => s.isSet === false && (s.declaredBy?.length ?? 0) > 0 && s.needed !== false,
      )
      .map((s) => s.name);
    const failed = b?.result === 'Error';
    return html`<div class="status">
        <div class="cell">
          <span class="label">Version</span>
          ${a.kind === 'active'
            ? html`<a
                class="value"
                href=${versionsPath(this.pipelineId)}
                @click=${navClick}
                >v${a.version}</a
              >`
            : a.kind === 'draining'
              ? html`<span class="value warn">v${a.from} → v${a.to}</span>`
              : html`<span class="value muted">none</span>`}
          <span class="sub">${a.kind === 'draining' ? 'switching' : 'active'}</span>
        </div>
        <div class="cell">
          <span class="label">Last run</span>
          ${last
            ? html`<a
                  class="value st t-${toneOf(last.status ?? '')}"
                  href=${runPath(this.pipelineId, last.number)}
                  @click=${navClick}
                  ><span class="dot"></span>#${last.number}</a
                >
                <span class="sub"
                  >${statusLabel(last.status ?? '')} ·
                  <relative-time
                    .value=${last.finishedAt ?? last.createdAt}
                  ></relative-time
                ></span>`
            : html`<span class="value muted">none yet</span>`}
        </div>
        ${this._critical?.runs?.length
          ? html`<div class="cell">
              <span class="label">Critical path</span>
              <a
                class="value"
                href=${criticalPathPath(this.pipelineId)}
                @click=${navClick}
                >${formatSeconds(
                  median(this._critical.runs.map((r) => r.seconds ?? 0)),
                )}</a
              >
              <span class="sub">median of ${this._critical.runs.length} runs</span>
            </div>`
          : nothing}
        ${running || held
          ? html`<div class="cell">
              <span class="label">Active</span>
              <a class="value" href=${runsPath(this.pipelineId)} @click=${navClick}
                >${running} running</a
              >
              <span class="sub ${held ? 'warn' : ''}">${held} held</span>
            </div>`
          : nothing}
        ${b
          ? html`<div class="cell grow">
              <span class="label">Source</span>
              <a
                class="value mono"
                href=${bindingPath(this.pipelineId)}
                @click=${navClick}
                >${b.repo}@${b.branch}</a
              >
              <span class="sub ${failed ? 'bad' : ''}"
                >${b.reconcileInProgress
                  ? html`reconciling ${shortSha(b.reconcileSha)}…`
                  : failed
                    ? html`reconcile failed at ${shortSha(b.reconcileSha)}`
                    : b.result === 'Success'
                      ? html`synced
                          <span class="mono">${shortSha(b.lastReconciledSha)}</span> ·
                          <relative-time .value=${b.lastSyncTime}></relative-time>`
                      : 'waiting for first sync'}</span
              >
            </div>`
          : nothing}
      </div>
      ${unset.length ||
      h?.pending?.version != null ||
      (failed && b?.problems?.length) ||
      this._waiting.length
        ? html`<div class="alerts">
            ${this._waiting.map(
              (w) =>
                html`<a
                  class="alert ask"
                  href="${runPath(this.pipelineId, w.run)}?step=${encodeURIComponent(
                    w.step ?? '',
                  )}"
                  @click=${navClick}
                  >Run #${w.run}: ${w.step} is waiting for input</a
                >`,
            )}
            ${unset.length
              ? html`<a
                  class="alert bad"
                  href=${secretsPath(this.pipelineId)}
                  @click=${navClick}
                  >Unset secrets: ${unset.join(', ')}</a
                >`
              : nothing}
            ${h?.pending?.version != null
              ? html`<a
                  class="alert warn"
                  href=${configPath(this.pipelineId)}
                  @click=${navClick}
                  >${h.pending.held ? 'Held' : 'Pending'} v${h.pending.version}:
                  ${(h.pending.reasons ?? []).join('; ')}</a
                >`
              : nothing}
            ${failed
              ? (b?.problems ?? []).map(
                  (x) =>
                    html`<a
                      class="alert bad"
                      href=${bindingPath(this.pipelineId)}
                      @click=${navClick}
                      >${x}</a
                    >`,
                )
              : nothing}
          </div>`
        : nothing}`;
  }
}
