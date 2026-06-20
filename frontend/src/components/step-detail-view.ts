import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { AnsiUp } from 'ansi_up';
import { client, extractErrorMessage } from '../api.js';
import { getAccessToken } from '../auth.js';
import { navigate } from '../router.js';
import './relative-time.js';
import './refresh-control.js';
import type {
  ProcessingStep,
  StepConfiguration,
  JobProcessingJob,
  JobProductionJob,
} from '@olve/olve-pipelines-client/src/models/index.js';
import './promotion-gate.js';

type Job = JobProcessingJob | JobProductionJob;

const ansi = new AnsiUp();
ansi.use_classes = false;

interface StatusInfo {
  label: string;
  cssClass: string;
}

function statusOf(job: Job | undefined): StatusInfo {
  const type = (job?.status as { type?: string } | undefined)?.type ?? '';
  switch (type) {
    case 'scheduled':
      return { label: 'Scheduled', cssClass: 'scheduled' };
    case 'in-progress':
      return { label: 'Running', cssClass: 'running' };
    case 'done':
      return { label: 'Done', cssClass: 'done' };
    case 'failed':
      return { label: 'Failed', cssClass: 'failed' };
    case 'cancelled':
      return { label: 'Cancelled', cssClass: 'cancelled' };
    case 'obsolete':
      return { label: 'Obsolete', cssClass: 'obsolete' };
    default:
      return { label: 'Idle', cssClass: 'idle' };
  }
}

function hasLogs(job: Job | undefined): boolean {
  const type = (job?.status as { type?: string } | undefined)?.type ?? '';
  return (
    type === 'done' || type === 'failed' || type === 'cancelled' || type === 'obsolete'
  );
}

