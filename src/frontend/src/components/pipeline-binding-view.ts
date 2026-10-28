import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  bindingsGet,
  bindingsReconcile,
  bindingsStatus,
  bindingsUpdate,
  secretsList,
  type PipelineBinding,
  type PipelineBindingStatus,
} from '@olve/api';
import { api, errorMessages } from '../api.js';
import { sharedStyles, navClick, shortSha, secretsPath } from '../v2/ui.js';
import { LiveController, forPipeline } from '../v2/events.js';
import './relative-time.js';
import './pipeline-header.js';

/** A pipeline's git binding: where its definition lives, its reconcile status, and its credentials. */
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

  @state() declare private _binding: PipelineBinding | null;
  @state() declare private _status: PipelineBindingStatus | null;
  @state() declare private _secretNames: string[];
  @state() declare private _busy: boolean;
  @state() declare private _errors: string[];
  @state() declare private _loaded: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this._binding = null;
    this._status = null;
    this._secretNames = [];
    this._busy = false;
    this._errors = [];
    this._loaded = false;
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId, 'binding', 'secrets'),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload();
  }

  async reload() {
    const path = { pipelineId: this.pipelineId };
    try {
      const [binding, status, secrets] = await Promise.all([
        bindingsGet({ client: api, path })
          .then((r) => r.data ?? null)
          .catch(() => null),
        bindingsStatus({ client: api, path })
          .then((r) => r.data)
          .catch(() => null),
        secretsList({ client: api, path: { pipelineId: this.pipelineId } })
          .then((r) => r.data)
          .catch(() => null),
      ]);
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
      <pipeline-header .pipelineId=${this.pipelineId} tab="binding"></pipeline-header>
      ${this._errors.length
        ? html`<div class="errors" role="alert">
            ${this._errors.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      ${b
        ? html`${this._renderSource(b)} ${this._renderCredentials(b)}`
        : this._loaded
          ? html`<p class="muted">This pipeline isn't bound to a repository.</p>`
          : html`<p class="muted">Loading…</p>`}
    `;
  }

  private _renderSource(b: PipelineBinding) {
    const s = this._status;
    const tree = `https://github.com/${b.repo}/tree/${encodeURIComponent(b.branch)}/${b.path}`;
    return html`<section class="card">
      <div class="head">
        <a href=${tree} target="_blank" rel="noopener"
          ><code>${b.repo}@${b.branch}:${b.path}</code></a
        >
        <span class="spacer"></span>
        <button
          class="btn"
          ?disabled=${this._busy || !!s?.reconcileInProgress}
          @click=${this._reconcile}
        >
          Reconcile now
        </button>
      </div>
      ${s
        ? html`<p class="status">
              ${s.reconcileInProgress
                ? html`Reconciling <code>${shortSha(s.reconcileSha)}</code>…`
                : s.result === 'Error'
                  ? html`<span class="bad"
                        >Reconcile failed at
                        <code>${shortSha(s.reconcileSha)}</code></span
                      >
                      <relative-time .value=${s.lastSyncTime}></relative-time>`
                  : s.result === 'Success'
                    ? html`Synced at
                        <code>${shortSha(s.lastReconciledSha)}</code> (version
                        ${s.reconciledVersion})
                        <relative-time .value=${s.lastSyncTime}></relative-time>`
                    : 'Waiting for the first sync.'}
            </p>
            ${s.result === 'Error' && s.problems?.length
              ? html`<ul class="problems">
                  ${s.problems.map((x) => html`<li>${x}</li>`)}
                </ul>`
              : nothing}
            ${s.warnings?.length
              ? html`<ul class="warnings">
                  ${s.warnings.map((x) => html`<li>${x}</li>`)}
                </ul>`
              : nothing}`
        : nothing}
    </section>`;
  }

  private _renderCredentials(b: PipelineBinding) {
    const current = b.credentialsSecret ?? '';
    const names =
      this._secretNames.includes(current) || !current
        ? this._secretNames
        : [current, ...this._secretNames];
    return html`<section class="card">
      <h3>Credentials</h3>
      <form @submit=${this._saveCredentials}>
        <select name="secret" ?disabled=${this._busy}>
          <option value="" ?selected=${!current}>None (public repository)</option>
          ${names.map(
            (n) => html`<option value=${n} ?selected=${n === current}>${n}</option>`,
          )}
        </select>
        <button class="btn small" ?disabled=${this._busy}>Save</button>
        <p class="help">
          A pipeline secret holding a GitHub read token, used to read the repository.
          Manage values on the
          <a href=${secretsPath(this.pipelineId)} @click=${navClick}>Secrets</a> page.
        </p>
      </form>
    </section>`;
  }

  private _reconcile = () =>
    this._act(() =>
      bindingsReconcile({ client: api, path: { pipelineId: this.pipelineId } }),
    );

  private _saveCredentials = (e: Event) => {
    e.preventDefault();
    const secret = new FormData(e.target as HTMLFormElement).get('secret') as string;
    this._act(() =>
      bindingsUpdate({
        client: api,
        path: { pipelineId: this.pipelineId },
        body: { credentialsSecret: secret || null },
      }),
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
