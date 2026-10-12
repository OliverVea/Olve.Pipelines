import { LitElement, html, css, svg } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, extractErrorMessage } from '../api.js';
import type {
  PromotionBundle,
  PromotionOption,
  PromotionSource,
  StepPromotionState,
  UnblockPromotion,
} from '@olve/olve-pipelines-client/src/models/index.js';

const ICON_BRAKE = svg`<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><line x1="8" y1="8" x2="16" y2="16" /></svg>`;
const ICON_PROMOTE = svg`<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="5 12 19 12" /><polyline points="13 6 19 12 13 18" /></svg>`;

/** The bundle of an option, or null. Kiota may hand back `{}` for a JSON null, so check the id. */
export function optionBundle(
  option: PromotionOption | null | undefined,
): PromotionBundle | null {
  const b = option?.bundle as PromotionBundle | null | undefined;
  return option?.allowed === true && b?.artifactBundleId ? b : null;
}

/** "build 2026-10-12 05:49:21 UTC (3f2a9c1e)" — the build time and short bundle id the server records. */
export function bundleLabel(b: PromotionBundle): string {
  const built = b.builtAt
    ? `${b.builtAt.toISOString().slice(0, 19).replace('T', ' ')} UTC`
    : 'unknown time';
  const id = String(b.artifactBundleId ?? '')
    .replace(/-/g, '')
    .slice(0, 8);
  return `build ${built} (${id})`;
}

/** What unblocking would start (null when the step is not blocked). */
function unblockOption(p: StepPromotionState | null): PromotionOption | null {
  return (p?.onUnblock as PromotionOption | null | undefined) ?? null;
}

export function sourceLabel(source: PromotionSource | null | undefined): string {
  if (!source) return 'the previous step';
  return source.isProduction ? 'production' : (source.name ?? 'the previous step');
}

