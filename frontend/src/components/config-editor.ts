import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { EditorView, basicSetup } from 'codemirror';
import { Compartment, EditorState } from '@codemirror/state';
import { yaml as yamlLanguage } from '@codemirror/lang-yaml';
import { linter, lintGutter, type Diagnostic } from '@codemirror/lint';
import {
  autocompletion,
  type Completion,
  type CompletionContext,
} from '@codemirror/autocomplete';
import { isAlias, parseDocument, visit, type Document } from 'yaml';
import type { JsonSchema } from '../v2/manifest.js';
import { propertiesAt, validate, valuesAt, type Path } from '../v2/config-schema.js';

/** Parses YAML like the service: one document, YAML 1.2 core schema, no anchors. */
export function parseConfigYaml(text: string): { doc: Document; errors: Diagnostic[] } {
  const doc = parseDocument(text, {
    schema: 'core',
    uniqueKeys: true,
    prettyErrors: false,
  });
  const errors: Diagnostic[] = [...doc.errors, ...doc.warnings].map((e) => ({
    from: e.pos[0],
    to: Math.max(e.pos[1], e.pos[0] + 1),
    severity: 'error' as const,
    message: e.message.split('\n')[0],
  }));
  visit(doc, (_, node) => {
    const anchored =
      isAlias(node) || Boolean((node as { anchor?: string } | null)?.anchor);
    const range = (node as { range?: [number, number, number] } | null)?.range;
    if (anchored && range)
      errors.push({
        from: range[0],
        to: Math.max(range[1], range[0] + 1),
        severity: 'error',
        message: 'anchors and aliases are not supported',
      });
  });
  return { doc, errors };
}

/** Schema problems of the document, placed on the offending node (or its nearest parent). */
export function schemaDiagnostics(
  doc: Document,
  schema: JsonSchema,
  textLength: number,
): Diagnostic[] {
  const value = doc.toJS() ?? {};
  if (typeof value !== 'object' || Array.isArray(value))
    return [
      {
        from: 0,
        to: Math.max(1, textLength),
        severity: 'error',
        message: 'the document must be a mapping of config entries',
      },
    ];
  const declared = Object.keys(schema.properties ?? {});
  const projected = Object.fromEntries(
    Object.entries(value as Record<string, unknown>).filter(([k]) =>
      declared.includes(k),
    ),
  );
  return validate(schema, projected).issues.map((i) => {
    const [from, to] = rangeOf(doc, i.path, textLength);
    return {
      from,
      to,
      severity: 'error' as const,
      message: `${['config', ...i.path].join('.')}: ${i.message}`,
    };
  });
}

function rangeOf(doc: Document, path: Path, textLength: number): [number, number] {
  for (let n = path.length; n >= 0; n--) {
    const node = n === 0 ? doc.contents : doc.getIn(path.slice(0, n), true);
    const range = (node as { range?: [number, number, number] } | null)?.range;
    // A missing key is marked at the start of its parent.
    if (range)
      return n === path.length
        ? [range[0], Math.max(range[1], range[0] + 1)]
        : [range[0], Math.min(range[1], range[0] + 1)];
  }
  return [0, Math.min(1, textLength)];
}

