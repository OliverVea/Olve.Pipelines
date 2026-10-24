import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { PipelineSummary } from '@olve/olve-pipelines-client/src/models/index.js';
import { client, apiFetch, apiPost, errorMessages } from '../api.js';
import { sharedStyles, navClick, pipelinePath, stepPath } from '../v2/ui.js';

type SecretInfo = {
  name: string;
  description?: string | null;
  declaredBy: string[];
  isSet?: boolean | null;
};

/** A pipeline's secrets: who uses each, whether it is set, and setting, deleting or copying values. */
@customElement('pipeline-secrets-view')
export class PipelineSecretsView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .card {
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: var(--radius-lg);
        padding: 0.4rem 1.1rem;
        margin-top: 1rem;
      }
      .card.form {
        padding: 1rem 1.1rem;
      }
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.5rem 1rem;
        padding: 0.75rem 0;
      }
      .row + .row {
        border-top: 1px solid var(--color-border);
      }
      .what {
        flex: 1;
        min-width: 14rem;
      }
      @media (max-width: 640px) {
        .what {
          flex-basis: 100%;
        }
        .actions {
          margin-left: auto;
        }
      }
      .what p {
        margin: 0.15rem 0 0;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .used {
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .state {
        font-size: 0.82rem;
        min-width: 4.5rem;
      }
      .state.set {
        color: var(--color-success);
      }
      .state.unset {
        color: var(--color-danger);
      }
      .actions {
        display: flex;
        gap: 0.4rem;
      }
      .edit {
        display: flex;
        flex-wrap: wrap;
        gap: 0.4rem;
        width: 100%;
      }
      input,
      select {
        font: inherit;
        font-size: 0.85rem;
        padding: 0.35rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-bg);
        color: var(--color-text);
        min-width: 0;
      }
      .edit input[type='password'],
      .edit input.name {
        flex: 1;
        min-width: 10rem;
      }
      h3 {
        margin: 0 0 0.6rem;
        font-size: 0.78rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        color: var(--color-text-muted);
      }
      .note {
        margin: 0.5rem 0 0;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _name: string;
  @state() declare private _secrets: SecretInfo[] | null;
  @state() declare private _pipelines: PipelineSummary[];
  @state() declare private _editing: string | null;
  @state() declare private _busy: boolean;
  @state() declare private _errors: string[];
  @state() declare private _note: string;

  constructor() {
    super();
    this.pipelineId = '';
    this._name = '';
    this._secrets = null;
    this._pipelines = [];
    this._editing = null;
    this._busy = false;
    this._errors = [];
    this._note = '';
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload();
  }

  async reload() {
    if (!this._name)
      client.api.pipelines
        .byId(this.pipelineId)
        .get()
        .then((p) => (this._name = p?.name ?? ''))
        .catch(() => {});
    try {
      const [secrets, pipelines] = await Promise.all([
        apiFetch<SecretInfo[]>(`/api/pipelines/${this.pipelineId}/secrets`),
        client.api.pipelines.summary.get().catch(() => null),
      ]);
      this._secrets = secrets ?? [];
      this._pipelines = (pipelines ?? []).filter((p) => p.id !== this.pipelineId);
    } catch (e) {
      this._errors = errorMessages(e);
    }
  }

  render() {
    const secrets = this._secrets;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}
          >${this._name || 'Pipeline'}</a
        >
      </div>
      <h2>Secrets</h2>
      ${this._errors.length
        ? html`<div class="errors" role="alert">
            ${this._errors.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      ${secrets === null
        ? this._errors.length
          ? nothing
          : html`<p class="muted">Loading…</p>`
        : html`<section class="card">
              ${secrets.length
                ? secrets.map((s) => this._row(s))
                : html`<p class="muted">No secrets.</p>`}
            </section>
            ${this._renderAdd()} ${this._renderCopy()}`}
    `;
  }

  private _row(s: SecretInfo) {
    const editing = this._editing === s.name;
    return html`<div class="row">
      <div class="what">
        <code>${s.name}</code>
        ${s.description ? html`<p>${s.description}</p>` : nothing}
      </div>
      <span class="used">
        ${s.declaredBy.length
          ? html`used by
            ${s.declaredBy.map(
              (d, i) =>
                html`${i ? ', ' : ''}<a
                    href=${stepPath(this.pipelineId, d)}
                    @click=${navClick}
                    >${d}</a
                  >`,
            )}`
          : 'not used by any step'}
      </span>
      <span class="state ${s.isSet ? 'set' : s.declaredBy.length ? 'unset' : ''}"
        >${s.isSet ? 'set' : 'not set'}</span
      >
      <span class="actions">
        <button
          class="btn small"
          ?disabled=${this._busy}
          @click=${() => (this._editing = editing ? null : s.name)}
        >
          ${editing ? 'Cancel' : s.isSet ? 'Change' : 'Set'}
        </button>
        ${s.isSet
          ? html`<button
              class="btn small danger"
              ?disabled=${this._busy}
              @click=${() => this._delete(s.name)}
            >
              Delete
            </button>`
          : nothing}
      </span>
      ${editing
        ? html`<form class="edit" @submit=${(e: Event) => this._submitValue(e, s.name)}>
            <input
              type="password"
              name="value"
              placeholder="Value"
              autocomplete="off"
              required
            />
            <button class="btn small primary" ?disabled=${this._busy}>Save</button>
          </form>`
        : nothing}
    </div>`;
  }

  private _renderAdd() {
    return html`<section class="card form">
      <h3>Add a secret</h3>
      <form class="edit" @submit=${(e: Event) => this._submitNew(e)}>
        <input
          class="name"
          name="name"
          placeholder="NAME"
          autocomplete="off"
          required
          pattern="[A-Za-z_][A-Za-z0-9_]*"
        />
        <input
          type="password"
          name="value"
          placeholder="Value"
          autocomplete="off"
          required
        />
        <button class="btn small primary" ?disabled=${this._busy}>Add</button>
      </form>
    </section>`;
  }

  private _renderCopy() {
    if (!this._pipelines.length) return nothing;
    return html`<section class="card form">
      <h3>Copy from another pipeline</h3>
      <form class="edit" @submit=${(e: Event) => this._submitCopy(e)}>
        <select name="source" required>
          ${this._pipelines.map(
            (p) => html`<option value=${p.id ?? ''}>${p.name}</option>`,
          )}
        </select>
        <label class="note"
          ><input type="checkbox" name="overwrite" /> overwrite existing</label
        >
        <button class="btn small" ?disabled=${this._busy}>Copy all</button>
      </form>
      ${this._note ? html`<p class="note">${this._note}</p>` : nothing}
    </section>`;
  }

  private _submitValue(e: Event, name: string) {
    e.preventDefault();
    const value = new FormData(e.target as HTMLFormElement).get('value') as string;
    this._act(async () => {
      await this._put(name, value);
      this._editing = null;
    });
  }

  private _submitNew(e: Event) {
    e.preventDefault();
    const form = e.target as HTMLFormElement;
    const data = new FormData(form);
    this._act(async () => {
      await this._put(data.get('name') as string, data.get('value') as string);
      form.reset();
    });
  }

  private _submitCopy(e: Event) {
    e.preventDefault();
    const data = new FormData(e.target as HTMLFormElement);
    this._act(async () => {
      const res = await apiPost<{ copied: string[] }>(
        `/api/pipelines/${this.pipelineId}/secrets/copy`,
        {
          sourcePipelineId: data.get('source'),
          overwrite: data.get('overwrite') === 'on',
        },
      );
      this._note = res?.copied?.length
        ? `Copied ${res.copied.join(', ')}.`
        : 'Nothing copied.';
    });
  }

  private _put(name: string, value: string) {
    return apiFetch(
      `/api/pipelines/${this.pipelineId}/secrets/${encodeURIComponent(name)}`,
      { method: 'PUT', body: { value } },
    );
  }

  private _delete(name: string) {
    if (
      !confirm(
        `Delete the value of ${name}? Steps that use it will fail until it is set again.`,
      )
    )
      return;
    this._act(() =>
      apiFetch(`/api/pipelines/${this.pipelineId}/secrets/${encodeURIComponent(name)}`, {
        method: 'DELETE',
      }),
    );
  }

  private async _act(fn: () => Promise<unknown>) {
    this._busy = true;
    this._errors = [];
    this._note = '';
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
    'pipeline-secrets-view': PipelineSecretsView;
  }
}
