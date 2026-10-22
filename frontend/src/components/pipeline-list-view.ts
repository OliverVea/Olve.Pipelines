import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, state, query } from 'lit/decorators.js';
import { client, extractErrorMessage, untypedToJson } from '../api.js';
import { navigate } from '../router.js';
import { formatRelative } from './relative-time.js';
import './confirm-delete-dialog.js';
import type { ConfirmDeleteDialog } from './confirm-delete-dialog.js';
import { sharedStyles, toneOf, statusLabel, navClick, runPath, stepPath } from '../v2/ui.js';
import { toActivation } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import { formatWhen } from '../v2/manifest.js';
import type {
  PipelineSummary,
  PipelineStageSummary,
  PipelineStepSummary,
  RunStageCondition,
} from '@olve/olve-pipelines-client/src/models/index.js';

const GITHUB_MARK = svg`<svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" /></svg>`;

@customElement('pipeline-list-view')
export class PipelineListView extends LitElement {
  static styles = [sharedStyles, css`
    :host {
      display: block;
    }

    .header {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 1rem;
    }

    .header h2 {
      margin: 0;
      font-size: 1.5rem;
      flex: 1;
    }

    .docs-link {
      display: inline-flex;
      align-items: center;
      gap: 0.35rem;
      padding: 0.35rem 0.7rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font-size: 0.85rem;
      text-decoration: none;
      transition: background var(--transition), border-color var(--transition);
    }

    .docs-link:hover {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
      text-decoration: none;
    }

    .docs-link svg {
      width: 1rem;
      height: 1rem;
    }

    .card-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 1rem;
    }

    @media (max-width: 720px) {
      .card-grid {
        grid-template-columns: 1fr;
      }
    }

    .pipeline-card {
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
      padding: 1rem;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      transition: border-color var(--transition);
    }

    .pipeline-card:hover {
      border-color: var(--color-primary);
    }

    .pc-top {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 0.5rem;
    }

    .pc-title {
      display: block;
      font-weight: 600;
      font-size: 1rem;
      color: var(--color-text);
      text-decoration: none;
    }

    .pc-title:hover {
      color: var(--color-primary);
    }

    .pc-repo {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      margin-top: 0.25rem;
      font-size: 0.78rem;
      color: var(--color-text-muted);
      text-decoration: none;
    }

    .pc-repo:hover {
      color: var(--color-primary);
    }

    .pc-repo svg {
      width: 0.85rem;
      height: 0.85rem;
    }

    .btn-delete {
      background: none;
      border: 1px solid var(--color-border);
      color: var(--color-text-muted);
      padding: 0.25rem 0.5rem;
      border-radius: var(--radius);
      font-size: 0.8rem;
      flex-shrink: 0;
      transition: all var(--transition);
    }

    .btn-delete:hover {
      border-color: var(--color-danger);
      color: var(--color-danger);
    }

    .cubes {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: 6px;
    }
    .col {
      display: flex;
      flex-direction: column;
      gap: 3px;
    }
    .when {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 2px;
    }
    .when-cols {
      display: flex;
      gap: 6px;
      padding: 0 3px 3px;
      border: 1px solid var(--color-border);
      border-top: none;
      border-radius: 0 0 4px 4px;
    }
    .when-label {
      font-size: 0.65rem;
      line-height: 1.1;
      color: var(--color-text-muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      max-width: 9rem;
    }
    .cube {
      display: block;
      width: 14px;
      height: 14px;
      border-radius: 3px;
      background: var(--tone);
      border: 1.5px solid var(--tone);
      transition: transform var(--transition);
    }
    .cube:hover,
    .cube:focus-visible {
      transform: scale(1.25);
      text-decoration: none;
    }
    .cube.never {
      --tone: var(--color-pending);
      background: transparent;
      border-style: dashed;
    }
    .cube.scheduled,
    .cube.skipped {
      background: color-mix(in srgb, var(--tone) 22%, transparent);
    }
    .cube.t-off {
      background: color-mix(in srgb, var(--tone) 55%, transparent);
      border-color: transparent;
    }
    .cube.blocked {
      outline: 2px solid var(--color-warning);
      outline-offset: 1px;
    }
    .cube.in-progress {
      animation: pulse 1.6s ease-in-out infinite;
    }
    @media (prefers-reduced-motion: reduce) {
      .cube.in-progress {
        animation: none;
      }
    }

    .pc-foot {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 0.3rem 0.6rem;
      margin-top: auto;
      font-size: 0.78rem;
      color: var(--color-text-muted);
    }
    .pc-foot .st {
      font-size: 0.78rem;
      color: var(--color-text);
    }
    .tag {
      font-size: 0.72rem;
      padding: 0 0.45rem;
      border-radius: 999px;
      border: 1px solid var(--color-border);
    }
    .tag.warn {
      color: var(--color-warning);
      border-color: color-mix(in srgb, var(--color-warning) 50%, transparent);
    }
    .tag.run {
      color: var(--color-primary);
      border-color: color-mix(in srgb, var(--color-primary) 50%, transparent);
    }

    .pc-empty {
      font-size: 0.8rem;
      color: var(--color-text-muted);
      font-style: italic;
    }

    /* ---- Add-pipeline card ---- */
    .add-card {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 0.4rem;
      min-height: 7rem;
      padding: 1rem;
      border: 1px dashed var(--color-border);
      border-radius: var(--radius-lg);
      background: transparent;
      color: var(--color-text-muted);
      font-size: 0.9rem;
      cursor: pointer;
      transition:
        border-color var(--transition),
        color var(--transition);
    }

    .add-card:hover {
      border-color: var(--color-primary);
      color: var(--color-primary);
    }

    .add-card svg {
      width: 1.4rem;
      height: 1.4rem;
    }

    .empty {
      color: var(--color-text-muted);
      padding: 2rem;
      text-align: center;
    }

    .error {
      color: var(--color-danger);
      padding: 1rem;
    }
  `];

