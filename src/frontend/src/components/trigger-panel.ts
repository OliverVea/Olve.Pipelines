import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import { api, errorMessages } from '../api/client.js';
import { runsStart } from '@olve/api';
import { navigate } from '../router.js';
import { sharedStyles, runPath } from '../styles/ui.js';
import { inputSchemaOf, type Manifest } from '../model/manifest.js';
import type { InputForm } from './input-form.js';
import './input-form.js';

/** Manual trigger: a form generated from the pipeline input model. */
@customElement('trigger-panel')
export class TriggerPanel extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        max-width: 32rem;
      }
      h4 {
        margin: 0 0 0.5rem;
        font-size: 0.8rem;
        font-weight: 600;
        color: var(--color-text-muted);
      }
      .actions {
        display: flex;
        gap: 0.5rem;
        margin-top: 1rem;
        align-items: center;
      }
      .outcome {
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property({ attribute: false }) declare manifest: Manifest | null;
  @property({ type: Number }) declare versionNumber: number | null;

  @state() declare private _errors: string[];
  @state() declare private _busy: boolean;
  @state() declare private _outcome: string;
  @query('input-form') declare private _form: InputForm;

  constructor() {
    super();
    this.pipelineId = '';
    this.manifest = null;
    this.versionNumber = null;
    this._errors = [];
    this._busy = false;
    this._outcome = '';
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No active version to run.</p>`;
    const schema = inputSchemaOf(m, m.input);
    return html` <h4>
        Input <code>${m.input}</code>${this.versionNumber
          ? html` on v${this.versionNumber}`
          : nothing}
      </h4>
      <input-form .schema=${schema}></input-form>
      ${this._errors.length
        ? html`<div class="errors" role="alert">
            ${this._errors.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      <div class="actions">
        <button class="btn primary" ?disabled=${this._busy} @click=${this._start}>
          Run
        </button>
        <button class="btn" @click=${() => this.dispatchEvent(new CustomEvent('close'))}>
          Close
        </button>
        ${this._outcome ? html`<span class="outcome">${this._outcome}</span>` : nothing}
      </div>`;
  }

  private _start = async () => {
    const r = this._form?.collect();
    if (!r || !r.ok) {
      this._errors = ['Fix the highlighted fields.'];
      return;
    }
    this._busy = true;
    this._errors = [];
    this._outcome = '';
    try {
      const { data: res } = await runsStart({
        client: api,
        path: { pipelineId: this.pipelineId },
        body: r.value,
      });
      if (res?.run?.number) navigate(runPath(this.pipelineId, res.run.number));
      else this._outcome = `No run started: ${res?.outcome ?? 'unknown outcome'}.`;
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  };
}

declare global {
  interface HTMLElementTagNameMap {
    'trigger-panel': TriggerPanel;
  }
}
