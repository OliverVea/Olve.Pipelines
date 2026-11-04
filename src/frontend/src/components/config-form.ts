import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { sharedStyles } from '../styles/ui.js';
import type { JsonSchema } from '../model/manifest.js';
import { validate, type Issue, type Path } from '../model/config-schema.js';
import {
  branchOf,
  editYaml,
  kindOf,
  optionsOf,
  secretNameOf,
  seedOf,
  switchBranch,
  unionOf,
  valueAt,
  yamlValue,
  type Union,
} from '../model/config-form.js';

const key = (path: Path) => path.join('.');
const show = (v: unknown) => (typeof v === 'string' ? v : JSON.stringify(v));

/**
 * The config document as a form generated from its schema. Edits go into the YAML text in place
 * (comments survive); fires `change` with the new text.
 */
@customElement('config-form')
export class ConfigForm extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        display: block;
      }
      fieldset {
        margin: 0.4rem 0;
        padding: 0.4rem 0.75rem 0.6rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        min-width: 0;
      }
      legend {
        padding: 0 0.3rem;
        font-weight: 600;
        font-size: 0.85rem;
      }
      .field {
        display: grid;
        grid-template-columns: minmax(8rem, 14rem) minmax(0, 1fr);
        gap: 0.2rem 0.75rem;
        align-items: start;
        padding: 0.3rem 0;
      }
      @media (max-width: 640px) {
        .field {
          grid-template-columns: 1fr;
        }
      }
      .name {
        font-family: var(--font-mono);
        font-size: 0.82rem;
        padding-top: 0.3rem;
        overflow-wrap: anywhere;
      }
      .name .req {
        color: var(--color-danger);
      }
      .control {
        display: flex;
        flex-direction: column;
        gap: 0.2rem;
        min-width: 0;
      }
      input[type='text'],
      input[type='number'],
      select,
      textarea {
        font: inherit;
        font-size: 0.85rem;
        padding: 0.25rem 0.4rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-surface);
        color: var(--color-text);
        max-width: 32rem;
      }
      textarea {
        font-family: var(--font-mono);
        min-height: 4rem;
      }
      .desc {
        font-size: 0.76rem;
        color: var(--color-text-muted);
      }
      .issue {
        font-size: 0.78rem;
        color: var(--color-danger);
      }
      .row {
        display: flex;
        gap: 0.4rem;
        align-items: start;
      }
      .row > :first-child {
        flex: 1;
        min-width: 0;
      }
      .secret.unset {
        color: var(--color-warning);
      }
    `,
  ];

  @property() declare text: string;
  @property({ attribute: false }) declare schema: JsonSchema | null;
  /** Secret name → whether it has a value, for secret references. */
  @property({ attribute: false }) declare secrets: Map<string, boolean>;
  @property({ type: Boolean }) declare readonly: boolean;

  @state() declare private _newKeys: Record<string, string>;
  private _issues = new Map<string, string[]>();

  constructor() {
    super();
    this.text = '';
    this.schema = null;
    this.secrets = new Map();
    this.readonly = false;
    this._newKeys = {};
  }

  render() {
    if (!this.schema?.properties) return html`<p class="muted">No config entries.</p>`;
    let value: Record<string, unknown>;
    try {
      value = yamlValue(this.text);
    } catch (e) {
      return html`<p class="error">
        The YAML doesn't parse (${(e as Error).message}); fix it in the YAML view.
      </p>`;
    }
    this._issues = new Map();
    for (const i of validate(this.schema, value).issues as Issue[]) {
      const k = key(i.path);
      this._issues.set(k, [...(this._issues.get(k) ?? []), i.message]);
    }
    const required = new Set(this.schema.required ?? []);
    return html`${Object.entries(this.schema.properties).map(([name, s]) =>
      this._field(s, [name], value[name], required.has(name), name),
    )}`;
  }

  private _set(path: Path, value: unknown) {
    const text = editYaml(this.text, path, value);
    this.text = text;
    this.dispatchEvent(new CustomEvent('change', { detail: text }));
  }

  private _issuesAt(path: Path) {
    const list = this._issues.get(key(path));
    return list ? list.map((m) => html`<span class="issue">${m}</span>`) : nothing;
  }

  private _field(
    s: JsonSchema,
    path: Path,
    value: unknown,
    required: boolean,
    label: string,
  ): TemplateResult {
    const kind = kindOf(s);
    if (kind === 'object' || kind === 'union')
      return this._group(s, path, value, required, label);
    return html`<div class="field">
      <span class="name"
        >${label}${required ? html`<span class="req">*</span>` : nothing}</span
      >
      <div class="control">
        ${this._control(s, path, value, required)}
        ${s.description ? html`<span class="desc">${s.description}</span>` : nothing}
        ${this._issuesAt(path)}
      </div>
    </div>`;
  }

  /** An object or a union: a fieldset of its properties (a union's after its discriminator). */
  private _group(
    s: JsonSchema,
    path: Path,
    value: unknown,
    required: boolean,
    label: string,
  ): TemplateResult {
    const union = unionOf(s);
    const shape = union ? branchOf(union, value) : s;
    const req = new Set(shape.properties ? (shape.required ?? []) : []);
    const absent = value === undefined;
    // An optional block without a default is either set as a whole or not at all.
    const optional = !required && s.default === undefined && !union;
    if (optional && absent)
      return html`<fieldset>
        <legend>${label}</legend>
        ${s.description ? html`<span class="desc">${s.description}</span>` : nothing}
        <div class="row">
          <span class="muted">not set</span>
          <button
            class="btn small"
            ?disabled=${this.readonly}
            @click=${() => this._set(path, seedOf(s))}
          >
            Add
          </button>
        </div>
      </fieldset>`;
    return html`<fieldset>
      <legend>
        ${label}${required ? html`<span class="name req">*</span>` : nothing}
        ${optional
          ? html`<button
              class="btn small"
              ?disabled=${this.readonly}
              @click=${() => this._set(path, undefined)}
            >
              Remove
            </button>`
          : nothing}
      </legend>
      ${s.description ? html`<span class="desc">${s.description}</span>` : nothing}
      ${union
        ? html`<div class="field">
            <span class="name">${union.discriminator}</span>
            <div class="control">
              <select
                ?disabled=${this.readonly}
                @change=${(e: Event) => {
                  const i =
                    (e.target as HTMLSelectElement).selectedIndex - (required ? 0 : 1);
                  this._set(
                    path,
                    i < 0
                      ? undefined
                      : switchBranch(union, value, union.options[i].value),
                  );
                }}
              >
                ${required
                  ? nothing
                  : html`<option ?selected=${absent}>(not set)</option>`}
                ${union.options.map(
                  (o) =>
                    html`<option
                      value=${show(o.value)}
                      ?selected=${!absent && branchOf(union, value) === o.branch}
                    >
                      ${show(o.value)}
                    </option>`,
                )}
              </select>
              ${this._secretState(union, value)} ${this._issuesAt(path)}
            </div>
          </div>`
        : this._issuesAt(path)}
      ${union && absent
        ? nothing
        : Object.entries(shape.properties ?? {})
            .filter(([k]) => k !== union?.discriminator)
            .map(([k, p]) =>
              this._field(p, [...path, k], valueAt(value, [k]), req.has(k), k),
            )}
    </fieldset>`;
  }

  /** For a choice of secrets (`{ secret: A } | { secret: B }`): whether the chosen one is set. */
  private _secretState(union: Union, value: unknown) {
    if (union.discriminator !== 'secret' || value === undefined) return nothing;
    const name = valueAt(value, ['secret']);
    const isSet = typeof name === 'string' ? this.secrets.get(name) : undefined;
    if (isSet === undefined) return nothing;
    return html`<span class="secret ${isSet ? '' : 'unset'}"
      >${isSet ? 'set' : 'not set: pl secret set'}</span
    >`;
  }

  private _control(
    s: JsonSchema,
    path: Path,
    value: unknown,
    required: boolean,
  ): TemplateResult {
    const ro = this.readonly;
    const unset = required
      ? nothing
      : html`<option ?selected=${value === undefined}>
          (not set${s.default !== undefined ? `, default ${show(s.default)}` : ''})
        </option>`;
    switch (kindOf(s)) {
      case 'secret': {
        const name = secretNameOf(s) ?? '?';
        const isSet = this.secrets.get(name);
        return html`<label class="row">
          <span>
            <input
              type="checkbox"
              ?disabled=${ro || required}
              .checked=${value !== undefined}
              @change=${(e: Event) =>
                this._set(
                  path,
                  (e.target as HTMLInputElement).checked ? { secret: name } : undefined,
                )}
            />
            uses secret <code>${name}</code>
          </span>
          ${value === undefined
            ? nothing
            : html`<span class="secret ${isSet === false ? 'unset' : ''}"
                >${isSet === undefined
                  ? ''
                  : isSet
                    ? 'set'
                    : 'not set: pl secret set'}</span
              >`}
        </label>`;
      }
      case 'enum':
      case 'boolean': {
        const options = kindOf(s) === 'boolean' ? [true, false] : optionsOf(s);
        return html`<select
          ?disabled=${ro}
          @change=${(e: Event) => {
            const i = (e.target as HTMLSelectElement).selectedIndex - (required ? 0 : 1);
            this._set(path, i < 0 ? undefined : options[i]);
          }}
        >
          ${unset}
          ${options.map(
            (o) =>
              html`<option value=${show(o)} ?selected=${o === value}>${show(o)}</option>`,
          )}
        </select>`;
      }
      case 'string':
      case 'number':
      case 'integer': {
        const numeric = kindOf(s) !== 'string';
        return html`<input
          type=${numeric ? 'number' : 'text'}
          ?readonly=${ro}
          .value=${value === undefined || value === null ? '' : String(value)}
          placeholder=${s.default !== undefined ? `default ${show(s.default)}` : ''}
          @change=${(e: Event) => {
            const text = (e.target as HTMLInputElement).value;
            if (text === '')
              return this._set(path, required ? (numeric ? undefined : '') : undefined);
            this._set(path, numeric ? Number(text) : text);
          }}
        />`;
      }
      case 'list': {
        const items = Array.isArray(value) ? value : [];
        return html`${items.map(
            (item, i): TemplateResult =>
              html`<div class="row">
                <div>${this._item(s.items!, [...path, i], item)}</div>
                <button
                  class="btn small"
                  ?disabled=${ro}
                  @click=${() =>
                    this._set(
                      path,
                      items.filter((_, j) => j !== i),
                    )}
                >
                  Remove
                </button>
              </div>`,
          )}
          <span
            ><button
              class="btn small"
              ?disabled=${ro}
              @click=${() => this._set(path, [...items, seedOf(s.items!)])}
            >
              Add
            </button></span
          >`;
      }
      case 'record': {
        const entries =
          value && typeof value === 'object' && !Array.isArray(value)
            ? Object.entries(value as Record<string, unknown>)
            : [];
        const item = s.additionalProperties as JsonSchema;
        const k = key(path);
        const draft = this._newKeys[k] ?? '';
        return html`${entries.map(
            ([name, v]) =>
              html`<div class="row">
                <div>
                  <span class="name">${name}</span> ${this._item(
                    item,
                    [...path, name],
                    v,
                  )}
                </div>
                <button
                  class="btn small"
                  ?disabled=${ro}
                  @click=${() => this._set([...path, name], undefined)}
                >
                  Remove
                </button>
              </div>`,
          )}
          <div class="row">
            <input
              type="text"
              placeholder="new key"
              ?readonly=${ro}
              .value=${draft}
              @input=${(e: Event) =>
                (this._newKeys = {
                  ...this._newKeys,
                  [k]: (e.target as HTMLInputElement).value,
                })}
            />
            <button
              class="btn small"
              ?disabled=${ro ||
              !draft.trim() ||
              entries.some(([n]) => n === draft.trim())}
              @click=${() => {
                this._set([...path, draft.trim()], seedOf(item));
                this._newKeys = { ...this._newKeys, [k]: '' };
              }}
            >
              Add
            </button>
          </div>`;
      }
      default:
        return html`<textarea
          ?readonly=${ro}
          .value=${value === undefined ? '' : JSON.stringify(value, null, 2)}
          @change=${(e: Event) => {
            const text = (e.target as HTMLTextAreaElement).value.trim();
            if (!text) return this._set(path, undefined);
            try {
              this._set(path, JSON.parse(text));
            } catch {
              (e.target as HTMLTextAreaElement).setCustomValidity('Not valid JSON.');
              (e.target as HTMLTextAreaElement).reportValidity();
            }
          }}
        ></textarea>`;
    }
  }

  /** A list item or record value: a group when it has fields, else just its control. */
  private _item(s: JsonSchema, path: Path, value: unknown): TemplateResult {
    const kind = kindOf(s);
    if (kind === 'object' || kind === 'union')
      return this._group(s, path, value, true, String(path.at(-1)));
    return html`${this._control(s, path, value, true)} ${this._issuesAt(path)}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'config-form': ConfigForm;
  }
}
