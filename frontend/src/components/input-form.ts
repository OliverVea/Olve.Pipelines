import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { JsonSchema } from '../v2/manifest.js';
import {
  formFields,
  parseField,
  toText,
  constraintHint,
  type FormField,
} from '../v2/schema-form.js';
import { sharedStyles } from '../v2/ui.js';

export type FormResult = { ok: true; value: Record<string, unknown> } | { ok: false };

/**
 * Form generated from an input model's JSON Schema. Values are validated as you
 * type (after a field has been touched) and on {@link collect}. Fires
 * `input-change` {value} with the currently-valid fields, for plan previews.
 *
 * With `initial` set (continue), fields start from those values and
 * {@link collect} returns only the fields that changed.
 */
@customElement('input-form')
export class InputForm extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .grid {
        display: grid;
        grid-template-columns: max-content minmax(0, 1fr);
        gap: 0.65rem 1rem;
        align-items: start;
      }
      @media (max-width: 560px) {
        .grid {
          grid-template-columns: 1fr;
        }
      }
      label.name {
        padding-top: 0.3rem;
        font-family: var(--font-mono);
        font-size: 0.82rem;
      }
      .req {
        color: var(--color-danger);
        margin-left: 0.15rem;
      }
      input[type='text'],
      select,
      textarea {
        width: 100%;
        padding: 0.3rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-bg);
        color: var(--color-text);
        font-size: 0.85rem;
      }
      input.mono,
      textarea {
        font-family: var(--font-mono);
        font-size: 0.8rem;
      }
      .invalid input,
      .invalid select,
      .invalid textarea {
        border-color: var(--color-danger);
      }
      .hint {
        margin-top: 0.15rem;
        font-size: 0.72rem;
        color: var(--color-text-muted);
        overflow-wrap: anywhere;
      }
      .field-error {
        margin-top: 0.15rem;
        font-size: 0.75rem;
        color: var(--color-danger);
      }
      .changed label.name::after {
        content: ' (changed)';
        color: var(--color-warning);
        font-family: var(--font-sans);
        font-size: 0.72rem;
      }
      .bools {
        display: flex;
        gap: 0.9rem;
        padding-top: 0.3rem;
        font-size: 0.85rem;
      }
      .bools label {
        display: inline-flex;
        gap: 0.3rem;
        align-items: center;
      }
    `,
  ];

  @property({ attribute: false }) declare schema: JsonSchema | null;
  @property({ attribute: false }) declare initial: Record<string, unknown> | null;

  @state() declare private _text: Record<string, string>;
  @state() declare private _touched: Set<string>;
  private _fields: FormField[] = [];
  private _forSchema: JsonSchema | null = null;

  constructor() {
    super();
    this.schema = null;
    this.initial = null;
    this._text = {};
    this._touched = new Set();
  }

  willUpdate(changed: Map<string, unknown>) {
    if (
      (changed.has('schema') || changed.has('initial')) &&
      this.schema !== this._forSchema
    ) {
      this._forSchema = this.schema;
      this._fields = formFields(this.schema);
      const text: Record<string, string> = {};
      for (const f of this._fields) {
        const v = this.initial ? this.initial[f.name] : undefined;
        text[f.name] = v === undefined ? '' : toText(f, v);
      }
      this._text = text;
      this._touched = new Set();
      queueMicrotask(() => this._emit());
    }
  }

  /** Validate every field; returns the value to send, or marks errors. */
  collect(): FormResult {
    this._touched = new Set(this._fields.map((f) => f.name));
    const value: Record<string, unknown> = {};
    let ok = true;
    for (const f of this._fields) {
      const r = parseField(f, this._text[f.name] ?? '');
      if (!r.ok) {
        ok = false;
        continue;
      }
      if (r.empty) continue;
      if (
        this.initial &&
        JSON.stringify(this.initial[f.name]) === JSON.stringify(r.value)
      )
        continue;
      value[f.name] = r.value;
    }
    return ok ? { ok: true, value } : { ok: false };
  }

  get hasFields(): boolean {
    return this._fields.length > 0;
  }

  render() {
    if (!this._fields.length)
      return html`<p class="muted">The input model has no fields this form can show.</p>`;
    return html`<div class="grid">${this._fields.map((f) => this._field(f))}</div>`;
  }

  private _field(f: FormField) {
    const text = this._text[f.name] ?? '';
    const parsed = parseField(f, text);
    const showError = this._touched.has(f.name) && !parsed.ok;
    const changed =
      this.initial &&
      parsed.ok &&
      !parsed.empty &&
      JSON.stringify(this.initial[f.name]) !== JSON.stringify(parsed.value);
    const id = `f-${f.name}`;
    const hint = constraintHint(f);
    return html`
      <label class="name ${changed ? 'changed' : ''}" for=${id}
        >${f.name}${f.required ? html`<span class="req">*</span>` : nothing}</label
      >
      <div class="${showError ? 'invalid' : ''} ${changed ? 'changed' : ''}">
        ${this._control(f, id, text)}
        ${showError && !parsed.ok
          ? html`<div class="field-error">${parsed.error}</div>`
          : nothing}
        ${f.description ? html`<div class="hint">${f.description}</div>` : nothing}
        ${hint ? html`<div class="hint">${hint}</div>` : nothing}
      </div>
    `;
  }

  private _control(f: FormField, id: string, text: string) {
    const set = (v: string) => this._set(f.name, v);
    switch (f.kind) {
      case 'enum':
        return html`<select
          id=${id}
          @change=${(e: Event) => set((e.target as HTMLSelectElement).value)}
        >
          ${!f.required
            ? html`<option value="" ?selected=${text === ''}>
                ${f.hasDefault ? `default (${String(f.default)})` : '—'}
              </option>`
            : html`<option value="" disabled ?selected=${text === ''}>choose…</option>`}
          ${(f.options ?? []).map((o) => {
            const v = JSON.stringify(o);
            return html`<option value=${v} ?selected=${text === v}>
              ${typeof o === 'string' ? o : v}
            </option>`;
          })}
        </select>`;
      case 'boolean':
        return html`<div class="bools" role="radiogroup" id=${id}>
          ${!f.required
            ? html`<label
                ><input
                  type="radio"
                  name=${id}
                  .checked=${text === ''}
                  @change=${() => set('')}
                />
                ${f.hasDefault ? `default (${String(f.default)})` : 'unset'}</label
              >`
            : nothing}
          <label
            ><input
              type="radio"
              name=${id}
              .checked=${text === 'true'}
              @change=${() => set('true')}
            />
            true</label
          >
          <label
            ><input
              type="radio"
              name=${id}
              .checked=${text === 'false'}
              @change=${() => set('false')}
            />
            false</label
          >
        </div>`;
      case 'json':
        return html`<textarea
          id=${id}
          rows="4"
          .value=${text}
          placeholder="JSON"
          @input=${(e: Event) => set((e.target as HTMLTextAreaElement).value)}
          @blur=${() => this._touch(f.name)}
        ></textarea>`;
      default:
        return html`<input
          id=${id}
          type="text"
          class=${f.patternSource || f.kind !== 'string' ? 'mono' : ''}
          inputmode=${f.kind === 'integer'
            ? 'numeric'
            : f.kind === 'number'
              ? 'decimal'
              : 'text'}
          .value=${text}
          placeholder=${f.hasDefault ? String(f.default) : ''}
          autocomplete="off"
          spellcheck="false"
          @input=${(e: Event) => set((e.target as HTMLInputElement).value)}
          @blur=${() => this._touch(f.name)}
        />`;
    }
  }

  private _touch(name: string) {
    if (this._touched.has(name)) return;
    this._touched = new Set([...this._touched, name]);
  }

  private _set(name: string, v: string) {
    this._text = { ...this._text, [name]: v };
    this._touch(name);
    this._emit();
  }

  private _emit() {
    const value: Record<string, unknown> = { ...(this.initial ?? {}) };
    for (const f of this._fields) {
      const r = parseField(f, this._text[f.name] ?? '');
      if (r.ok && !r.empty) value[f.name] = r.value;
      else if (r.ok && r.empty && !this.initial) delete value[f.name];
    }
    this.dispatchEvent(new CustomEvent('input-change', { detail: { value } }));
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'input-form': InputForm;
  }
}
