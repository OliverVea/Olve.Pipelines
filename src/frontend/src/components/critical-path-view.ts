import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type {
  CriticalPathLead,
  CriticalPathReport,
} from '@olve/olve-pipelines-client/src/models/index.js';
import { client, errorMessages } from '../api.js';
import {
  sharedStyles,
  statusCardStyles,
  toneOf,
  statusLabel,
  formatSeconds,
  median,
  navClick,
  runPath,
  stepRunsPath,
} from '../v2/ui.js';
import './pipeline-header.js';

const RANGES = [20, 50, 100];

function leadTitle(lead: CriticalPathLead | null | undefined): string {
  if (!lead) return 'Manual runs and runs from before this was recorded have no lead.';
  const part = (label: string, v: number | null | undefined) =>
    `${label} ${v == null ? '?' : formatSeconds(v)}`;
  return [
    `via ${lead.via}`,
    part('detect', lead.detect),
    part('check', lead.check),
    part('map', lead.map),
    part('start', lead.start),
  ].join(' · ');
}

/** Where a range of runs spent its time: each step's share of the critical path, and each run's path. */
@customElement('critical-path-view')
export class CriticalPathView extends LitElement {
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
      select.since {
        padding: 0.22rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-surface);
        color: var(--color-text);
        font: inherit;
        font-size: 0.8rem;
      }
      select.since.on {
        border-color: var(--color-primary);
        box-shadow: 0 0 0 1px var(--color-primary);
      }
      label.check {
        display: inline-flex;
        align-items: center;
        gap: 0.3rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      h4 {
        margin: 1.4rem 0 0.4rem;
        font-size: 0.9rem;
      }
      .share {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        min-width: 9rem;
      }
      .share .bar {
        flex: 1;
        height: 0.45rem;
        border-radius: 999px;
        background: var(--color-surface-hover);
        overflow: hidden;
      }
      .share .bar span {
        display: block;
        height: 100%;
        background: var(--color-primary);
      }
      .share .pct {
        width: 3rem;
        text-align: right;
        font-variant-numeric: tabular-nums;
      }
      .path {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.2rem 0.35rem;
        font-size: 0.8rem;
      }
      .path .seg-step {
        padding: 0.05rem 0.4rem;
        border-radius: var(--radius);
        background: var(--color-surface-hover);
        white-space: nowrap;
      }
      .path .seg-step.top {
        background: color-mix(in srgb, var(--color-primary) 18%, transparent);
      }
      .path .arrow {
        color: var(--color-text-muted);
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

  @state() declare private _report: CriticalPathReport | null;
  @state() declare private _error: string | null;
  /** Latest N runs, or 0 when ranging by version. */
  @state() declare private _range: number;
  @state() declare private _sinceVersion: number | null;
  @state() declare private _versions: number[];
  @state() declare private _all: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this._report = null;
    this._error = null;
    this._range = 20;
    this._sinceVersion = null;
    this._versions = [];
    this._all = false;
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) void this._loadVersions();
    if (
      changed.has('pipelineId') ||
      changed.has('_range') ||
      changed.has('_sinceVersion') ||
      changed.has('_all')
    )
      this.reload();
  }

  async reload() {
    try {
      const api = client.api.pipelines.byId(this.pipelineId).runs.criticalPath;
      let report = await api.get({
        queryParameters: {
          includeUnsuccessful: this._all,
          sinceVersion: this._sinceVersion ?? undefined,
        },
      });
      const to = report?.to ?? 0;
      if (this._sinceVersion === null && this._range !== 20 && to > 0)
        report = await api.get({
          queryParameters: {
            from: Math.max(1, to - this._range + 1),
            to,
            includeUnsuccessful: this._all,
          },
        });
      this._report = report ?? null;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  private async _loadVersions() {
    const list = await client.api.pipelines
      .byId(this.pipelineId)
      .versions.get()
      .catch(() => null);
    this._versions = (list?.versions ?? [])
      .map((v) => v.number ?? 0)
      .filter((n) => n > 0)
      .sort((a, b) => b - a);
  }

  private _pickVersion(e: Event) {
    const value = (e.target as HTMLSelectElement).value;
    this._sinceVersion = value ? Number(value) : null;
    if (this._sinceVersion !== null) this._range = 0;
    else if (this._range === 0) this._range = 20;
  }

  render() {
    const r = this._report;
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="runs"></pipeline-header>
      <div class="head">
        <h3>Critical path</h3>
        ${r?.from ? html`<span class="muted">runs #${r.from}–#${r.to}</span>` : nothing}
        <span class="spacer"></span>
        <label class="check"
          ><input
            type="checkbox"
            .checked=${this._all}
            @change=${(e: Event) => (this._all = (e.target as HTMLInputElement).checked)}
          />Include failed runs</label
        >
        <span class="seg" role="group" aria-label="Range">
          ${RANGES.map(
            (n) =>
              html`<button
                aria-pressed=${this._range === n}
                @click=${() => {
                  this._range = n;
                  this._sinceVersion = null;
                }}
              >
                Last ${n}
              </button>`,
          )}
        </span>
        <select
          class="since ${this._sinceVersion !== null ? 'on' : ''}"
          aria-label="Since version"
          .value=${this._sinceVersion === null ? '' : String(this._sinceVersion)}
          @change=${this._pickVersion}
        >
          <option value="">Since version…</option>
          ${this._versions.map(
            (v) =>
              html`<option value=${String(v)} ?selected=${this._sinceVersion === v}>
                Since v${v}
              </option>`,
          )}
        </select>
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${r === null
        ? this._error
          ? nothing
          : html`<p class="muted">Loading…</p>`
        : !r.runs?.length
          ? html`<p class="muted">No finished runs in this range.</p>`
          : html`${this._summary(r)} ${this._steps(r)} ${this._runs(r)}`}
    `;
  }

  private _summary(r: CriticalPathReport) {
    const runs = r.runs ?? [];
    const seconds = runs.map((x) => x.seconds ?? 0);
    const longest = runs.reduce((a, b) => ((b.seconds ?? 0) > (a.seconds ?? 0) ? b : a));
    const top = r.steps?.[0];
    const overheads = runs
      .filter((x) => x.activeSeconds != null)
      .map(
        (x) =>
          (x.seconds ?? 0) -
          (x.path ?? []).reduce((a, p) => a + (p.waitSeconds ?? 0), 0) -
          x.activeSeconds!,
      );
    const leads = runs.filter((x) => x.lead).map((x) => x.lead!.seconds ?? 0);
    return html`<div class="status">
      <div class="cell">
        <span class="label">Median</span>
        <span class="value">${formatSeconds(median(seconds))}</span>
        <span class="sub">${runs.length} runs</span>
      </div>
      ${overheads.length
        ? html`<div class="cell">
            <span class="label">Overhead</span>
            <span class="value">${formatSeconds(median(overheads))}</span>
            <span class="sub">median, of ${overheads.length} runs with timings</span>
          </div>`
        : nothing}
      ${leads.length
        ? html`<div class="cell">
            <span class="label">Lead</span>
            <span class="value">${formatSeconds(median(leads))}</span>
            <span class="sub">median push → first step, ${leads.length} runs</span>
          </div>`
        : nothing}
      <div class="cell">
        <span class="label">Average</span>
        <span class="value">${formatSeconds(r.averageSeconds)}</span>
        <span class="sub">per run</span>
      </div>
      <div class="cell">
        <span class="label">Longest</span>
        <a
          class="value"
          href=${runPath(this.pipelineId, longest.number)}
          @click=${navClick}
          >#${longest.number}</a
        >
        <span class="sub">${formatSeconds(longest.seconds)}</span>
      </div>
      ${top
        ? html`<div class="cell grow">
            <span class="label">Biggest share</span>
            <span class="value">${top.step}</span>
            <span class="sub"
              >${Math.round((top.share ?? 0) * 100)}% of the critical path, on it in
              ${top.runs}/${runs.length} runs</span
            >
          </div>`
        : nothing}
    </div>`;
  }

  private _steps(r: CriticalPathReport) {
    const n = r.runs?.length ?? 0;
    return html`<h4>Steps</h4>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Step</th>
              <th>Share</th>
              <th class="num">On path</th>
              <th class="num">Total</th>
              <th class="num">Avg when on</th>
              <th class="num">Wait</th>
              <th class="num">Avg overhead</th>
            </tr>
          </thead>
          <tbody>
            ${(r.steps ?? []).map(
              (s) =>
                html`<tr>
                  <td>
                    <a
                      href=${stepRunsPath(this.pipelineId, s.step ?? '')}
                      @click=${navClick}
                      >${s.step}</a
                    >
                  </td>
                  <td>
                    <div class="share">
                      <span class="bar"
                        ><span style="width:${Math.round((s.share ?? 0) * 100)}%"></span
                      ></span>
                      <span class="pct">${((s.share ?? 0) * 100).toFixed(1)}%</span>
                    </div>
                  </td>
                  <td class="num">${s.runs}/${n}</td>
                  <td class="num">${formatSeconds(s.seconds)}</td>
                  <td class="num">${formatSeconds(s.averageSeconds)}</td>
                  <td class="num">${formatSeconds(s.waitSeconds)}</td>
                  <td class="num">
                    ${s.overheadSeconds == null ? '—' : formatSeconds(s.overheadSeconds)}
                  </td>
                </tr>`,
            )}
          </tbody>
        </table>
      </div>`;
  }

  private _runs(r: CriticalPathReport) {
    const notes = [
      ...(r.runs ?? []).flatMap((x) => (x.notes ?? []).map((n) => `#${x.number}: ${n}`)),
      ...(r.skipped ?? []).map((s) => `#${s.number}: not included (${s.reason})`),
    ];
    return html`<h4>Runs</h4>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Run</th>
              <th>Status</th>
              <th class="num">Lead</th>
              <th class="num">Critical path</th>
              <th class="num">Idle</th>
              <th>Path</th>
            </tr>
          </thead>
          <tbody>
            ${[...(r.runs ?? [])].reverse().map((x) => {
              const top = Math.max(...(x.path ?? []).map((p) => p.seconds ?? 0));
              return html`<tr>
                <td>
                  <a href=${runPath(this.pipelineId, x.number)} @click=${navClick}
                    >#${x.number}</a
                  >
                </td>
                <td>
                  <span class="st t-${toneOf(x.status)}"
                    ><span class="dot"></span>${statusLabel(x.status)}</span
                  >
                </td>
                <td class="num" title=${leadTitle(x.lead)}>
                  ${x.lead ? formatSeconds(x.lead.seconds) : '—'}
                </td>
                <td class="num">${formatSeconds(x.seconds)}</td>
                <td class="num">
                  ${(x.idleSeconds ?? 0) >= 1 ? formatSeconds(x.idleSeconds) : '—'}
                </td>
                <td>
                  <div class="path">
                    ${(x.path ?? []).map(
                      (p, i) =>
                        html`${i ? html`<span class="arrow">→</span>` : nothing}<span
                            class="seg-step ${p.seconds === top ? 'top' : ''}"
                            title=${(p.waitSeconds ?? 0) >= 1
                              ? `waited ${formatSeconds(p.waitSeconds)} before starting`
                              : ''}
                            >${p.step}${(p.attempts ?? 1) > 1 ? `×${p.attempts}` : ''}
                            ${formatSeconds(p.seconds)}</span
                          >`,
                    )}
                  </div>
                </td>
              </tr>`;
            })}
          </tbody>
        </table>
      </div>
      ${notes.length
        ? html`<ul class="notes">
            ${notes.map((n) => html`<li>${n}</li>`)}
          </ul>`
        : nothing}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'critical-path-view': CriticalPathView;
  }
}
