import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { stepsHistory, type StepHistoryItem } from '@olve/api';
import { api, errorMessages } from '../api.js';
import {
  sharedStyles,
  statusCardStyles,
  toneOf,
  statusLabel,
  formatDuration,
  formatSeconds,
  median,
  navClick,
  runPath,
  stepPath,
  stepLogsPath,
} from '../v2/ui.js';
import { LiveController } from '../v2/events.js';
import './phase-bar.js';
import './pipeline-header.js';
import './relative-time.js';

type Filter = 'all' | 'succeeded' | 'failed' | 'other';
const FILTERS: [Filter, string][] = [
  ['all', 'All'],
  ['succeeded', 'Succeeded'],
  ['failed', 'Failed'],
  ['other', 'Other'],
];

const seconds = (
  from: Date | string | null | undefined,
  to: Date | string | null | undefined,
) => (from && to ? (new Date(to).getTime() - new Date(from).getTime()) / 1000 : null);

function quantile(sorted: number[], q: number): number | null {
  if (!sorted.length) return null;
  return sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
}

const kind = (status: string | null | undefined): Filter => {
  const s = (status ?? '').toLowerCase();
  return s === 'succeeded' ? 'succeeded' : s === 'failed' ? 'failed' : 'other';
};

/** Every attempt of one step across runs, newest first: a summary, a status filter and a table. */
@customElement('step-runs-view')
export class StepRunsView extends LitElement {
  static styles = [
    sharedStyles,
    statusCardStyles,
    css`
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: 0.4rem 0.8rem;
        margin-top: 1.5rem;
      }
      .head h3 {
        margin: 0;
        font-size: 1.05rem;
      }
      .head h3 code {
        font-family: var(--font-mono);
      }
      .head .spacer {
        flex: 1;
      }
      .seg {
        display: inline-flex;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        overflow: hidden;
        background: var(--color-surface);
      }
      .seg button {
        border: 0;
        background: transparent;
        color: var(--color-text);
        padding: 0.25rem 0.7rem;
        font-size: 0.8rem;
      }
      .seg button + button {
        border-left: 1px solid var(--color-border);
      }
      .seg button:hover {
        background: var(--color-surface-hover);
      }
      .seg button[aria-pressed='true'] {
        background: var(--color-primary);
        color: var(--color-surface);
      }
      .seg .n {
        opacity: 0.7;
        margin-left: 0.3rem;
      }
      .table-wrap {
        margin-top: 0.75rem;
      }
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
      .stamp {
        font-family: var(--font-mono);
        font-size: 0.75rem;
        color: var(--color-text-muted);
      }
      .ok {
        color: var(--color-success);
      }
      .bad {
        color: var(--color-danger);
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare step: string;

  @state() declare private _rows: StepHistoryItem[] | null;
  @state() declare private _error: string | null;
  @state() declare private _filter: Filter;

  constructor() {
    super();
    this.pipelineId = '';
    this.step = '';
    this._rows = null;
    this._error = null;
    this._filter = 'all';
    new LiveController(
      this,
      () => this.reload(),
      (n) =>
        n.pipelineId === this.pipelineId &&
        (n.type === 'run' ||
          n.type === 'blocked' ||
          (n.type === 'attempt' && n.data.step === this.step)),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('step')) {
      this._rows = null;
      this.reload();
    }
  }

  async reload() {
    try {
      const { data: rows } = await stepsHistory({
        client: api,
        throwOnError: true,
        path: { pipelineId: this.pipelineId, step: this.step },
        query: { limit: 200 },
      });
      this._rows = rows;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const rows = this._rows;
    const shown = rows?.filter(
      (r) => this._filter === 'all' || kind(r.status) === this._filter,
    );
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="runs"></pipeline-header>
      <div class="head">
        <h3>
          Runs of
          <a href=${stepPath(this.pipelineId, this.step)} @click=${navClick}
            ><code>${this.step}</code></a
          >
        </h3>
        <span class="spacer"></span>
        ${rows?.length
          ? html`<span class="seg" role="group" aria-label="Status">
              ${FILTERS.map(
                ([f, label]) =>
                  html`<button
                    aria-pressed=${this._filter === f}
                    @click=${() => (this._filter = f)}
                  >
                    ${label}<span class="n"
                      >${f === 'all'
                        ? rows.length
                        : rows.filter((r) => kind(r.status) === f).length}</span
                    >
                  </button>`,
              )}
            </span>`
          : nothing}
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${rows === null
        ? this._error
          ? nothing
          : html`<p class="muted">Loading…</p>`
        : rows.length === 0
          ? html`<p class="muted">This step has never run.</p>`
          : html`${this._summary(rows)}
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Run</th>
                      <th>Status</th>
                      <th>Version</th>
                      <th>Started</th>
                      <th class="num">Wait</th>
                      <th class="num">Duration</th>
                      <th>Time</th>
                    </tr>
                  </thead>
                  <tbody>
                    ${shown!.length
                      ? shown!.map((r) => this._row(r))
                      : html`<tr>
                          <td colspan="7" class="muted">No attempts match.</td>
                        </tr>`}
                  </tbody>
                </table>
              </div>`}
    `;
  }

