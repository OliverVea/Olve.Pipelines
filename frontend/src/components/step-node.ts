import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type {
  JobProcessingJob,
  JobProductionJob,
} from '@olve/olve-pipelines-client/src/models/index.js';

type Job = JobProcessingJob | JobProductionJob;

interface StatusInfo {
  label: string;
  cssClass: string;
}

function getStatus(job: Job | undefined): StatusInfo {
  if (!job || !job.status) return { label: 'Idle', cssClass: 'idle' };

  const status = job.status as Record<string, unknown>;
  const type = (status.type as string) ?? '';

  switch (type) {
    case 'Scheduled':
      return { label: 'Scheduled', cssClass: 'scheduled' };
    case 'InProgress':
      return { label: 'Running', cssClass: 'running' };
    case 'Done':
      return { label: 'Done', cssClass: 'done' };
    case 'Failed':
      return { label: 'Failed', cssClass: 'failed' };
    case 'Cancelled':
      return { label: 'Cancelled', cssClass: 'cancelled' };
    case 'Obsolete':
      return { label: 'Obsolete', cssClass: 'obsolete' };
    default:
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
      font-size: 0.75rem;
      color: var(--color-text-muted);
      margin-top: 0.25rem;
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

  @property() stepId = '';
  @property() stepName = '';
  @property() stepType: 'production' | 'processing' = 'production';
  @property() pipelineId = '';
  @property({ attribute: false }) latestJob: Job | undefined;

  @state() private _expanded = false;

  render() {
    const status = getStatus(this.latestJob);

    return html`
      <div
        class="node"
        @click=${() => (this._expanded = !this._expanded)}
      >
        <div class="node-header">
          <span class="step-name">${this.stepName}</span>
          <span class="status-badge ${status.cssClass}">${status.label}</span>
        </div>
        ${this.latestJob?.createdAt
          ? html`<div class="node-detail">
              ${this.latestJob.createdAt.toLocaleString()}
            </div>`
          : nothing}
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-node': StepNode;
  }
}
