import { LitElement, html, css, nothing, svg, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { type Manifest, type ManifestStep, type Stage, isWhen, formatWhen } from '../v2/manifest.js';
import { sharedStyles, toneOf, statusLabel, formatDuration, navClick } from '../v2/ui.js';
import './relative-time.js';

export type StepState = {
  status: string;
  reason?: string | null;
  runNumber?: number | null;
  runHref?: string;
  logsHref?: string;
  attemptCount?: number;
  latestAttempt?: number | null;
  createdAt?: Date | string | null;
  startedAt?: Date | string | null;
  finishedAt?: Date | string | null;
  blocked?: boolean;
  heldRuns?: number[];
};

const LOCK = svg`<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/></svg>`;

/**
 * Stages left to right (parallel steps stacked, `when` blocks bracketed); top to
 * bottom on narrow screens. Events: `step-select` {step}, `gate-toggle` {step, block}.
 */
@customElement('stage-graph')
export class StageGraph extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .scroller {
        overflow-x: auto;
        padding: 0.9rem 0 0.4rem;
      }
      .row,
      .when-body {
        display: flex;
        align-items: stretch;
        min-width: min-content;
      }
      .col {
        display: flex;
        flex-direction: column;
        justify-content: center;
        gap: 0.5rem;
      }
      .barrier {
        position: relative;
        width: 1.5rem;
        flex-shrink: 0;
      }
      .barrier::after {
        content: '';
        position: absolute;
        left: calc(50% - 4px);
        top: calc(50% - 4px);
        width: 7px;
        height: 7px;
        border-top: 1.5px solid var(--color-text-muted);
        border-right: 1.5px solid var(--color-text-muted);
        transform: rotate(45deg);
      }
      .when {
        position: relative;
        display: flex;
        --bracket: color-mix(in srgb, var(--color-when) 55%, transparent);
        border-top: 1.5px dashed var(--bracket);
        padding: 0.7rem 0.4rem 0;
        margin-top: 0.5rem;
      }
      .when::before,
      .when::after {
        content: '';
        position: absolute;
        top: 0;
        height: 0.45rem;
        border-left: 1.5px solid var(--bracket);
      }
      .when::before {
        left: 0;
      }
      .when::after {
        right: 0;
      }
      .when-label {
        position: absolute;
        top: -0.65rem;
        left: 0.6rem;
        padding: 0 0.35rem;
        background: var(--graph-bg, var(--color-bg));
        color: var(--color-when);
        font-size: 0.72rem;
        white-space: nowrap;
      }

      .tile {
        position: relative;
        width: 14.5rem;
        padding: 0.5rem 0.65rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-left: 3px solid var(--tone, var(--color-border));
        border-radius: var(--radius);
        color: var(--color-text);
        cursor: pointer;
        transition: border-color var(--transition);
      }
      .tile:hover {
        border-color: var(--color-primary);
        border-left-color: var(--tone, var(--color-primary));
      }
      .tile.selected {
        box-shadow: 0 0 0 2px var(--color-primary);
      }
      .tile.plain {
        --tone: var(--color-border);
      }
      .head {
        display: flex;
        align-items: center;
        gap: 0.4rem;
      }
      .name {
        flex: 1;
        min-width: 0;
        font-weight: 600;
        font-size: 0.88rem;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .badge {
        font-size: 0.66rem;
        font-weight: 650;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        padding: 0.1rem 0.35rem;
        border-radius: 3px;
        white-space: nowrap;
        color: var(--tone);
        background: color-mix(in srgb, var(--tone) 15%, transparent);
      }
      .t-idle .badge,
      .t-skip .badge,
      .t-off .badge {
        background: transparent;
        border: 1px solid color-mix(in srgb, var(--tone) 45%, transparent);
      }
      .t-run .badge {
        animation: pulse 1.6s ease-in-out infinite;
      }
      .meta {
        margin-top: 0.3rem;
        min-height: 1.15rem;
        display: flex;
        align-items: center;
        gap: 0.35rem;
        font-size: 0.74rem;
        color: var(--color-text-muted);
        white-space: nowrap;
        overflow: hidden;
      }
      .meta > :not(.lock) + :not(.lock)::before {
        content: '·';
        margin-right: 0.35rem;
      }
      .run {
        color: inherit;
      }
      a.badge {
        text-decoration: none;
      }
      a.badge:hover {
        text-decoration: underline;
      }
      .run:hover {
        color: var(--color-primary);
      }
      .reason {
        margin-top: 0.2rem;
        font-size: 0.72rem;
        color: var(--color-text-muted);
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
      }
      .gate-note {
        margin-top: 0.2rem;
        font-size: 0.72rem;
        color: var(--color-warning);
      }
      .lock {
        display: inline-flex;
        margin-left: auto;
        visibility: hidden;
        padding: 0.15rem;
        border: 0;
        border-radius: var(--radius);
        background: transparent;
        color: var(--color-text-muted);
        opacity: 0.45;
      }
      .tile:hover .lock,
      .lock:focus-visible {
        visibility: visible;
      }
      .lock:hover,
      .lock:focus-visible {
        opacity: 1;
        background: var(--color-surface-hover);
      }
      .lock.closed {
        visibility: visible;
        opacity: 1;
        color: var(--color-warning);
      }
      .lock svg {
        width: 0.85rem;
        height: 0.85rem;
      }

      @media (max-width: 640px) {
        .row,
        .when-body {
          flex-direction: column;
          min-width: 0;
        }
        .tile {
          width: 100%;
        }
        .barrier {
          width: auto;
          height: 1.4rem;
        }
        .barrier::after {
          transform: rotate(135deg);
        }
        .when {
          margin-top: 0.6rem;
        }
        .when-body {
          flex: 1;
        }
      }
    `,
  ];

  @property({ attribute: false }) declare manifest: Manifest | null;
  @property({ attribute: false }) declare states: Map<string, StepState> | null;
  @property({ attribute: false }) declare blocked: Set<string> | null;
  @property() declare selected: string;
  @property({ type: Boolean }) declare gateControls: boolean;

  constructor() {
    super();
    this.manifest = null;
    this.states = null;
    this.blocked = null;
    this.selected = '';
    this.gateControls = false;
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No manifest.</p>`;
    return html`<div class="scroller"><div class="row">${this._stages(m.stages)}</div></div>`;
  }

  private _stages(stages: Stage[]): TemplateResult[] {
    const out: TemplateResult[] = [];
    stages.forEach((stage, i) => {
      if (i > 0) out.push(html`<div class="barrier" aria-hidden="true"></div>`);
      if (isWhen(stage)) {
        out.push(html`<div class="when">
          <span class="when-label" title="Runs only when the pipeline input matches">when ${formatWhen(stage.when)}</span>
          <div class="when-body">${this._stages(stage.stages)}</div>
        </div>`);
      } else {
        out.push(html`<div class="col">${stage.steps.map((s) => this._tile(s))}</div>`);
      }
    });
    return out;
  }

  private _tile(step: ManifestStep) {
    const st = this.states?.get(step.name);
    const blocked = st?.blocked ?? this.blocked?.has(step.name) ?? false;
    const tone = st ? toneOf(st.status) : 'plain';
    const duration = st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '';
    const at = st?.finishedAt ?? st?.startedAt ?? st?.createdAt;
    const held = st?.heldRuns ?? [];
    return html`<div
      class="tile t-${tone} ${tone === 'plain' ? 'plain' : ''} ${this.selected === step.name ? 'selected' : ''}"
      role="button"
      tabindex="0"
      aria-label="${step.name}${st ? `, ${statusLabel(st.status)}` : ''}"
      title="${step.name} (${step.kind}) — ${step.image}"
      @click=${() => this._select(step.name)}
      @keydown=${(e: KeyboardEvent) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          this._select(step.name);
        }
      }}
    >
      <div class="head">
        <span class="name">${step.name}</span>
        ${st && st.status !== 'not-run'
          ? st.logsHref
            ? html`<a class="badge" href=${st.logsHref} title="Open the log" @click=${this._link} @keydown=${this._link}
                >${statusLabel(st.status)}</a
              >`
            : html`<span class="badge">${statusLabel(st.status)}</span>`
          : nothing}
      </div>
      <div class="meta">
        ${st?.status === 'not-run' ? html`<span>never run</span>` : nothing}
        ${st?.runNumber ? this._run(st) : nothing}
        ${st?.runNumber && at ? html`<relative-time .value=${at}></relative-time>` : nothing}
        ${duration ? html`<span>${duration}</span>` : nothing}
        ${this.gateControls ? this._lock(step.name, blocked) : nothing}
      </div>
      ${st?.reason ? html`<div class="reason" title=${st.reason}>${st.reason}</div>` : nothing}
      ${blocked ? html`<div class="gate-note">blocked${held.length ? ` · holding ${held.map((n) => `#${n}`).join(', ')}` : ''}</div>` : nothing}
    </div>`;
  }

  private _run(st: StepState) {
    const label = `#${st.runNumber}${(st.attemptCount ?? 0) > 1 ? `@${st.latestAttempt}` : ''}`;
    if (!st.runHref) return html`<span>${label}</span>`;
    return html`<a
      class="run"
      href=${st.runHref}
      title="Open run #${st.runNumber}"
      @click=${this._link}
      @keydown=${this._link}
      >${label}</a
    >`;
  }

  /** Links inside a tile must not also trigger the tile's own navigation. */
  private _link(e: MouseEvent | KeyboardEvent) {
    e.stopPropagation();
    if (e instanceof MouseEvent) navClick(e);
  }

  private _lock(step: string, blocked: boolean) {
    return html`<button
      class="lock ${blocked ? 'closed' : ''}"
      title=${blocked ? 'Blocked: runs hold before this step. Click to unblock.' : 'Block: hold runs before this step.'}
      aria-label=${blocked ? `Unblock ${step}` : `Block ${step}`}
      @click=${(e: Event) => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('gate-toggle', { detail: { step, block: !blocked } }));
      }}
    >
      ${LOCK}
    </button>`;
  }

  private _select(step: string) {
    this.dispatchEvent(new CustomEvent('step-select', { detail: { step } }));
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'stage-graph': StageGraph;
  }
}
