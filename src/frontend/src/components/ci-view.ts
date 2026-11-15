import { LitElement, html, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  bindingsStatus,
  ciPullsList,
  ciQueueGet,
  ciRunsGet,
  pipelinesGet,
  type CiRun,
  type Pull,
  type Queue,
  type QueueEntry,
} from '@olve/api';
import { api, errorMessages } from '../api/client.js';
import {
  sharedStyles,
  formatDuration,
  navClick,
  shortSha,
  ciPath,
  ciQueuePath,
  ciClosedPath,
  ciRunPath,
} from '../styles/ui.js';
import { LiveController, forPipeline } from '../api/events.js';
import { navigate } from '../router.js';
import {
  previewCell,
  pullRun,
  pullStatus,
  queuePill,
  queueWhy,
  staleTitle,
} from '../model/ci.js';
import { ciStyles, ciStatus, stepStrip, commitUrl } from './ci-parts.js';
import './pipeline-header.js';
import './relative-time.js';

type Filter = 'open' | 'queue' | 'closed';

/** The CI tab: open or closed pull requests with their latest CI run, or the merge queue. */
@customElement('ci-view')
export class CiView extends LitElement {
  static styles = [sharedStyles, ciStyles];

  @property() declare pipelineId: string;
  @property() declare filter: Filter;

  @state() declare private _pulls: Pull[] | null;
  @state() declare private _queue: Queue | null;
  @state() declare private _queueRuns: Map<number, CiRun>;
  @state() declare private _repo: string | null;
  @state() declare private _head: string | null;
  @state() declare private _error: string | null;

