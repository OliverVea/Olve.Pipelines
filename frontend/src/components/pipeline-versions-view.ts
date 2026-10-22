import { LitElement, html } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { client, errorMessages } from '../api.js';
import type { PipelineVersionSummary } from '@olve/olve-pipelines-client/src/models/index.js';
import { sharedStyles, navClick, pipelinePath } from '../v2/ui.js';
import { toActivation, type Activation } from '../v2/versions.js';
import { PollController } from '../v2/poll.js';
import './versions-panel.js';

@customElement('pipeline-versions-view')
export class PipelineVersionsView extends LitElement {
  static styles = [sharedStyles];

  @property() declare pipelineId: string;

  @state() private declare _name: string;
  @state() private declare _versions: PipelineVersionSummary[];
  @state() private declare _activation: Activation;
  @state() private declare _error: string | null;

  constructor() {
    super();
    this.pipelineId = '';
    this._name = '';
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
      const [pipeline, versions] = await Promise.all([p.get(), p.versions.get()]);
      this._name = pipeline?.name ?? '';
      this._versions = versions?.versions ?? [];
      this._activation = toActivation(versions?.activation);
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}>${this._name || 'Pipeline'}</a>
      </div>
      <h2>Versions</h2>
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
