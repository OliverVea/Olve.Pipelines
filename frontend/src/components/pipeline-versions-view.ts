import { LitElement, html } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, errorMessages } from '../api.js';
import type { PipelineVersionSummary } from '@olve/olve-pipelines-client/src/models/index.js';
import { sharedStyles } from '../v2/ui.js';
import { toActivation, type Activation } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import './versions-panel.js';
import './pipeline-header.js';

@customElement('pipeline-versions-view')
export class PipelineVersionsView extends LitElement {
  static styles = [sharedStyles];

  @property() declare pipelineId: string;

  @state() declare private _versions: PipelineVersionSummary[];
  @state() declare private _activation: Activation;
  @state() declare private _error: string | null;

  constructor() {
    super();
    this.pipelineId = '';
    this._versions = [];
    this._activation = { kind: 'none' };
    this._error = null;
    new PollController(this, () => this.reload());
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload();
  }

  async reload() {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const versions = await p.versions.get();
      this._versions = versions?.versions ?? [];
      this._activation = toActivation(versions?.activation);
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="versions"></pipeline-header>
      ${this._error ? html`<p class="error">${this._error}</p>` : ''}
      <section>
        <versions-panel
          .pipelineId=${this.pipelineId}
          .versions=${this._versions}
          .activation=${this._activation}
          @switched=${() => this.reload()}
        ></versions-panel>
      </section>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-versions-view': PipelineVersionsView;
  }
}
