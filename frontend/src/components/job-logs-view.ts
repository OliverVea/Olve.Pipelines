import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { AnsiUp } from 'ansi_up';
import { getAccessToken } from '../auth.js';
import { navigate } from '../router.js';

const ansi = new AnsiUp();
ansi.use_classes = false;

@customElement('job-logs-view')
export class JobLogsView extends LitElement {
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
      font-size: 1.25rem;
    }

    .back {
      color: var(--color-text-muted);
      text-decoration: none;
      font-size: 0.9rem;
    }

    .back:hover {
      color: var(--color-primary);
    }

    .toolbar {
      display: flex;
      justify-content: flex-end;
      margin-bottom: 0.5rem;
    }

    .reload-btn {
      padding: 0.35rem 0.75rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font-size: 0.8rem;
      cursor: pointer;
      transition: background var(--transition), border-color var(--transition);
    }

    .reload-btn:hover {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    pre {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      padding: 1rem;
      overflow: auto;
      max-height: calc(100vh - 220px);
      font-size: 0.8rem;
      line-height: 1.4;
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    }

    .empty {
      color: var(--color-text-muted);
      font-style: italic;
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }
  `;

  @property() declare pipelineId: string;
  @property() declare jobId: string;

  @state() private declare _logs: string;
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;
  @state() private declare _unavailable: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.jobId = '';
    this._logs = '';
    this._loading = true;
    this._error = null;
    this._unavailable = false;
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
  }

  updated(changed: Map<string, unknown>): void {
    if (changed.has('jobId') && changed.get('jobId') !== this.jobId) {
      this._load();
    }
  }

  private async _load() {
    this._loading = true;
    this._error = null;
    this._unavailable = false;
    try {
      const token = await getAccessToken();
      const res = await fetch(`/api/jobs/${this.jobId}/logs`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (res.status === 400) {
        const body = await res.json().catch(() => null);
        const first = Array.isArray(body) ? body[0] : body;
        const msg: string = first?.message ?? '';
        if (msg.startsWith('Logs not available')) {
          this._unavailable = true;
          this._logs = '';
        } else {
          this._error = msg || `Request failed (${res.status})`;
        }
        return;
      }
      if (!res.ok) {
        this._error = `Request failed (${res.status})`;
        return;
      }
      const ct = res.headers.get('content-type') ?? '';
      if (ct.includes('application/json')) {
        const body = await res.json();
        this._logs = typeof body === 'string' ? body : JSON.stringify(body);
      } else {
        this._logs = await res.text();
      }
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    return html`
      <div class="header">
        <a
          class="back"
          href="/pipeline/${this.pipelineId}"
          @click=${this._back}
          >&larr; Back</a
        >
        <h2>Job Logs</h2>
      </div>
      <div class="toolbar">
        <button class="reload-btn" @click=${this._load} ?disabled=${this._loading}>
          ${this._loading ? 'Loading...' : 'Reload'}
        </button>
      </div>
      ${this._error
        ? html`<p class="error">${this._error}</p>`
        : this._loading
        ? html`<p class="empty">Loading logs...</p>`
        : this._unavailable
        ? html`<p class="empty">
            Logs are not available for this job yet. Try again once it has
            finished running.
          </p>`
        : this._logs.length === 0
        ? html`<p class="empty">No logs available.</p>`
        : html`<pre>${unsafeHTML(ansi.ansi_to_html(this._logs))}</pre>`}
    `;
  }

  private _back(e: Event) {
    e.preventDefault();
    navigate(`/pipeline/${this.pipelineId}`);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'job-logs-view': JobLogsView;
  }
}
