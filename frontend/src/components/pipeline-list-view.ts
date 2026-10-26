import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, state, query } from 'lit/decorators.js';
import { client, extractErrorMessage } from '../api.js';
import { navigate } from '../router.js';
import { formatRelative } from './relative-time.js';
import './confirm-delete-dialog.js';
import type { ConfirmDeleteDialog } from './confirm-delete-dialog.js';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  navClick,
  runPath,
  stepPath,
} from '../v2/ui.js';
import { toActivation } from '../v2/versions.js';
import { LiveController } from '../v2/events.js';
import type {
  PipelineSummary,
  PipelineStepSummary,
} from '@olve/olve-pipelines-client/src/models/index.js';

const GITHUB_MARK = svg`<svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" /></svg>`;

@customElement('pipeline-list-view')
export class PipelineListView extends LitElement {
  static styles = [
    sharedStyles,
    css`
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

      .docs-link,
      .add-btn {
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
        cursor: pointer;
        transition:
          background var(--transition),
          border-color var(--transition);
      }

      .docs-link:hover,
      .add-btn:hover {
        background: var(--color-surface-hover);
        border-color: var(--color-primary);
        text-decoration: none;
      }

      .docs-link svg,
      .add-btn svg {
        width: 1rem;
        height: 1rem;
      }

      .rows {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 30rem), 1fr));
        gap: 0.75rem;
      }

      .pipeline-row {
        display: grid;
        grid-template-columns: minmax(0, 13rem) minmax(0, 1fr) auto;
        grid-template-areas: 'status graph menu';
        align-items: center;
        gap: 1rem;
        padding: 0.6rem 0.5rem 0.6rem 1rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: var(--radius-lg);
        box-shadow: var(--shadow);
        transition: border-color var(--transition);
        cursor: pointer;
      }

      .pipeline-row:hover {
        border-color: var(--color-primary);
      }

      .pr-status {
        grid-area: status;
        min-width: 0;
      }

      .pr-graph {
        grid-area: graph;
        min-width: 0;
        display: flex;
        justify-content: center;
      }

      .pr-menu {
        grid-area: menu;
        align-self: center;
      }

      @media (max-width: 640px) {
        .pipeline-row {
          grid-template-columns: minmax(0, 1fr) auto;
          grid-template-areas:
            'status menu'
            'graph graph';
          gap: 0.6rem 0.5rem;
          padding: 0.6rem 0.5rem 0.75rem 0.85rem;
        }
        .pr-menu {
          align-self: start;
        }
      }

      .pc-title {
        display: block;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
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
        max-width: 100%;
        margin-top: 0.1rem;
        font-size: 0.78rem;
        color: var(--color-text-muted);
        text-decoration: none;
      }

      .pc-repo:hover {
        color: var(--color-primary);
      }

      .pc-repo svg {
        flex-shrink: 0;
        width: 0.85rem;
        height: 0.85rem;
      }

      .pc-repo span {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .btn-delete {
        background: none;
        border: 1px solid var(--color-border);
        color: var(--color-text-muted);
        padding: 0.25rem 0.5rem;
        border-radius: var(--radius);
        font-size: 0.8rem;
        transition: all var(--transition);
      }

      .btn-delete:hover {
        border-color: var(--color-danger);
        color: var(--color-danger);
      }

      .cubes {
        display: flex;
        align-items: center;
        gap: 4px;
        width: max-content;
        max-width: 100%;
        padding: 3px;
        overflow-x: auto;
      }
      .col {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .cube {
        display: block;
        width: 10px;
        height: 10px;
        box-sizing: border-box;
        border-radius: 2px;
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
        outline: 1.5px solid var(--color-warning);
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
        gap: 0.2rem 0.6rem;
        margin-top: 0.3rem;
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

      .empty {
        color: var(--color-text-muted);
        padding: 2rem;
        text-align: center;
      }

      .error {
        color: var(--color-danger);
        padding: 1rem;
      }
    `,
  ];

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
    new LiveController(
      this,
      () => this._load(true),
      (n) => n.type !== 'poll',
    );
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
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
          </svg>
          <span>Docs</span>
        </a>
        <button class="add-btn" type="button" @click=${this._create}>
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
      </div>
      ${this._error ? html`<p class="error">${this._error}</p>` : ''}
      ${!this._loaded
        ? ''
        : this._pipelines.length
          ? html`<div class="rows">
              ${this._pipelines.map((p) => this._renderRow(p))}
            </div>`
          : html`<p class="empty">No pipelines yet. Add one to bind a repository.</p>`}
      <confirm-delete-dialog
        .name=${this._deleting?.name ?? ''}
        consequence="Its runs, versions, binding and secrets are deleted. This can't be undone."
        .onConfirm=${this._delete}
      ></confirm-delete-dialog>
    `;
  }

  private _renderRow(p: PipelineSummary) {
    return html`
      <div class="pipeline-row" @click=${(e: MouseEvent) => this._cardClick(e, p)}>
        <div class="pr-status">
          <a
            class="pc-title"
            href="/pipeline/${p.id}"
            title=${p.name ?? ''}
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
          ${this._renderFoot(p)}
        </div>
        <div class="pr-graph">
          ${p.stages?.length
            ? this._renderCubes(p)
            : html`<span class="pc-empty">No steps yet</span>`}
        </div>
        <div class="pr-menu">
          <button class="btn-delete" type="button" @click=${() => this._askDelete(p)}>
            Delete
          </button>
        </div>
      </div>
    `;
  }

  /** Stages left to right, a stage's steps stacked and centred. */
  private _renderCubes(p: PipelineSummary) {
    const held = new Set(
      (p.health?.heldRuns ?? []).map((r) => r.heldAt).filter((x): x is string => !!x),
    );
    const blocked = new Set(p.health?.blockedSteps ?? []);
    return html`<div class="cubes">
      ${(p.stages ?? []).map(
        (s) =>
          html`<div class="col">
            ${(s.steps ?? []).map((st) => this._cube(p.id ?? '', st, held, blocked))}
          </div>`,
      )}
    </div>`;
  }

  private _cube(
    pipelineId: string,
    step: PipelineStepSummary,
    held: Set<string>,
    blocked: Set<string>,
  ) {
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
      class="cube t-${toneOf(status)} ${status || 'never'} ${blocked.has(name)
        ? 'blocked'
        : ''}"
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
      <span class="st t-${toneOf(h.status)}"
        ><span class="dot"></span>${statusLabel(h.status)}</span
      >
      ${last
        ? html`<span
            ><a href=${runPath(p.id ?? '', last.number)} @click=${navClick}
              >#${last.number}</a
            >
            ${statusLabel(last.status)}
            <relative-time .value=${last.finishedAt ?? last.createdAt}></relative-time
          ></span>`
        : html`<span>never run</span>`}
      ${a.kind === 'draining'
        ? html`<span class="tag warn">switching v${a.from} → v${a.to}</span>`
        : nothing}
      ${a.kind === 'none' ? html`<span class="tag">no version</span>` : nothing}
      ${(h.runningRuns ?? 0) > 1
        ? html`<span class="tag run">${h.runningRuns} running</span>`
        : nothing}
      ${held.length ? html`<span class="tag warn">${held.length} held</span>` : nothing}
      ${blocked.length
        ? html`<span class="tag warn">${blocked.length} blocked</span>`
        : nothing}
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

  // Links, cubes and buttons keep their own targets; text selection doesn't navigate.
  private _cardClick(e: MouseEvent, p: PipelineSummary) {
    if (
      e.composedPath().some((el) => el instanceof HTMLElement && el.matches('a, button'))
    )
      return;
    if (window.getSelection()?.toString()) return;
    const href = `/pipeline/${p.id}`;
    if (e.ctrlKey || e.metaKey) window.open(href, '_blank');
    else navigate(href, { pipelineName: p.name ?? '' });
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

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-list-view': PipelineListView;
  }
}
