import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  type Manifest,
  type ManifestStep,
  type Stage,
  type MappingLeaf,
  isWhen,
  stepWiring,
  producersOf,
  readsInput,
  whenMatches,
  formatWhen,
  inputTypeLabel,
  allSteps,
} from '../v2/manifest.js';
import { sharedStyles, toneOf, statusLabel, formatDuration } from '../v2/ui.js';

/** Per-step state overlaid on the graph in the run view. */
export type StepState = {
  status: string;
  reason?: string | null;
  attemptCount?: number;
  latestAttempt?: number | null;
  startedAt?: Date | string | null;
  finishedAt?: Date | string | null;
  blocked?: boolean;
};

/**
 * The pipeline's stages left to right: each column is a stage whose steps run
 * in parallel, the rule between columns is the stage barrier, and a `when`
 * block is a bracket around the stages it guards, labelled with its condition.
 * Hovering a step highlights where its inputs come from (and what reads its
 * output); "Show wiring" lists the field-level mapping on every step.
 *
 * Events: `step-select` {step}, `gate-toggle` {step, block}.
 */
@customElement('stage-graph')
export class StageGraph extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        display: block;
        --graph-bg: var(--color-bg);
      }
      .toolbar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.75rem;
        margin-bottom: 0.6rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .toolbar label {
        display: inline-flex;
        align-items: center;
        gap: 0.3rem;
        cursor: pointer;
      }
      @media (max-width: 640px) {
        .toolbar .hint {
          display: none;
        }
      }
      .legend {
        display: flex;
        flex-wrap: wrap;
        gap: 0.2rem 0.75rem;
        margin-left: auto;
      }
      .scroller {
        overflow-x: auto;
        padding: 0.8rem 0 0.4rem;
      }
      .row {
        display: flex;
        align-items: stretch;
        gap: 0;
        min-width: min-content;
      }
      .col {
        display: flex;
        flex-direction: column;
        justify-content: center;
        gap: 0.5rem;
        padding: 0.25rem 0;
      }
      /* The stage barrier: the next stage starts only when this one is done. */
      .barrier {
        position: relative;
        width: 1.6rem;
        flex-shrink: 0;
        align-self: stretch;
      }
      .barrier::before {
        content: '';
        position: absolute;
        left: 50%;
        top: 0.4rem;
        bottom: 0.4rem;
        border-left: 1px solid var(--color-border);
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
        background: var(--graph-bg);
      }
      .when {
        display: flex;
        flex-direction: column;
        border: 1.5px dashed var(--color-when);
        border-radius: var(--radius-lg);
        padding: 0 0.6rem 0.5rem;
        background: color-mix(in srgb, var(--color-when) 5%, transparent);
      }
      .when-label {
        align-self: flex-start;
        margin: -0.7rem 0 0.25rem;
        padding: 0 0.4rem;
        background: var(--graph-bg);
        color: var(--color-when);
        font-size: 0.75rem;
        font-weight: 600;
        white-space: nowrap;
      }
      .when-label code {
        font-weight: 500;
      }
      .when.plan-skip {
        opacity: 0.45;
        border-color: var(--color-text-muted);
      }
      .when.plan-skip .when-label {
        color: var(--color-text-muted);
      }
      .when-body {
        display: flex;
        align-items: stretch;
        flex: 1;
      }

      .tile {
        position: relative;
        width: 11.5rem;
        padding: 0.45rem 0.6rem 0.5rem;
        background: var(--color-surface);
        border: 1px solid var(--color-border);
        border-left: 3px solid var(--tone, var(--color-border));
        border-radius: var(--radius);
        text-align: left;
        color: var(--color-text);
        font: inherit;
        cursor: pointer;
        transition:
          opacity var(--transition),
          border-color var(--transition),
          box-shadow var(--transition);
      }
      .tile:hover {
        border-color: var(--color-primary);
        border-left-color: var(--tone, var(--color-primary));
      }
      .tile.selected {
        box-shadow: 0 0 0 2px var(--color-primary);
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
      .tile.input-node {
        width: auto;
        min-width: 7rem;
        border-left-color: var(--color-primary);
        cursor: default;
      }
      .tile.plain {
        --tone: var(--color-border);
      }
      .t-skip.tile,
      .t-idle.tile,
      .t-off.tile {
        background: transparent;
      }
      .t-off.tile .name {
        text-decoration: line-through;
        text-decoration-color: var(--color-text-muted);
      }
      .t-idle.tile {
        border-style: dashed;
        border-left-style: solid;
      }
      .head {
        display: flex;
        align-items: center;
        gap: 0.4rem;
      }
      .name {
        font-weight: 600;
        font-size: 0.88rem;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        flex: 1;
      }
      .ver {
        font-size: 0.7rem;
        color: var(--color-text-muted);
        font-family: var(--font-mono);
      }
      .meta {
        margin-top: 0.15rem;
        font-size: 0.74rem;
        color: var(--color-text-muted);
        display: flex;
        gap: 0.5rem;
        flex-wrap: wrap;
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
      .gate {
        position: absolute;
        top: 0.3rem;
        right: 0.3rem;
      }
      .gate-btn {
        border: 1px solid transparent;
        background: transparent;
        color: var(--color-text-muted);
        border-radius: var(--radius);
        padding: 0.05rem 0.25rem;
        font-size: 0.72rem;
        line-height: 1.2;
      }
      .gate-btn:hover {
        border-color: var(--color-border);
        color: var(--color-text);
      }
      .gate-btn.closed {
        color: var(--color-warning);
        border-color: color-mix(in srgb, var(--color-warning) 50%, transparent);
        background: color-mix(in srgb, var(--color-warning) 10%, transparent);
      }
      .gated-note {
        margin-top: 0.2rem;
        font-size: 0.72rem;
        color: var(--color-warning);
      }
      .wiring {
        margin: 0.35rem 0 0;
        padding: 0.3rem 0 0;
        border-top: 1px solid var(--color-border);
        list-style: none;
        font-size: 0.72rem;
        font-family: var(--font-mono);
        line-height: 1.45;
      }
      .wiring li {
        display: flex;
        gap: 0.3rem;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .wiring .to {
        color: var(--color-text);
      }
      .wiring .arrow {
        color: var(--color-text-muted);
      }
      .wiring .from {
        color: var(--color-primary);
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .wiring .const {
        color: var(--color-when);
      }
      .wiring .type {
        color: var(--color-text-muted);
        margin-left: auto;
        padding-left: 0.4rem;
      }
    `,
  ];

  @property({ attribute: false }) declare manifest: Manifest | null;
  @property({ attribute: false }) declare states: Map<string, StepState> | null;
  @property({ attribute: false }) declare stepVersions: Record<string, number> | null;
  @property({ attribute: false }) declare blocked: Set<string> | null;
  @property({ attribute: false }) declare planInput: Record<string, unknown> | null;
  @property() declare selected: string;
  @property({ type: Boolean }) declare gateControls: boolean;

  @state() private declare _hover: string | null;
  @state() private declare _wiring: boolean;

  constructor() {
    super();
    this.manifest = null;
    this.states = null;
    this.stepVersions = null;
    this.blocked = null;
    this.planInput = null;
    this.selected = '';
    this.gateControls = false;
    this._hover = null;
    try {
      this._wiring = localStorage.getItem('stage-graph.wiring') === '1';
    } catch {
      this._wiring = false;
    }
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No manifest.</p>`;
    const highlight = this._highlight(m);
    return html`
      <div class="toolbar">
        <label
          ><input type="checkbox" .checked=${this._wiring} @change=${this._toggleWiring} /> Show wiring</label
        >
        <span class="hint">Hover a step to trace its inputs.</span>
        ${this.states
          ? html`<span class="legend">
              ${['succeeded', 'failed', 'in-progress', 'held', 'skipped', 'pending', 'cancelled'].map(
                (s) => html`<span class="st t-${toneOf(s)}"><span class="dot"></span>${statusLabel(s)}</span>`,
              )}
            </span>`
          : nothing}
      </div>
      <div class="scroller">
        <div class="row">
          ${this._inputNode(m, highlight)}
          <div class="barrier" aria-hidden="true"></div>
          ${this._stages(m.stages, highlight)}
        </div>
      </div>
    `;
  }

  private _highlight(m: Manifest): { src: Set<string>; dst: Set<string>; input: boolean } | null {
    if (!this._hover) return null;
    const steps = allSteps(m.stages);
    if (this._hover === '#input') {
      return { src: new Set(), dst: new Set(steps.filter(readsInput).map((s) => s.name)), input: false };
    }
    const hovered = steps.find((s) => s.name === this._hover);
    if (!hovered) return null;
    const dst = new Set(steps.filter((s) => producersOf(s).has(hovered.name)).map((s) => s.name));
    return { src: producersOf(hovered), dst, input: readsInput(hovered) };
  }

  private _inputNode(m: Manifest, hl: ReturnType<StageGraph['_highlight']>) {
    const cls = hl ? (hl.input ? 'src' : this._hover === '#input' ? '' : 'dim') : '';
    return html`<div class="col">
      <div
        class="tile input-node ${cls}"
        @mouseenter=${() => (this._hover = '#input')}
        @mouseleave=${() => (this._hover = null)}
        title="Pipeline input: ${m.input}"
      >
        <div class="head"><span class="name">input</span></div>
        <div class="meta"><span>${m.input}</span></div>
      </div>
    </div>`;
  }

  private _stages(stages: Stage[], hl: ReturnType<StageGraph['_highlight']>): TemplateResult[] {
    const out: TemplateResult[] = [];
    stages.forEach((stage, i) => {
      if (i > 0) out.push(html`<div class="barrier" aria-hidden="true"></div>`);
      if (isWhen(stage)) {
        const planSkip = this.planInput ? !whenMatches(stage.when, this.planInput) : false;
        out.push(html`<div class="when ${planSkip ? 'plan-skip' : ''}">
          <span class="when-label" title="Runs only when the pipeline input matches"
            >when <code>${formatWhen(stage.when)}</code>${planSkip ? ' — won’t run' : ''}</span
          >
          <div class="when-body">${this._stages(stage.stages, hl)}</div>
        </div>`);
      } else {
        out.push(html`<div class="col">${stage.steps.map((s) => this._tile(s, hl))}</div>`);
      }
    });
    return out;
  }

  private _tile(step: ManifestStep, hl: ReturnType<StageGraph['_highlight']>) {
    const st = this.states?.get(step.name);
    const blocked = st?.blocked ?? this.blocked?.has(step.name) ?? false;
    const tone = st ? toneOf(st.status) : 'plain';
    let rel = '';
    if (hl) {
      if (this._hover === step.name) rel = '';
      else if (hl.src.has(step.name)) rel = 'src';
      else if (hl.dst.has(step.name)) rel = 'dst';
      else rel = 'dim';
    }
    const version = this.stepVersions?.[step.name];
    const wiring = stepWiring(step);
    const duration = st?.startedAt ? formatDuration(st.startedAt, st.finishedAt) : '';
    return html`<div
      class="tile t-${tone} ${tone === 'plain' ? 'plain' : ''} ${rel} ${this.selected === step.name ? 'selected' : ''}"
      role="button"
      tabindex="0"
      aria-label="${step.name}${st ? `, ${statusLabel(st.status)}` : ''}"
      title=${this._tooltip(step, wiring)}
      @mouseenter=${() => (this._hover = step.name)}
      @mouseleave=${() => (this._hover = null)}
      @focus=${() => (this._hover = step.name)}
      @blur=${() => (this._hover = null)}
      @click=${() => this._select(step.name)}
      @keydown=${(e: KeyboardEvent) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          this._select(step.name);
        }
      }}
    >
      <div class="head">
        ${st ? html`<span class="dot t-${toneOf(st.status)}"></span>` : nothing}
        <span class="name">${step.name}</span>
        ${version !== undefined ? html`<span class="ver" title="Step version">.${version}</span>` : nothing}
        ${this.gateControls ? html`<span style="width:1.4rem"></span>` : nothing}
      </div>
      <div class="meta">
        ${st
          ? html`<span>${statusLabel(st.status)}</span>${duration ? html`<span>${duration}</span>` : nothing}${(st.attemptCount ?? 0) > 1
                ? html`<span title="Attempts">@${st.latestAttempt}</span>`
                : nothing}`
          : html`<span title="Output model">${step.output ? `→ ${step.output}` : 'no output'}</span>`}
      </div>
      ${st?.reason ? html`<div class="reason" title=${st.reason}>${st.reason}</div>` : nothing}
      ${blocked && !this.gateControls ? html`<div class="gated-note">gate closed</div>` : nothing}
      ${this.gateControls
        ? html`<span class="gate"
            ><button
              class="gate-btn ${blocked ? 'closed' : ''}"
              title=${blocked ? 'Gate closed: runs hold before this step. Click to open.' : 'Close the gate: hold runs before this step.'}
              aria-label=${blocked ? `Unblock ${step.name}` : `Block ${step.name}`}
              @click=${(e: Event) => {
                e.stopPropagation();
                this.dispatchEvent(new CustomEvent('gate-toggle', { detail: { step: step.name, block: !blocked } }));
              }}
            >
              ${blocked ? 'blocked' : 'gate'}
            </button></span
          >`
        : nothing}
      ${this._wiring ? this._wiringList(step, wiring) : nothing}
    </div>`;
  }

  private _wiringList(step: ManifestStep, wiring: MappingLeaf[]) {
    if (!wiring.length) return html`<ul class="wiring"><li class="muted">no inputs</li></ul>`;
    return html`<ul class="wiring">
      ${wiring.map((leaf) => {
        const to = leaf.path.join('.');
        const type = leaf.path.length === 1 ? inputTypeLabel(step.inputTypes?.[leaf.path[0]]) : '';
        return html`<li>
          <span class="to">${to}</span><span class="arrow">←</span>${leaf.kind === 'ref'
            ? html`<span class="from">${leaf.ref}</span>`
            : html`<span class="const">${JSON.stringify(leaf.value)}</span>`}${type
            ? html`<span class="type">${type}</span>`
            : nothing}
        </li>`;
      })}
      ${step.output ? html`<li><span class="arrow">→</span><span class="to">${step.output}</span></li>` : nothing}
    </ul>`;
  }

  private _tooltip(step: ManifestStep, wiring: MappingLeaf[]): string {
    const lines = [`${step.name} (${step.kind}) — ${step.image}`];
    for (const leaf of wiring) {
      lines.push(`  ${leaf.path.join('.')} ← ${leaf.kind === 'ref' ? leaf.ref : JSON.stringify(leaf.value)}`);
    }
    lines.push(`  → ${step.output ?? 'no output'}`);
    if (step.secrets?.length) lines.push(`  secrets: ${step.secrets.join(', ')}`);
    return lines.join('\n');
  }

  private _select(step: string) {
    this.dispatchEvent(new CustomEvent('step-select', { detail: { step } }));
  }

  private _toggleWiring(e: Event) {
    this._wiring = (e.target as HTMLInputElement).checked;
    try {
      localStorage.setItem('stage-graph.wiring', this._wiring ? '1' : '0');
    } catch {
      /* per-viewer convenience only */
    }
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'stage-graph': StageGraph;
  }
}
