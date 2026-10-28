import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import {
  type Manifest,
  type ManifestStep,
  allSteps,
  producersOf,
  stepsByName,
} from '../v2/manifest.js';
import { sharedStyles, toneOf, statusLabel, navClick, runPath } from '../v2/ui.js';
import type { StepState } from './stage-graph.js';

/** Height of the sheet in px, as its CSS sets it. */
export function sheetHeight(): number {
  const rem = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
  return Math.min(((innerWidth <= 640 ? 48 : 42) * innerHeight) / 100, 26 * rem);
}

/** Styles for content the views slot into the sheet. */
export const sheetContentStyles = css`
  .sheet-h {
    margin: 1rem 0 0.35rem;
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--color-text-muted);
  }
  .kv {
    display: grid;
    grid-template-columns: max-content minmax(0, 1fr);
    gap: 0.15rem 1rem;
    margin: 0;
    font-size: 0.84rem;
  }
  .kv dt {
    color: var(--color-text-muted);
  }
  .kv dd {
    margin: 0;
    overflow-wrap: anywhere;
  }
`;

/**
 * A step's details in a bottom sheet over the page, full width. The element itself is an
 * in-flow spacer of the sheet's height, so the page keeps scrolling above it.
 * Slots: `actions`, `attempt` (first column), default (full width, below the columns).
 * Events: `close`, `step-select` {step}.
 */
