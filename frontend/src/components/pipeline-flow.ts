import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type {
  ProductionStep,
  ProcessingStep,
  JobProcessingJob,
  JobProductionJob,
} from '@olve/olve-pipelines-client/src/models/index.js';
import './step-node.js';

type Job = JobProcessingJob | JobProductionJob;

@customElement('pipeline-flow')
export class PipelineFlow extends LitElement {
  static styles = css`
    :host {
      display: block;
      overflow-x: auto;
    }

    .flow {
      display: flex;
      align-items: stretch;
      gap: 0;
      min-width: min-content;
      padding: 1rem 0;
    }

    .column {
      display: flex;
      flex-direction: column;
      justify-content: center;
      gap: 0.5rem;
      min-width: 180px;
    }

    .column-label {
      font-size: 0.7rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-text-muted);
      text-align: center;
      margin-bottom: 0.25rem;
    }

    .connector {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 48px;
      flex-shrink: 0;
    }

    .connector svg {
      width: 48px;
      height: 24px;
    }

    .connector line {
      stroke: var(--color-border);
      stroke-width: 2;
    }

    .connector polygon {
      fill: var(--color-border);
    }

    .bundle-node {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 48px;
      flex-shrink: 0;
    }

    .bundle-dot {
      width: 12px;
      height: 12px;
      border-radius: 50%;
      background: var(--color-border);
      border: 2px solid var(--color-text-muted);
    }

    .empty-column {
      display: flex;
      align-items: center;
      justify-content: center;
      min-width: 180px;
      color: var(--color-text-muted);
      font-style: italic;
      font-size: 0.85rem;
    }
  `;

  @property({ attribute: false }) declare pipelineId: string;
  @property({ attribute: false }) declare productionSteps: ProductionStep[];
  @property({ attribute: false }) declare processingSteps: ProcessingStep[];
  @property({ attribute: false }) declare jobs: Job[];

  constructor() {
    super();
    this.pipelineId = '';
    this.productionSteps = [];
    this.processingSteps = [];
    this.jobs = [];
  }

  render() {
    const hasProduction = this.productionSteps.length > 0;
    const hasProcessing = this.processingSteps.length > 0;

    return html`
      <div class="flow">
        <!-- Production column (parallel, stacked vertically) -->
        <div class="column">
          <div class="column-label">Production</div>
          ${hasProduction
            ? this.productionSteps.map(
                (step) => html`
                  <step-node
                    .stepId=${step.id ?? ''}
                    .stepName=${step.name ?? ''}
                    .stepType=${'production'}
                    .pipelineId=${this.pipelineId}
                    .latestJob=${this._latestProductionJob(step.id ?? '')}
                  ></step-node>
                `,
              )
            : html`<div class="empty-column">No steps</div>`}
        </div>

        ${hasProduction || hasProcessing ? this._renderConnector() : nothing}

        <!-- Artifact Bundle node -->
        <div class="bundle-node" title="Artifact Bundle">
          <div class="bundle-dot"></div>
        </div>

        <!-- Processing columns (sequential, one per column) -->
        ${hasProcessing
          ? this.processingSteps.map(
              (step, i) => html`
                ${i > 0 ? this._renderConnector() : this._renderConnector()}
                <div class="column">
                  ${i === 0
                    ? html`<div class="column-label">Processing</div>`
                    : html`<div class="column-label">&nbsp;</div>`}
                  <step-node
                    .stepId=${step.id ?? ''}
                    .stepName=${step.name ?? ''}
                    .stepType=${'processing'}
                    .pipelineId=${this.pipelineId}
                    .latestJob=${this._latestProcessingJob(step.id ?? '')}
                  ></step-node>
                </div>
              `,
            )
          : html`
              ${this._renderConnector()}
              <div class="empty-column">No processing steps</div>
            `}
      </div>
    `;
  }

  private _renderConnector() {
    return html`
      <div class="connector">
        <svg viewBox="0 0 48 24">
          <line x1="0" y1="12" x2="38" y2="12" />
          <polygon points="38,6 48,12 38,18" />
        </svg>
      </div>
    `;
  }

  private _latestProductionJob(
    stepId: string,
  ): JobProductionJob | undefined {
    const stepJobs = this.jobs.filter(
      (j): j is JobProductionJob =>
        'productionStepId' in j && j.productionStepId === stepId,
    );
    return this._mostRecent(stepJobs);
  }

  private _latestProcessingJob(
    stepId: string,
  ): JobProcessingJob | undefined {
    const stepJobs = this.jobs.filter(
      (j): j is JobProcessingJob =>
        'processingStepId' in j && j.processingStepId === stepId,
    );
    return this._mostRecent(stepJobs);
  }

  private _mostRecent<T extends Job>(jobs: T[]): T | undefined {
    if (jobs.length === 0) return undefined;
    return jobs.reduce((latest, j) => {
      const lDate = latest.createdAt?.getTime() ?? 0;
      const jDate = j.createdAt?.getTime() ?? 0;
      return jDate > lDate ? j : latest;
    });
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-flow': PipelineFlow;
  }
}
