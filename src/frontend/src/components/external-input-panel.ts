import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { keyed } from 'lit/directives/keyed.js';
import { runStepsAnswer, runStepsDecline, type Attempt } from '@olve/api';
import { api, errorMessages } from '../api.js';
import { sharedStyles } from '../v2/ui.js';
import {
  byPerson,
  inputSchemaOf,
  stepsByName,
  type ExternalInput,
  type JsonSchema,
  type Manifest,
} from '../v2/manifest.js';
import { formFields } from '../v2/schema-form.js';
import type { InputForm } from './input-form.js';
import './input-form.js';
import './relative-time.js';

export type Answer = { value: unknown; answeredBy: string; answeredAt: string };

/** The recorded answers of an attempt's external inputs, by input name. */
export function answersOf(a: Attempt | null | undefined): Record<string, Answer> {
  const out: Record<string, Answer> = {};
  for (const [name, x] of Object.entries(a?.answers ?? {})) {
    let value: unknown = x?.value;
    try {
      value = JSON.parse(x?.value ?? '');
    } catch {
      /* keep the text */
    }
    out[name] = {
      value,
      answeredBy: x?.answeredBy ?? '',
      answeredAt: String(x?.answeredAt ?? ''),
    };
  }
  return out;
}

/** "name: value, by user, 3 min ago" lines for an attempt's answers. */
export function renderAnswers(a: Attempt | null | undefined) {
  const answers = Object.entries(answersOf(a));
  if (!answers.length) return nothing;
  return html`<h4 class="sheet-h">Answers</h4>
    <dl class="kv">
      ${answers.map(
        ([name, x]) =>
          html`<dt>${name}</dt>
            <dd>
              <code>${JSON.stringify(x.value)}</code>
              <span class="muted"
                >by
                ${x.answeredBy || 'unknown'}${x.answeredAt
                  ? html`, <relative-time .value=${x.answeredAt}></relative-time>`
                  : nothing}</span
              >
            </dd>`,
      )}
    </dl>`;
}

/** How a model's schema is asked for: a generated form, nothing (an empty object), or raw JSON. */
function formKind(schema: JsonSchema | null): 'form' | 'empty' | 'json' {
  if (formFields(schema).length) return 'form';
  const t = Array.isArray(schema?.type) ? schema.type : [schema?.type];
  return t.includes('object') && !Object.keys(schema?.properties ?? {}).length
    ? 'empty'
    : 'json';
}

/**
 * A waiting step's external inputs: per input its title, description and a form from its
 * model; submit sends one answer per unanswered input, decline fails the attempt.
 * Fires `answered` after either succeeds.
 */
