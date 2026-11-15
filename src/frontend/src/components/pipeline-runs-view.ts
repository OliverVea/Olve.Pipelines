import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { api, errorMessages } from '../api/client.js';
import { runsChanges, runsList, type RunChanges, type RunSummary } from '@olve/api';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  runPath,
} from '../styles/ui.js';
import { LiveController, forPipeline } from '../api/events.js';
import './relative-time.js';
import './pipeline-header.js';
import { hasChanges } from '../model/changes.js';
import './run-changes.js';

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
      td.changes {
        max-width: 28rem;
      }
      .head-commit {
        display: inline-block;
        max-width: 22rem;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        vertical-align: bottom;
      }
      .toggle {
        display: inline-flex;
        align-items: center;
        gap: 0.25rem;
        padding: 0.1rem 0.2rem;
        border: 0;
        background: transparent;
        color: var(--color-text-muted);
        font-size: 0.78rem;
      }
      .toggle::after {
        content: '▸';
      }
      .toggle[aria-expanded='true']::after {
        content: '▾';
      }
      .toggle:hover {
        color: var(--color-text);
      }
      tr.open > td,
      tr.sub > td {
        background: var(--color-surface-hover);
      }
      tr.sub > td {
        padding: 0.2rem 0.5rem 0.7rem 1.3rem;
      }
      tr.sub run-changes {
        padding-left: 0.8rem;
        border-left: 2px solid var(--color-border);
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _runs: RunSummary[] | null;
  @state() declare private _total: number;
  @state() declare private _error: string | null;
  @state() declare private _changes: Map<number, RunChanges>;
  @state() declare private _open: Set<number>;

  constructor() {
    super();
    this.pipelineId = '';
    this._runs = null;
    this._total = 0;
    this._error = null;
    this._changes = new Map();
    this._open = new Set();
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId, 'run', 'attempt'),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) {
      this._changes = new Map();
      this._open = new Set();
      this.reload();
    }
  }

  async reload() {
    try {
      const { data: runs } = await runsList({
        client: api,
        path: { pipelineId: this.pipelineId },
        query: { limit: 50 },
      });
      this._runs = runs?.items ?? [];
      this._total = runs?.total ?? this._runs.length;
      this._error = null;
      void this._loadChanges(this._runs);
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  /** The runs and statuses the changes were loaded for: a live event that moves neither loads nothing. */
  private _changesKey = '';

  private async _loadChanges(runs: RunSummary[]) {
    const key = `${this.pipelineId}:${runs.map((r) => `${r.number}${r.status}`).join()}`;
    if (!runs.length || key === this._changesKey) return;
    this._changesKey = key;
    const to = runs[0].number;
    const from = Math.max(runs[runs.length - 1].number, to - 49);
    const pipelineId = this.pipelineId;
    const { data } = await runsChanges({
      client: api,
      path: { pipelineId },
      query: { from, to },
    }).catch(() => ({ data: undefined }));
    if (!data || pipelineId !== this.pipelineId) return;
    this._changes = new Map(data.items.map((c) => [c.number, c]));
  }

  private _toggle(n: number) {
    const open = new Set(this._open);
    if (!open.delete(n)) open.add(n);
    this._open = open;
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
                    <th>Changes</th>
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
    const c = this._changes.get(r.number);
    const shown = hasChanges(c, r.versionNumber, r.configSnapshot);
    const open = shown && this._open.has(r.number);
    return html`<tr class=${open ? 'open' : ''}>
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
        <td class="changes">${shown ? this._changesCell(r, c, open) : nothing}</td>
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
                <a
                  href=${runPath(this.pipelineId, r.supersededByNumber)}
                  @click=${navClick}
                  >#${r.supersededByNumber}</a
                >`
            : nothing}
        </td>
      </tr>
      ${open
        ? html`<tr class="sub">
            <td colspan="8">
              <run-changes
                heading="h4"
                .pipelineId=${this.pipelineId}
                .changes=${c}
                .versionNumber=${r.versionNumber}
                .configSnapshot=${r.configSnapshot ?? null}
              ></run-changes>
            </td>
          </tr>`
        : nothing}`;
  }

  /** The newest commit and how many more, or the version move when there are no commits. */
  private _changesCell(r: RunSummary, c: RunChanges, open: boolean) {
    const head = c.commits?.[0];
    const total = c.totalCommits ?? c.commits?.length ?? 0;
    const label = head
      ? total > 1
        ? `+${total - 1}`
        : ''
      : c.sinceVersionNumber !== r.versionNumber
        ? `v${c.sinceVersionNumber} → v${r.versionNumber}`
        : '';
    return html`${head
        ? html`<span class="head-commit" title=${head.title}>${head.title}</span>`
        : nothing}
      <button
        class="toggle"
        aria-expanded=${open}
        aria-label="Changes since #${c.sinceNumber}"
        @click=${() => this._toggle(r.number)}
      >
        ${label}
      </button>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-runs-view': PipelineRunsView;
  }
}
