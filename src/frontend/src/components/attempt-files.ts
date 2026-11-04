import { LitElement, html } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { runStepsFiles, type StepFile } from '@olve/api';
import { api, errorMessages } from '../api/client.js';
import { downloadFile, downloadName } from '../api/files.js';
import { sharedStyles, formatSize } from '../styles/ui.js';
import { sheetContentStyles } from './step-sheet.js';
import './relative-time.js';

/** The stored files of one succeeded attempt, each downloadable. */
@customElement('attempt-files')
export class AttemptFiles extends LitElement {
  static styles = [sharedStyles, sheetContentStyles];

  @property() declare pipelineId: string;
  @property({ type: Number }) declare run: number;
  @property() declare step: string;
  @property({ type: Number }) declare attempt: number;

  @state() declare private _files: StepFile[] | null;
  @state() declare private _error: string | null;
  private _key = '';

  constructor() {
    super();
    this.pipelineId = '';
    this.run = 0;
    this.step = '';
    this.attempt = 0;
    this._files = null;
    this._error = null;
  }

  updated() {
    const key = `${this.pipelineId}#${this.run}/${this.step}@${this.attempt}`;
    if (key !== this._key) void this._load(key);
  }

  private async _load(key: string) {
    this._key = key;
    this._files = null;
    this._error = null;
    try {
      const res = await runStepsFiles({
        client: api,
        path: { pipelineId: this.pipelineId, number: this.run, step: this.step },
        query: { attempt: this.attempt },
      });
      if (this._key === key) this._files = res.data?.files ?? [];
    } catch (e) {
      if (this._key === key) this._error = errorMessages(e).join(' ');
    }
  }

  private async _download(path: string) {
    const file = {
      pipelineId: this.pipelineId,
      number: this.run,
      step: this.step,
      attempt: this.attempt,
      path,
    };
    try {
      await downloadFile(file, downloadName(path));
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    return html`<h4 class="sheet-h">Files</h4>
      ${this._error
        ? html`<p class="error">${this._error}</p>`
        : this._files === null
          ? html`<span class="muted">Loading…</span>`
          : this._files.length === 0
            ? html`<span class="muted">No files.</span>`
            : html`<div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Path</th>
                      <th class="num">Size</th>
                      <th>Modified</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    ${this._files.map(
                      (f) =>
                        html`<tr>
                          <td class="mono">${f.path}</td>
                          <td class="num">${formatSize(f.size)}</td>
                          <td>
                            ${f.lastModified
                              ? html`<relative-time
                                  .value=${f.lastModified}
                                ></relative-time>`
                              : ''}
                          </td>
                          <td>
                            <button
                              class="btn small"
                              @click=${() => this._download(f.path ?? '')}
                            >
                              Download
                            </button>
                          </td>
                        </tr>`,
                    )}
                  </tbody>
                </table>
              </div>`}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'attempt-files': AttemptFiles;
  }
}
