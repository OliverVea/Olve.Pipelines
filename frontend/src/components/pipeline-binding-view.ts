import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PipelineBindingStatus } from '@olve/olve-pipelines-client/src/models/index.js';
import { client, apiFetch, apiPost, errorMessages } from '../api.js';
import { sharedStyles, navClick, pipelinePath, shortSha, secretsPath } from '../v2/ui.js';
import { PollController } from '../v2/poll.js';
import './relative-time.js';

type Binding = { repo: string; branch: string; path: string; credentialsSecret?: string | null; deployTrigger: number };

const TRIGGERS = [
  { value: 0, label: 'Webhook', help: 'GitHub push hook drives deploys; polling stays on as a slow safety net.' },
  { value: 1, label: 'Webhook only', help: 'Only the push hook; polling stops once the hook is live.' },
  { value: 2, label: 'Poll', help: 'No webhook; the service polls the branch.' },
];

/** A pipeline's git binding: where its definition lives, reconcile status, deploy trigger and credentials. */
@customElement('pipeline-binding-view')
export class PipelineBindingView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .card {
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: var(--radius-lg);
        padding: 1rem 1.1rem;
        margin-top: 1rem;
      }
      h3 {
        margin: 0 0 0.6rem;
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
        gap: 0.5rem 1rem;
      }
      .head .spacer {
        flex: 1;
      }
      .status {
        margin: 0.5rem 0 0;
        font-size: 0.88rem;
      }
      .bad {
        color: var(--color-danger);
      }
      ul {
        margin: 0.4rem 0 0;
        padding-left: 1.2rem;
        font-size: 0.8rem;
      }
      ul.problems {
        color: var(--color-danger);
      }
      ul.warnings {
        color: var(--color-text-muted);
      }
      label.choice {
        display: flex;
        gap: 0.5rem;
        align-items: baseline;
        padding: 0.3rem 0;
        font-size: 0.88rem;
      }
      label.choice span {
        color: var(--color-text-muted);
        font-size: 0.8rem;
      }
      form {
        display: flex;
        flex-wrap: wrap;
        gap: 0.4rem;
        align-items: center;
      }
      select {
        font: inherit;
        font-size: 0.85rem;
        padding: 0.35rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-bg);
        color: var(--color-text);
      }
      .help {
        width: 100%;
        margin: 0.4rem 0 0;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() private declare _name: string;
  @state() private declare _binding: Binding | null;
  @state() private declare _status: PipelineBindingStatus | null;
  @state() private declare _secretNames: string[];
  @state() private declare _busy: boolean;
  @state() private declare _errors: string[];
  @state() private declare _loaded: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this._name = '';
    this._binding = null;
    this._status = null;
    this._secretNames = [];
    this._busy = false;
    this._errors = [];
    this._loaded = false;
    new PollController(this, () => this.reload(), 5000);
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload();
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const [pipeline, binding, status, secrets] = await Promise.all([
        this._name ? null : p.get(),
        apiFetch<Binding>(`/api/pipelines/${this.pipelineId}/binding`).catch(() => null),
        p.binding.status.get().catch(() => null),
        apiFetch<{ name: string }[]>(`/api/pipelines/${this.pipelineId}/secrets`).catch(() => null),
      ]);
      if (pipeline) this._name = pipeline.name ?? '';
      this._binding = binding;
      this._status = status ?? null;
      if (secrets) this._secretNames = secrets.map((s) => s.name);
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._loaded = true;
    }
  }

  render() {
    const b = this._binding;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._name || 'Pipeline'}</a>
      </div>
      <h2>Binding</h2>
      ${this._errors.length ? html`<div class="errors" role="alert">${this._errors.map((e) => html`<p>${e}</p>`)}</div>` : nothing}
      ${b
        ? html`${this._renderSource(b)} ${this._renderTrigger(b)} ${this._renderCredentials(b)}`
        : this._loaded
          ? html`<p class="muted">This pipeline isn't bound to a repository.</p>`
          : html`<p class="muted">Loading…</p>`}
    `;
  }

  private _renderSource(b: Binding) {
    const s = this._status;
    const tree = `https://github.com/${b.repo}/tree/${encodeURIComponent(b.branch)}/${b.path}`;
    return html`<section class="card">
      <div class="head">
        <a href=${tree} target="_blank" rel="noopener"><code>${b.repo}@${b.branch}:${b.path}</code></a>
        <span class="spacer"></span>
        <button class="btn" ?disabled=${this._busy || !!s?.reconcileInProgress} @click=${this._reconcile}>Reconcile now</button>
      </div>
      ${s
        ? html`<p class="status">
              ${s.reconcileInProgress
                ? html`Reconciling <code>${shortSha(s.reconcileSha)}</code>…`
                : s.result === 2
                  ? html`<span class="bad">Reconcile failed at <code>${shortSha(s.reconcileSha)}</code></span>
                      <relative-time .value=${s.lastSyncTime}></relative-time>`
                  : s.result === 1
                    ? html`Synced at <code>${shortSha(s.lastReconciledSha)}</code> (version ${s.reconciledVersion})
                        <relative-time .value=${s.lastSyncTime}></relative-time>`
                    : 'Waiting for the first sync.'}
            </p>
            ${s.result === 2 && s.problems?.length ? html`<ul class="problems">${s.problems.map((x) => html`<li>${x}</li>`)}</ul>` : nothing}
            ${s.warnings?.length ? html`<ul class="warnings">${s.warnings.map((x) => html`<li>${x}</li>`)}</ul>` : nothing}`
        : nothing}
    </section>`;
  }

  private _renderTrigger(b: Binding) {
    return html`<section class="card">
      <h3>Deploy trigger</h3>
      ${TRIGGERS.map(
        (t) => html`<label class="choice">
          <input
            type="radio"
            name="trigger"
            .checked=${b.deployTrigger === t.value}
            ?disabled=${this._busy}
            @change=${() => this._setTrigger(t.value)}
          />
          <b>${t.label}</b> <span>${t.help}</span>
        </label>`,
      )}
    </section>`;
  }

  private _renderCredentials(b: Binding) {
    const current = b.credentialsSecret ?? '';
    const names = this._secretNames.includes(current) || !current ? this._secretNames : [current, ...this._secretNames];
    return html`<section class="card">
      <h3>Credentials</h3>
      <form @submit=${this._saveCredentials}>
        <select name="secret" ?disabled=${this._busy}>
          <option value="" ?selected=${!current}>None (public repository)</option>
          ${names.map((n) => html`<option value=${n} ?selected=${n === current}>${n}</option>`)}
        </select>
        <button class="btn small" ?disabled=${this._busy}>Save</button>
        <p class="help">
          A pipeline secret holding a GitHub read token, used to read the repository. Manage values on the
          <a href=${secretsPath(this.pipelineId)} @click=${navClick}>Secrets</a> page.
        </p>
      </form>
    </section>`;
  }

  private _reconcile = () => this._act(() => apiPost(`/api/pipelines/${this.pipelineId}/binding/reconcile`));

  private _setTrigger(value: number) {
    this._act(() => apiFetch(`/api/pipelines/${this.pipelineId}/binding/deploy-trigger`, { method: 'PATCH', body: { deployTrigger: value } }));
  }

  private _saveCredentials = (e: Event) => {
    e.preventDefault();
    const secret = new FormData(e.target as HTMLFormElement).get('secret') as string;
    this._act(() =>
      apiFetch(`/api/pipelines/${this.pipelineId}/binding`, { method: 'PATCH', body: { credentialsSecret: secret } }),
    );
  };

  private async _act(fn: () => Promise<unknown>) {
    this._busy = true;
    this._errors = [];
    try {
      await fn();
      await this.reload();
    } catch (e) {
      this._errors = errorMessages(e);
      await this.reload();
    } finally {
      this._busy = false;
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-binding-view': PipelineBindingView;
  }
}
