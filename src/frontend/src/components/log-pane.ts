import { LitElement, html, css } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { AnsiUp } from 'ansi_up';
import { runStepsLogs } from '@olve/api';
import { logEvents } from '@olve/client';
import { api, extractErrorMessage } from '../api.js';
import { sharedStyles } from '../v2/ui.js';

const ansi = new AnsiUp();
ansi.use_classes = false;

/** One attempt of a step in a run. */
export interface LogAddress {
  pipelineId: string;
  number: number;
  step: string;
  attempt: number;
}

const keyOf = (a: LogAddress | null) =>
  a ? `${a.pipelineId}#${a.number}/${a.step}@${a.attempt}` : '';

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

  @property({ attribute: false }) declare address: LogAddress | null;
  @property({ type: Boolean }) declare live: boolean;

  @state() declare private _text: string;
  @state() declare private _error: string | null;
  @state() declare private _loading: boolean;
  @query('pre') declare private _pre: HTMLPreElement | null;

  private _timer?: ReturnType<typeof setInterval>;
  private _stream?: AbortController;
  private _loadedKey = '';

  constructor() {
    super();
    this.address = null;
    this.live = false;
    this._text = '';
    this._error = null;
    this._loading = false;
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    this._stop();
  }

  private get _key() {
    return keyOf(this.address);
  }

  willUpdate(changed: Map<string, unknown>) {
    if (changed.has('address') && this._key !== this._loadedKey) this._text = '';
  }

  updated(changed: Map<string, unknown>) {
    const moved =
      changed.has('address') &&
      keyOf(changed.get('address') as LogAddress | null) !== this._key;
    if (moved && this._key !== this._loadedKey) this._load();
    if (changed.has('live') || moved) {
      this._stop();
      if (this.live && this.address) this._follow(this.address);
    }
  }

  private _stop() {
    if (this._timer) clearInterval(this._timer);
    this._timer = undefined;
    this._stream?.abort();
    this._stream = undefined;
  }

  private async _follow(address: LogAddress) {
    const stream = new AbortController();
    this._stream = stream;
    const key = keyOf(address);
    const { pipelineId, number, step, attempt } = address;
    try {
      for await (const event of logEvents(
        api,
        stream.signal,
        { pipelineId, number, step },
        attempt,
      )) {
        if (key !== this._key) return;
        if (event.type === 'log') {
          await this._show(event.reset ? event.text : this._text + event.text);
        } else if (event.type === 'end') {
          break;
        }
      }
      if (!stream.signal.aborted) this._load(true); // the stored log, once the attempt is done
    } catch {
      if (!stream.signal.aborted && key === this._key)
        this._timer = setInterval(() => this._load(true), 3000);
    }
  }

  private async _show(text: string) {
    const pre = this._pre;
    const atBottom = !pre || pre.scrollHeight - pre.scrollTop - pre.clientHeight < 24;
    this._loadedKey = this._key;
    this._text = text;
    this._error = null;
    if (atBottom) {
      await this.updateComplete;
      if (this._pre) this._pre.scrollTop = this._pre.scrollHeight;
    }
  }

  private async _load(silent = false) {
    if (!this.address) return;
    const { pipelineId, number, step, attempt } = this.address;
    const key = this._key;
    if (!silent) this._loading = true;
    try {
      const res = await runStepsLogs({
        client: api,
        path: { pipelineId, number, step },
        query: { attempt },
      });
      if (key !== this._key) return;
      await this._show(res.data?.text ?? '');
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
        : html`<pre>
${this._text
              ? unsafeHTML(ansi.ansi_to_html(this._text))
              : this._loading
                ? 'Loading…'
                : 'No output.'}</pre
          >`}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'log-pane': LogPane;
  }
}
