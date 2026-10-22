import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PipelineBindingStatus, PipelineHealth, StepHistoryItem } from '@olve/olve-pipelines-client/src/models/index.js';
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
import { type JsonSchema, type Manifest, type ManifestStep, type Mapping, stepsByName } from '../v2/manifest.js';
import { loadVersion, toActivation, currentVersion, type LoadedVersion } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import './relative-time.js';

/** One-line type of a schema: `string`, `file`, `"beta" | "prod"`, `{ a, b }`, `string[]`. */
function typeOf(s: JsonSchema | undefined): string {
  if (!s) return 'any';
  if (s['x-olve-kind'] === 'fileRef') return 'file';
  if (s.enum) return s.enum.map((v) => JSON.stringify(v)).join(' | ');
  if (s.const !== undefined) return JSON.stringify(s.const);
  if (s.anyOf) return s.anyOf.map(typeOf).join(' | ');
  const t = Array.isArray(s.type) ? s.type.join(' | ') : s.type;
  if (t === 'array') return `${typeOf(s.items)}[]`;
  if (t === 'object' && s.properties) return `{ ${Object.keys(s.properties).join(', ')} }`;
  if (t === 'integer') return 'number';
  return t ?? 'any';
}

function fieldsOf(s: JsonSchema | null | undefined): { name: string; type: string; optional: boolean }[] {
  if (!s?.properties) return [];
  const required = new Set(s.required ?? []);
  return Object.entries(s.properties).map(([name, p]) => ({ name, type: typeOf(p), optional: !required.has(name) }));
}

function refsOf(m: Mapping): string[] {
  if (!m || typeof m !== 'object') return [];
  if ('ref' in m && typeof m.ref === 'string') return [m.ref];
  if ('const' in m) return [];
  return Object.values(m as Record<string, Mapping>).flatMap(refsOf);
}

const LIBRARY: Record<string, string> = {
  script: 'steps.script',
  gitHubSource: 'steps.gitHubSource',
  imageBuild: 'steps.imageBuild',
  helmDeployOverSsh: 'steps.helmDeployOverSsh',
};