/**
 * The per-processing-step promotion gate: a brake (block/unblock promotion into
 * the step) and Promote (run the step with the bundle that last succeeded at the
 * step before it). Operational STATE, not GitOps config — mutable even on a
 * bound pipeline.
 *
 * `promotion` is the server's read model for the step (what Promote would use
 * now, and what unblocking would start); the buttons and confirmations name
 * exactly that build, and Promote sends its id back as `expectedArtifactBundleId`
 * so the server refuses if the build changed in between.
 *
 * `variant="compact"` renders round icon badges for the promotion arrow in the
 * pipeline flow (the build is named in the tooltip); `variant="full"` renders
 * labelled buttons for the step-detail page. Emits `changed` after a successful
 * mutation, `gate-notice` with the server's message (which build started, or why
 * none), and `gate-error` on failure.
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
      transition:
        border-color var(--transition),
        color var(--transition),
        background var(--transition),
        transform var(--transition),
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
      box-shadow:
        0 4px 10px rgba(4, 32, 55, 0.2),
        0 1px 3px rgba(4, 32, 55, 0.12);
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

    /* Promote disabled when there is nothing to promote (the tooltip says why). */
    .arrow-badge.promote:disabled,
    .text-button:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }

    .arrow-badge.busy,
    .text-button.busy {
      opacity: 0.6;
      pointer-events: none;
    }

    .full {
      display: inline-flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.4rem;
    }

    .text-button {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      padding: 0.15rem 0.6rem;
      border-radius: 999px;
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font: inherit;
      font-size: 0.8rem;
      cursor: pointer;
    }

    .text-button svg {
      width: 0.8rem;
      height: 0.8rem;
    }

    .text-button:hover:not(:disabled) {
      border-color: var(--color-primary);
      color: var(--color-primary);
    }

    .text-button.brake.on {
      background: var(--color-warning);
      border-color: var(--color-warning);
      color: #fff;
    }

    .why {
      font-size: 0.8rem;
      color: var(--color-text-muted);
    }
  `;

  @property() declare stepId: string;
  @property() declare stepName: string;
  @property({ type: Boolean }) declare blocked: boolean;
  @property({ attribute: false }) declare promotion: StepPromotionState | null;
  @property() declare variant: 'compact' | 'full';

  @state() declare private _busy: boolean;

  constructor() {
    super();
    this.stepId = '';
    this.stepName = '';
    this.blocked = false;
    this.promotion = null;
    this.variant = 'compact';
    this._busy = false;
  }

  /** "Promote build … (…) from test-after-beta", or why Promote is unavailable. */
  private _promoteText(): { enabled: boolean; text: string } {
    const option = this.promotion?.promote;
    const bundle = optionBundle(option);
    if (bundle) {
      return {
        enabled: true,
        text: `Promote ${bundleLabel(bundle)} from ${sourceLabel(this.promotion?.source)}`,
      };
    }
    return {
      enabled: false,
      text: option?.message ?? 'Nothing to promote (promotion state unavailable)',
    };
  }

  private _brakeTitle(): string {
    if (!this.blocked) return `Block promotion into ${this.stepName}`;
    const bundle = optionBundle(unblockOption(this.promotion));
    return bundle
      ? `Promotion blocked — click to unblock; this starts ${bundleLabel(bundle)} from ${sourceLabel(this.promotion?.source)}`
      : `Promotion blocked — click to unblock${
          unblockOption(this.promotion)?.message
            ? ` (no promotion will start: ${unblockOption(this.promotion)?.message})`
            : ''
        }`;
  }

  render() {
    const promote = this._promoteText();
    const busy = this._busy ? ' busy' : '';

    if (this.variant === 'full') {
      return html`
        <div class="full">
          <button
            type="button"
            class="text-button brake${this.blocked ? ' on' : ''}${busy}"
            aria-pressed=${String(this.blocked)}
            ?disabled=${this._busy}
            title=${this._brakeTitle()}
            @click=${this._toggleBrake}
          >
            ${ICON_BRAKE} ${this.blocked ? 'Unblock' : 'Block'}
          </button>
          <button
            type="button"
            class="text-button promote${busy}"
            ?disabled=${!promote.enabled || this._busy}
            title=${promote.text}
            @click=${this._promote}
          >
            ${ICON_PROMOTE} ${promote.enabled ? promote.text : 'Promote'}
          </button>
          ${promote.enabled ? '' : html`<span class="why">${promote.text}</span>`}
        </div>
      `;
    }

    return html`
      <div class="arrow-badges">
        <button
          type="button"
          class="arrow-badge brake${this.blocked ? ' on' : ''}${busy}"
          aria-pressed=${String(this.blocked)}
          aria-label=${this._brakeTitle()}
          ?disabled=${this._busy}
          title=${this._brakeTitle()}
          @click=${this._toggleBrake}
        >
          ${ICON_BRAKE}
        </button>
        <button
          type="button"
          class="arrow-badge promote${busy}"
          aria-label=${promote.text}
          ?disabled=${!promote.enabled || this._busy}
          title=${promote.text}
          @click=${this._promote}
        >
          ${ICON_PROMOTE}
        </button>
      </div>
    `;
  }

  private async _toggleBrake(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    if (this._busy) return;
    const next = !this.blocked;

    if (!next) {
      const bundle = optionBundle(unblockOption(this.promotion));
      const question = bundle
        ? `Unblock ${this.stepName}?\n\nThis starts promotion of ${bundleLabel(bundle)} from ${sourceLabel(this.promotion?.source)} into ${this.stepName}.`
        : `Unblock ${this.stepName}?\n\nNo promotion will start: ${
            unblockOption(this.promotion)?.message ??
            'the promotion state is unavailable.'
          }`;
      if (!window.confirm(question)) return;
    }

    this._busy = true;
    try {
      const result = await client.api.processingSteps
        .byStepId(this.stepId)
        .promotion.put({ blocked: next });
      this.blocked = next;
      // The server re-checks at unblock time and reports what it actually started.
      const promotion = result?.promotion as UnblockPromotion | null | undefined;
      if (!next && promotion?.message) this._emitNotice(promotion.message);
      this._emitChanged();
    } catch (err) {
      this._emitError(err);
    } finally {
      this._busy = false;
    }
  }

  private async _promote(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    const bundle = optionBundle(this.promotion?.promote);
    if (this._busy || !bundle) return;
    if (
      !window.confirm(
        `Promote ${bundleLabel(bundle)} from ${sourceLabel(this.promotion?.source)} into ${this.stepName}?`,
      )
    )
      return;

    this._busy = true;
    try {
      const result = await client.api.processingSteps.byStepId(this.stepId).promote.post({
        queryParameters: { expectedArtifactBundleId: String(bundle.artifactBundleId) },
      });
      if (result?.message) this._emitNotice(result.message);
      this._emitChanged();
    } catch (err) {
      this._emitError(err);
    } finally {
      this._busy = false;
    }
  }

  private _emitChanged() {
    this.dispatchEvent(new CustomEvent('changed', { bubbles: true, composed: true }));
  }

  private _emitNotice(message: string) {
    this.dispatchEvent(
      new CustomEvent('gate-notice', {
        detail: { message },
        bubbles: true,
        composed: true,
      }),
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