  @state() declare private _pipelines: PipelineSummary[];
  @state() declare private _loading: boolean;
  @state() declare private _error: string | null;
  @state() declare private _loaded: boolean;
  @state() declare private _deleting: PipelineSummary | null;
  @query('confirm-delete-dialog') declare private _confirmDelete: ConfirmDeleteDialog;

  constructor() {
    super();
    this._pipelines = [];
    this._loading = true;
    this._error = null;
    this._loaded = false;
    this._deleting = null;
    new PollController(this, () => this._load(true));
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
  }

  private async _load(silent = false) {
    if (!silent) {
      this._loading = true;
      this._error = null;
    }
    try {
      this._pipelines = (await client.api.pipelines.summary.get()) ?? [];
      this._loaded = true;
    } catch (e) {
      if (!silent) this._error = extractErrorMessage(e);
    } finally {
      if (!silent) this._loading = false;
    }
  }

  render() {
    return html`
      <div class="header">
        <h2>Pipelines</h2>
        <a class="docs-link" href="/docs/index" @click=${this._navToDocs}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
          </svg>
          <span>Docs</span>
        </a>
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : ''}
      ${!this._loaded
        ? ''
        : html`<div class="card-grid">
            ${this._pipelines.map((p) => this._renderCard(p))}
            <button class="add-card" type="button" @click=${this._create}>
              <svg
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              >
                <path d="M12 5v14" />
                <path d="M5 12h14" />
              </svg>
              <span>Add pipeline</span>
            </button>
          </div>`}
      <confirm-delete-dialog
        .name=${this._deleting?.name ?? ''}
        consequence="Its runs, versions, binding and secrets are deleted. This can't be undone."
        .onConfirm=${this._delete}
      ></confirm-delete-dialog>
    `;
  }

  private _renderCard(p: PipelineSummary) {
    return html`
      <div class="pipeline-card">
        <div class="pc-top">
          <div>
            <a
              class="pc-title"
              href="/pipeline/${p.id}"
              @click=${(e: Event) => this._navToPipeline(e, p.name ?? '')}
              >${p.name}</a
            >
            ${p.repo
              ? html`<a
                  class="pc-repo"
                  href=${this._repoUrl(p.repo)}
                  target="_blank"
                  rel="noopener"
                  >${GITHUB_MARK}<span>${p.repo}</span></a
                >`
              : nothing}
          </div>
          <button class="btn-delete" @click=${() => this._askDelete(p)}>
            Delete
          </button>
        </div>
        ${p.stages?.length ? this._renderCubes(p) : html`<span class="pc-empty">No steps yet</span>`}
        ${this._renderFoot(p)}
      </div>
    `;
  }

