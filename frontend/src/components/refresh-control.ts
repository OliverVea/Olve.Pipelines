import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';

/**
 * A paired auto-refresh interval picker + manual refresh button, sized to sit
 * flush together in a view header.
 *
 * Owns its own polling timer: while a non-"Off" interval is selected and the
 * tab is visible, it fires a `refresh` event every N seconds with
 * `detail.silent === true`. The manual button fires `refresh` with
 * `detail.silent === false`. Switching back to a backgrounded tab triggers one
 * immediate silent refresh so you see fresh state on return.
 *
 * The chosen interval is persisted to localStorage and shared across every view
 * that uses this control, so the preference is set once and remembered.
 *
 * The parent passes `loading` (its load-in-flight flag) to spin the button.
 */

const STORAGE_KEY = 'olve.pipelines.refreshIntervalSeconds';
const DEFAULT_SECONDS = 10;

const OPTIONS: ReadonlyArray<{ label: string; value: number }> = [
  { label: 'Off', value: 0 },
  { label: 'Every 5s', value: 5 },
  { label: 'Every 10s', value: 10 },
  { label: 'Every 30s', value: 30 },
  { label: 'Every 1m', value: 60 },
];

function loadInterval(): number {
  const raw = localStorage.getItem(STORAGE_KEY);
  if (raw === null) return DEFAULT_SECONDS;
  const n = Number(raw);
  return OPTIONS.some((o) => o.value === n) ? n : DEFAULT_SECONDS;
}

@customElement('refresh-control')
export class RefreshControl extends LitElement {
  static styles = css`
    :host {
      display: inline-flex;
      align-items: center;
      gap: 0.4rem;
    }

    /* Interval picker — sized and bordered to match the reload button so the
       two read as one control cluster. */
    .interval-select {
      height: 2rem;
      padding: 0 1.75rem 0 0.6rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background-color: var(--color-surface);
      color: var(--color-text-muted);
      font-size: 0.85rem;
      cursor: pointer;
      appearance: none;
      background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23496c89' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpolyline points='6 9 12 15 18 9'/%3E%3C/svg%3E");
      background-repeat: no-repeat;
      background-position: right 0.45rem center;
      background-size: 0.85rem;
      transition:
        background-color var(--transition),
        border-color var(--transition);
    }
    .interval-select:hover {
      border-color: var(--color-primary);
    }
    .interval-select:focus-visible {
      outline: 2px solid var(--color-primary);
      outline-offset: 1px;
    }

    .reload-btn {
      width: 2rem;
      height: 2rem;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      cursor: pointer;
      transition:
        background var(--transition),
        border-color var(--transition);
    }
    .reload-btn:hover:not(:disabled) {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }
    .reload-btn:disabled {
      opacity: 0.6;
      cursor: default;
    }
    .reload-btn svg {
      width: 1rem;
      height: 1rem;
    }
    .reload-btn.loading svg {
      animation: spin 0.8s linear infinite;
    }
    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }
  `;

  /** Parent's load-in-flight flag — spins and disables the button while true. */
  @property({ type: Boolean }) declare loading: boolean;

  @state() declare private _seconds: number;

  private _timer?: number;

  constructor() {
    super();
    this.loading = false;
    this._seconds = loadInterval();
  }

  connectedCallback(): void {
    super.connectedCallback();
    document.addEventListener('visibilitychange', this._onVisibility);
    this._restartTimer();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    document.removeEventListener('visibilitychange', this._onVisibility);
    this._stopTimer();
  }

  private _onVisibility = (): void => {
    if (!document.hidden && this._seconds > 0) this._emit(true);
  };

  private _stopTimer(): void {
    if (this._timer !== undefined) {
      window.clearInterval(this._timer);
      this._timer = undefined;
    }
  }

  private _restartTimer(): void {
    this._stopTimer();
    if (this._seconds > 0) {
      this._timer = window.setInterval(() => {
        if (!document.hidden) this._emit(true);
      }, this._seconds * 1000);
    }
  }

  private _emit(silent: boolean): void {
    this.dispatchEvent(
      new CustomEvent('refresh', { detail: { silent }, bubbles: true, composed: true }),
    );
  }

  private _onSelect(e: Event): void {
    const value = Number((e.target as HTMLSelectElement).value);
    this._seconds = value;
    localStorage.setItem(STORAGE_KEY, String(value));
    this._restartTimer();
  }

  private _onClick(): void {
    this._emit(false);
  }

  render() {
    return html`
      <select
        class="interval-select"
        aria-label="Auto-refresh interval"
        title="Auto-refresh interval"
        .value=${String(this._seconds)}
        @change=${this._onSelect}
      >
        ${OPTIONS.map(
          (o) => html`<option value=${o.value} ?selected=${o.value === this._seconds}>
            ${o.label}
          </option>`,
        )}
      </select>
      <button
        class="reload-btn ${this.loading ? 'loading' : ''}"
        @click=${this._onClick}
        ?disabled=${this.loading}
        title="Refresh now"
        aria-label="Refresh now"
      >
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path d="M21 12a9 9 0 1 1-3-6.7" />
          <polyline points="21 3 21 9 15 9" />
        </svg>
      </button>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'refresh-control': RefreshControl;
  }
}
