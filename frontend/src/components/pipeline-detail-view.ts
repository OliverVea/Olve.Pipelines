import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client } from '../api.js';
import { navigate } from '../router.js';
import type {
  Pipeline,
  ProductionStep,
  ProcessingStep,
  JobProcessingJob,
  JobProductionJob,
} from '@olve/olve-pipelines-client/src/models/index.js';
import './pipeline-flow.js';

@customElement('pipeline-detail-view')
export class PipelineDetailView extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .header {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 1.5rem;
    }

    .header h2 {
      margin: 0;
      font-size: 1.5rem;
    }

    .back {
      color: var(--color-text-muted);
      text-decoration: none;
      font-size: 0.9rem;
    }

    .back:hover {
      color: var(--color-primary);
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }
  `;

  @property() declare pipelineId: string;

  @state() private declare _pipeline: Pipeline | null;
  @state() private declare _productionSteps: ProductionStep[];
  @state() private declare _processingSteps: ProcessingStep[];
  @state() private declare _jobs: (JobProcessingJob | JobProductionJob)[];
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;

  constructor() {
    super();
    this.pipelineId = '';
    this._pipeline = null;
    this._productionSteps = [];
    this._processingSteps = [];
    this._jobs = [];
    this._loading = true;
    this._error = null;
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
  }

  private async _load() {
    this._loading = true;
    this._error = null;
    try {
      const p = client.api.pipelines.byId(this.pipelineId);
      const [pipeline, production, processing, jobsPage] = await Promise.all([
        p.get(),
        p.production.get(),
        p.processing.get(),
        client.api.jobs.get({
          queryParameters: {
            pipelineId: this.pipelineId,
            pageSize: '50',
          },
        }),
      ]);
      this._pipeline = pipeline ?? null;
      this._productionSteps = production ?? [];
      this._processingSteps = (processing ?? []).sort(
        (a: ProcessingStep, b: ProcessingStep) => {
          const ao = (a.order as unknown as number) ?? 0;
          const bo = (b.order as unknown as number) ?? 0;
          return ao - bo;
        },
      );
      this._jobs = (jobsPage?.items ?? []) as (
        | JobProcessingJob
        | JobProductionJob
      )[];
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    if (this._loading) return html`<p>Loading...</p>`;
    if (this._error) return html`<p class="error">${this._error}</p>`;
    if (!this._pipeline) return html`<p>Pipeline not found.</p>`;

    return html`
      <div class="header">
        <a class="back" href="/" @click=${this._back}>&larr; Back</a>
        <h2>${this._pipeline.name}</h2>
      </div>
      <pipeline-flow
        .pipelineId=${this.pipelineId}
        .productionSteps=${this._productionSteps}
        .processingSteps=${this._processingSteps}
        .jobs=${this._jobs}
        @reload=${() => this._load()}
      ></pipeline-flow>
    `;
  }

  private _back(e: Event) {
    e.preventDefault();
    navigate('/');
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-detail-view': PipelineDetailView;
  }
}