  /** Stages left to right, a stage's steps stacked; consecutive stages under the same `when` share a bracket. */
  private _renderCubes(p: PipelineSummary) {
    const held = new Set((p.health?.heldRuns ?? []).map((r) => r.heldAt).filter((x): x is string => !!x));
    const blocked = new Set(p.health?.blockedSteps ?? []);
    const groups: { when: string; stages: PipelineStageSummary[] }[] = [];
    for (const stage of p.stages ?? []) {
      const when = formatConditions(stage.when ?? []);
      const last = groups[groups.length - 1];
      if (last && last.when === when) last.stages.push(stage);
      else groups.push({ when, stages: [stage] });
    }
    const cols = (stages: PipelineStageSummary[]) =>
      stages.map((s) => html`<div class="col">${(s.steps ?? []).map((st) => this._cube(p.id ?? '', st, held, blocked))}</div>`);
    return html`<div class="cubes">
      ${groups.map((g) =>
        g.when
          ? html`<div class="when" title="when ${g.when}">
              <div class="when-cols">${cols(g.stages)}</div>
              <span class="when-label">${g.when}</span>
            </div>`
          : cols(g.stages),
      )}
    </div>`;
  }

  private _cube(pipelineId: string, step: PipelineStepSummary, held: Set<string>, blocked: Set<string>) {
    const name = step.name ?? '';
    const status = held.has(name) ? 'held' : (step.status ?? '');
    const label = status ? statusLabel(status) : 'never run';
    const tip = [
      name,
      label,
      step.runNumber && !held.has(name) ? `#${step.runNumber}` : '',
      step.at && !held.has(name) ? formatRelative(new Date(step.at)) : '',
      blocked.has(name) ? 'blocked' : '',
    ]
      .filter(Boolean)
      .join(' · ');
    return html`<a
      class="cube t-${toneOf(status)} ${status || 'never'} ${blocked.has(name) ? 'blocked' : ''}"
      href=${stepPath(pipelineId, name)}
      @click=${navClick}
      title=${tip}
      aria-label=${tip}
    ></a>`;
  }

  private _renderFoot(p: PipelineSummary) {
    const h = p.health;
    if (!h) return nothing;
    const a = toActivation(h.activation);
    const last = h.lastRun;
    const held = h.heldRuns ?? [];
    const blocked = h.blockedSteps ?? [];
    return html`<div class="pc-foot">
      <span class="st t-${toneOf(h.status)}"><span class="dot"></span>${statusLabel(h.status)}</span>
      ${last
        ? html`<span
            ><a href=${runPath(p.id ?? '', last.number)} @click=${navClick}>#${last.number}</a>
            ${statusLabel(last.status)} <relative-time .value=${last.finishedAt ?? last.createdAt}></relative-time
          ></span>`
        : html`<span>never run</span>`}
      ${a.kind === 'draining' ? html`<span class="tag warn">switching v${a.from} → v${a.to}</span>` : nothing}
      ${a.kind === 'none' ? html`<span class="tag">no version</span>` : nothing}
      ${(h.runningRuns ?? 0) > 1 ? html`<span class="tag run">${h.runningRuns} running</span>` : nothing}
      ${held.length ? html`<span class="tag warn">${held.length} held</span>` : nothing}
      ${blocked.length ? html`<span class="tag warn">${blocked.length} blocked</span>` : nothing}
    </div>`;
  }

  private _repoUrl(repo: string): string {
    // Bindings store an "owner/name" slug; render a GitHub URL. If a full URL is
    // ever stored, pass it through.
    if (repo.startsWith('http://') || repo.startsWith('https://')) return repo;
    return `https://github.com/${repo}`;
  }

  private _navToDocs(e: Event) {
    e.preventDefault();
    navigate('/docs/index');
  }

  private _navToPipeline(e: Event, name: string) {
    e.preventDefault();
    const href = (e.currentTarget as HTMLAnchorElement).getAttribute('href')!;
    navigate(href, { pipelineName: name });
  }

  private async _create() {
    const repo = window.prompt('Repository (owner/name)');
    if (!repo) return;
    await client.api.pipelines.withRepo.post({ repo });
    await this._load();
  }

  private async _askDelete(p: PipelineSummary) {
    this._deleting = p;
    await this.updateComplete;
    this._confirmDelete.open();
  }

  private _delete = async () => {
    const id = this._deleting?.id;
    if (!id) return;
    try {
      await client.api.pipelines.byId(id).delete();
    } catch (e) {
      throw new Error(extractErrorMessage(e));
    }
    this._deleting = null;
    await this._load();
  };
}

function formatConditions(when: RunStageCondition[]): string {
  if (!when.length) return '';
  return formatWhen(Object.fromEntries(when.map((c) => [c.field ?? '', (untypedToJson(c.values) as unknown[]) ?? []])));
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-list-view': PipelineListView;
  }
}
