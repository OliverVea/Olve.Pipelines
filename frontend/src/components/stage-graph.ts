import { LitElement, html, css, nothing, svg, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { type Manifest, type ManifestStep, type Stage, isWhen, formatWhen, allSteps, producersOf } from '../v2/manifest.js';
import { sharedStyles, toneOf, statusLabel, formatDuration, navClick } from '../v2/ui.js';
import './relative-time.js';

export type StepState = {
  status: string;
  reason?: string | null;
  runNumber?: number | null;
  runHref?: string;
  logsHref?: string;
  retryable?: boolean;
  cancellable?: boolean;
  attemptCount?: number;
  latestAttempt?: number | null;
  createdAt?: Date | string | null;
  startedAt?: Date | string | null;
  finishedAt?: Date | string | null;
  blocked?: boolean;
  heldRuns?: number[];
};

type Related = { src: Set<string>; dst: Set<string> };

const STOP = svg`<svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><rect x="4" y="4" width="8" height="8" rx="1.5"/></svg>`;
const RETRY = svg`<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M13 8a5 5 0 1 1-1.5-3.55"/><path d="M13 2.5v3h-3"/></svg>`;
const LOCK = svg`<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/></svg>`;

/**
 * Stages left to right (parallel steps stacked, `when` blocks bracketed); top to
 * bottom on narrow screens. Hovering a step outlines what it reads from and what reads it.
 * Events: `step-select` {step}, `step-retry` {step}, `step-cancel` {step}, `block-toggle` {step, block}.
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
        width: 15.5rem;
        padding: 0.5rem 0.65rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-left: 3px solid var(--tone, var(--color-border));
        border-radius: var(--radius);
        color: var(--color-text);
        cursor: pointer;
        transition:
          opacity var(--transition),
          border-color var(--transition),
          box-shadow var(--transition);
      }
      .tile.dim {
        opacity: 0.4;
      }
      .tile.src {
        box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-primary) 55%, transparent);
      }
      .tile.dst {
        box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-when) 60%, transparent);
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
        align-items: flex-start;
        gap: 0.4rem;
      }
      .name {
        flex: 1;
        min-width: 0;
        font-weight: 600;
        font-size: 0.88rem;
        overflow-wrap: anywhere;
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
      .meta > :not(.icon) + :not(.icon)::before {
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
      .block-note {
        margin-top: 0.2rem;
        font-size: 0.72rem;
        color: var(--color-warning);
      }
      .icon {
        display: inline-flex;
        visibility: hidden;
        padding: 0.15rem;
        border: 0;
        border-radius: var(--radius);
        background: transparent;
        color: var(--color-text-muted);
        opacity: 0.45;
      }
      .icon.stop,
      .icon.retry {
        margin-left: auto;
      }
      .icon.stop + .icon,
      .icon.retry + .lock {
        margin-left: 0;
      }
      .icon.stop {
        visibility: visible;
        opacity: 0.8;
        color: var(--color-danger);
      }
      .lock {
        margin-left: auto;
      }
      .tile:hover .icon,
      .icon:focus-visible,
      .t-fail .retry {
        visibility: visible;
      }
      .t-fail .retry {
        opacity: 1;
        color: var(--color-danger);
      }
      @media (hover: none) {
        .icon {
          visibility: visible;
        }
      }
      .icon:hover,
      .icon:focus-visible {
        opacity: 1;
        background: var(--color-surface-hover);
      }
      .lock.closed {
        visibility: visible;
        opacity: 1;
        color: var(--color-warning);
      }
      .icon svg {
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
  @property({ type: Boolean }) declare stepControls: boolean;
  @state() private declare _hover: string | null;

  constructor() {
    super();
    this.manifest = null;
    this.states = null;
    this.blocked = null;
    this.selected = '';
    this.stepControls = false;
    this._hover = null;
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No manifest.</p>`;
    return html`<div class="scroller"><div class="row">${this._stages(m.stages, this._related(m))}</div></div>`;
  }

  private _related(m: Manifest): Related | null {
    if (!this._hover) return null;
    const steps = allSteps(m.stages);
    const hovered = steps.find((s) => s.name === this._hover);
    if (!hovered) return null;
    return {
      src: producersOf(hovered),
      dst: new Set(steps.filter((s) => producersOf(s).has(hovered.name)).map((s) => s.name)),
    };
  }

  private _stages(stages: Stage[], rel: Related | null): TemplateResult[] {
    const out: TemplateResult[] = [];
    stages.forEach((stage, i) => {
      if (i > 0) out.push(html`<div class="barrier" aria-hidden="true"></div>`);
      if (isWhen(stage)) {
        out.push(html`<div class="when">
          <span class="when-label">when ${formatWhen(stage.when)}</span>
          <div class="when-body">${this._stages(stage.stages, rel)}</div>
        </div>`);
      } else {
        out.push(html`<div class="col">${stage.steps.map((s) => this._tile(s, rel))}</div>`);
      }
    });
    return out;
  }

  private _tile(step: ManifestStep, rel: Related | null) {
    const st = this.states?.get(step.name);
    const blocked = st?.blocked ?? this.blocked?.has(step.name) ?? false;
    const tone = st ? toneOf(st.status) : 'plain';
    const duration = st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '';
    const at = st?.finishedAt ?? st?.startedAt ?? st?.createdAt;
    const held = st?.heldRuns ?? [];
    const relation = !rel || this._hover === step.name ? '' : rel.src.has(step.name) ? 'src' : rel.dst.has(step.name) ? 'dst' : 'dim';
    return html`<div
      class="tile t-${tone} ${tone === 'plain' ? 'plain' : ''} ${relation} ${this.selected === step.name ? 'selected' : ''}"
      role="button"
      tabindex="0"
      aria-label="${step.name}${st ? `, ${statusLabel(st.status)}` : ''}"
      @mouseenter=${() => (this._hover = step.name)}
      @mouseleave=${() => (this._hover = null)}
      @focus=${() => (this._hover = step.name)}
      @blur=${() => (this._hover = null)}
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
            ? html`<a class="badge" href=${st.logsHref} @click=${this._link} @keydown=${this._link}
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
        ${this.stepControls ? this._cancel(step.name, st) : nothing}
        ${this.stepControls ? this._retry(step.name, st) : nothing}
        ${this.stepControls ? this._lock(step.name, blocked) : nothing}
      </div>
      ${st?.reason ? html`<div class="reason">${st.reason}</div>` : nothing}
      ${blocked ? html`<div class="block-note">blocked${held.length ? ` · holding ${held.map((n) => `#${n}`).join(', ')}` : ''}</div>` : nothing}
    </div>`;
  }

  private _run(st: StepState) {
    const label = `#${st.runNumber}${(st.attemptCount ?? 0) > 1 ? `@${st.latestAttempt}` : ''}`;
    if (!st.runHref) return html`<span>${label}</span>`;
    return html`<a
      class="run"
      href=${st.runHref}
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

  private _cancel(step: string, st: StepState | undefined) {
    if (!st?.cancellable) return nothing;
    return html`<button
      class="icon stop"
      aria-label="Cancel ${step}"
      @click=${(e: Event) => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('step-cancel', { detail: { step } }));
      }}
    >
      ${STOP}
    </button>`;
  }

  private _retry(step: string, st: StepState | undefined) {
    if (!st?.retryable) return nothing;
    return html`<button
      class="icon retry"
      aria-label="Retry ${step}"
      @click=${(e: Event) => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('step-retry', { detail: { step } }));
      }}
    >
      ${RETRY}
    </button>`;
  }

  private _lock(step: string, blocked: boolean) {
    return html`<button
      class="icon lock ${blocked ? 'closed' : ''}"
      aria-label=${blocked ? `Unblock ${step}` : `Block ${step}`}
      @click=${(e: Event) => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('block-toggle', { detail: { step, block: !blocked } }));
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
