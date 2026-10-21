import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, extractErrorMessage } from '../api.js';
import { navigate } from '../router.js';
import type {
  Pipeline,
  Attempt,
  PipelineBindingStatus,
} from '@olve/olve-pipelines-client/src/models/index.js';
import './refresh-control.js';
import './relative-time.js';

@customElement('pipeline-detail-view')
export class PipelineDetailView extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .header {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 1.5rem;
    }

    .header h2 {
      margin: 0;
      font-size: 1.5rem;
      flex: 1;
    }

    .back {
      color: var(--color-text-muted);
      text-decoration: none;
      font-size: 0.9rem;
    }

    .back:hover {
      color: var(--color-primary);
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }

    .binding-bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem;
      margin-bottom: 1.25rem;
    }

    .pill {
      display: inline-flex;
      align-items: center;
      gap: 0.35rem;
      padding: 0.2rem 0.6rem;
      border-radius: 999px;
      font-size: 0.8rem;
      line-height: 1.4;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text-muted);
      max-width: 100%;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .pill .branch {
      opacity: 0.6;
    }

    .pill.ok {
      color: var(--color-success, #2e7d32);
      border-color: var(--color-success, #2e7d32);
    }

    .pill.error,
    .pill.warn {
      color: var(--color-danger);
      border-color: var(--color-danger);
    }

    .placeholder {
      padding: 1.25rem;
      margin-bottom: 1.5rem;
      border: 1px dashed var(--color-border);
      border-radius: 8px;
      background: var(--color-surface);
      color: var(--color-text-muted);
    }

    .placeholder strong {
      color: var(--color-text);
    }

    h3 {
      font-size: 1rem;
      margin: 0 0 0.5rem;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.9rem;
    }

    th,
    td {
      text-align: left;
      padding: 0.4rem 0.5rem;
      border-bottom: 1px solid var(--color-border);
    }

    th {
      color: var(--color-text-muted);
      font-weight: 500;
    }

    .muted {
      color: var(--color-text-muted);
    }

    .table-wrap {
      overflow-x: auto;
    }
  `;

  @property() declare pipelineId: string;

  @state() private declare _pipeline: Pipeline | null;
  @state() private declare _jobs: Attempt[];
  @state() private declare _bindingStatus: PipelineBindingStatus | null;
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;
  @state() private declare _titleHint: string;

  constructor() {
    super();
    this.pipelineId = '';
    this._pipeline = null;
    this._jobs = [];
    this._bindingStatus = null;
    this._loading = true;
    this._error = null;
    const state = history.state as { pipelineName?: string } | null;
    this._titleHint = state?.pipelineName ?? '';
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
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
      const [pipeline, jobsPage, bindingStatus] = await Promise.all([
        p.get(),
        client.api.jobs.get({
          queryParameters: {
            pipelineId: this.pipelineId,
            pageSize: 50,
          },
        }),
        // Unbound (draft) pipelines have no binding — tolerate the 404.
        p.binding.status.get().catch(() => null),
      ]);
      this._pipeline = pipeline ?? null;
      this._bindingStatus = bindingStatus ?? null;
      this._jobs = jobsPage?.items ?? [];
    } catch (e) {
      if (!silent) this._error = extractErrorMessage(e);
    } finally {
      if (!silent) this._loading = false;
    }
  }

  render() {
    if (this._error) return html`<p class="error">${this._error}</p>`;

    return html`
      <div class="header">
        <a class="back" href="/" @click=${this._back}>&larr; Back</a>
        <h2>${this._pipeline?.name ?? this._titleHint}</h2>
        <refresh-control
          .loading=${this._loading}
          @refresh=${this._onRefresh}
        ></refresh-control>
      </div>
      ${this._renderBindingBadge()}
      ${this._pipeline
        ? html`<div class="placeholder">
              <strong>v2 pipeline view coming.</strong> Pipelines are being rebuilt around
              TypeScript-defined stages and runs; the stage graph and run history will appear here.
            </div>
            ${this._renderJobs()}`
        : this._loading
        ? html``
        : html`<p>Pipeline not found.</p>`}
    `;
  }

  private _renderJobs() {
    if (this._jobs.length === 0) return html`<p class="muted">No jobs yet.</p>`;
    return html`
      <h3>Recent jobs</h3>
      <div class="table-wrap">
        <table>
          <thead>
            <tr><th>Run</th><th>Step</th><th>Attempt</th><th>Status</th><th>Created</th><th></th></tr>
          </thead>
          <tbody>
            ${this._jobs.map(
              (j) => html`<tr>
                <td>#${j.runNumber ?? ''}</td>
                <td>${j.stepName}</td>
                <td>${j.number ?? ''}</td>
                <td>${j.status?.type ?? ''}</td>
                <td>${j.createdAt
                  ? html`<relative-time .value=${j.createdAt}></relative-time>`
                  : ''}</td>
                <td>
                  <a
                    href="/pipeline/${this.pipelineId}/job/${j.id}/logs"
                    @click=${this._navTo}
                    >logs</a
                  >
                </td>
              </tr>`,
            )}
          </tbody>
        </table>
      </div>
    `;
  }

  private _renderBindingBadge() {
    const b = this._bindingStatus;
    if (!b) return html``; // unbound (draft) pipeline — no GitOps badge

    const result = b.result ?? 0; // 0 NeverRun, 1 Success, 2 Error
    const problems = b.problems ?? [];
    const unset = (b.secrets ?? [])
      .filter((s) => s.isSet === false)
      .map((s) => s.name);

    return html`
      <div class="binding-bar">
        <span class="pill repo" title="GitOps source — configuration is git-only">
          ${b.repo}${b.branch
            ? html`<span class="branch">@${b.branch}</span>`
            : ''}
        </span>
        ${result === 2
          ? html`<span class="pill error" title=${problems.join('\n')}
              >⚠ reconcile error${problems.length
                ? html`: ${problems[0]}`
                : ''}</span
            >`
          : result === 1
          ? html`<span class="pill ok">✓ config synced</span>`
          : html`<span class="pill">pending first sync</span>`}
        ${unset.length
          ? html`<span
              class="pill warn"
              title="Set these via the secrets API or kubectl"
              >⚠ unset secrets: ${unset.join(', ')}</span
            >`
          : ''}
      </div>
    `;
  }

  private _navTo(e: Event) {
    e.preventDefault();
    navigate((e.currentTarget as HTMLAnchorElement).getAttribute('href')!);
  }

  private _back(e: Event) {
    e.preventDefault();
    navigate('/');
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-detail-view': PipelineDetailView;
  }
}
