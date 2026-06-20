import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, extractErrorMessage } from '../api.js';

const ICON_BRAKE = svg`<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><line x1="8" y1="8" x2="16" y2="16" /></svg>`;
const ICON_RESTART = svg`<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 12a9 9 0 1 1-3-6.7" /><polyline points="21 3 21 9 15 9" /></svg>`;

/**
 * The per-processing-step promotion gate: a brake (block/unblock promotion into
 * the step) and a re-promote (redrive the step's last bundle). Operational
 * STATE, not GitOps config — mutable even on a bound pipeline.
 *
 * Shared between the pipeline flow (mounted on the promotion arrow into a
 * processing step) and the step-detail view. Owns its API calls and emits a
 * `changed` event after a successful mutation so the host can reload. `blocked`
 * mirrors the bulk/per-step promotion state; `hasBundle` comes from job history
 * (re-promote needs a prior bundle to redrive) — neither lives in the promotion
 * payload, so the host supplies them.
 */
@customElement('promotion-gate')
export class PromotionGate extends LitElement {
  static styles = css`
    :host {
      display: inline-flex;
    }

    .arrow-badges {
      display: inline-flex;
      align-items: center;
      gap: 0.25rem;
    }

    .arrow-badge {
      width: 1.25rem;
      height: 1.25rem;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border-radius: 999px;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text-muted);
      cursor: pointer;
      transition: border-color var(--transition), color var(--transition),
        background var(--transition), transform var(--transition),
        box-shadow var(--transition);
    }

    .arrow-badge svg {
      width: 0.72rem;
      height: 0.72rem;
    }

    .arrow-badge:hover:not(:disabled) {
      border-color: var(--color-primary);
      color: var(--color-primary);
      transform: translateY(-1px);
      box-shadow: 0 4px 10px rgba(4, 32, 55, 0.2), 0 1px 3px rgba(4, 32, 55, 0.12);
    }

    /* Brake when ACTIVE (promotion blocked): filled warning-gold. */
    .arrow-badge.brake.on {
      background: var(--color-warning);
      border-color: var(--color-warning);
      color: #fff;
    }

    .arrow-badge.brake.on:hover {
      filter: brightness(1.08);
      color: #fff;
    }

    /* Re-promote disabled while promotion is blocked — nothing to redrive into. */
    .arrow-badge.repromote:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }

    .arrow-badge.busy {
      opacity: 0.6;
      pointer-events: none;
    }
  `;

  @property() declare stepId: string;
  @property() declare stepName: string;
  @property({ type: Boolean }) declare blocked: boolean;
  @property({ type: Boolean }) declare hasBundle: boolean;

  @state() private declare _busy: boolean;

  constructor() {
    super();
    this.stepId = '';
    this.stepName = '';
    this.blocked = false;
    this.hasBundle = false;
    this._busy = false;
  }

  render() {
    const brakeClass = `arrow-badge brake${this.blocked ? ' on' : ''}${
      this._busy ? ' busy' : ''
    }`;
    return html`
      <div class="arrow-badges">
        <button
          type="button"
          class=${brakeClass}
          aria-pressed=${String(this.blocked)}
          ?disabled=${this._busy}
          title=${this.blocked
            ? `Promotion blocked — click to allow promotion into ${this.stepName}`
            : `Block promotion into ${this.stepName}`}
          @click=${this._toggleBrake}
        >
          ${ICON_BRAKE}
        </button>
        ${this.hasBundle
          ? html`<button
              type="button"
              class="arrow-badge repromote${this._busy ? ' busy' : ''}"
              ?disabled=${this.blocked || this._busy}
              title=${this.blocked
                ? 'Unblock promotion to re-promote'
                : `Re-promote ${this.stepName} with its last bundle`}
              @click=${this._rePromote}
            >
              ${ICON_RESTART}
            </button>`
          : nothing}
      </div>
    `;
  }

  private async _toggleBrake(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    if (this._busy) return;
    this._busy = true;
    try {
      const next = !this.blocked;
      await client.api.processingSteps
        .byStepId(this.stepId)
        .promotion.put({ blocked: next });
      this.blocked = next;
      this._emitChanged();
    } catch (err) {
      this._emitError(err);
    } finally {
      this._busy = false;
    }
  }

  private async _rePromote(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    if (this._busy || this.blocked) return;
    this._busy = true;
    try {
      await client.api.processingSteps.byStepId(this.stepId).rePromote.post();
      this._emitChanged();
    } catch (err) {
      this._emitError(err);
    } finally {
      this._busy = false;
    }
  }

  private _emitChanged() {
    this.dispatchEvent(
      new CustomEvent('changed', { bubbles: true, composed: true }),
    );
  }

  private _emitError(err: unknown) {
    const message = extractErrorMessage(err);
    this.dispatchEvent(
      new CustomEvent('gate-error', {
        detail: { message },
        bubbles: true,
        composed: true,
      }),
    );
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'promotion-gate': PromotionGate;
  }
}
