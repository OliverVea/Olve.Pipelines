import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type {
  JobProcessingJob,
  JobProductionJob,
} from '@olve/olve-pipelines-client/src/models/index.js';
import { navigate } from '../router.js';
import './relative-time.js';

type Job = JobProcessingJob | JobProductionJob;

interface StatusInfo {
  label: string;
  cssClass: string;
}

/** Human-readable duration, e.g. "2m 13s", "1h 4m", "820ms". */
function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const totalSec = Math.round(ms / 1000);
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

/**
 * The step's processing time for its latest run: total elapsed for a finished
 * run, or time-so-far for a running one. Null when there's no usable timing
 * (never started, or a status without timestamps).
 */
function processingTime(job: Job | undefined): string | null {
  const status = job?.status as
    | {
        type?: string;
        startedAt?: Date | null;
        completedAt?: Date | null;
        failedAt?: Date | null;
        cancelledAt?: Date | null;
      }
    | undefined;
  if (!status?.startedAt) return null;
  const start = status.startedAt.getTime();
  const end =
    status.completedAt?.getTime() ??
    status.failedAt?.getTime() ??
    status.cancelledAt?.getTime() ??
    (status.type === 'in-progress' ? Date.now() : null);
  if (end === null || end < start) return null;
  return formatDuration(end - start);
}

function getStatus(job: Job | undefined): StatusInfo {
  if (!job || !job.status) return { label: 'Idle', cssClass: 'idle' };

  const type = (job.status as { type?: string }).type ?? '';

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
      console.warn('step-node: unrecognized job status', { job });
      return { label: type || 'Unknown', cssClass: 'idle' };
  }
}

@customElement('step-node')
export class StepNode extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .node {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      padding: 0.75rem 1rem;
      transition: border-color var(--transition);
      cursor: default;
    }

    .node:hover {
      border-color: var(--color-primary);
    }

    .node-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
    }

    .step-name {
      font-weight: 500;
      font-size: 0.9rem;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      color: var(--color-text);
      text-decoration: none;
    }

    a.step-name:hover {
      color: var(--color-primary);
      text-decoration: underline;
    }

    .status-badge {
      font-size: 0.7rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      padding: 0.15rem 0.4rem;
      border-radius: 3px;
      white-space: nowrap;
      text-decoration: none;
    }

    a.status-badge:hover {
      filter: brightness(1.2);
      outline: 1px solid currentColor;
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
      animation: pulse 1.5s ease-in-out infinite;
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

    .node-detail {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 0.3rem;
      font-size: 0.75rem;
      color: var(--color-text-muted);
      margin-top: 0.25rem;
    }

    .detail-sep {
      opacity: 0.5;
    }

    @keyframes pulse {
      0%,
      100% {
        opacity: 1;
      }
      50% {
        opacity: 0.6;
      }
    }
  `;

  @property() declare stepId: string;
  @property() declare stepName: string;
  @property() declare stepType: 'production' | 'processing';
  @property() declare pipelineId: string;
  @property({ attribute: false }) declare latestJob: Job | undefined;

  constructor() {
    super();
    this.stepId = '';
    this.stepName = '';
    this.stepType = 'production';
    this.pipelineId = '';
  }

  render() {
    const status = getStatus(this.latestJob);
    const jobId = this.latestJob?.id;
    const statusType = (this.latestJob?.status as { type?: string } | undefined)?.type;
    const hasLogs =
      statusType === 'done' ||
      statusType === 'failed' ||
      statusType === 'cancelled' ||
      statusType === 'obsolete';
    const logsHref =
      jobId && hasLogs ? `/pipeline/${this.pipelineId}/job/${jobId}/logs` : undefined;

    const badge = logsHref
      ? html`<a
          class="status-badge ${status.cssClass}"
          href=${logsHref}
          title="View logs"
          @click=${this._openLogs}
          >${status.label}</a
        >`
      : html`<span class="status-badge ${status.cssClass}">${status.label}</span>`;

    const stepHref =
      this.stepType === 'processing' && this.stepId
        ? `/pipeline/${this.pipelineId}/step/${this.stepId}`
        : undefined;
    const name = stepHref
      ? html`<a
          class="step-name"
          href=${stepHref}
          title="Step detail"
          @click=${this._openStep}
          >${this.stepName}</a
        >`
      : html`<span class="step-name">${this.stepName}</span>`;

    return html`
      <div class="node">
        <div class="node-header">${name} ${badge}</div>
        ${this._renderDetail()}
      </div>
    `;
  }

  private _renderDetail() {
    const job = this.latestJob;
    if (!job?.createdAt) return nothing;

    const duration = processingTime(job);
    const statusType = (job.status as { type?: string } | undefined)?.type;
    const durationText = duration
      ? statusType === 'in-progress'
        ? `running ${duration}`
        : `ran in ${duration}`
      : null;

    return html`<div class="node-detail">
      <relative-time .value=${job.createdAt}></relative-time>
      ${durationText
        ? html`<span class="detail-sep">·</span><span>${durationText}</span>`
        : nothing}
    </div>`;
  }

  private _openStep(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    const href = (e.currentTarget as HTMLAnchorElement).getAttribute('href');
    if (href) navigate(href);
  }

  private _openLogs(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    const anchor = e.currentTarget as HTMLAnchorElement;
    const href = anchor.getAttribute('href');
    if (href) navigate(href);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-node': StepNode;
  }
}