/** One step: what it is and its latest attempt, what it reads and produces, and its secrets. */
@customElement('step-view')
export class StepView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .card {
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: var(--radius-lg);
        padding: 1rem 1.1rem;
      }
      .cards {
        display: grid;
        grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
        gap: 1rem;
        align-items: start;
        margin-top: 1rem;
      }
      .cards > .card {
        margin: 0;
      }
      @media (max-width: 760px) {
        .cards {
          grid-template-columns: minmax(0, 1fr);
        }
      }
      h3 {
        margin: 0 0 0.7rem;
        font-size: 0.78rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--color-text-muted);
      }
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
      .source {
        margin-top: 0.25rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .desc {
        margin: 0.6rem 0 0;
        font-size: 0.92rem;
      }
      .latest {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.3rem 0.5rem;
        margin-top: 0.8rem;
        padding-top: 0.8rem;
        border-top: 1px solid var(--color-border);
        font-size: 0.88rem;
      }
      .latest > :not(:first-child):not(.runs)::before {
        content: '·';
        margin-right: 0.5rem;
        color: var(--color-text-muted);
      }
      .latest .runs {
        margin-left: auto;
      }
      .muted {
        color: var(--color-text-muted);
      }
      .reason {
        margin: 0.4rem 0 0;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .warn {
        color: var(--color-warning);
      }
      .label {
        margin: 1rem 0 0.4rem;
        font-size: 0.75rem;
        color: var(--color-text-muted);
      }
      h3 + .label {
        margin-top: 0;
      }
      .io + .io {
        margin-top: 0.8rem;
      }
      .io-head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0.2rem 0.5rem;
        font-size: 0.9rem;
      }
      .from {
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .fields {
        margin: 0.3rem 0 0;
        padding: 0;
        list-style: none;
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem 1rem;
        font-family: var(--font-mono);
        font-size: 0.76rem;
      }
      .type {
        color: var(--color-text-muted);
      }
      .secret + .secret {
        margin-top: 0.7rem;
      }
      .secret p {
        margin: 0.15rem 0 0;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      p.empty {
        margin: 0;
        font-size: 0.85rem;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare step: string;

  @state() private declare _name: string;
  @state() private declare _health: PipelineHealth | null;
  @state() private declare _binding: PipelineBindingStatus | null;
  @state() private declare _version: LoadedVersion | null;
  @state() private declare _history: StepHistoryItem[];
  @state() private declare _loaded: boolean;
  @state() private declare _busy: boolean;
  @state() private declare _errors: string[];

  constructor() {
    super();
    this.pipelineId = '';
    this.step = '';
    this._name = '';
    this._health = null;
    this._binding = null;
    this._version = null;
    this._history = [];
    this._loaded = false;
    this._busy = false;
    this._errors = [];
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('step')) {
      this._loaded = false;
      this._version = null;
      this._history = [];
      this.reload();
    }
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const [pipeline, health, binding, history] = await Promise.all([
        this._name ? null : p.get(),
        p.health.get().catch(() => null),
        p.binding.status.get().catch(() => null),
        p.steps.byStep(this.step).history.get({ queryParameters: { limit: 200 } }),
      ]);
      if (pipeline) this._name = pipeline.name ?? '';
      this._health = health ?? null;
      this._binding = binding ?? null;
      this._history = history ?? [];
      const n = currentVersion(toActivation(health?.activation));
      this._version = n !== null ? await loadVersion(this.pipelineId, n) : null;
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._loaded = true;
    }
  }

  render() {
    const m = this._version?.manifest ?? null;
    const d = stepsByName(m).get(this.step) ?? null;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._name || 'Pipeline'}</a>
      </div>
      ${this._renderOverview(d)}
      ${m && d
        ? html`<div class="cards">
            <section class="card">${this._renderIo(m, d)}</section>
            <section class="card">${this._renderSecrets(m, d)}</section>
          </div>`
        : this._loaded
          ? html`<p class="muted">This step isn't in the active version of the pipeline.</p>`
          : nothing}
    `;
  }

  private _renderOverview(d: ManifestStep | null) {
    const blocked = (this._health?.blockedSteps ?? []).includes(this.step);
    const held = (this._health?.heldRuns ?? []).filter((r) => r.heldAt === this.step);
    return html`<section class="card">
      <div class="head">
        <h2>${this.step}</h2>
        ${blocked ? html`<span class="pill t-wait">blocked</span>` : nothing}
        <span class="spacer"></span>
        ${this._retryable(blocked) ? html`<button class="btn" ?disabled=${this._busy} @click=${this._retry}>Retry</button>` : nothing}
        <button class="btn" ?disabled=${this._busy} @click=${() => this._setGate(!blocked)}>${blocked ? 'Unblock' : 'Block'}</button>
      </div>
      ${d ? html`<div class="source">${this._source(d)}</div>` : nothing}
      ${d?.description ? html`<p class="desc">${d.description}</p>` : nothing}
      ${this._renderLatest()}
      ${held.length
        ? html`<p class="reason warn">
            Holding
            ${held.map((r, i) => html`${i ? ', ' : ''}<a href=${runPath(this.pipelineId, r.number)} @click=${navClick}>#${r.number}</a>`)}
          </p>`
        : nothing}
      ${this._errors.length ? html`<div class="errors" role="alert">${this._errors.map((e) => html`<p>${e}</p>`)}</div>` : nothing}
    </section>`;
  }

  private _source(d: ManifestStep) {
    if (LIBRARY[d.kind]) return html`<code>${LIBRARY[d.kind]}</code> library step`;
    const b = this._binding;
    const sha = this._version?.commitSha;
    const path = [b?.path, `handlers/${d.name}.ts`].filter(Boolean).join('/');
    if (!b?.repo || !sha) return html`<code>${path}</code>`;
    return html`<a href="https://github.com/${b.repo}/blob/${sha}/${path}" target="_blank" rel="noopener"><code>${path}</code></a>`;
  }

  private _renderLatest() {
    const a = this._history[0];
    if (!a) return html`<div class="latest"><span class="muted">${this._loaded ? 'Never run' : 'Loading…'}</span></div>`;
    const at = a.finishedAt ?? a.startedAt ?? a.createdAt;
    const count = this._history.length >= 200 ? '200+' : String(this._history.length);
    return html`<div class="latest">
        <a class="st t-${toneOf(a.status)}" href=${stepLogsPath(this.pipelineId, a.runNumber, this.step, a.attemptNumber)} @click=${navClick}
          ><span class="dot"></span>${statusLabel(a.status)}</a
        >
        <a href="${runPath(this.pipelineId, a.runNumber)}?step=${encodeURIComponent(this.step)}&attempt=${a.attemptNumber}" @click=${navClick}
          >#${a.runNumber}${(a.attemptNumber ?? 1) > 1 ? `@${a.attemptNumber}` : ''}</a
        >
        <span class="muted"><relative-time .value=${at}></relative-time></span>
        ${a.startedAt ? html`<span class="muted">${formatDuration(a.startedAt, a.finishedAt)}</span>` : nothing}
        <a class="runs" href=${stepRunsPath(this.pipelineId, this.step)} @click=${navClick}>${count} runs</a>
      </div>
      ${a.reason ? html`<p class="reason">${a.reason}</p>` : nothing}`;
  }

  private _renderIo(m: Manifest, d: ManifestStep) {
    const inputs = Object.keys(d.inputTypes ?? {});
    return html`<h3>Inputs and output</h3>
      <div class="label">Reads</div>
      ${inputs.length ? inputs.map((k) => this._input(m, d, k)) : html`<p class="muted empty">Nothing.</p>`}
      <div class="label">Produces</div>
      ${d.output
        ? html`<div class="io">
            <div class="io-head"><b>${d.output}</b></div>
            ${this._fields(m.models[d.output]?.output)}
          </div>`
        : html`<p class="muted empty">No output.</p>`}`;
  }

  private _input(m: Manifest, d: ManifestStep, key: string) {
    const t = d.inputTypes[key];
    const model = 'model' in t ? t.model : null;
    const schema = model ? m.models[model]?.output : 'schema' in t ? t.schema.output : undefined;
    const refs = refsOf(d.input?.[key] ?? { const: null });
    return html`<div class="io">
      <div class="io-head">
        <code>${key}</code>
        ${model ? html`<b>${model}</b>` : schema?.properties ? nothing : html`<span class="type mono">${typeOf(schema)}</span>`}
        ${'optional' in t && t.optional ? html`<span class="muted">optional</span>` : nothing}
        <span class="from">from ${refs.length ? refs.map((r, i) => html`${i ? ', ' : ''}${this._ref(r)}`) : 'a constant'}</span>
      </div>
      ${this._fields(schema)}
    </div>`;
  }

  private _ref(ref: string) {
    const [root, ...rest] = ref.split('.');
    const path = rest.length ? html` <code>${rest.join('.')}</code>` : nothing;
    if (root === 'input') return html`the pipeline input${path}`;
    if (root === 'failure') return 'the failure context';
    return html`<a href=${stepPath(this.pipelineId, root)} @click=${navClick}>${root}</a>${path}`;
  }

  private _fields(s: JsonSchema | null | undefined) {
    const fields = fieldsOf(s);
    if (!fields.length) return nothing;
    return html`<ul class="fields">
      ${fields.map((f) => html`<li>${f.name}${f.optional ? '?' : ''} <span class="type">${f.type}</span></li>`)}
    </ul>`;
  }

  private _renderSecrets(m: Manifest, d: ManifestStep) {
    const described = new Map(m.secrets.map((s) => [s.name, s.description]));
    return html`<h3>Secrets</h3>
      ${d.secrets?.length
        ? d.secrets.map(
            (s) => html`<div class="secret">
              <code>${s}</code>
              ${described.get(s) ? html`<p>${described.get(s)}</p>` : nothing}
            </div>`,
          )
        : html`<p class="muted empty">None.</p>`}`;
  }

  private _retryable(blocked: boolean) {
    const a = this._history[0];
    return !!a && a.versionNumber === this._version?.number && !isActiveStatus(a.status) && !blocked;
  }

  private _retry = () =>
    this._act(() =>
      apiPost(`/api/pipelines/${this.pipelineId}/runs/${this._history[0]!.runNumber}/steps/${encodeURIComponent(this.step)}/rerun`, {
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