@customElement('external-input-panel')
export class ExternalInputPanel extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        margin-top: 0.75rem;
        max-width: 56rem;
      }
      .panel {
        border-left: 3px solid var(--color-primary);
      }
      h3 {
        display: flex;
        align-items: center;
        gap: 0.4rem;
        margin-bottom: 0.2rem;
      }
      .since {
        margin: 0 0 0.6rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .input + .input {
        margin-top: 1rem;
        padding-top: 0.9rem;
        border-top: 1px solid var(--color-border);
      }
      h4 {
        margin: 0;
        font-size: 0.92rem;
        font-weight: 600;
      }
      .desc {
        margin: 0.2rem 0 0.6rem;
        font-size: 0.84rem;
        color: var(--color-text-muted);
        white-space: pre-line;
      }
      .answered {
        font-size: 0.82rem;
      }
      textarea {
        width: 100%;
        padding: 0.3rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-bg);
        color: var(--color-text);
        font-family: var(--font-mono);
        font-size: 0.8rem;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.5rem;
        margin-top: 1rem;
      }
      .decline {
        display: flex;
        flex: 1;
        min-width: 14rem;
        gap: 0.5rem;
      }
      .decline input {
        flex: 1;
        min-width: 0;
        padding: 0.3rem 0.5rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-bg);
        color: var(--color-text);
        font-size: 0.82rem;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare run: string;
  @property() declare step: string;
  @property({ attribute: false }) declare manifest: Manifest | null;
  @property({ attribute: false }) declare attempt: Attempt | null;

  @state() declare private _errors: Record<string, string[]>;
  @state() declare private _json: Record<string, string>;
  @state() declare private _comment: string;
  @state() declare private _busy: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.run = '';
    this.step = '';
    this.manifest = null;
    this.attempt = null;
    this._errors = {};
    this._json = {};
    this._comment = '';
    this._busy = false;
  }

  willUpdate(changed: Map<string, unknown>) {
    if (changed.has('step') || changed.has('run')) {
      this._errors = {};
      this._json = {};
      this._comment = '';
    }
  }

  private get _inputs() {
    return Object.entries(
      stepsByName(this.manifest).get(this.step)?.externalInputs ?? {},
    );
  }

  render() {
    const m = this.manifest;
    const inputs = this._inputs;
    if (!m || !inputs.length) return nothing;
    const answers = answersOf(this.attempt);
    const since = (this.attempt?.status as { since?: Date | string } | null)?.since;
    const general = this._errors[''] ?? [];
    return html`<div class="panel" role="region" aria-label="Waiting for input">
      <h3><span class="dot t-ask"></span>${this.step} is waiting for input</h3>
      <p class="since">
        The step runs once ${inputs.length > 1 ? 'every input is' : 'this is'} answered.
        ${since
          ? html`Waiting since <relative-time .value=${since}></relative-time>.`
          : nothing}
      </p>
      ${inputs.map(([name, x]) => {
        const a = answers[name];
        const schema = inputSchemaOf(m, x.model);
        const kind = formKind(schema);
        const errors = this._errors[name] ?? [];
        return html`<div class="input">
          <h4>${x.title}</h4>
          ${x.description ? html`<p class="desc">${x.description}</p>` : nothing}
          ${a
            ? html`<p class="answered">
                Answered <code>${JSON.stringify(a.value)}</code>
                <span class="muted">by ${a.answeredBy || 'unknown'}</span>
              </p>`
            : !byPerson(x)
              ? html`<p class="answered muted">${sourceLine(x)}</p>`
              : kind === 'form'
                ? keyed(
                    `${this.run}/${this.step}/${name}`,
                    html`<input-form data-input=${name} .schema=${schema}></input-form>`,
                  )
                : kind === 'json'
                  ? html`<textarea
                      rows="4"
                      placeholder="JSON value of ${x.model}"
                      aria-label=${x.title}
                      .value=${this._json[name] ?? ''}
                      @input=${(e: Event) =>
                        (this._json = {
                          ...this._json,
                          [name]: (e.target as HTMLTextAreaElement).value,
                        })}
                    ></textarea>`
                  : nothing}
          ${errors.length
            ? html`<div class="errors" role="alert">
                ${errors.map((e) => html`<p>${e}</p>`)}
              </div>`
            : nothing}
        </div>`;
      })}
      ${general.length
        ? html`<div class="errors" role="alert">
            ${general.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      ${inputs.some(([, x]) => byPerson(x))
        ? this._actions(
            m,
            inputs.filter(([name, x]) => byPerson(x) && !answers[name]),
          )
        : nothing}
    </div>`;
  }

  private _actions(m: Manifest, open: [string, ExternalInput][]) {
    return html`<div class="actions">
      ${open.length
        ? html`<button class="btn primary" ?disabled=${this._busy} @click=${this._submit}>
            ${open.every(([, x]) => formKind(inputSchemaOf(m, x.model)) === 'empty')
              ? 'Confirm'
              : 'Submit'}
          </button>`
        : nothing}
      <span class="decline">
        <input
          type="text"
          placeholder="Comment (optional)"
          aria-label="Decline comment"
          .value=${this._comment}
          @input=${(e: Event) => (this._comment = (e.target as HTMLInputElement).value)}
        />
        <button class="btn danger" ?disabled=${this._busy} @click=${this._decline}>
          Decline
        </button>
      </span>
    </div>`;
  }

  private get _path() {
    return { pipelineId: this.pipelineId, number: Number(this.run), step: this.step };
  }

  /** The value to send for one input, or null when its form has errors. */
  private _value(name: string, kind: 'form' | 'empty' | 'json'): { v: unknown } | null {
    if (kind === 'empty') return { v: {} };
    if (kind === 'json') {
      try {
        return { v: JSON.parse(this._json[name] ?? '') };
      } catch {
        this._errors = { ...this._errors, [name]: ['Not valid JSON.'] };
        return null;
      }
    }
    const form = this.renderRoot.querySelector<InputForm>(
      `input-form[data-input="${CSS.escape(name)}"]`,
    );
    const r = form?.collect();
    if (!r || !r.ok) {
      this._errors = { ...this._errors, [name]: ['Fix the highlighted fields.'] };
      return null;
    }
    return { v: r.value };
  }

  private _submit = async () => {
    const m = this.manifest;
    if (!m) return;
    const answers = answersOf(this.attempt);
    const open = this._inputs.filter(([name, x]) => byPerson(x) && !answers[name]);
    this._errors = {};
    const values: [string, unknown][] = [];
    for (const [name, x] of open) {
      const r = this._value(name, formKind(inputSchemaOf(m, x.model)));
      if (r) values.push([name, r.v]);
    }
    if (values.length < open.length) return;
    this._busy = true;
    let ok = true;
    for (const [name, value] of values) {
      try {
        await runStepsAnswer({ client: api, path: this._path, body: { name, value } });
      } catch (e) {
        ok = false;
        this._errors = { ...this._errors, [name]: errorMessages(e) };
      }
    }
    this._busy = false;
    this._done(ok || values.length > 1);
  };

  private _decline = async () => {
    if (
      !confirm(
        `Decline ${this.step} in run #${this.run}? The step fails without running.`,
      )
    )
      return;
    this._busy = true;
    this._errors = {};
    try {
      await runStepsDecline({
        client: api,
        path: this._path,
        body: this._comment ? { comment: this._comment } : {},
      });
      this._comment = '';
      this._done(true);
    } catch (e) {
      this._errors = { '': errorMessages(e) };
    } finally {
      this._busy = false;
    }
  };

  private _done(changed: boolean) {
    if (changed) this.dispatchEvent(new CustomEvent('answered'));
  }
}

function sourceLine(x: ExternalInput): string {
  const poll = x.from.find((f) => f.kind === 'poll');
  const callback = x.from.some((f) => f.kind === 'callback');
  const parts = [
    ...(callback ? ['a callback from another system'] : []),
    ...(poll && poll.kind === 'poll'
      ? [`a value from ${poll.url} (checked every ${poll.intervalSeconds}s)`]
      : []),
  ];
  return `Waiting for ${parts.join(' or ')}.`;
}

declare global {
  interface HTMLElementTagNameMap {
    'external-input-panel': ExternalInputPanel;
  }
}