@customElement('step-detail-view')
export class StepDetailView extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .step-head {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 0.5rem;
    }

    .step-head h2 {
      margin: 0;
      font-size: 1.5rem;
      flex: 1;
      font-family: var(--font-mono);
    }

    .back {
      color: var(--color-text-muted);
      text-decoration: none;
      font-size: 0.9rem;
    }

    .back:hover {
      color: var(--color-primary);
    }

    .status-badge {
      font-size: 0.7rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      padding: 0.15rem 0.4rem;
      border-radius: 3px;
      white-space: nowrap;
    }

    .status-badge.idle {
      color: var(--color-text-muted);
      background: transparent;
      border: 1px solid var(--color-border);
    }
    .status-badge.scheduled {
      color: var(--color-warning);
      background: color-mix(in srgb, var(--color-warning) 15%, transparent);
    }
    .status-badge.running {
      color: var(--color-primary);
      background: color-mix(in srgb, var(--color-primary) 15%, transparent);
    }
    .status-badge.done {
      color: var(--color-success);
      background: color-mix(in srgb, var(--color-success) 15%, transparent);
    }
    .status-badge.failed {
      color: var(--color-danger);
      background: color-mix(in srgb, var(--color-danger) 15%, transparent);
    }
    .status-badge.cancelled {
      color: var(--color-text-muted);
      background: color-mix(in srgb, var(--color-text-muted) 15%, transparent);
    }
    .status-badge.obsolete {
      color: var(--color-text-muted);
      background: color-mix(in srgb, var(--color-text-muted) 10%, transparent);
      text-decoration: line-through;
    }

    .step-sub {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 0.6rem;
      font-size: 0.85rem;
      color: var(--color-text-muted);
      margin-bottom: 1.5rem;
    }

    .step-kind {
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-size: 0.72rem;
      font-weight: 600;
    }

    .sep {
      color: var(--color-border);
    }

    .step-sub a {
      color: var(--color-text-muted);
    }
    .step-sub a:hover {
      color: var(--color-primary);
    }

    .step-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 1rem;
      margin-bottom: 1rem;
    }

    @media (max-width: 720px) {
      .step-grid {
        grid-template-columns: 1fr;
      }
    }

    .panel {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      padding: 1rem 1.25rem;
    }

    .panel h3 {
      margin: 0 0 0.75rem;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-text-muted);
    }

    .config {
      margin: 0;
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 0.5rem 1rem;
    }

    .config dt {
      font-size: 0.78rem;
      font-weight: 600;
      color: var(--color-text-muted);
    }

    .config dd {
      margin: 0;
      min-width: 0;
    }

    .config code {
      font-size: 0.78rem;
      word-break: break-all;
    }

    .env-list {
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
    }

    .muted {
      color: var(--color-text-muted);
    }

    .runs {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
    }

    .run-row {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.35rem 0.5rem;
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      text-decoration: none;
      color: var(--color-text);
      transition:
        border-color var(--transition),
        background var(--transition);
    }

    .run-row:hover {
      text-decoration: none;
      border-color: var(--color-primary);
      background: var(--color-surface-hover);
    }

    .run-when {
      font-size: 0.78rem;
      color: var(--color-text-muted);
    }

    .run-row .status-badge {
      margin-left: auto;
    }

    .log-panel {
      padding-bottom: 1.25rem;
    }

    .log-panel-head {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 0.75rem;
    }

    .log-panel-head h3 {
      margin: 0;
      flex: 1;
    }

    .btn {
      padding: 0.35rem 0.75rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font-size: 0.8rem;
      text-decoration: none;
      transition:
        background var(--transition),
        border-color var(--transition);
    }

    .btn:hover {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    .btn.disabled {
      opacity: 0.45;
      pointer-events: none;
    }

    pre.log {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      padding: 0.75rem 1rem;
      overflow: auto;
      max-height: 16rem;
      font-size: 0.8rem;
      line-height: 1.4;
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
      font-family: var(--font-mono);
    }

    .log-skeleton {
      color: var(--color-text-muted);
      font-style: italic;
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }
  `;

  @property() declare pipelineId: string;
  @property() declare stepId: string;

  @state() declare private _step: ProcessingStep | null;
  @state() declare private _config: StepConfiguration | null;
  @state() declare private _stepJobs: JobProcessingJob[];
  @state() declare private _blocked: boolean;
  @state() declare private _loading: boolean;
  @state() declare private _error: string | null;
  @state() declare private _logTail: string | null;
  @state() declare private _logUnavailable: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.stepId = '';
    this._step = null;
    this._config = null;
    this._stepJobs = [];
    this._blocked = false;
    this._loading = true;
    this._error = null;
    this._logTail = null;
    this._logUnavailable = false;
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
      this._logTail = null;
      this._logUnavailable = false;
    }
    try {
      const step = client.api.processingSteps.byStepId(this.stepId);
      const [stepRecord, config, jobsPage, promotion] = await Promise.all([
        step.get(),
        step.configuration.get().catch(() => null),
        client.api.jobs.get({
          queryParameters: { pipelineId: this.pipelineId, pageSize: '50' },
        }),
        step.promotion.get().catch(() => null),
      ]);

      this._step = stepRecord ?? null;
      this._config = config ?? null;
      this._blocked = promotion?.blocked === true;
      this._stepJobs = ((jobsPage?.items ?? []) as Job[])
        .filter(
          (j): j is JobProcessingJob =>
            'processingStepId' in j && j.processingStepId === this.stepId,
        )
        .sort((a, b) => (b.createdAt?.getTime() ?? 0) - (a.createdAt?.getTime() ?? 0));

      const latest = this._stepJobs[0];
      if (latest?.id && hasLogs(latest)) {
        void this._loadLogTail(latest.id);
      }
    } catch (e) {
      if (!silent) this._error = extractErrorMessage(e);
    } finally {
      if (!silent) this._loading = false;
    }
  }

  private async _loadLogTail(jobId: string) {
    try {
      const token = await getAccessToken();
      const res = await fetch(`/api/jobs/${jobId}/logs`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (res.status === 400) {
        this._logUnavailable = true;
        return;
      }
      if (!res.ok) return;
      const ct = res.headers.get('content-type') ?? '';
      const body = ct.includes('application/json') ? await res.json() : await res.text();
      const text = typeof body === 'string' ? body : JSON.stringify(body);
      // Show the tail (last ~40 lines) — this is a preview, not the full log.
      this._logTail = text.split('\n').slice(-40).join('\n');
    } catch {
      // A log-tail failure is non-fatal; the full-logs link still works.
    }
  }

  render() {
    if (this._error) return html`<p class="error">${this._error}</p>`;
    if (!this._step && this._loading) return html``;
    if (!this._step) return html`<p>Step not found.</p>`;

    const latest = this._stepJobs[0];
    const status = statusOf(latest);

    return html`
      <div class="step-head">
        <a class="back" href="/pipeline/${this.pipelineId}" @click=${this._backToPipeline}
          >&larr; Back to pipeline</a
        >
        <h2>${this._step.name}</h2>
        <span class="status-badge ${status.cssClass}">${status.label}</span>
        <refresh-control
          .loading=${this._loading}
          @refresh=${this._onRefresh}
        ></refresh-control>
      </div>

      <div class="step-sub">
        <span class="step-kind">processing step</span>
        <span class="sep">·</span>
        <a href="/pipeline/${this.pipelineId}" @click=${this._backToPipeline}>pipeline</a>
        ${latest?.createdAt
          ? html`<span class="sep">·</span>
              <span
                >last ran <relative-time .value=${latest.createdAt}></relative-time
              ></span>`
          : nothing}
        <span class="sep">·</span>
        <promotion-gate
          .stepId=${this.stepId}
          .stepName=${this._step.name ?? ''}
          .blocked=${this._blocked}
          .hasBundle=${latest !== undefined}
          @changed=${() => this._load()}
          @gate-error=${(e: CustomEvent<{ message: string }>) =>
            (this._error = e.detail.message)}
        ></promotion-gate>
      </div>

      <div class="step-grid">
        <section class="panel">
          <h3>Configuration</h3>
          ${this._renderConfig()}
        </section>

        <section class="panel">
          <h3>Run history</h3>
          ${this._renderRuns()}
        </section>
      </div>

      <section class="panel log-panel">
        <div class="log-panel-head">
          <h3>Recent log</h3>
          ${latest?.id && hasLogs(latest)
            ? html`<a
                class="btn"
                href="/pipeline/${this.pipelineId}/job/${latest.id}/logs"
                @click=${this._openLogs}
                >View full logs</a
              >`
            : html`<span class="btn disabled">View full logs</span>`}
        </div>
        ${this._renderLogTail(latest)}
      </section>
    `;
  }

  private _renderConfig() {
    const cfg = this._config;
    if (!cfg) return html`<p class="muted">No configuration.</p>`;
    const env = (cfg.environmentVariables ?? {}) as Record<string, string>;
    const envKeys = Object.keys(env);
    return html`
      <dl class="config">
        <dt>Image</dt>
        <dd><code>${cfg.image || '—'}</code></dd>
        <dt>Script</dt>
        <dd><code>${cfg.script || '—'}</code></dd>
        <dt>Env</dt>
        <dd>
          ${envKeys.length
            ? html`<div class="env-list">
                ${envKeys.map((k) => html`<code>${k}=${env[k]}</code>`)}
              </div>`
            : html`<span class="muted">none</span>`}
        </dd>
      </dl>
    `;
  }

  private _renderRuns() {
    if (this._stepJobs.length === 0) {
      return html`<p class="muted">Never run</p>`;
    }
    return html`
      <ul class="runs">
        ${this._stepJobs.map((job) => {
          const s = statusOf(job);
          const linkable = job.id && hasLogs(job);
          const inner = html`
            <span class="run-when"
              ><relative-time .value=${job.createdAt}></relative-time
            ></span>
            <span class="status-badge ${s.cssClass}">${s.label}</span>
          `;
          return html`<li>
            ${linkable
              ? html`<a
                  class="run-row"
                  href="/pipeline/${this.pipelineId}/job/${job.id}/logs"
                  @click=${this._openLogs}
                  >${inner}</a
                >`
              : html`<div class="run-row">${inner}</div>`}
          </li>`;
        })}
      </ul>
    `;
  }

  private _renderLogTail(latest: JobProcessingJob | undefined) {
    if (!latest || !hasLogs(latest)) {
      return html`<pre class="log"><span class="muted"
          >No output yet — this step has not produced a finished run.</span
        ></pre>`;
    }
    if (this._logUnavailable) {
      return html`<pre class="log"><span class="muted"
          >Logs are not available for this run yet.</span
        ></pre>`;
    }
    if (this._logTail === null) {
      return html`<pre class="log"><span class="log-skeleton"
          >loading recent output…</span
        ></pre>`;
    }
    return html`<pre class="log">${unsafeHTML(ansi.ansi_to_html(this._logTail))}</pre>`;
  }

  private _backToPipeline(e: Event) {
    e.preventDefault();
    navigate(`/pipeline/${this.pipelineId}`);
  }

  private _openLogs(e: Event) {
    e.preventDefault();
    const href = (e.currentTarget as HTMLAnchorElement).getAttribute('href');
    if (href) navigate(href);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-detail-view': StepDetailView;
  }
}
