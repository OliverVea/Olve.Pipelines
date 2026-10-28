import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type { AttemptPhases } from '@olve/olve-pipelines-client/src/models/index.js';
import { formatSeconds } from '../v2/ui.js';

const PHASES: [keyof AttemptPhases & string, string][] = [
  ['queued', 'Queued'],
  ['podStart', 'Pod start'],
  ['fetch', 'Fetch'],
  ['image', 'Image'],
  ['active', 'Active'],
  ['notice', 'Notice'],
  ['collect', 'Collect'],
];

/** Where an attempt's time went: a stacked bar of its phases, optionally scaled against `max` seconds. */
@customElement('phase-bar')
export class PhaseBar extends LitElement {
  static styles = css`
    :host {
      display: block;
      min-width: 6rem;
    }
    .bar {
      display: flex;
      height: 0.55rem;
      border-radius: 999px;
      overflow: hidden;
      background: var(--color-surface-hover);
    }
    .bar span {
      height: 100%;
    }
    .queued {
      background: color-mix(in srgb, var(--color-text-muted) 35%, transparent);
    }
    .podStart {
      background: var(--gold-200);
    }
    .fetch {
      background: var(--coral-200);
    }
    .image {
      background: var(--blue-100);
    }
    .active {
      background: var(--color-success);
    }
    .notice {
      background: var(--coral-300);
    }
    .collect {
      background: var(--gold-300);
    }
    .legend {
      display: flex;
      flex-wrap: wrap;
      gap: 0.15rem 0.7rem;
      margin-top: 0.35rem;
      font-size: 0.72rem;
      color: var(--color-text-muted);
    }
    .legend i {
      display: inline-block;
      width: 0.55rem;
      height: 0.55rem;
      margin-right: 0.25rem;
      border-radius: 2px;
      vertical-align: -0.05rem;
    }
  `;

  @property({ attribute: false }) declare phases: AttemptPhases | null | undefined;
  /** Seconds the full width stands for; default: this attempt's total. */
  @property({ type: Number }) declare max: number | undefined;
  @property({ type: Boolean }) declare legend: boolean;

  constructor() {
    super();
    this.phases = null;
    this.max = undefined;
    this.legend = false;
  }

  render() {
    const p = this.phases;
    if (!p || p.active == null) return html`<span class="legend">—</span>`;
    const parts = PHASES.map(([k, label]) => ({
      k,
      label,
      s: (p[k] as number | null) ?? 0,
    }));
    const total = parts.reduce((a, x) => a + x.s, 0);
    const width = Math.max(this.max ?? total, total, 0.001);
    const title = parts.map((x) => `${x.label} ${formatSeconds(x.s)}`).join(' · ');
    return html`<div class="bar" title=${title} role="img" aria-label=${title}>
        ${parts.map((x) =>
          x.s > 0
            ? html`<span class="${x.k}" style="width:${(100 * x.s) / width}%"></span>`
            : nothing,
        )}
      </div>
      ${this.legend
        ? html`<div class="legend">
            ${parts.map(
              (x) =>
                html`<span><i class="${x.k}"></i>${x.label} ${formatSeconds(x.s)}</span>`,
            )}
          </div>`
        : nothing}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'phase-bar': PhaseBar;
  }
}
