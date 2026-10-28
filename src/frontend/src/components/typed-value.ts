import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import type { JsonSchema } from '../v2/manifest.js';
import { sharedStyles } from '../v2/ui.js';

export type FileRefValue = {
  run: number;
  step: string;
  attempt: number;
  path: string;
  sha256?: string;
};

function isFileRef(schema: JsonSchema | undefined, v: unknown): v is FileRefValue {
  if (!v || typeof v !== 'object') return false;
  if (schema?.['x-olve-kind'] === 'fileRef') return true;
  const o = v as Record<string, unknown>;
  return (
    typeof o.run === 'number' &&
    typeof o.step === 'string' &&
    typeof o.attempt === 'number' &&
    typeof o.path === 'string'
  );
}

/**
 * A JSON value rendered against its JSON Schema (the pinned model version):
 * objects as aligned field lists, file refs as downloadable chips (event
 * `file-download` {ref}), with fields the schema declares but the value lacks
 * shown as absent. Without a schema it still renders structurally.
 */
@customElement('typed-value')
export class TypedValue extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        display: block;
        font-size: 0.82rem;
      }
      dl {
        display: grid;
        grid-template-columns: max-content minmax(0, 1fr);
        gap: 0.2rem 0.9rem;
        margin: 0;
      }
      dt {
        color: var(--color-text-muted);
        font-family: var(--font-mono);
        font-size: 0.78rem;
        padding-top: 0.05rem;
      }
      dd {
        margin: 0;
        min-width: 0;
        overflow-wrap: anywhere;
      }
      dd > dl {
        padding-left: 0.6rem;
        border-left: 1px solid var(--color-border);
      }
      .str {
        font-family: var(--font-mono);
        font-size: 0.8rem;
      }
      .lit {
        font-family: var(--font-mono);
        font-size: 0.8rem;
        color: var(--color-primary);
      }
      .absent {
        color: var(--color-text-muted);
        font-style: italic;
      }
      .file {
        display: inline-flex;
        align-items: center;
        gap: 0.4rem;
        flex-wrap: wrap;
      }
      .file .path {
        font-family: var(--font-mono);
        font-size: 0.8rem;
      }
      .file .src {
        color: var(--color-text-muted);
        font-size: 0.74rem;
      }
      ol {
        margin: 0;
        padding-left: 1.2rem;
      }
    `,
  ];

  @property({ attribute: false }) declare value: unknown;
  @property({ attribute: false }) declare schema: JsonSchema | null;

  constructor() {
    super();
    this.value = undefined;
    this.schema = null;
  }

  render() {
    return this._render(this.value, this.schema ?? undefined);
  }

  private _render(v: unknown, s: JsonSchema | undefined): TemplateResult {
    if (v === undefined) return html`<span class="absent">absent</span>`;
    if (v === null) return html`<span class="lit">null</span>`;
    if (isFileRef(s, v)) return this._file(v);
    if (Array.isArray(v)) {
      if (!v.length) return html`<span class="absent">empty list</span>`;
      return html`<ol>
        ${v.map((x) => html`<li>${this._render(x, s?.items)}</li>`)}
      </ol>`;
    }
    if (typeof v === 'object') {
      const o = v as Record<string, unknown>;
      const declared = Object.keys(s?.properties ?? {});
      const keys = [...declared, ...Object.keys(o).filter((k) => !declared.includes(k))];
      if (!keys.length) return html`<span class="absent">{}</span>`;
      return html`<dl>
        ${keys.map(
          (k) =>
            html`<dt>${k}</dt>
              <dd>${this._render(o[k], s?.properties?.[k])}</dd>`,
        )}
      </dl>`;
    }
    if (typeof v === 'string') return html`<span class="str">${v}</span>`;
    return html`<span class="lit">${JSON.stringify(v)}</span>`;
  }

  private _file(ref: FileRefValue) {
    return html`<span class="file">
      <span class="path">${ref.path}</span>
      <span class="src">from ${ref.step} #${ref.run}@${ref.attempt}</span>
      <button
        class="btn small"
        @click=${() =>
          this.dispatchEvent(
            new CustomEvent('file-download', {
              detail: { ref },
              bubbles: true,
              composed: true,
            }),
          )}
      >
        Download
      </button>
      ${ref.sha256
        ? html`<span class="src mono">${ref.sha256.slice(0, 10)}</span>`
        : nothing}
    </span>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'typed-value': TypedValue;
  }
}
