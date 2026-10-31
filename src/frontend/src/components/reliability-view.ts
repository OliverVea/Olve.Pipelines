import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { api, errorMessages } from '../api/client.js';
import {
  stepsReliability,
  type ReliabilityReport,
  type StepReliability,
} from '@olve/api';
import {
  sharedStyles,
  statusCardStyles,
  navClick,
  runPath,
  stepRunsPath,
} from '../styles/ui.js';
import './pipeline-header.js';
import './relative-time.js';

const RANGES = [50, 100, 500];

const percent = (rate: number) => `${(rate * 100).toFixed(1)}%`;

/** The latest failure rate against the step's usual one, when it has the history for that. */
function trend(s: StepReliability) {
  if (s.recentFailureRate == null || s.usualFailureRate == null)
    return html`<span class="muted">—</span>`;
  return html`${percent(s.recentFailureRate)}
    <span class="muted">vs ${percent(s.usualFailureRate)}</span>`;
}

/** How reliable a pipeline's steps were over its latest runs. Failure rates stay neutral: a test step fails on real problems. */
@customElement('reliability-view')
export class ReliabilityView extends LitElement {
  static styles = [
    sharedStyles,
    statusCardStyles,
    css`
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.5rem 0.8rem;
        margin-top: 1.5rem;
      }
      .head h3 {
        margin: 0;
        font-size: 1.05rem;
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
      h4 {
        margin: 1.4rem 0 0.4rem;
        font-size: 0.9rem;
      }
      .above {
        margin-left: 0.4rem;
        padding: 0.05rem 0.4rem;
        border-radius: var(--radius);
        font-size: 0.75rem;
        white-space: nowrap;
        color: var(--color-warning);
        background: color-mix(in srgb, var(--color-warning) 14%, transparent);
      }
      .notes {
        margin: 0.8rem 0 0;
        padding-left: 1.2rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _report: ReliabilityReport | null;
  @state() declare private _error: string | null;
  @state() declare private _range: number;

  constructor() {
    super();
    this.pipelineId = '';
    this._report = null;
    this._error = null;
    this._range = 100;
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('_range')) this.reload();
  }

  async reload() {
    try {
      const report = await stepsReliability({
        client: api,
        path: { pipelineId: this.pipelineId },
        query: { runs: this._range },
      }).then((r) => r.data);
      this._report = report ?? null;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const r = this._report;
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="runs"></pipeline-header>
      <div class="head">
        <h3>Reliability</h3>
        ${r ? this._window(r) : nothing}
        <span class="spacer"></span>
        <span class="seg" role="group" aria-label="Range">
          ${RANGES.map(
            (n) =>
              html`<button
                aria-pressed=${this._range === n}
                @click=${() => (this._range = n)}
              >
                Last ${n}
              </button>`,
          )}
        </span>
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${
        r === null
          ? this._error
            ? nothing
            : html`<p class="muted">Loading…</p>`
          : !r.steps.length
            ? html`<p class="muted">No attempts have finished in this range.</p>`
            : html`${this._summary(r)} ${this._steps(r)} ${this._notes(r)}`
      }
    `;
  }

  private _window({ window: w }: ReliabilityReport) {
    if (!w.runs) return nothing;
    return html`<span class="muted"
      >runs #${w.fromRun}–#${w.toRun}, <relative-time .value=${w.from}></relative-time> to
      <relative-time .value=${w.to}></relative-time>${
        w.limitedByRetention ? ', older runs were removed by retention' : ''
      }</span
    >`;
  }

  private _summary({ runs, steps }: ReliabilityReport) {
    const above = steps.filter((s) => s.aboveUsual);
    const flaky = steps.filter((s) => s.flakyFailures > 0);
    return html`<div class="status">
      <div class="cell">
        <span class="label">Failed for no real reason</span>
        <span class="value">${runs.settled ? percent(runs.rate) : '—'}</span>
        <span class="sub">${runs.failedForNoReason} of ${runs.settled} settled runs</span>
      </div>
      ${
        runs.absorbed
          ? html`<div class="cell">
              <span class="label">Absorbed</span>
              <span class="value">${runs.absorbed}</span>
              <span class="sub">more runs covered by automatic retries</span>
            </div>`
          : nothing
      }
      <div class="cell">
        <span class="label">Flaky steps</span>
        <span class="value">${flaky.length}</span>
        <span class="sub">of ${steps.length} steps</span>
      </div>
      <div class="cell">
        <span class="label">Above usual</span>
        <span class="value">${above.length}</span>
        <span class="sub">failing more than they usually do</span>
      </div>
      ${
        runs.runNumbers.length
          ? html`<div class="cell grow">
              <span class="label">Latest such runs</span>
              <span class="value"
                >${runs.runNumbers
                  .slice(0, 5)
                  .map(
                    (n, i) =>
                      html`${i ? ', ' : ''}<a
                          href=${runPath(this.pipelineId, n)}
                          @click=${navClick}
                          >#${n}</a
                        >`,
                  )}</span
              >
            </div>`
          : nothing
      }
    </div>`;
  }

  private _steps({ steps }: ReliabilityReport) {
    return html`<h4>Steps</h4>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Step</th>
              <th class="num">Attempts</th>
              <th class="num">Failure rate</th>
              <th class="num" title="Latest 10 attempts vs the ones before them">
                Latest vs usual
              </th>
              <th class="num" title="The step ran and reported a failure">Own</th>
              <th class="num" title="Pod, image pull, launch, storage">Infra</th>
              <th class="num">Timeout</th>
              <th class="num" title="Same code, config and input also succeeded">
                Flaky
              </th>
              <th class="num" title="Same code, config and input failed again">
                Reproduced
              </th>
              <th class="num" title="Never ran again on the same input">Unverified</th>
            </tr>
          </thead>
          <tbody>
            ${steps.map(
              (s) =>
                html`<tr>
                  <td>
                    <a href=${stepRunsPath(this.pipelineId, s.step)} @click=${navClick}
                      >${s.step}</a
                    >${
                      s.aboveUsual
                        ? html`<span class="above">above usual</span>`
                        : nothing
                    }
                  </td>
                  <td class="num">${s.attempts}</td>
                  <td class="num">${percent(s.failureRate)}</td>
                  <td class="num">${trend(s)}</td>
                  <td class="num">${s.ownFailures}</td>
                  <td class="num">${s.infrastructureFailures}</td>
                  <td class="num">${s.timeouts}</td>
                  <td class="num">${s.flakyFailures}</td>
                  <td class="num">${s.reproducedFailures}</td>
                  <td class="num">${s.unverifiedFailures}</td>
                </tr>`,
            )}
          </tbody>
        </table>
      </div>`;
  }

  private _notes({ notes }: ReliabilityReport) {
    return html`<ul class="notes">
      <li>
        A failure rate alone is no defect: a step that tests something should fail on real
        problems.
      </li>
      <li>
        A run failed for no real reason when a flaky, infrastructure or timeout failure
        stopped it. Flaky failures only show once the same input runs again, so this is a
        lower bound.
      </li>
      ${notes.map((n) => html`<li>${n}</li>`)}
    </ul>`;
  }
}
