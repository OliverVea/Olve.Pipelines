import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { RunChanges } from '@olve/api';
import { sharedStyles, headerStyles, navClick, runPath, shortSha } from '../styles/ui.js';
import { hasChanges } from '../model/changes.js';
import './relative-time.js';

const FOLDED = 8;

/** What a run changes since the newest older run that succeeded: version and config moves, and its commits. */
@customElement('run-changes')
export class RunChangesView extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
    css`
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.3rem 0.75rem;
        margin-bottom: 0.4rem;
      }
      .head h3,
      .head h4 {
        margin: 0;
        flex: 1;
      }
      .head h4 {
        font-size: 0.8rem;
        font-weight: 600;
        color: var(--color-text-muted);
      }
      .meta {
        display: flex;
        flex-wrap: wrap;
        gap: 0.3rem 1.2rem;
        margin: 0 0 0.4rem;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .meta b {
        font-weight: 500;
        color: var(--color-text);
      }
      ul {
        list-style: none;
        margin: 0;
        padding: 0;
        font-size: 0.84rem;
      }
      li {
        display: grid;
        grid-template-columns: 5.2rem minmax(0, 1fr) auto auto;
        gap: 0 0.9rem;
        align-items: baseline;
        padding: 0.18rem 0;
      }
      .title {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .who,
      .when {
        color: var(--color-text-muted);
        font-size: 0.8rem;
        white-space: nowrap;
      }
      @media (max-width: 640px) {
        li {
          grid-template-columns: 4.6rem minmax(0, 1fr);
        }
        .who,
        .when {
          display: none;
        }
      }
      .errors {
        margin: 0;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property({ attribute: false }) declare changes: RunChanges | null;
  @property({ type: Number }) declare versionNumber: number;
  @property({ type: Number }) declare configSnapshot: number | null;
  /** `h3` on the run page, `h4` inside a row of the runs list. */
  @property() declare heading: 'h3' | 'h4';

  @state() declare private _all: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.changes = null;
    this.versionNumber = 0;
    this.configSnapshot = null;
    this.heading = 'h3';
    this._all = false;
  }

  render() {
    const c = this.changes;
    if (!hasChanges(c, this.versionNumber, this.configSnapshot)) return nothing;
    const title = html`Changes since
      <a href=${runPath(this.pipelineId, c.sinceNumber)} @click=${navClick}
        >#${c.sinceNumber}</a
      >`;
    const version = c.sinceVersionNumber !== this.versionNumber;
    const config = (c.sinceConfigSnapshot ?? null) !== (this.configSnapshot ?? null);
    return html`<div class="head">
        ${this.heading === 'h3' ? html`<h3>${title}</h3>` : html`<h4>${title}</h4>`}
        ${c.compareUrl
          ? html`<a class="btn small" href=${c.compareUrl} target="_blank" rel="noopener"
              >Compare on GitHub</a
            >`
          : nothing}
      </div>
      ${version || config
        ? html`<div class="meta">
            ${version
              ? html`<span
                  >Version <b>v${c.sinceVersionNumber}</b> →
                  <b>v${this.versionNumber}</b></span
                >`
              : nothing}
            ${config
              ? html`<span
                  >Config <b>${snapshot(c.sinceConfigSnapshot)}</b> →
                  <b>${snapshot(this.configSnapshot)}</b></span
                >`
              : nothing}
          </div>`
        : nothing}
      ${c.commitsError
        ? html`<div class="errors" role="alert">
            <p>Couldn't list the commits: ${c.commitsError}</p>
          </div>`
        : this._commits(c)}`;
  }

  private _commits(c: RunChanges) {
    const commits = c.commits ?? [];
    if (!commits.length) return nothing;
    const shown = this._all ? commits : commits.slice(0, FOLDED);
    return html`<ul>
        ${shown.map(
          (x) =>
            html`<li>
              <a class="mono" href=${x.url} target="_blank" rel="noopener"
                >${shortSha(x.sha)}</a
              ><span class="title" title=${x.title}>${x.title}</span
              ><span class="who">${x.author}</span
              ><span class="when"
                ><relative-time .value=${x.committedAt}></relative-time
              ></span>
            </li>`,
        )}
      </ul>
      ${commits.length > FOLDED
        ? html`<button
            class="more"
            aria-expanded=${this._all}
            @click=${() => (this._all = !this._all)}
          >
            Show all ${c.totalCommits ?? commits.length}
          </button>`
        : nothing}`;
  }
}

const snapshot = (n: number | null | undefined) => (n == null ? 'none' : `#${n}`);

declare global {
  interface HTMLElementTagNameMap {
    'run-changes': RunChangesView;
  }
}
