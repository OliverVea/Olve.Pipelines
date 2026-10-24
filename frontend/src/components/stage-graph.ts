import { LitElement, html, css, nothing, svg, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  type Manifest,
  type ManifestStep,
  type Stage,
  isWhen,
  isSelect,
  formatCase,
  formatWhen,
  allSteps,
  producersOf,
} from '../v2/manifest.js';
import { sharedStyles, toneOf, statusLabel, formatDuration, navClick } from '../v2/ui.js';
import { sheetHeight } from './step-sheet.js';
import { formatRelative } from './relative-time.js';

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

export type Level = 'chips' | 'cards';
type Orientation = 'horizontal' | 'vertical';
type Related = { src: Set<string>; dst: Set<string> };

const LEVEL_KEY = 'olve-graph-level';
const LEVELS: [Level, string][] = [
  ['chips', 'Chips'],
  ['cards', 'Cards'],
];

function storedLevel(): Level {
  try {
    const v = localStorage.getItem(LEVEL_KEY);
    return v === 'cards' || v === 'chips' ? v : 'chips';
  } catch {
    return 'chips';
  }
}

const currentOrientation = (): Orientation =>
  innerHeight > innerWidth ? 'vertical' : 'horizontal';
const canHover = () => matchMedia('(hover: hover)').matches;

const STOP = svg`<svg viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><rect x="4" y="4" width="8" height="8" rx="1.5"/></svg>`;
const RETRY = svg`<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><path d="M13 8a5 5 0 1 1-1.5-3.55"/><path d="M13 2.5v3h-3"/></svg>`;
const LOCK = svg`<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/></svg>`;

/**
 * The stage graph at two levels (chips, cards). Landscape viewports lay stages out left to right,
 * portrait ones top to bottom. Hovering a step outlines what it reads from and what reads it.
 * A selected step is assumed to have the step sheet open below it.
 * Events: `step-select` {step}, `step-retry` {step}, `step-cancel` {step}, `block-toggle` {step, block}.
 */
