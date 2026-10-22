import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PipelineHealth, StepHistoryItem } from '@olve/olve-pipelines-client/src/models/index.js';
import { client, apiPost, errorMessages } from '../api.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  pipelinePath,
  runPath,
  stepPath,
  stepRunsPath,
  stepLogsPath,
  isActiveStatus,
} from '../v2/ui.js';
import { type ManifestStep, type Mapping, stepsByName } from '../v2/manifest.js';
import { loadVersion, toActivation, currentVersion } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import './relative-time.js';

function formatSeconds(s: number): string {
  const parts = [[Math.floor(s / 3600), 'h'], [Math.floor((s % 3600) / 60), 'm'], [s % 60, 's']] as const;
  return parts.filter(([n]) => n > 0).map(([n, u]) => `${n}${u}`).join('') || '0s';
}

function configValue(v: unknown): unknown {
  if (Array.isArray(v)) return v.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join(', ');
  if (v && typeof v === 'object')
    return Object.entries(v).map(([k, x]) => html`<div>${k} → ${typeof x === 'string' ? x : JSON.stringify(x)}</div>`);
  return typeof v === 'string' ? v : JSON.stringify(v);
}

/** One step: its latest attempt, gate, and what it is. */
@customElement('step-view')
export class StepView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.5rem 0.8rem;
      }
      .head h2 {
        margin: 0;
      }
      .head .spacer {
        flex: 1;
      }
      .facts {
        display: flex;
        flex-wrap: wrap;
        gap: 0.2rem 0.5rem;
        margin-top: 0.4rem;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .facts > :not(:last-child)::after {
        content: '·';
        margin-left: 0.5rem;
      }
      .latest {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.3rem 0.6rem;
        font-size: 0.9rem;
      }
      .latest .muted {
        font-size: 0.82rem;
      }
      .reason {
        margin: 0.4rem 0 0;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .warn {
        color: var(--color-warning);
      }
      dl {
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: 0.35rem 1rem;
        margin: 0;
        font-size: 0.85rem;
      }
      dt {
        color: var(--color-text-muted);
      }
      dd {
        margin: 0;
        min-width: 0;
        overflow-wrap: anywhere;
      }
      .list > * + *::before {
        content: ', ';
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare step: string;

  @state() private declare _name: string;
  @state() private declare _health: PipelineHealth | null;
  @state() private declare _def: ManifestStep | null;
  @state() private declare _version: number | null;
  @state() private declare _latest: StepHistoryItem | null;
  @state() private declare _loaded: boolean;
  @state() private declare _busy: boolean;
  @state() private declare _errors: string[];

  constructor() {
    super();
    this.pipelineId = '';
    this.step = '';
    this._name = '';
    this._health = null;
    this._def = null;
    this._version = null;
    this._latest = null;
    this._loaded = false;
    this._busy = false;
    this._errors = [];
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('step')) {
      this._loaded = false;
      this._def = null;
      this._latest = null;
      this.reload();
    }
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const [pipeline, health, history] = await Promise.all([
        this._name ? null : p.get(),
        p.health.get().catch(() => null),
        p.steps.byStep(this.step).history.get({ queryParameters: { limit: 1 } }),
      ]);
      if (pipeline) this._name = pipeline.name ?? '';
      this._health = health ?? null;
      this._latest = history?.[0] ?? null;
      this._version = currentVersion(toActivation(health?.activation));
      const v = this._version !== null ? await loadVersion(this.pipelineId, this._version) : null;
      this._def = stepsByName(v?.manifest ?? null).get(this.step) ?? null;
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._loaded = true;
    }
  }

  render() {
    const blocked = (this._health?.blockedSteps ?? []).includes(this.step);
    const held = (this._health?.heldRuns ?? []).filter((r) => r.heldAt === this.step);
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._name || 'Pipeline'}</a>
      </div>
      <section>
        <div class="head">
          <h2>${this.step}</h2>
          ${blocked ? html`<span class="pill t-wait">blocked</span>` : nothing}
          <span class="spacer"></span>
          ${this._retryable(blocked)
            ? html`<button class="btn" ?disabled=${this._busy} @click=${this._retry}>Retry</button>`
            : nothing}
          <button class="btn" ?disabled=${this._busy} @click=${() => this._setGate(!blocked)}>
            ${blocked ? 'Unblock' : 'Block'}
          </button>
        </div>
        ${this._renderFacts()}
        ${held.length
          ? html`<p class="reason warn">
              Holding
              ${held.map((r, i) => html`${i ? ', ' : ''}<a href=${runPath(this.pipelineId, r.number)} @click=${navClick}>#${r.number}</a>`)}
            </p>`
          : nothing}
        ${this._errors.length ? html`<div class="errors" role="alert">${this._errors.map((e) => html`<p>${e}</p>`)}</div>` : nothing}
      </section>
      <section class="panel">${this._renderLatest()}</section>
      ${this._def ? html`<section class="panel">${this._renderDefinition(this._def)}</section>` : nothing}
      ${this._loaded && !this._def
        ? html`<p class="muted">This step isn't in the active version of the pipeline.</p>`
        : nothing}
    `;
  }

  private _renderFacts() {
    const d = this._def;
    return html`<div class="facts">
      ${d ? html`<span>${d.kind}</span>` : nothing}
      ${d?.timeoutSeconds ? html`<span>timeout ${formatSeconds(d.timeoutSeconds)}</span>` : nothing}
      ${typeof d?.retries === 'number' && d.retries > 0 ? html`<span>${d.retries} ${d.retries === 1 ? 'retry' : 'retries'}</span>` : nothing}
      <a href=${stepRunsPath(this.pipelineId, this.step)} @click=${navClick}>Runs</a>
    </div>`;
  }

  private _renderLatest() {
    const a = this._latest;
    if (!a) return html`<p class="muted">${this._loaded ? 'Never run.' : 'Loading…'}</p>`;
    const at = a.finishedAt ?? a.startedAt ?? a.createdAt;
    return html`<div class="latest">
        <a class="st t-${toneOf(a.status)}" href=${stepLogsPath(this.pipelineId, a.runNumber, this.step, a.attemptNumber)} @click=${navClick}><span class="dot"></span>${statusLabel(a.status)}</a
        >
        <a href="${runPath(this.pipelineId, a.runNumber)}?step=${encodeURIComponent(this.step)}&attempt=${a.attemptNumber}" @click=${navClick}
          >#${a.runNumber}${(a.attemptNumber ?? 1) > 1 ? `@${a.attemptNumber}` : ''}</a
        >
        <span class="muted"><relative-time .value=${at}></relative-time></span>
        ${a.startedAt ? html`<span class="muted">${formatDuration(a.startedAt, a.finishedAt)}</span>` : nothing}
      </div>
      ${a.reason ? html`<p class="reason">${a.reason}</p>` : nothing}`;
  }

  private _renderDefinition(d: ManifestStep) {
    const inputs = Object.entries(d.input ?? {});
    const config = d.config && typeof d.config === 'object' ? Object.entries(d.config as Record<string, unknown>) : [];
    return html`<dl>
      <dt>Image</dt>
      <dd class="mono">${d.image}</dd>
      <dt>Reads</dt>
      <dd>
        ${inputs.length
          ? html`<span class="list">${inputs.map(([name, m]) => html`<span><code>${name}</code> from ${this._source(m)}</span>`)}</span>`
          : html`<span class="muted">nothing</span>`}
      </dd>
      <dt>Output</dt>
      <dd>${d.output ? html`<code>${d.output}</code>` : html`<span class="muted">none</span>`}</dd>
      ${d.secrets?.length
        ? html`<dt>Secrets</dt>
            <dd class="list">${d.secrets.map((s) => html`<code>${s}</code>`)}</dd>`
        : nothing}
      ${config.map(
        ([k, v]) => html`<dt>${k}</dt>
          <dd class="mono">${configValue(v)}</dd>`,
      )}
    </dl>`;
  }

  private _source(m: Mapping) {
    const roots = new Set<string>();
    const walk = (x: Mapping) => {
      if (!x || typeof x !== 'object') return;
      if ('ref' in x && typeof x.ref === 'string') roots.add(x.ref.split('.')[0]);
      else if (!('const' in x)) Object.values(x as Record<string, Mapping>).forEach(walk);
    };
    walk(m);
    if (!roots.size) return html`<span class="muted">a constant</span>`;
    return [...roots].map(
      (r, i) => html`${i ? ', ' : ''}${r === 'input'
        ? 'the pipeline input'
        : html`<a href=${stepPath(this.pipelineId, r)} @click=${navClick}>${r}</a>`}`,
    );
  }

  private _retryable(blocked: boolean) {
    const a = this._latest;
    return !!a && a.versionNumber === this._version && !isActiveStatus(a.status) && !blocked;
  }

  private _retry = () =>
    this._act(() =>
      apiPost(`/api/pipelines/${this.pipelineId}/runs/${this._latest!.runNumber}/steps/${encodeURIComponent(this.step)}/rerun`, {
        continue: true,
      }),
    );

  private _setGate(block: boolean) {
    return this._act(() => apiPost(`/api/pipelines/${this.pipelineId}/steps/${encodeURIComponent(this.step)}/${block ? 'block' : 'unblock'}`));
  }

  private async _act(fn: () => Promise<unknown>) {
    this._busy = true;
    this._errors = [];
    try {
      await fn();
      await this.reload();
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-view': StepView;
  }
}
