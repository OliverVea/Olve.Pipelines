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
      flex: 1;
    }

    .back {
      color: var(--color-text-muted);
      text-decoration: none;
      font-size: 0.9rem;
    }

    .back:hover {
      color: var(--color-primary);
    }

    .reload-btn {
      width: 2rem;
      height: 2rem;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      cursor: pointer;
      transition: background var(--transition), border-color var(--transition);
    }

    .reload-btn:hover:not(:disabled) {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    .reload-btn:disabled {
      opacity: 0.6;
      cursor: default;
    }

    .reload-btn svg {
      width: 1rem;
      height: 1rem;
    }

    .reload-btn.loading svg {
      animation: spin 0.8s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
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
  @state() private declare _titleHint: string;

  constructor() {
    super();
    this.pipelineId = '';
    this._pipeline = null;
    this._productionSteps = [];
    this._processingSteps = [];
    this._jobs = [];
    this._loading = true;
    this._error = null;
    const state = history.state as { pipelineName?: string } | null;
    this._titleHint = state?.pipelineName ?? '';
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
    if (this._error) return html`<p class="error">${this._error}</p>`;

    return html`
      <div class="header">
        <a class="back" href="/" @click=${this._back}>&larr; Back</a>
        <h2>${this._pipeline?.name ?? this._titleHint}</h2>
        <button
          class="reload-btn ${this._loading ? 'loading' : ''}"
          @click=${this._load}
          ?disabled=${this._loading}
          title="Refresh"
          aria-label="Refresh"
        >
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M21 12a9 9 0 1 1-3-6.7" />
            <polyline points="21 3 21 9 15 9" />
          </svg>
        </button>
      </div>
      ${this._pipeline
        ? html`<pipeline-flow
            .pipelineId=${this.pipelineId}
            .productionSteps=${this._productionSteps}
            .processingSteps=${this._processingSteps}
            .jobs=${this._jobs}
            @reload=${() => this._load()}
          ></pipeline-flow>`
        : this._loading
        ? html``
        : html`<p>Pipeline not found.</p>`}
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