  constructor() {
    super();
    this.pipelineId = '';
    this.filter = 'open';
    this._pulls = null;
    this._queue = null;
    this._queueRuns = new Map();
    this._repo = null;
    this._head = null;
    this._error = null;
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId, 'pull', 'ci-run', 'ci-attempt', 'ci-queue'),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') && this.pipelineId) {
      const path = { pipelineId: this.pipelineId };
      pipelinesGet({ client: api, path })
        .then((r) => (this._repo = r.data?.repo ?? null))
        .catch(() => {});
    }
    if (changed.has('pipelineId') || changed.has('filter')) {
      this._pulls = null;
      this._queue = null;
      this.reload();
    }
  }

  async reload() {
    const path = { pipelineId: this.pipelineId };
    const key = `${this.pipelineId}:${this.filter}`;
    try {
      const binding = bindingsStatus({ client: api, path })
        .then((r) => r.data?.head?.sha ?? r.data?.lastPushSha ?? null)
        .catch(() => null);
      if (this.filter === 'queue') {
        const { data: queue } = await ciQueueGet({ client: api, path });
        const runs = await this._loadRuns(queue ?? null);
        if (key !== `${this.pipelineId}:${this.filter}`) return;
        this._queue = queue ?? null;
        this._queueRuns = runs;
      } else {
        const { data } = await ciPullsList({
          client: api,
          path,
          query: { state: this.filter, limit: 100 },
        });
        if (key !== `${this.pipelineId}:${this.filter}`) return;
        this._pulls = data?.items ?? [];
      }
      this._head = await binding;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  /** The latest run of each queue entry: its steps, start and whether it failed. */
  private async _loadRuns(queue: Queue | null): Promise<Map<number, CiRun>> {
    const numbers = new Set(
      [...(queue?.entries ?? []), ...(queue?.finished ?? [])]
        .map((e) => e.runs[0])
        .filter((n): n is number => n !== undefined),
    );
    const runs = await Promise.all(
      [...numbers].map((number) =>
        ciRunsGet({ client: api, path: { pipelineId: this.pipelineId, number } })
          .then((r) => r.data ?? null)
          .catch(() => null),
      ),
    );
    return new Map(runs.filter((r): r is CiRun => !!r).map((r) => [r.number, r]));
  }

  render() {
    return html`<pipeline-header
        .pipelineId=${this.pipelineId}
        tab="ci"
      ></pipeline-header>
      <div class="toolbar">
        <div class="seg">
          ${(
            [
              ['open', 'Open', ciPath(this.pipelineId)],
              ['queue', 'Queue', ciQueuePath(this.pipelineId)],
              ['closed', 'Closed', ciClosedPath(this.pipelineId)],
            ] as [Filter, string, string][]
          ).map(
            ([f, label, href]) =>
              html`<button
                aria-pressed=${f === this.filter}
                @click=${() => f !== this.filter && navigate(href)}
              >
                ${label}
              </button>`,
          )}
        </div>
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${this.filter === 'queue' ? this._renderQueue() : this._renderPulls()}`;
  }

  private _renderPulls() {
    const pulls = this._pulls;
    if (pulls === null)
      return this._error ? nothing : html`<p class="muted">Loading…</p>`;
    if (!pulls.length)
      return html`<p class="muted">
        ${this.filter === 'open' ? 'No open pull requests.' : 'No closed pull requests.'}
      </p>`;
    return html`<section class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Pull request</th>
            <th>Status</th>
            <th class="hide-phone">Steps</th>
            <th class="hide-phone">Commit</th>
            <th>Run</th>
            <th class="hide-phone">Started</th>
            <th class="num hide-phone">Duration</th>
            <th class="hide-phone">Preview</th>
            <th class="hide-phone"></th>
          </tr>
        </thead>
        <tbody>
          ${pulls.map((p) => this._pullRow(p))}
        </tbody>
      </table>
    </section>`;
  }

  private _pullRow(p: Pull) {
    const run = pullRun(p);
    const href = run ? ciRunPath(this.pipelineId, run.number) : null;
    const commit = commitUrl(this._repo, p.headSha);
    const pill =
      p.state !== 'open'
        ? html`<span class="pill">${p.state}</span>`
        : queuePill(p)
          ? html`<span class="pill">${queuePill(p)}</span>`
          : run && p.stale
            ? html`<span class="pill" title=${staleTitle(run, this._head)}>stale</span>`
            : nothing;
    return html`<tr>
      <td>
        <div class="change">
          ${href
            ? html`<a class="title" href=${href} @click=${navClick}>${p.title}</a>`
            : html`<span class="title">${p.title}</span>`}
          <span class="branch">#${p.number} · ${p.branch}</span>
        </div>
      </td>
      <td>${ciStatus(pullStatus(p))}</td>
      <td class="hide-phone">${run ? stepStrip(run.steps) : nothing}</td>
      <td class="hide-phone mono">
        ${run
          ? commit
            ? html`<a href=${commit} target="_blank">${shortSha(p.headSha)}</a>`
            : shortSha(p.headSha)
          : nothing}
      </td>
      <td class="num">
        ${run && href
          ? html`<a href=${href} @click=${navClick}><b>#${run.number}</b></a>`
          : '—'}
      </td>
      <td class="hide-phone">
        ${run ? html`<relative-time .value=${run.createdAt}></relative-time>` : nothing}
      </td>
      <td class="num hide-phone">
        ${run ? formatDuration(run.createdAt, run.finishedAt) : nothing}
      </td>
      <td class="hide-phone preview">${this._preview(p)}</td>
      <td class="hide-phone">${pill}</td>
    </tr>`;
  }

  private _preview(p: Pull) {
    const cell = previewCell(p);
    if (!cell) return html`<span class="muted">—</span>`;
    return html`<span class="st t-${cell.tone}"
        ><span class="dot"></span>${cell.label ?? nothing}</span
      >${cell.links.map(
        (l, i) =>
          html`${i ? html`<span class="sep">·</span>` : nothing}<a
              href=${l.href}
              title=${l.href}
              target="_blank"
              >${l.text}</a
            >`,
      )}`;
  }

  private _renderQueue() {
    const q = this._queue;
    if (q === null) return this._error ? nothing : html`<p class="muted">Loading…</p>`;
    const entries = [...q.entries, ...q.finished];
    if (!entries.length) return html`<p class="muted">The merge queue is empty.</p>`;
    return html`<section class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Pull request</th>
            <th>Status</th>
            <th class="hide-phone">Steps</th>
            <th>Run</th>
            <th class="hide-phone">Started</th>
            <th class="hide-phone"></th>
          </tr>
        </thead>
        <tbody>
          ${entries.map((e) => this._queueRow(e))}
        </tbody>
      </table>
    </section>`;
  }

  private _queueRow(e: QueueEntry) {
    const number = e.runs[0];
    const run = number === undefined ? undefined : this._queueRuns.get(number);
    const why = queueWhy(e, (n) => this._queueRuns.get(n)?.status);
    return html`<tr>
      <td>
        <div class="change">
          ${run
            ? html`<a
                class="title"
                href=${ciRunPath(this.pipelineId, run.number)}
                @click=${navClick}
                >${e.title}</a
              >`
            : html`<span class="title">${e.title}</span>`}
          <span class="branch">#${e.pull} · ${e.branch}</span>
        </div>
      </td>
      <td>${ciStatus(e.state)}</td>
      <td class="hide-phone">
        ${run && e.state !== 'queued' ? stepStrip(run.steps) : nothing}
      </td>
      <td class="num">
        ${run && e.state !== 'queued'
          ? html`<a href=${ciRunPath(this.pipelineId, run.number)} @click=${navClick}
              ><b>#${run.number}</b></a
            >`
          : '—'}
      </td>
      <td class="hide-phone">
        ${run && e.state !== 'queued'
          ? html`<relative-time .value=${run.createdAt}></relative-time>`
          : nothing}
      </td>
      <td class="why hide-phone">
        ${why && 'split' in why
          ? html`after a split of
              <a href=${ciRunPath(this.pipelineId, why.split)} @click=${navClick}
                >#${why.split}</a
              >`
          : (why?.text ?? nothing)}
      </td>
    </tr>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ci-view': CiView;
  }
}
