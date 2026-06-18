import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';

/**
 * A timestamp that renders as relative time ("2 hours ago") by default and
 * toggles to an absolute local-time stamp ("2026/06/18 14:05") on a single
 * click. Each instance toggles independently. Times are always shown in the
 * viewer's local timezone; the wire value is a UTC `DateTimeOffset` (Kiota
 * surfaces it as a `Date`).
 */
@customElement('relative-time')
export class RelativeTime extends LitElement {
  static styles = css`
    :host {
      cursor: pointer;
      user-select: none;
    }
    .empty {
      cursor: default;
    }
  `;

  /** The instant to display. Accepts a `Date`, an ISO string, or null/undefined. */
  @property({ attribute: false }) declare value: Date | string | null | undefined;
  /** Show the absolute stamp instead of relative time. Toggled on click. */
  @state() declare private _absolute: boolean;

  constructor() {
    super();
    this.value = undefined;
    this._absolute = false;
  }

  private _toggle(e: Event) {
    e.stopPropagation();
    e.preventDefault();
    this._absolute = !this._absolute;
  }

  render() {
    const date = toDate(this.value);
    if (!date) return html`<span class="empty">—</span>`;

    const relative = formatRelative(date);
    const absolute = formatAbsolute(date);
    return html`<span title=${this._absolute ? relative : absolute} @click=${this._toggle}
        >${this._absolute ? absolute : relative}</span
      >${nothing}`;
  }
}

function toDate(value: Date | string | null | undefined): Date | null {
  if (!value) return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatRelative(date: Date): string {
  const sec = Math.round((Date.now() - date.getTime()) / 1000);
  const abs = Math.abs(sec);
  const suffix = sec >= 0 ? 'ago' : 'from now';
  if (abs < 45) return 'just now';

  const plural = (n: number, unit: string) =>
    `${n} ${unit}${n === 1 ? '' : 's'} ${suffix}`;

  const mins = Math.round(abs / 60);
  if (abs < 5400) return plural(Math.max(1, mins), 'minute'); // < 90 min
  const hours = Math.round(abs / 3600);
  if (abs < 79200) return plural(hours, 'hour'); // < 22 h
  const days = Math.round(abs / 86400);
  if (abs < 2160000) return plural(days, 'day'); // < 25 d
  const months = Math.round(abs / 2592000);
  if (abs < 29808000) return plural(months, 'month'); // < ~11.5 mo
  return plural(Math.round(abs / 31536000), 'year');
}

function formatAbsolute(date: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}/${p(date.getMonth() + 1)}/${p(date.getDate())} ${p(date.getHours())}:${p(date.getMinutes())}`;
}

declare global {
  interface HTMLElementTagNameMap {
    'relative-time': RelativeTime;
  }
}