  private _summary(rows: StepHistoryItem[]) {
    const finished = rows.filter(
      (r) => kind(r.status) === 'succeeded' || kind(r.status) === 'failed',
    );
    const ok = finished.filter((r) => kind(r.status) === 'succeeded');
    const rate = finished.length ? Math.round((100 * ok.length) / finished.length) : null;
    const durations = ok
      .map((r) => seconds(r.startedAt, r.finishedAt))
      .filter((s): s is number => s !== null)
      .sort((a, b) => a - b);
    const lastFail = rows.find((r) => kind(r.status) === 'failed');
    const overheads = rows
      .filter((r) => r.phases?.active != null && r.finishedAt)
      .map((r) => (seconds(r.createdAt, r.finishedAt) ?? 0) - (r.phases!.active ?? 0));
    return html`<div class="status">
      <div class="cell">
        <span class="label">Attempts</span>
        <span class="value">${rows.length}</span>
        <span class="sub">${new Set(rows.map((r) => r.runNumber)).size} runs</span>
      </div>
      <div class="cell">
        <span class="label">Success rate</span>
        <span
          class="value ${rate === null ? '' : rate >= 90 ? 'ok' : rate < 70 ? 'bad' : ''}"
          >${rate === null ? '—' : `${rate}%`}</span
        >
        <span class="sub">${ok.length} of ${finished.length} finished</span>
      </div>
      <div class="cell">
        <span class="label">Median</span>
        <span class="value">${formatSeconds(quantile(durations, 0.5))}</span>
        <span class="sub">successful attempts</span>
      </div>
      <div class="cell">
        <span class="label">p90</span>
        <span class="value">${formatSeconds(quantile(durations, 0.9))}</span>
        <span class="sub">max ${formatSeconds(durations.at(-1) ?? null)}</span>
      </div>
      ${overheads.length
        ? html`<div class="cell">
            <span class="label">Overhead</span>
            <span class="value">${formatSeconds(median(overheads))}</span>
            <span class="sub">median, outside the step container</span>
          </div>`
        : nothing}
      <div class="cell grow">
        <span class="label">Last failure</span>
        ${lastFail
          ? html`<a
                class="value"
                href=${stepLogsPath(
                  this.pipelineId,
                  lastFail.runNumber,
                  this.step,
                  lastFail.attemptNumber,
                )}
                @click=${navClick}
                >#${lastFail.runNumber}${(lastFail.attemptNumber ?? 1) > 1
                  ? `@${lastFail.attemptNumber}`
                  : ''}</a
              >
              <span class="sub"
                ><relative-time
                  .value=${lastFail.finishedAt ?? lastFail.createdAt}
                ></relative-time
              ></span>`
          : html`<span class="value">none</span
              ><span class="sub">in these attempts</span>`}
      </div>
    </div>`;
  }

  private get _maxSeconds() {
    return Math.max(
      0,
      ...(this._rows ?? []).map((r) =>
        r.phases?.active == null
          ? 0
          : (r.phases.queued ?? 0) +
            (r.phases.podStart ?? 0) +
            (r.phases.fetch ?? 0) +
            (r.phases.image ?? 0) +
            r.phases.active +
            (r.phases.collect ?? 0),
      ),
    );
  }

  private _row(r: StepHistoryItem) {
    const run = `${runPath(this.pipelineId, r.runNumber)}?step=${encodeURIComponent(this.step)}&attempt=${r.attemptNumber}`;
    return html`
      <tr>
        <td>
          <a href=${run} @click=${navClick}>#${r.runNumber}</a>${(r.attemptNumber ?? 1) >
          1
            ? html`<span class="muted">@${r.attemptNumber}</span>`
            : nothing}
        </td>
        <td>
          <a
            class="st t-${toneOf(r.status)}"
            href=${stepLogsPath(this.pipelineId, r.runNumber, this.step, r.attemptNumber)}
            @click=${navClick}
            ><span class="dot"></span>${statusLabel(r.status)}</a
          >
          ${r.reason
            ? html`<div class="reason" title=${r.reason}>${r.reason}</div>`
            : nothing}
        </td>
        <td>
          v${r.versionNumber}
          <span class="stamp">${r.versionStamp ?? ''}</span>
        </td>
        <td>
          ${r.startedAt
            ? html`<relative-time .value=${r.startedAt}></relative-time>`
            : html`<span class="muted">—</span>`}
        </td>
        <td class="num">
          ${r.startedAt ? formatSeconds(seconds(r.createdAt, r.startedAt)) : ''}
        </td>
        <td class="num">
          ${r.startedAt ? formatDuration(r.startedAt, r.finishedAt) : ''}
        </td>
        <td><phase-bar .phases=${r.phases} .max=${this._maxSeconds}></phase-bar></td>
      </tr>
      ${r.stepVersionChanged
        ? html`<tr class="marker">
            <td colspan="7">
              Definition changed in version ${r.versionNumber} (step v${r.stepVersion})
            </td>
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
