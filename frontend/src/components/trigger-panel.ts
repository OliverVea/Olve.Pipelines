import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import { apiPost, errorMessages } from '../api.js';
import { navigate } from '../router.js';
import { sharedStyles, runPath } from '../v2/ui.js';
import { inputSchemaOf, withDefaults, type Manifest } from '../v2/manifest.js';
import type { InputForm } from './input-form.js';
import './input-form.js';
import './stage-graph.js';

/**
 * Manual trigger: a form generated from the pipeline input model, a run plan
 * preview (stages whose `when` won't match are greyed out) and Start run.
 * Server-side validation problems are listed under the form.
 */
@customElement('trigger-panel')
export class TriggerPanel extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .layout {
        display: grid;
        grid-template-columns: minmax(18rem, 26rem) minmax(0, 1fr);
        gap: 1.5rem;
      }
      @media (max-width: 900px) {
        .layout {
          grid-template-columns: 1fr;
        }
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
      stage-graph {
        --graph-bg: var(--color-surface);
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

  @state() private declare _plan: Record<string, unknown>;
  @state() private declare _errors: string[];
  @state() private declare _busy: boolean;
  @state() private declare _outcome: string;
  @query('input-form') private declare _form: InputForm;

  constructor() {
    super();
    this.pipelineId = '';
    this.manifest = null;
    this.versionNumber = null;
    this._plan = {};
    this._errors = [];
    this._busy = false;
    this._outcome = '';
  }

  render() {
    const m = this.manifest;
    if (!m) return html`<p class="muted">No active version to run.</p>`;
    const schema = inputSchemaOf(m, m.input);
    return html`<div class="layout">
      <div>
        <h4>Input <code>${m.input}</code>${this.versionNumber ? html` on v${this.versionNumber}` : nothing}</h4>
        <input-form
          .schema=${schema}
          @input-change=${(e: CustomEvent<{ value: Record<string, unknown> }>) => (this._plan = e.detail.value)}
        ></input-form>
        ${this._errors.length
          ? html`<div class="errors" role="alert">${this._errors.map((e) => html`<p>${e}</p>`)}</div>`
          : nothing}
        <div class="actions">
          <button class="btn primary" ?disabled=${this._busy} @click=${this._start}>Start run</button>
          <button class="btn" @click=${() => this.dispatchEvent(new CustomEvent('close'))}>Close</button>
          ${this._outcome ? html`<span class="outcome">${this._outcome}</span>` : nothing}
        </div>
      </div>
      <div>
        <h4>Run plan</h4>
        <stage-graph .manifest=${m} .planInput=${withDefaults(schema, this._plan)}></stage-graph>
      </div>
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
      const res = await apiPost<{ outcome: string; run: { number: number } | null }>(
        `/api/pipelines/${this.pipelineId}/runs`,
        r.value,
      );
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
