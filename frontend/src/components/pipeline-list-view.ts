import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { client } from '../api.js';
import { navigate } from '../router.js';
import type { Pipeline } from '@olve/olve-pipelines-client/src/models/index.js';

@customElement('pipeline-list-view')
export class PipelineListView extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .header {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 1rem;
    }

    .header h2 {
      margin: 0;
      font-size: 1.5rem;
      flex: 1;
    }

    .reload-btn {
      width: 2rem;
      height: 2rem;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      cursor: pointer;
      transition: background var(--transition), border-color var(--transition);
    }

    .reload-btn:hover:not(:disabled) {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    .reload-btn:disabled {
      opacity: 0.6;
      cursor: default;
    }

    .reload-btn svg {
      width: 1rem;
      height: 1rem;
    }

    .reload-btn.loading svg {
      animation: spin 0.8s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }

    .pipeline-card {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.75rem 1rem;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      margin-bottom: 0.5rem;
      transition: border-color var(--transition);
    }

    .pipeline-card:hover {
      border-color: var(--color-primary);
    }

    .pipeline-card a {
      color: var(--color-text);
      font-weight: 500;
      text-decoration: none;
    }

    .pipeline-card a:hover {
      color: var(--color-primary);
    }

    .btn-delete {
      background: none;
      border: 1px solid var(--color-danger);
      color: var(--color-danger);
      padding: 0.25rem 0.5rem;
      border-radius: var(--radius);
      font-size: 0.8rem;
      transition: all var(--transition);
    }

    .btn-delete:hover {
      background: var(--color-danger);
      color: white;
    }

    .create-form {
      display: flex;
      gap: 0.5rem;
      margin-top: 1rem;
    }

    .create-form input {
      flex: 1;
      padding: 0.5rem 0.75rem;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      color: var(--color-text);
    }

    .create-form input:focus {
      outline: none;
      border-color: var(--color-primary);
    }

    .btn-primary {
      background: var(--color-primary);
      color: white;
      border: none;
      padding: 0.5rem 1rem;
      border-radius: var(--radius);
      font-weight: 500;
      transition: background var(--transition);
    }

    .btn-primary:hover {
      background: var(--color-primary-hover);
    }

    .empty {
      color: var(--color-text-muted);
      padding: 2rem;
      text-align: center;
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }
  `;

  @state() private declare _pipelines: Pipeline[];
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;
  @state() private declare _loaded: boolean;

  constructor() {
    super();
    this._pipelines = [];
    this._loading = true;
    this._error = null;
    this._loaded = false;
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
  }

  private async _load() {
    this._loading = true;
    this._error = null;
    try {
      this._pipelines = (await client.api.pipelines.get()) ?? [];
      this._loaded = true;
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    return html`
      <div class="header">
        <h2>Pipelines</h2>
        <button
          class="reload-btn ${this._loading ? 'loading' : ''}"
          @click=${this._load}
          ?disabled=${this._loading}
          title="Refresh"
          aria-label="Refresh"
        >
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M21 12a9 9 0 1 1-3-6.7" />
            <polyline points="21 3 21 9 15 9" />
          </svg>
        </button>
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : ''}
      ${!this._loaded
        ? ''
        : this._pipelines.length === 0
        ? html`<p class="empty">No pipelines yet.</p>`
        : this._pipelines.map(
            (p) => html`
              <div class="pipeline-card">
                <a
                  href="/pipeline/${p.id}"
                  @click=${(e: Event) => this._navToPipeline(e, p.name ?? '')}
                  >${p.name}</a
                >
                <button class="btn-delete" @click=${() => this._delete(p.id!)}>
                  Delete
                </button>
              </div>
            `,
          )}
      <form class="create-form" @submit=${this._create}>
        <input type="text" name="name" placeholder="New pipeline name" required />
        <button class="btn-primary" type="submit">Create</button>
      </form>
    `;
  }

  private _navToPipeline(e: Event, name: string) {
    e.preventDefault();
    const href = (e.currentTarget as HTMLAnchorElement).getAttribute('href')!;
    navigate(href, { pipelineName: name });
  }

  private async _create(e: SubmitEvent) {
    e.preventDefault();
    const form = e.target as HTMLFormElement;
    const name = new FormData(form).get('name') as string;
    await client.api.pipelines.post({ queryParameters: { name } });
    form.reset();
    await this._load();
  }

  private async _delete(id: string) {
    await client.api.pipelines.byId(id).delete();
    await this._load();
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-list-view': PipelineListView;
  }
}