/** The key path of the block mapping the cursor's line belongs to (by indentation). */
function pathAtLine(state: EditorState, lineNumber: number, indent: number): Path {
  const path: Path = [];
  let limit = indent;
  for (let n = lineNumber - 1; n >= 1 && limit > 0; n--) {
    const text = state.doc.line(n).text;
    if (!text.trim() || text.trim().startsWith('#')) continue;
    const ind = text.length - text.trimStart().length;
    const key = /^\s*(?:-\s+)?([A-Za-z0-9_$-]+)\s*:\s*(#.*)?$/.exec(text);
    if (ind < limit) {
      if (key) path.unshift(key[1]);
      limit = ind;
    }
  }
  return path;
}

function complete(schema: () => JsonSchema | null) {
  return (ctx: CompletionContext) => {
    const s = schema();
    if (!s) return null;
    const line = ctx.state.doc.lineAt(ctx.pos);
    const before = line.text.slice(0, ctx.pos - line.from);
    const indent = before.length - before.trimStart().length;
    const valueMatch = /^\s*(?:-\s+)?([A-Za-z0-9_$-]+):\s*(\S*)$/.exec(before);
    if (valueMatch) {
      const path = [...pathAtLine(ctx.state, line.number, indent), valueMatch[1]];
      const values = valuesAt(s, path);
      if (!values.length) return null;
      return {
        from: ctx.pos - valueMatch[2].length,
        options: values.map(
          (v): Completion => ({
            label: typeof v === 'string' ? v : JSON.stringify(v),
            type: 'enum',
          }),
        ),
      };
    }
    const keyMatch = /^\s*(?:-\s+)?([A-Za-z0-9_$-]*)$/.exec(before);
    if (!keyMatch || (!keyMatch[1] && !ctx.explicit)) return null;
    const path = pathAtLine(ctx.state, line.number, indent);
    const props = propertiesAt(s, path);
    if (!props.size) return null;
    return {
      from: ctx.pos - keyMatch[1].length,
      options: [...props].map(
        ([name, p]): Completion => ({
          label: name,
          apply: `${name}: `,
          type: 'property',
          detail:
            p.default !== undefined ? `default ${JSON.stringify(p.default)}` : undefined,
          info: typeof p.description === 'string' ? p.description : undefined,
        }),
      ),
    };
  };
}

/**
 * A YAML editor for a config document: errors on the offending lines (YAML syntax and the version's
 * schema) and completion of keys and enum values from the schema. Fires `change` with the text.
 */
@customElement('config-editor')
export class ConfigEditor extends LitElement {
  static styles = css`
    :host {
      display: block;
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      background: var(--color-surface);
      overflow: hidden;
    }
    .cm-editor {
      min-height: 14rem;
      max-height: 34rem;
      font-size: 0.8rem;
    }
    .cm-editor.cm-focused {
      outline: none;
    }
    .cm-scroller {
      font-family: var(--font-mono);
    }
    .cm-gutters {
      background: var(--color-code-bg);
      color: var(--color-text-muted);
      border-right: 1px solid var(--color-border);
    }
  `;

  @property() declare value: string;
  @property({ attribute: false }) declare schema: JsonSchema | null;
  @property({ type: Boolean }) declare readonly: boolean;

  private _view: EditorView | null = null;
  private readonly _lint = new Compartment();
  private readonly _editable = new Compartment();

  constructor() {
    super();
    this.value = '';
    this.schema = null;
    this.readonly = false;
  }

  /** The editor's current text (what the user typed). */
  get text(): string {
    return this._view?.state.doc.toString() ?? this.value;
  }

  firstUpdated() {
    this._view = new EditorView({
      root: this.shadowRoot!,
      parent: this.renderRoot.querySelector('.host')!,
      state: EditorState.create({
        doc: this.value,
        extensions: [
          basicSetup,
          yamlLanguage(),
          lintGutter(),
          this._lint.of(this._linter()),
          this._editable.of(EditorView.editable.of(!this.readonly)),
          autocompletion({ override: [complete(() => this.schema)] }),
          EditorView.updateListener.of((u) => {
            if (u.docChanged)
              this.dispatchEvent(
                new CustomEvent('change', { detail: u.state.doc.toString() }),
              );
          }),
        ],
      }),
    });
  }

  updated(changed: Map<string, unknown>) {
    const view = this._view;
    if (!view) return;
    if (changed.has('value') && this.value !== view.state.doc.toString())
      view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: this.value },
      });
    if (changed.has('schema'))
      view.dispatch({ effects: this._lint.reconfigure(this._linter()) });
    if (changed.has('readonly'))
      view.dispatch({
        effects: this._editable.reconfigure(EditorView.editable.of(!this.readonly)),
      });
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    this._view?.destroy();
    this._view = null;
  }

  private _linter() {
    return linter(
      (view) => {
        const text = view.state.doc.toString();
        const { doc, errors } = parseConfigYaml(text);
        if (errors.length || !this.schema) return errors;
        return schemaDiagnostics(doc, this.schema, text.length);
      },
      { delay: 300 },
    );
  }

  render() {
    return html`<div class="host"></div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'config-editor': ConfigEditor;
  }
}
