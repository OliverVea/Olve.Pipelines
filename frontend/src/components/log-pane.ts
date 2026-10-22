import { LitElement, html, css } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { AnsiUp } from 'ansi_up';
import { apiEvents, apiText, extractErrorMessage } from '../api.js';
import { sharedStyles } from '../v2/ui.js';

const ansi = new AnsiUp();
ansi.use_classes = false;

/**
 * Logs of one attempt. While `live`, follows the log stream (falling back to re-fetching every few
 * seconds) and keeps the view pinned to the bottom unless the reader has scrolled up.
 */
@customElement('log-pane')
export class LogPane extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .bar {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        margin-bottom: 0.35rem;
        font-size: 0.75rem;
        color: var(--color-text-muted);
      }
      pre {
        margin: 0;
        max-height: var(--log-max-height, 28rem);
        overflow: auto;
        padding: 0.6rem 0.75rem;
        background: var(--color-code-bg);
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        font-family: var(--font-mono);
        font-size: 0.76rem;
        line-height: 1.45;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }
    `,
  ];

  @property() declare url: string;
  @property({ type: Boolean }) declare live: boolean;

  @state() private declare _text: string;
  @state() private declare _error: string | null;
  @state() private declare _loading: boolean;
  @query('pre') private declare _pre: HTMLPreElement | null;

  private _timer?: ReturnType<typeof setInterval>;
  private _stream?: AbortController;
  private _loadedUrl = '';

  constructor() {
    super();
    this.url = '';
    this.live = false;
    this._text = '';
    this._error = null;
    this._loading = false;
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    this._stop();
  }

  willUpdate(changed: Map<string, unknown>) {
    if (changed.has('url') && this.url !== this._loadedUrl) this._text = '';
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('url') && this.url !== this._loadedUrl) this._load();
    if (changed.has('live') || changed.has('url')) {
      this._stop();
      if (this.live && this.url) this._follow(this.url);
    }
  }

  private _stop() {
    if (this._timer) clearInterval(this._timer);
    this._timer = undefined;
    this._stream?.abort();
    this._stream = undefined;
  }

  private async _follow(url: string) {
    const stream = new AbortController();
    this._stream = stream;
    const [path, query] = url.split('?');
    try {
      for await (const { event, data } of apiEvents(`${path}/stream${query ? `?${query}` : ''}`, stream.signal)) {
        if (url !== this.url) return;
        if (event === 'log') {
          const chunk = JSON.parse(data) as { text: string; reset?: boolean };
          await this._show(chunk.reset ? chunk.text : this._text + chunk.text);
        } else if (event === 'end') {
          break;
        }
      }
      if (!stream.signal.aborted) this._load(true); // the stored log, once the attempt is done
    } catch {
      if (!stream.signal.aborted && url === this.url) this._timer = setInterval(() => this._load(true), 3000);
    }
  }

  private async _show(text: string) {
    const pre = this._pre;
    const atBottom = !pre || pre.scrollHeight - pre.scrollTop - pre.clientHeight < 24;
    this._loadedUrl = this.url;
    this._text = text;
    this._error = null;
    if (atBottom) {
      await this.updateComplete;
      if (this._pre) this._pre.scrollTop = this._pre.scrollHeight;
    }
  }

  private async _load(silent = false) {
    if (!this.url) return;
    const url = this.url;
    if (!silent) this._loading = true;
    try {
      const text = await apiText(url);
      if (url !== this.url) return;
      await this._show(text);
    } catch (e) {
      this._error = extractErrorMessage(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    return html`
      ${this.live ? html`<div class="bar"><span>Live</span></div>` : ''}
      ${this._error
        ? html`<p class="error">${this._error}</p>`
        : html`<pre>${this._text ? unsafeHTML(ansi.ansi_to_html(this._text)) : this._loading ? 'Loading…' : 'No output.'}</pre>`}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'log-pane': LogPane;
  }
}
