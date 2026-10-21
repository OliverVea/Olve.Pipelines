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
} from '../v2/ui.js';
import './relative-time.js';

/**
 * History of one step identity (its name) across runs and pipeline versions,
 * newest first. A marker row shows where the step's definition changed.
 */
@customElement('step-history-view')
export class StepHistoryView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .head {
        display: flex;
        align-items: baseline;
        gap: 0.75rem;
        margin-bottom: 0.25rem;
      }
      .sub {
        margin: 0 0 1rem;
        font-size: 0.85rem;
        color: var(--color-text-muted);
      }
      .stats {
        display: flex;
        gap: 1.5rem;
        flex-wrap: wrap;
        font-size: 0.82rem;
        color: var(--color-text-muted);
        margin-bottom: 1rem;
      }
      .stats b {
        color: var(--color-text);
        font-weight: 600;
      }
      tr.marker td {
        padding: 0.25rem 0.5rem;
        font-size: 0.75rem;
        color: var(--color-when);
        border-bottom: 1px dashed var(--color-when);
        background: color-mix(in srgb, var(--color-when) 6%, transparent);
      }
      .reason {
        color: var(--color-text-muted);
        font-size: 0.78rem;
        max-width: 28rem;
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
  @state() private declare _pipelineName: string;

  constructor() {
    super();
    this.pipelineId = '';
    this.step = '';
    this._rows = null;
    this._error = null;
    this._pipelineName = '';
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('step')) this._load();
  }

  private async _load() {
    this._rows = null;
    this._error = null;
    const p = client.api.pipelines.byId(this.pipelineId);
    p.get()
      .then((x) => (this._pipelineName = x?.name ?? ''))
      .catch(() => {});
    try {
      this._rows = (await p.steps.byStep(this.step).history.get({ queryParameters: { limit: 200 } })) ?? [];
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const rows = this._rows ?? [];
    const settled = rows.filter((r) => r.status === 'succeeded' || r.status === 'failed');
    const ok = settled.filter((r) => r.status === 'succeeded').length;
    const sinceChange = rows.findIndex((r) => r.stepVersionChanged);
    const durations = rows
      .filter((r) => r.status === 'succeeded' && r.startedAt && r.finishedAt)
      .map((r) => new Date(r.finishedAt!).getTime() - new Date(r.startedAt!).getTime());
    const median = durations.length ? [...durations].sort((a, b) => a - b)[Math.floor(durations.length / 2)] : null;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._pipelineName || 'Pipeline'}</a>
      </div>
      <div class="head"><h2>${this.step}</h2><span class="muted">step history</span></div>
      <p class="sub">Every attempt of this step across runs and pipeline versions, newest first.</p>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${this._rows === null && !this._error ? html`<p class="muted">Loading…</p>` : nothing}
      ${this._rows && rows.length === 0 ? html`<p class="muted">This step has never run.</p>` : nothing}
      ${rows.length
        ? html`<div class="stats">
              <span>Attempts <b>${rows.length}</b></span>
              <span>Succeeded <b>${ok}</b> of <b>${settled.length}</b> settled</span>
              ${median !== null ? html`<span>Median duration <b>${formatDuration(new Date(0), new Date(median))}</b></span>` : nothing}
              ${sinceChange >= 0
                ? html`<span>Since last definition change <b>${sinceChange + 1}</b> attempts</span>`
                : nothing}
            </div>
            <div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Attempt</th>
                    <th>Status</th>
                    <th>Step version</th>
                    <th>Pipeline</th>
                    <th>Started</th>
                    <th class="num">Duration</th>
                    <th>Run status</th>
                  </tr>
                </thead>
                <tbody>
                  ${rows.map((r, i) => this._row(r, rows[i + 1]))}
                </tbody>
              </table>
            </div>`
        : nothing}
    `;
  }

  private _row(r: StepHistoryItem, older: StepHistoryItem | undefined) {
    const href = `${runPath(this.pipelineId, r.runNumber)}?step=${encodeURIComponent(this.step)}&attempt=${r.attemptNumber}`;
    return html`
      ${r.stepVersionChanged
        ? html`<tr class="marker">
            <td colspan="8">
              Definition changed in v${r.versionNumber}: ${this.step}.${older?.stepVersion ?? '?'} →
              ${this.step}.${r.stepVersion ?? '?'}
            </td>
          </tr>`
        : nothing}
      <tr>
        <td><a href=${href} @click=${navClick}>#${r.runNumber}</a></td>
        <td class="num">@${r.attemptNumber}</td>
        <td>
          <span class="st t-${toneOf(r.status)}"><span class="dot"></span>${statusLabel(r.status)}</span>
          ${r.reason ? html`<div class="reason" title=${r.reason}>${r.reason}</div>` : nothing}
        </td>
        <td class="mono">${r.stepVersion !== null && r.stepVersion !== undefined ? `${this.step}.${r.stepVersion}` : '—'}</td>
        <td class="mono">v${r.versionNumber} <span class="muted">${r.versionStamp}</span></td>
        <td>${r.startedAt ? html`<relative-time .value=${r.startedAt}></relative-time>` : html`<span class="muted">—</span>`}</td>
        <td class="num">${r.startedAt ? formatDuration(r.startedAt, r.finishedAt) : ''}</td>
        <td><span class="st t-${toneOf(r.runStatus)}"><span class="dot"></span>${statusLabel(r.runStatus)}</span></td>
      </tr>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-history-view': StepHistoryView;
  }
}
