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
  PipelineBindingStatus,
} from '@olve/olve-pipelines-client/src/models/index.js';
import './pipeline-flow.js';
import './refresh-control.js';

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

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }

    .binding-bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem;
      margin-bottom: 1.25rem;
    }

    .pill {
      display: inline-flex;
      align-items: center;
      gap: 0.35rem;
      padding: 0.2rem 0.6rem;
      border-radius: 999px;
      font-size: 0.8rem;
      line-height: 1.4;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text-muted);
      max-width: 100%;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .pill .branch {
      opacity: 0.6;
    }

    .pill.ok {
      color: var(--color-success, #2e7d32);
      border-color: var(--color-success, #2e7d32);
    }

    .pill.error,
    .pill.warn {
      color: var(--color-danger);
      border-color: var(--color-danger);
    }
  `;

  @property() declare pipelineId: string;

  @state() private declare _pipeline: Pipeline | null;
  @state() private declare _productionSteps: ProductionStep[];
  @state() private declare _processingSteps: ProcessingStep[];
  @state() private declare _jobs: (JobProcessingJob | JobProductionJob)[];
  @state() private declare _bindingStatus: PipelineBindingStatus | null;
  @state() private declare _promotions: Record<string, boolean>;
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
    this._bindingStatus = null;
    this._promotions = {};
    this._loading = true;
    this._error = null;
    const state = history.state as { pipelineName?: string } | null;
    this._titleHint = state?.pipelineName ?? '';
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
    }
    try {
      const p = client.api.pipelines.byId(this.pipelineId);
      const [pipeline, production, processing, jobsPage, bindingStatus, promotions] =
        await Promise.all([
          p.get(),
          p.production.get(),
          p.processing.get(),
          client.api.jobs.get({
            queryParameters: {
              pipelineId: this.pipelineId,
              pageSize: '50',
            },
          }),
          // Unbound (draft) pipelines have no binding — tolerate the 404.
          p.binding.status.get().catch(() => null),
          p.processing.promotions.get().catch(() => null),
        ]);
      this._pipeline = pipeline ?? null;
      this._bindingStatus = bindingStatus ?? null;
      this._promotions = Object.fromEntries(
        (promotions ?? [])
          .filter((s) => s.processingStepId)
          .map((s) => [s.processingStepId as string, s.blocked === true]),
      );
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
      if (!silent) this._error = e instanceof Error ? e.message : String(e);
    } finally {
      if (!silent) this._loading = false;
    }
  }

  render() {
    if (this._error) return html`<p class="error">${this._error}</p>`;

    return html`
      <div class="header">
        <a class="back" href="/" @click=${this._back}>&larr; Back</a>
        <h2>${this._pipeline?.name ?? this._titleHint}</h2>
        <refresh-control
          .loading=${this._loading}
          @refresh=${this._onRefresh}
        ></refresh-control>
      </div>
      ${this._renderBindingBadge()}
      ${this._pipeline
        ? html`<pipeline-flow
            .pipelineId=${this.pipelineId}
            .productionSteps=${this._productionSteps}
            .processingSteps=${this._processingSteps}
            .jobs=${this._jobs}
            .promotions=${this._promotions}
            @reload=${() => this._load()}
            @changed=${() => this._load()}
            @gate-error=${(e: CustomEvent<{ message: string }>) =>
              (this._error = e.detail.message)}
          ></pipeline-flow>`
        : this._loading
        ? html``
        : html`<p>Pipeline not found.</p>`}
    `;
  }

  private _renderBindingBadge() {
    const b = this._bindingStatus;
    if (!b) return html``; // unbound (draft) pipeline — no GitOps badge

    const result = b.result ?? 0; // 0 NeverRun, 1 Success, 2 Error
    const problems = b.problems ?? [];
    const unset = (b.secrets ?? [])
      .filter((s) => s.isSet === false)
      .map((s) => s.name);

    return html`
      <div class="binding-bar">
        <span class="pill repo" title="GitOps source — configuration is git-only">
          ${b.repo}${b.branch
            ? html`<span class="branch">@${b.branch}</span>`
            : ''}
        </span>
        ${result === 2
          ? html`<span class="pill error" title=${problems.join('\n')}
              >⚠ reconcile error${problems.length
                ? html`: ${problems[0]}`
                : ''}</span
            >`
          : result === 1
          ? html`<span class="pill ok">✓ config synced</span>`
          : html`<span class="pill">pending first sync</span>`}
        ${unset.length
          ? html`<span
              class="pill warn"
              title="Set these via the secrets API or kubectl"
              >⚠ unset secrets: ${unset.join(', ')}</span
            >`
          : ''}
      </div>
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
