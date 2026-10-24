import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import { sharedStyles } from '../v2/ui.js';

/** Asks the user to type `delete <name>` before `onConfirm` runs. */
@customElement('confirm-delete-dialog')
export class ConfirmDeleteDialog extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .dialog-body {
        font-size: 0.88rem;
      }
      code {
        font-family: var(--font-mono);
        background: var(--color-bg);
        padding: 0 0.25rem;
        border-radius: 4px;
      }
      input {
        width: 100%;
        box-sizing: border-box;
        margin-top: 0.4rem;
        padding: 0.45rem 0.6rem;
        font: inherit;
        color: var(--color-text);
        background: var(--color-bg);
        border: 1px solid var(--color-border);
        border-radius: 6px;
      }
      .btn.danger:not(:disabled) {
        border-color: var(--color-danger);
        color: var(--color-danger);
      }
    `,
  ];

  @property() declare name: string;
  @property() declare consequence: string;
  @property({ attribute: false }) declare onConfirm: () => Promise<void>;

  @state() declare private _typed: string;
  @state() declare private _busy: boolean;
  @state() declare private _error: string | null;
  @query('dialog') declare private _dialog: HTMLDialogElement;
  @query('input') declare private _input: HTMLInputElement;

  constructor() {
    super();
    this.name = '';
    this.consequence = '';
    this._typed = '';
    this._busy = false;
    this._error = null;
  }

  open() {
    this._typed = '';
    this._error = null;
    this._dialog.showModal();
    this._input.focus();
  }

  private get _phrase() {
    return `delete ${this.name}`;
  }

  render() {
    const matches = this._typed === this._phrase;
    return html`<dialog>
      <h3>Delete ${this.name}?</h3>
      <div class="dialog-body">
        ${this.consequence ? html`<p>${this.consequence}</p>` : nothing}
        <label>
          Type <code>${this._phrase}</code> to confirm.
          <input
            .value=${this._typed}
            autocomplete="off"
            spellcheck="false"
            @input=${(e: Event) => (this._typed = (e.target as HTMLInputElement).value)}
            @keydown=${(e: KeyboardEvent) =>
              e.key === 'Enter' && matches && this._confirm()}
          />
        </label>
      </div>
      ${this._error
        ? html`<div class="errors" role="alert"><p>${this._error}</p></div>`
        : nothing}
      <div class="dialog-actions">
        <button class="btn" @click=${() => this._dialog.close()}>Cancel</button>
        <button
          class="btn danger"
          ?disabled=${!matches || this._busy}
          @click=${this._confirm}
        >
          Delete
        </button>
      </div>
    </dialog>`;
  }

  private _confirm = async () => {
    this._busy = true;
    this._error = null;
    try {
      await this.onConfirm();
      this._dialog.close();
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._busy = false;
    }
  };
}

declare global {
  interface HTMLElementTagNameMap {
    'confirm-delete-dialog': ConfirmDeleteDialog;
  }
}
