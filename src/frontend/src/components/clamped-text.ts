import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';

/** Text cut to one line with an ellipsis, and a "more" toggle when it doesn't fit. */
@customElement('clamped-text')
export class ClampedText extends LitElement {
  static styles = css`
    :host {
      display: flex;
      align-items: baseline;
      gap: 0.4rem;
      min-width: 0;
    }
    p {
      margin: 0;
      min-width: 0;
    }
    :host(:not([open])) p {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    button {
      flex: none;
      padding: 0 0.2rem;
      border: 0;
      background: transparent;
      color: var(--color-primary);
      font: inherit;
      cursor: pointer;
    }
    button:hover {
      text-decoration: underline;
    }
    button:focus-visible {
      outline: 2px solid var(--color-primary);
      border-radius: var(--radius);
    }
  `;

  @property() declare text: string;
  @property({ type: Boolean, reflect: true }) declare open: boolean;
  @state() declare private _overflows: boolean;
  @query('p') declare private _p: HTMLParagraphElement | null;

  private _resize = new ResizeObserver(() => this._measure());

  constructor() {
    super();
    this.text = '';
    this.open = false;
    this._overflows = false;
  }

  connectedCallback() {
    super.connectedCallback();
    this._resize.observe(this);
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    this._resize.disconnect();
  }

  updated() {
    this._measure();
  }

  render() {
    return html`<p>${this.text}</p>
      ${this._overflows || this.open
        ? html`<button
            aria-expanded=${this.open}
            @click=${() => (this.open = !this.open)}
          >
            ${this.open ? 'less' : 'more'}
          </button>`
        : nothing}`;
  }

  private _measure() {
    if (this.open || !this._p) return;
    const overflows = this._p.scrollWidth > this._p.clientWidth;
    if (overflows !== this._overflows) this._overflows = overflows;
  }
}