@customElement('step-sheet')
export class StepSheet extends LitElement {
  static styles = [
    sharedStyles,
    sheetContentStyles,
    css`
      :host {
        display: block;
        height: calc(min(42vh, 26rem) + 1rem);
      }
      .sheet {
        position: fixed;
        z-index: 30;
        left: 0;
        right: 0;
        bottom: 0;
        height: min(42vh, 26rem);
        overflow-y: auto;
        padding: 0.9rem 1.5rem 1.2rem;
        background: var(--color-surface);
        border-top: 1px solid var(--color-border);
        border-radius: var(--radius-lg) var(--radius-lg) 0 0;
        box-shadow: 0 -6px 24px rgba(4, 32, 55, 0.14);
        animation: sheet-up 160ms ease-out;
      }
      .sheet::before {
        content: '';
        display: block;
        width: 2.5rem;
        height: 4px;
        margin: -0.4rem auto 0.6rem;
        border-radius: 2px;
        background: var(--color-border);
      }
      @keyframes sheet-up {
        from {
          transform: translateY(2rem);
          opacity: 0;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .sheet {
          animation: none;
        }
      }
      .detail {
        max-width: 1272px;
        margin: 0 auto;
        font-size: 0.85rem;
      }
      .d-head {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        flex-wrap: wrap;
      }
      .d-head h3 {
        margin: 0;
        font-size: 1.05rem;
        overflow-wrap: anywhere;
      }
      .d-close {
        margin-left: auto;
        border: 0;
        background: transparent;
        color: var(--color-text-muted);
        font-size: 1.4rem;
        line-height: 1;
        padding: 0.2rem 0.4rem;
        border-radius: var(--radius);
      }
      .d-close:hover {
        background: var(--color-surface-hover);
        color: var(--color-text);
      }
      .d-close:focus-visible {
        outline: 2px solid var(--color-primary);
      }
      .d-desc {
        margin: 0.35rem 0 0;
        color: var(--color-text-muted);
      }
      .d-reason {
        margin: 0.5rem 0 0;
        padding: 0.35rem 0.6rem;
        border-left: 3px solid var(--tone, var(--color-border));
        background: color-mix(in srgb, var(--tone, var(--color-border)) 8%, transparent);
        border-radius: 0 var(--radius) var(--radius) 0;
        font-size: 0.8rem;
      }
      .d-reason.t-wait {
        --tone: var(--color-warning);
      }
      .d-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 0.35rem;
        margin-top: 0.8rem;
      }
      .rel-list {
        display: flex;
        gap: 0.3rem;
        flex-wrap: wrap;
      }
      .rel-list button {
        border: 1px solid var(--color-border);
        background: var(--color-surface);
        color: var(--color-text);
        border-radius: 999px;
        padding: 0.05rem 0.55rem;
        font-size: 0.76rem;
      }
      .rel-list button:hover {
        border-color: var(--color-primary);
      }
      .blocked {
        color: var(--color-warning);
      }
      @media (min-width: 901px) {
        .detail {
          display: grid;
          grid-template-columns: repeat(3, minmax(0, 1fr));
          gap: 0 2rem;
        }
        .detail > .wide {
          grid-column: 1 / -1;
        }
      }
      @media (max-width: 640px) {
        :host {
          height: calc(min(48vh, 26rem) + 1rem);
        }
        .sheet {
          height: min(48vh, 26rem);
          padding: 0.9rem 1rem 1.2rem;
        }
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property({ attribute: false }) declare manifest: Manifest | null;
  @property() declare step: string;
  @property({ attribute: false }) declare state: StepState | undefined;

  constructor() {
    super();
    this.pipelineId = '';
    this.manifest = null;
    this.step = '';
    this.state = undefined;
  }

  connectedCallback() {
    super.connectedCallback();
    document.addEventListener('keydown', this._onKey);
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    document.removeEventListener('keydown', this._onKey);
  }

  private _onKey = (e: KeyboardEvent) => {
    if (e.key !== 'Escape' || e.defaultPrevented) return;
    // Escape in an open dialog closes the dialog, not the sheet.
    if (e.composedPath().some((n) => n instanceof HTMLDialogElement)) return;
    this._close();
  };

  render() {
    const st = this.state;
    const def = stepsByName(this.manifest).get(this.step);
    const held = st?.blocked ? (st.heldRuns ?? []) : [];
    const tone = st && st.status !== 'not-run' ? toneOf(st.status) : null;
    return html`<aside class="sheet" aria-label="Step ${this.step}">
      <div class="detail">
        <div class="d-head wide">
          <h3>${this.step}</h3>
          ${tone
            ? html`<span class="st t-${tone}"
                ><span class="dot"></span>${statusLabel(st!.status)}</span
              >`
            : html`<span class="muted">never run</span>`}
          ${st?.blocked ? html`<span class="pill blocked">blocked</span>` : nothing}
          <button class="d-close" aria-label="Close" @click=${this._close}>×</button>
        </div>
        ${def?.description
          ? html`<p class="d-desc wide">${def.description}</p>`
          : nothing}
        ${st?.reason
          ? html`<p class="d-reason wide t-${tone ?? 'idle'}">${st.reason}</p>`
          : nothing}
        ${held.length
          ? html`<p class="d-reason wide t-wait">
              Holding
              ${held.map(
                (n, i) =>
                  html`${i ? ', ' : ''}<a
                      href=${runPath(this.pipelineId, n)}
                      @click=${navClick}
                      >#${n}</a
                    >`,
              )}.
              Unblock to let ${held.length > 1 ? 'them' : 'it'} continue.
            </p>`
          : nothing}
        <div class="d-actions wide"><slot name="actions"></slot></div>
        <div><slot name="attempt"></slot></div>
        <div>${this._relations()}</div>
        <div>${def ? this._definition(def) : nothing}</div>
        <div class="wide"><slot></slot></div>
      </div>
    </aside>`;
  }

  private _relations() {
    const steps = allSteps(this.manifest?.stages ?? []);
    const def = steps.find((s) => s.name === this.step);
    const from = def ? [...producersOf(def)] : [];
    const by = steps.filter((s) => producersOf(s).has(this.step)).map((s) => s.name);
    const list = (names: string[]) =>
      names.length
        ? html`<div class="rel-list">
            ${names.map(
              (n) =>
                html`<button
                  @click=${() =>
                    this.dispatchEvent(
                      new CustomEvent('step-select', { detail: { step: n } }),
                    )}
                >
                  ${n}
                </button>`,
            )}
          </div>`
        : html`<span class="muted">—</span>`;
    return html`<h4 class="sheet-h">Reads from</h4>
      ${list(from)}
      <h4 class="sheet-h">Read by</h4>
      ${list(by)}`;
  }

  private _definition(def: ManifestStep) {
    const retries = typeof def.retries === 'number' ? def.retries : 0;
    return html`<h4 class="sheet-h">Definition</h4>
      <dl class="kv">
        <dt>Kind</dt>
        <dd>${def.kind}</dd>
        <dt>Image</dt>
        <dd class="mono">${def.image}</dd>
        ${def.output
          ? html`<dt>Output</dt>
              <dd class="mono">${def.output}</dd>`
          : nothing}
        ${def.timeoutSeconds
          ? html`<dt>Timeout</dt>
              <dd>${Math.round(def.timeoutSeconds / 60)} min</dd>`
          : nothing}
        ${retries
          ? html`<dt>Retries</dt>
              <dd>${retries}</dd>`
          : nothing}
        ${def.secrets?.length
          ? html`<dt>Secrets</dt>
              <dd class="mono">${def.secrets.join(', ')}</dd>`
          : nothing}
      </dl>`;
  }

  private _close = () => this.dispatchEvent(new CustomEvent('close'));
}

declare global {
  interface HTMLElementTagNameMap {
    'step-sheet': StepSheet;
  }
}
