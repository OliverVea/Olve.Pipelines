import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, errorMessages } from '../api.js';
import type { RunSummary } from '@olve/olve-pipelines-client/src/models/index.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  runPath,
} from '../v2/ui.js';
import { LiveController, forPipeline } from '../v2/events.js';
import './relative-time.js';
import './pipeline-header.js';

@customElement('pipeline-runs-view')
export class PipelineRunsView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .hint {
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
      .links {
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _runs: RunSummary[] | null;
  @state() declare private _total: number;
  @state() declare private _error: string | null;

  constructor() {
    super();
    this.pipelineId = '';
    this._runs = null;
    this._total = 0;
    this._error = null;
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId, 'run', 'attempt'),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload();
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const runs = await p.runs.get({ queryParameters: { pageSize: 50 } });
      this._runs = runs?.items ?? [];
      this._total = runs?.totalCount ?? this._runs.length;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const runs = this._runs;
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="runs"></pipeline-header>
      ${runs && this._total > runs.length
        ? html`<p class="hint">Latest ${runs.length} of ${this._total}</p>`
        : nothing}
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${runs === null
        ? html`<p class="muted">Loading…</p>`
        : runs.length === 0
          ? html`<p class="muted">No runs yet.</p>`
          : html`<section class="table-wrap">
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
                  ${runs.map((r) => this._row(r))}
                </tbody>
              </table>
            </section>`}
    `;
  }

  private _row(r: RunSummary) {
    return html`<tr>
      <td>
        <a href=${runPath(this.pipelineId, r.number)} @click=${navClick}
          ><b>#${r.number}</b></a
        >
      </td>
      <td>
        <span class="st t-${toneOf(r.status)}"
          ><span class="dot"></span>${statusLabel(r.status)}</span
        >
        ${r.heldAt && r.status === 'Running'
          ? html`<span class="pill">held at ${r.heldAt}</span>`
          : nothing}
      </td>
      <td class="mono">
        v${r.versionNumber} <span class="muted">${r.versionStamp}</span>
      </td>
      <td>${(r.triggerKind ?? '').toLowerCase()}</td>
      <td><relative-time .value=${r.createdAt}></relative-time></td>
      <td class="num">${formatDuration(r.createdAt, r.finishedAt)}</td>
      <td class="links">
        ${r.rerunOfNumber
          ? html`rerun of
              <a href=${runPath(this.pipelineId, r.rerunOfNumber)} @click=${navClick}
                >#${r.rerunOfNumber}</a
              >`
          : nothing}
        ${r.supersededByNumber
          ? html`superseded by
              <a href=${runPath(this.pipelineId, r.supersededByNumber)} @click=${navClick}
                >#${r.supersededByNumber}</a
              >`
          : nothing}
      </td>
    </tr>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-runs-view': PipelineRunsView;
  }
}
