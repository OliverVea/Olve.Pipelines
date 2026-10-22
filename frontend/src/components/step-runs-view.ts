import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { StepHistoryItem } from '@olve/olve-pipelines-client/src/models/index.js';
import { client, errorMessages } from '../api.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  pipelinePath,
  runPath,
  stepPath,
  stepLogsPath,
} from '../v2/ui.js';
import { PollController } from '../v2/poll.js';
import './relative-time.js';

/** Every attempt of one step across runs, newest first, marking where its definition changed. */
@customElement('step-runs-view')
export class StepRunsView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      tr.marker td {
        padding: 0.25rem 0.5rem;
        font-size: 0.75rem;
        color: var(--color-when);
        border-bottom: 1px dashed var(--color-when);
      }
      .reason {
        color: var(--color-text-muted);
        font-size: 0.78rem;
        max-width: 32rem;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare step: string;

  @state() private declare _rows: StepHistoryItem[] | null;
  @state() private declare _error: string | null;
  @state() private declare _name: string;

  constructor() {
    super();
    this.pipelineId = '';
    this.step = '';
    this._rows = null;
    this._error = null;
    this._name = '';
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('step')) {
      this._rows = null;
      this.reload();
    }
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const [pipeline, rows] = await Promise.all([
        this._name ? null : p.get(),
        p.steps.byStep(this.step).history.get({ queryParameters: { limit: 200 } }),
      ]);
      if (pipeline) this._name = pipeline.name ?? '';
      this._rows = rows ?? [];
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const rows = this._rows;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._name || 'Pipeline'}</a><span>/</span>
        <a href=${stepPath(this.pipelineId, this.step)} @click=${navClick}>${this.step}</a>
      </div>
      <h2>Runs</h2>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${rows === null
        ? this._error
          ? nothing
          : html`<p class="muted">Loading…</p>`
        : rows.length === 0
          ? html`<p class="muted">This step has never run.</p>`
          : html`<div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Status</th>
                    <th>Started</th>
                    <th class="num">Duration</th>
                  </tr>
                </thead>
                <tbody>
                  ${rows.map((r) => this._row(r))}
                </tbody>
              </table>
            </div>`}
    `;
  }

  private _row(r: StepHistoryItem) {
    const run = `${runPath(this.pipelineId, r.runNumber)}?step=${encodeURIComponent(this.step)}&attempt=${r.attemptNumber}`;
    return html`
      <tr>
        <td>
          <a href=${run} @click=${navClick}>#${r.runNumber}</a>${(r.attemptNumber ?? 1) > 1
            ? html`<span class="muted">@${r.attemptNumber}</span>`
            : nothing}
        </td>
        <td>
          <a class="st t-${toneOf(r.status)}" href=${stepLogsPath(this.pipelineId, r.runNumber, this.step, r.attemptNumber)} @click=${navClick}
            ><span class="dot"></span>${statusLabel(r.status)}</a
          >
          ${r.reason ? html`<div class="reason">${r.reason}</div>` : nothing}
        </td>
        <td>${r.startedAt ? html`<relative-time .value=${r.startedAt}></relative-time>` : html`<span class="muted">—</span>`}</td>
        <td class="num">${r.startedAt ? formatDuration(r.startedAt, r.finishedAt) : ''}</td>
      </tr>
      ${r.stepVersionChanged
        ? html`<tr class="marker">
            <td colspan="4">Definition changed in version ${r.versionNumber}</td>
          </tr>`
        : nothing}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-runs-view': StepRunsView;
  }
}
