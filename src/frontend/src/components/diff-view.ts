import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type { DiffHunk } from '../v2/diff.js';

/** A folded line diff: two line-number columns, +/− markers, collapsed unchanged runs. */
@customElement('diff-view')
export class DiffView extends LitElement {
  static styles = css`
    .diff {
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      background: var(--color-code-bg);
      font-family: var(--font-mono);
      font-size: 0.74rem;
      line-height: 1.45;
      overflow: auto;
      max-height: 34rem;
    }
    .ln {
      display: grid;
      grid-template-columns: 3rem 3rem 1.2rem minmax(0, 1fr);
      white-space: pre;
    }
    .ln span:nth-child(-n + 2) {
      color: var(--color-text-muted);
      text-align: right;
      padding-right: 0.5rem;
      user-select: none;
    }
    .ln.add {
      background: var(--color-diff-add);
    }
    .ln.del {
      background: var(--color-diff-del);
    }
    .skip {
      padding: 0.15rem 0.75rem;
      color: var(--color-text-muted);
      background: var(--color-surface);
      border-top: 1px solid var(--color-border);
      border-bottom: 1px solid var(--color-border);
      font-family: var(--font-sans);
    }
  `;

  @property({ attribute: false }) declare hunks: DiffHunk[];
  @property() declare label: string;

  constructor() {
    super();
    this.hunks = [];
    this.label = 'Diff';
  }

  render() {
    return html`<div class="diff" role="table" aria-label=${this.label}>
      ${this.hunks.map((h) =>
        'skipped' in h
          ? html`<div class="skip">${h.skipped} unchanged lines</div>`
          : h.lines.map(
              (l) =>
                html`<div class="ln ${l.kind}">
                  <span>${l.a ?? ''}</span><span>${l.b ?? ''}</span
                  ><span>${l.kind === 'add' ? '+' : l.kind === 'del' ? '−' : ''}</span
                  ><span>${l.text}</span>
                </div>`,
            ),
      )}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'diff-view': DiffView;
  }
}