@customElement('stage-graph')
export class StageGraph extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .bar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.4rem 0.8rem;
        font-size: 0.75rem;
      }
      .note {
        margin-left: auto;
      }
      .seg {
        display: inline-flex;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        overflow: hidden;
        background: var(--color-surface);
      }
      .seg button {
        border: 0;
        background: transparent;
        color: var(--color-text);
        padding: 0.2rem 0.6rem;
        font-size: 0.76rem;
      }
      .seg button + button {
        border-left: 1px solid var(--color-border);
      }
      .seg button:hover {
        background: var(--color-surface-hover);
      }
      .seg button[aria-pressed='true'] {
        background: var(--color-primary);
        color: var(--color-surface);
      }
      .seg button:focus-visible {
        outline: 2px solid var(--color-primary);
        outline-offset: -2px;
      }

      .scroller {
        overflow-x: auto;
        overscroll-behavior-x: contain;
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
      .barrier::after,
      .down::after {
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
        padding: 0.7rem 0 0;
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
      .lv-chips .col {
        gap: 0.4rem;
      }
      .lv-chips .barrier {
        width: 1.3rem;
      }
      .lv-chips .when {
        padding-top: 0.8rem;
      }

      .vflow {
        --rail: 2.2rem;
        padding-top: 0.9rem;
      }
      .vflow .stage {
        display: grid;
        grid-template-columns: var(--rail) minmax(0, 1fr) var(--rail);
        align-items: start;
      }
      .num {
        padding-top: 0.9rem;
        font-size: 0.72rem;
        color: var(--color-text-muted);
        font-variant-numeric: tabular-nums;
      }
      .lv-chips .num {
        padding-top: 0.45rem;
      }
      .steps {
        display: flex;
        flex-wrap: wrap;
        justify-content: center;
        gap: 0.5rem;
      }
      .lv-chips .steps {
        gap: 0.4rem;
      }
      .down {
        position: relative;
        height: 1.4rem;
        margin: 0 var(--rail);
      }
      .down::after {
        top: calc(50% - 6px);
        transform: rotate(135deg);
      }
      .vwhen {
        position: relative;
        margin-top: 0.3rem;
      }
      .vwhen::before {
        content: '';
        position: absolute;
        left: calc(var(--rail) - 0.6rem);
        top: 0.55rem;
        bottom: 0.2rem;
        border-left: 1.5px dashed color-mix(in srgb, var(--color-when) 60%, transparent);
      }
      .vlabel {
        display: block;
        margin: 0 var(--rail) 0.35rem;
        text-align: center;
        color: var(--color-when);
        font-size: 0.72rem;
      }
      .vflow .tile {
        max-width: 100%;
      }

      .tile,
      .chip {
        position: relative;
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
      .tile {
        width: 15.5rem;
        padding: 0.5rem 0.65rem;
      }
      .chip {
        display: flex;
        align-items: center;
        gap: 0.4rem;
        width: var(--chip-w, 9rem);
        height: 2rem;
        padding: 0 0.5rem 0 0.45rem;
        font-size: 0.78rem;
        font-weight: 600;
      }
      .dim {
        opacity: 0.4;
      }
      .src {
        box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-primary) 55%, transparent);
      }
      .dst {
        box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-when) 60%, transparent);
      }
      .tile:hover,
      .chip:hover {
        border-color: var(--color-primary);
        border-left-color: var(--tone, var(--color-primary));
      }
      .selected {
        box-shadow: 0 0 0 2px var(--color-primary);
      }
      .plain {
        --tone: var(--color-border);
      }
      .tile:focus-visible,
      .chip:focus-visible {
        outline: 2px solid var(--color-primary);
        outline-offset: 2px;
      }
      .chip .label {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .chip .mark {
        display: inline-flex;
        color: var(--color-warning);
      }
      .chip .mark svg {
        width: 0.75rem;
        height: 0.75rem;
      }
      .chip .att {
        font-size: 0.66rem;
        color: var(--tone);
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
      .case {
        display: inline-block;
        margin-top: 4px;
        padding: 0 6px;
        border-radius: 999px;
        font-size: 11px;
        color: var(--muted, #888);
        border: 1px solid var(--border, #ccc);
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
      .icon.retry,
      .lock {
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

      .hovercard {
        position: fixed;
        z-index: 20;
        pointer-events: none;
        min-width: 13rem;
        max-width: 19rem;
        padding: 0.5rem 0.65rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        box-shadow: 0 6px 20px rgba(4, 32, 55, 0.18);
        font-size: 0.78rem;
        color: var(--color-text-muted);
      }
      .hc-name {
        display: flex;
        align-items: center;
        gap: 0.4rem;
        color: var(--color-text);
        font-weight: 600;
        font-size: 0.85rem;
      }
      .hc-name .badge {
        margin-left: auto;
      }
      .hc-meta > span + span::before {
        content: '·';
        margin: 0 0.35rem;
      }

      @media (max-width: 640px) {
        .note {
          margin-left: 0;
        }
        .vflow {
          --rail: 1.6rem;
        }
        .vflow.lv-chips .chip {
          --chip-w: calc(50% - 0.2rem);
        }
      }
    `,
  ];

  @property({ attribute: false }) declare manifest: Manifest | null;
  @property({ attribute: false }) declare states: Map<string, StepState> | null;
  @property({ attribute: false }) declare blocked: Set<string> | null;
  @property() declare selected: string;
  @property({ type: Boolean }) declare stepControls: boolean;
  /** Step name → the select cases that picked it, shown as a chip (effective pipelines). */
  @property({ attribute: false }) declare cases: Record<string, string[]> | null;
  @state() declare private _hover: string | null;
  @state() declare private _card: { step: string; anchor: DOMRect } | null;
  @state() declare private _level: Level;
  @state() declare private _orientation: Orientation;
  @state() declare private _overflow: boolean;

  /** Centre the selected card in the scroller after the next render (horizontal). */
  private _centre = false;
  /** Bring the selected step into view above the sheet after the next render. */
  private _reveal = false;
  private _levelChanged = false;
  private readonly _portrait = matchMedia('(orientation: portrait)');
  private readonly _resizeObserver = new ResizeObserver(() => this._measureOverflow());

  constructor() {
    super();
    this.manifest = null;
    this.states = null;
    this.blocked = null;
    this.selected = '';
    this.stepControls = false;
    this._hover = null;
    this._card = null;
    this._level = storedLevel();
    this._orientation = currentOrientation();
    this._overflow = false;
  }

  connectedCallback() {
    super.connectedCallback();
    document.addEventListener('keydown', this._onKey);
    addEventListener('resize', this._reorient);
    this._portrait.addEventListener('change', this._reorient);
    this._resizeObserver.observe(this);
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    document.removeEventListener('keydown', this._onKey);
    removeEventListener('resize', this._reorient);
    this._portrait.removeEventListener('change', this._reorient);
    this._resizeObserver.disconnect();
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No manifest.</p>`;
    const rel = this._related(m);
    const vertical = this._orientation === 'vertical';
    return html`${this._bar()}
    ${vertical
      ? html`<div class="vflow lv-${this._level}">
          ${this._rows(m.stages, rel, { n: 0 })}
        </div>`
      : html`<div class="flow lv-${this._level}">
          <div class="scroller">
            <div class="row">${this._stages(m.stages, rel)}</div>
          </div>
        </div>`}
    ${this._hovercard()}`;
  }

  protected updated(changed: Map<string, unknown>) {
    this.dataset.level = this._level;
    this.dataset.orientation = this._orientation;
    const sc = this._scroller;
    const sel = this.selected ? this._el(this.selected) : null;
    if (sc && this._centre && sel) {
      const a = sc.getBoundingClientRect();
      const b = sel.getBoundingClientRect();
      sc.scrollLeft += b.left - a.left - (sc.clientWidth - b.width) / 2;
    } else if (sc && this._levelChanged) {
      sc.scrollLeft = 0;
    }
    if (this.selected && (changed.has('selected') || this._reveal)) {
      // After the sheet below has rendered.
      requestAnimationFrame(() =>
        this._orientation === 'vertical'
          ? this._centreVertically()
          : this._revealAboveSheet(),
      );
    }
    this._centre = this._reveal = this._levelChanged = false;
    this._measureOverflow();
    this._placeCard();
  }

  private get _scroller() {
    return this.renderRoot.querySelector<HTMLElement>('.scroller');
  }

  private _el(step: string) {
    return this.renderRoot.querySelector<HTMLElement>(
      `[data-step="${CSS.escape(step)}"]`,
    );
  }

  private _measureOverflow() {
    const sc = this._scroller;
    const overflow = !!sc && sc.scrollWidth > sc.clientWidth + 1;
    if (overflow !== this._overflow) this._overflow = overflow;
  }

  /** Vertical: the selected step to the middle of what's visible above the sheet. */
  private _centreVertically() {
    const el = this.selected ? this._el(this.selected) : null;
    if (!el) return;
    const visible = innerHeight - sheetHeight();
    const r = el.getBoundingClientRect();
    scrollBy({ top: r.top + r.height / 2 - visible / 2 });
  }

  /** Horizontal: scroll the page only when the selected step is hidden behind the sheet or above the fold. */
  private _revealAboveSheet() {
    const el = this.selected ? this._el(this.selected) : null;
    if (!el) return;
    const top = innerHeight - sheetHeight();
    const r = el.getBoundingClientRect();
    if (r.bottom > top - 12) scrollBy({ top: r.bottom - top + 24 });
    else if (r.top < 8) scrollBy({ top: r.top - 24 });
  }

  private _choose(level: Level) {
    if (level === this._level) return;
    this._level = level;
    try {
      localStorage.setItem(LEVEL_KEY, level);
    } catch {
      /* storage unavailable */
    }
    this._centre =
      level === 'cards' && !!this.selected && this._orientation === 'horizontal';
    this._reveal = !!this.selected && this._orientation === 'vertical';
    this._levelChanged = true;
    this._card = null;
  }

  private _onKey = (e: KeyboardEvent) => {
    if (e.ctrlKey || e.metaKey || e.altKey || (e.key !== '[' && e.key !== ']')) return;
    const t = e.composedPath()[0];
    if (
      t instanceof HTMLElement &&
      (t.isContentEditable || t.matches('input, textarea, select'))
    )
      return;
    e.preventDefault();
    this._choose(e.key === ']' ? 'cards' : 'chips');
  };

  private _reorient = () => {
    const or = currentOrientation();
    if (or === this._orientation) return;
    this._orientation = or;
    this._reveal = !!this.selected;
    this._centre = or === 'horizontal' && this._level === 'cards' && !!this.selected;
    this._card = null;
  };

  private _bar() {
    const chips = this._level === 'chips';
    const note = `${chips ? 'Chips' : 'Cards'} · ${this._orientation}${this._overflow && this._orientation === 'horizontal' ? ' · scroll sideways for more' : ''}`;
    return html`<div class="bar">
      <span class="muted"
        >Click a step for its
        details.${chips && canHover() ? ' Hover for run and duration.' : ''}</span
      >
      <span class="note muted" aria-live="polite">${note}</span>
      <span class="seg" role="group" aria-label="Detail level">
        ${LEVELS.map(
          ([v, label]) =>
            html`<button
              aria-pressed=${this._level === v}
              @click=${async (e: Event) => {
                const b = e.currentTarget as HTMLElement;
                this._choose(v);
                await this.updateComplete;
                b.focus({ preventScroll: true });
              }}
            >
              ${label}
            </button>`,
        )}
      </span>
    </div>`;
  }

  private _related(m: Manifest): Related | null {
    if (!this._hover) return null;
    const steps = allSteps(m.stages);
    const hovered = steps.find((s) => s.name === this._hover);
    if (!hovered) return null;
    return {
      src: producersOf(hovered),
      dst: new Set(
        steps.filter((s) => producersOf(s).has(hovered.name)).map((s) => s.name),
      ),
    };
  }

  private _stages(stages: Stage[], rel: Related | null): TemplateResult[] {
    const out: TemplateResult[] = [];
    stages.forEach((stage, i) => {
      if (i > 0) out.push(html`<div class="barrier" aria-hidden="true"></div>`);
      if (isWhen(stage)) {
        out.push(
          html`<div class="when">
            <span class="when-label">when ${formatWhen(stage.when)}</span>
            <div class="when-body">${this._stages(stage.stages, rel)}</div>
          </div>`,
        );
      } else if (isSelect(stage)) {
        for (const [value, inner] of Object.entries(stage.cases))
          out.push(
            html`<div class="when">
              <span class="when-label">config ${formatCase(stage, value)}</span>
              <div class="when-body">${this._stages(inner, rel)}</div>
            </div>`,
          );
      } else {
        out.push(
          html`<div class="col">${stage.steps.map((s) => this._node(s, rel))}</div>`,
        );
      }
    });
    return out;
  }

  private _rows(
    stages: Stage[],
    rel: Related | null,
    counter: { n: number },
  ): TemplateResult[] {
    return stages.map((stage, i) => {
      const down = i > 0 ? html`<div class="down" aria-hidden="true"></div>` : nothing;
      if (isWhen(stage)) {
        return html`${down}
          <div class="vwhen">
            <span class="vlabel">when ${formatWhen(stage.when)}</span>${this._rows(
              stage.stages,
              rel,
              counter,
            )}
          </div>`;
      }
      if (isSelect(stage)) {
        const at = counter.n;
        return html`${down}${Object.entries(stage.cases).map(([value, inner]) => {
          counter.n = at;
          return html`<div class="vwhen">
            <span class="vlabel">config ${formatCase(stage, value)}</span>${this._rows(
              inner,
              rel,
              counter,
            )}
          </div>`;
        })}`;
      }
      counter.n++;
      return html`${down}
        <div class="stage">
          <span class="num">${counter.n}</span>
          <div class="steps">${stage.steps.map((s) => this._node(s, rel))}</div>
        </div>`;
    });
  }

  private _node(step: ManifestStep, rel: Related | null) {
    return this._level === 'chips' ? this._chip(step, rel) : this._tile(step, rel);
  }

  private _relation(step: string, rel: Related | null) {
    if (!rel || this._hover === step) return '';
    return rel.src.has(step)
      ? 'src'
      : rel.dst.has(step)
        ? 'dst'
        : step === this.selected
          ? ''
          : 'dim';
  }

  /** Shared attributes and handlers of a step node. */
  private _nodeEvents(step: string, hovercard: boolean) {
    return {
      enter: (e: Event) => {
        this._hover = step;
        if (hovercard && e.type === 'mouseenter' && canHover())
          this._card = {
            step,
            anchor: (e.currentTarget as HTMLElement).getBoundingClientRect(),
          };
      },
      leave: () => {
        this._hover = null;
        this._card = null;
      },
      click: () => {
        this._card = null;
        this._select(step);
      },
      key: (e: KeyboardEvent) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          this._select(step);
        }
      },
    };
  }

  private _chip(step: ManifestStep, rel: Related | null) {
    const st = this.states?.get(step.name);
    const blocked = st?.blocked ?? this.blocked?.has(step.name) ?? false;
    const tone = st && st.status !== 'not-run' ? toneOf(st.status) : 'plain';
    const ev = this._nodeEvents(step.name, true);
    return html`<div
      class="chip t-${tone} ${tone === 'plain' ? 'plain' : ''} ${this._relation(
        step.name,
        rel,
      )} ${this.selected === step.name ? 'selected' : ''}"
      role="button"
      tabindex="0"
      data-step=${step.name}
      aria-label="${step.name}${st ? `, ${statusLabel(st.status)}` : ''}${blocked
        ? ', blocked'
        : ''}"
      @mouseenter=${ev.enter}
      @mouseleave=${ev.leave}
      @focus=${ev.enter}
      @blur=${ev.leave}
      @click=${ev.click}
      @keydown=${ev.key}
    >
      <span class="dot"></span><span class="label">${step.name}</span>
      ${(st?.attemptCount ?? 0) > 1
        ? html`<span class="att">@${st!.latestAttempt}</span>`
        : nothing}
      ${blocked ? html`<span class="mark" title="blocked">${LOCK}</span>` : nothing}
    </div>`;
  }

  private _hovercard() {
    const c = this._card;
    if (!c || this._level !== 'chips') return nothing;
    const st = this.states?.get(c.step);
    const blocked = st?.blocked ?? this.blocked?.has(c.step) ?? false;
    const tone = st && st.status !== 'not-run' ? toneOf(st.status) : 'plain';
    const at = st?.finishedAt ?? st?.startedAt ?? st?.createdAt;
    const meta = [
      !st || st.status === 'not-run' ? 'never run' : '',
      st?.runNumber ? this._runLabel(st) : '',
      st?.runNumber && at ? formatRelative(new Date(at)) : '',
      st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '',
    ].filter(Boolean);
    const held = st?.heldRuns ?? [];
    return html`<div class="hovercard t-${tone}" role="tooltip">
      <div class="hc-name">
        ${c.step}${st && st.status !== 'not-run'
          ? html`<span class="badge">${statusLabel(st.status)}</span>`
          : nothing}
      </div>
      <div class="hc-meta">${meta.map((x) => html`<span>${x}</span>`)}</div>
      ${st?.reason ? html`<div class="reason">${st.reason}</div>` : nothing}
      ${blocked
        ? html`<div class="block-note">
            blocked${held.length
              ? ` · holding ${held.map((n) => `#${n}`).join(', ')}`
              : ''}
          </div>`
        : nothing}
    </div>`;
  }

  private _placeCard() {
    const el = this.renderRoot.querySelector<HTMLElement>('.hovercard');
    const r = this._card?.anchor;
    if (!el || !r) return;
    const left = Math.min(
      Math.max(8, r.left + r.width / 2 - el.offsetWidth / 2),
      innerWidth - el.offsetWidth - 8,
    );
    let top = r.bottom + 8;
    if (top + el.offsetHeight > innerHeight - 8) top = r.top - el.offsetHeight - 8;
    el.style.left = `${left}px`;
    el.style.top = `${top}px`;
  }

  private _tile(step: ManifestStep, rel: Related | null) {
    const st = this.states?.get(step.name);
    const blocked = st?.blocked ?? this.blocked?.has(step.name) ?? false;
    const tone = st && st.status !== 'not-run' ? toneOf(st.status) : 'plain';
    const duration = st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '';
    const at = st?.finishedAt ?? st?.startedAt ?? st?.createdAt;
    const held = st?.heldRuns ?? [];
    const ev = this._nodeEvents(step.name, false);
    return html`<div
      class="tile t-${tone} ${tone === 'plain' ? 'plain' : ''} ${this._relation(
        step.name,
        rel,
      )} ${this.selected === step.name ? 'selected' : ''}"
      role="button"
      tabindex="0"
      data-step=${step.name}
      aria-label="${step.name}${st ? `, ${statusLabel(st.status)}` : ''}"
      @mouseenter=${ev.enter}
      @mouseleave=${ev.leave}
      @focus=${ev.enter}
      @blur=${ev.leave}
      @click=${ev.click}
      @keydown=${ev.key}
    >
      <div class="head">
        <span class="name">${step.name}</span>
        ${st && st.status !== 'not-run'
          ? st.logsHref
            ? html`<a
                class="badge"
                href=${st.logsHref}
                @click=${this._link}
                @keydown=${this._link}
                >${statusLabel(st.status)}</a
              >`
            : html`<span class="badge">${statusLabel(st.status)}</span>`
          : nothing}
      </div>
      <div class="meta">
        ${!st || st.status === 'not-run' ? html`<span>never run</span>` : nothing}
        ${st?.runNumber ? this._run(st) : nothing}
        ${st?.runNumber && at
          ? html`<span title=${new Date(at).toLocaleString()}
              >${formatRelative(new Date(at))}</span
            >`
          : nothing}
        ${duration ? html`<span>${duration}</span>` : nothing}
        ${this.stepControls ? this._cancel(step.name, st) : nothing}
        ${this.stepControls ? this._retry(step.name, st) : nothing}
        ${this.stepControls ? this._lock(step.name, blocked) : nothing}
      </div>
      ${st?.reason ? html`<div class="reason">${st.reason}</div>` : nothing}
      ${blocked
        ? html`<div class="block-note">
            blocked${held.length
              ? ` · holding ${held.map((n) => `#${n}`).join(', ')}`
              : ''}
          </div>`
        : nothing}
      ${(this.cases?.[step.name] ?? []).map(
        (c) =>
          html`<div class="case" title="picked by the config">
            ${c.replace('=', ' = ')}
          </div>`,
      )}
    </div>`;
  }

  private _runLabel(st: StepState) {
    return `#${st.runNumber}${(st.attemptCount ?? 0) > 1 ? `@${st.latestAttempt}` : ''}`;
  }

  private _run(st: StepState) {
    if (!st.runHref) return html`<span>${this._runLabel(st)}</span>`;
    return html`<a
      class="run"
      href=${st.runHref}
      @click=${this._link}
      @keydown=${this._link}
      >${this._runLabel(st)}</a
    >`;
  }

  /** Links inside a tile must not also select the tile. */
  private _link(e: MouseEvent | KeyboardEvent) {
    e.stopPropagation();
    if (e instanceof MouseEvent) navClick(e);
  }

  private _cancel(step: string, st: StepState | undefined) {
    if (!st?.cancellable) return nothing;
    return html`<button
      class="icon stop"
      aria-label="Cancel ${step}"
      @click=${(e: Event) => this._emit(e, 'step-cancel', { step })}
    >
      ${STOP}
    </button>`;
  }

  private _retry(step: string, st: StepState | undefined) {
    if (!st?.retryable) return nothing;
    return html`<button
      class="icon retry"
      aria-label="Retry ${step}"
      @click=${(e: Event) => this._emit(e, 'step-retry', { step })}
    >
      ${RETRY}
    </button>`;
  }

  private _lock(step: string, blocked: boolean) {
    return html`<button
      class="icon lock ${blocked ? 'closed' : ''}"
      aria-label=${blocked ? `Unblock ${step}` : `Block ${step}`}
      @click=${(e: Event) => this._emit(e, 'block-toggle', { step, block: !blocked })}
    >
      ${LOCK}
    </button>`;
  }

  private _emit(e: Event, name: string, detail: unknown) {
    e.stopPropagation();
    this.dispatchEvent(new CustomEvent(name, { detail }));
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
