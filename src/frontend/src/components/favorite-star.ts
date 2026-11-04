import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { favorites, onFavoritesChanged, setFavorite } from '../model/favorites.js';

@customElement('favorite-star')
export class FavoriteStar extends LitElement {
  static styles = css`
    :host {
      display: inline-flex;
    }
    button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0.2rem;
      border: none;
      border-radius: var(--radius);
      background: none;
      color: var(--color-text-muted);
      cursor: pointer;
      transition: color var(--transition);
    }
    button:hover,
    button:focus-visible {
      color: var(--color-warning);
    }
    button[aria-pressed='true'] {
      color: var(--color-warning);
    }
    svg {
      width: 1.1rem;
      height: 1.1rem;
    }
  `;

  @property() declare pipelineId: string;
  @state() declare private _on: boolean;
  private _unsubscribe?: () => void;

  constructor() {
    super();
    this.pipelineId = '';
    this._on = false;
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._unsubscribe = onFavoritesChanged(() => this._sync());
    this._sync();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    this._unsubscribe?.();
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this._sync();
  }

  private _sync() {
    this._on = favorites().has(this.pipelineId);
  }

  render() {
    const label = this._on ? 'Remove from favorites' : 'Add to favorites';
    return html`<button
      type="button"
      aria-pressed=${this._on ? 'true' : 'false'}
      aria-label=${label}
      title=${label}
      @click=${this._toggle}
    >
      <svg
        viewBox="0 0 24 24"
        fill=${this._on ? 'currentColor' : 'none'}
        stroke="currentColor"
        stroke-width="2"
        stroke-linejoin="round"
        aria-hidden="true"
      >
        <path
          d="M12 2.5l2.94 5.96 6.56.95-4.75 4.63 1.12 6.54L12 17.5l-5.87 3.08 1.12-6.54L2.5 9.41l6.56-.95z"
        />
      </svg>
    </button>`;
  }

  private _toggle(e: Event) {
    e.stopPropagation();
    if (this.pipelineId) setFavorite(this.pipelineId, !this._on);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'favorite-star': FavoriteStar;
  }
}
