import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state, query } from 'lit/decorators.js';
import type { PipelineVersionSummary } from '@olve/olve-pipelines-client/src/models/index.js';
import { apiPost, errorMessages, numberMap } from '../api.js';
import { sharedStyles, shortSha } from '../v2/ui.js';
import { loadVersion, type Activation } from '../v2/versions.js';
import { canonicalJson } from '../v2/manifest.js';
import { diffLines, foldDiff, type DiffHunk } from '../v2/diff.js';
import './relative-time.js';

type EntityChange = { name: string; from: number | null; to: number | null };

function changes(a: Record<string, number>, b: Record<string, number>): EntityChange[] {
  const names = [...new Set([...Object.keys(a), ...Object.keys(b)])].sort();
  return names
    .filter((n) => a[n] !== b[n])
    .map((n) => ({ name: n, from: a[n] ?? null, to: b[n] ?? null }));
}

/**
 * The pipeline's versions (newest first) with the active/draining marker, a
 * manifest diff between any two, and switch (optionally cancelling the old
 * version's running work). Fires `switched` after a successful switch.
 */
@customElement('versions-panel')
export class VersionsPanel extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .active-mark {
        color: var(--color-success);
        font-weight: 600;
        font-size: 0.78rem;
      }
      .draining-mark {
        color: var(--color-warning);
        font-weight: 600;
        font-size: 0.78rem;
      }
      .chg {
        font-size: 0.75rem;
        color: var(--color-text-muted);
        max-width: 26rem;
      }
      .chg code {
        color: var(--color-text);
      }
      .compare {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        flex-wrap: wrap;
        margin: 1rem 0 0.6rem;
        font-size: 0.85rem;
      }
      select {
        padding: 0.2rem 0.4rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-surface);
        color: var(--color-text);
      }
      .summary {
        display: flex;
        flex-wrap: wrap;
        gap: 0.3rem 1.2rem;
        font-size: 0.8rem;
        margin-bottom: 0.6rem;
      }
      .diff {
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-code-bg);
        font-family: var(--font-mono);
        font-size: 0.74rem;
        line-height: 1.45;
        overflow: auto;
        max-height: 34rem;
      }
      .ln {
        display: grid;
        grid-template-columns: 3rem 3rem 1.2rem minmax(0, 1fr);
        white-space: pre;
      }
      .ln span:nth-child(-n + 2) {
        color: var(--color-text-muted);
        text-align: right;
        padding-right: 0.5rem;
        user-select: none;
      }
      .ln.add {
        background: var(--color-diff-add);
      }
      .ln.del {
        background: var(--color-diff-del);
      }
      .skip {
        padding: 0.15rem 0.75rem;
        color: var(--color-text-muted);
        background: var(--color-surface);
        border-top: 1px solid var(--color-border);
        border-bottom: 1px solid var(--color-border);
        font-family: var(--font-sans);
      }
      label.check {
        display: flex;
        gap: 0.45rem;
        align-items: flex-start;
        font-size: 0.85rem;
        margin-top: 0.6rem;
      }
      .dialog-body {
        font-size: 0.88rem;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property({ attribute: false }) declare versions: PipelineVersionSummary[];
  @property({ attribute: false }) declare activation: Activation;

  @state() private declare _from: number | null;
  @state() private declare _to: number | null;
  @state() private declare _diff: DiffHunk[] | null;
  @state() private declare _diffError: string | null;
  @state() private declare _switchTarget: number | null;
  @state() private declare _cancelRunning: boolean;
  @state() private declare _switchErrors: string[];
  @state() private declare _busy: boolean;
  @query('dialog') private declare _dialog: HTMLDialogElement;

  constructor() {
    super();
    this.pipelineId = '';
    this.versions = [];
    this.activation = { kind: 'none' };
    this._from = null;
    this._to = null;
    this._diff = null;
    this._diffError = null;
    this._switchTarget = null;
    this._cancelRunning = false;
    this._switchErrors = [];
    this._busy = false;
  }

  willUpdate(changed: Map<string, unknown>) {
    if (changed.has('versions') && this._to === null && this.versions.length >= 1) {
      const sorted = this._sorted();
      this._to = sorted[0]?.number ?? null;
      this._from = sorted[1]?.number ?? sorted[0]?.number ?? null;
    }
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('_from') || changed.has('_to')) this._computeDiff();
  }

  private _sorted() {
    return [...this.versions].sort((a, b) => (b.number ?? 0) - (a.number ?? 0));
  }

  private async _computeDiff() {
    const from = this._from;
    const to = this._to;
    if (from === null || to === null) return;
    this._diff = null;
    this._diffError = null;
    try {
      const [a, b] = await Promise.all([loadVersion(this.pipelineId, from), loadVersion(this.pipelineId, to)]);
      if (from !== this._from || to !== this._to) return;
      const ta = a?.manifest ? canonicalJson(a.manifest) : (a?.manifestText ?? '');
      const tb = b?.manifest ? canonicalJson(b.manifest) : (b?.manifestText ?? '');
      this._diff = foldDiff(diffLines(ta, tb), 3);
    } catch (e) {
      this._diffError = errorMessages(e).join(' ');
    }
  }

  render() {
    const sorted = this._sorted();
    if (!sorted.length) return html`<p class="muted">No versions yet. A version is added when reconcile describes the pipeline.</p>`;
    const byNumber = new Map(sorted.map((v) => [v.number ?? 0, v]));
    return html`
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Version</th>
              <th>Commit</th>
              <th>Added</th>
              <th>Changed from the previous version</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            ${sorted.map((v, i) => this._row(v, sorted[i + 1]))}
          </tbody>
        </table>
      </div>
      ${sorted.length > 1
        ? html`<div class="compare">
              <span>Compare manifest</span>
              ${this._select(this._from, (n) => (this._from = n), sorted, 'from')}
              <span>→</span>
              ${this._select(this._to, (n) => (this._to = n), sorted, 'to')}
            </div>
            ${this._renderSummary(byNumber)} ${this._renderDiff()}`
        : nothing}
      ${this._renderDialog()}
    `;
  }

  private _select(value: number | null, set: (n: number) => void, sorted: PipelineVersionSummary[], label: string) {
    return html`<select aria-label="Compare ${label}" @change=${(e: Event) => set(Number((e.target as HTMLSelectElement).value))}>
      ${sorted.map((v) => html`<option value=${v.number ?? 0} ?selected=${v.number === value}>v${v.number}</option>`)}
    </select>`;
  }

  private _row(v: PipelineVersionSummary, prev: PipelineVersionSummary | undefined) {
    const n = v.number ?? 0;
    const act = this.activation;
    const isActive = act.kind === 'active' && act.version === n;
    const isTarget = act.kind === 'draining' && act.to === n;
    const isDraining = act.kind === 'draining' && act.from === n;
    const stepChanges = prev ? changes(numberMap(prev.stepVersions), numberMap(v.stepVersions)) : [];
    const modelChanges = prev ? changes(numberMap(prev.modelVersions), numberMap(v.modelVersions)) : [];
    return html`<tr>
      <td>
        <b class="mono">v${n}</b>
        ${isActive ? html`<span class="active-mark">active</span>` : nothing}
        ${isTarget ? html`<span class="draining-mark">switching to</span>` : nothing}
        ${isDraining ? html`<span class="draining-mark">draining</span>` : nothing}
      </td>
      <td class="mono" title=${v.commitSha ?? ''}>${shortSha(v.commitSha) || '—'}</td>
      <td><relative-time .value=${v.createdAt}></relative-time></td>
      <td class="chg">
        ${!prev
          ? html`first version`
          : stepChanges.length || modelChanges.length
            ? html`${[...stepChanges, ...modelChanges].map((c, i) => html`${i ? ', ' : ''}${this._chg(c)}`)}`
            : html`no step or model changes (layout, conditions or triggers)`}
      </td>
      <td>
        ${isActive || isTarget
          ? nothing
          : html`<button class="btn small" @click=${() => this._openSwitch(n)}>Switch to v${n}</button>`}
      </td>
    </tr>`;
  }

  private _chg(c: EntityChange) {
    if (c.from === null) return html`<code>${c.name}</code> added`;
    if (c.to === null) return html`<code>${c.name}</code> removed`;
    return html`<code>${c.name}</code> .${c.from}→.${c.to}`;
  }

  private _renderSummary(byNumber: Map<number, PipelineVersionSummary>) {
    const a = this._from !== null ? byNumber.get(this._from) : undefined;
    const b = this._to !== null ? byNumber.get(this._to) : undefined;
    if (!a || !b) return nothing;
    const steps = changes(numberMap(a.stepVersions), numberMap(b.stepVersions));
    const models = changes(numberMap(a.modelVersions), numberMap(b.modelVersions));
    return html`<div class="summary">
      <span>Steps: ${steps.length ? steps.map((c, i) => html`${i ? ', ' : ''}${this._chg(c)}`) : html`<span class="muted">unchanged</span>`}</span>
      <span>Models: ${models.length ? models.map((c, i) => html`${i ? ', ' : ''}${this._chg(c)}`) : html`<span class="muted">unchanged</span>`}</span>
    </div>`;
  }

  private _renderDiff() {
    if (this._diffError) return html`<p class="error">${this._diffError}</p>`;
    if (!this._diff) return html`<p class="muted">Loading manifests…</p>`;
    if (this._from === this._to) return html`<p class="muted">Pick two different versions.</p>`;
    if (!this._diff.some((h) => 'lines' in h)) return html`<p class="muted">The manifests are identical.</p>`;
    return html`<div class="diff" role="table" aria-label="Manifest diff">
      ${this._diff.map((h) =>
        'skipped' in h
          ? html`<div class="skip">${h.skipped} unchanged lines</div>`
          : h.lines.map(
              (l) => html`<div class="ln ${l.kind}">
                <span>${l.a ?? ''}</span><span>${l.b ?? ''}</span><span>${l.kind === 'add' ? '+' : l.kind === 'del' ? '−' : ''}</span><span>${l.text}</span>
              </div>`,
            ),
      )}
    </div>`;
  }

  private _renderDialog() {
    const act = this.activation;
    const current = act.kind === 'active' ? act.version : act.kind === 'draining' ? act.to : null;
    return html`<dialog @close=${() => (this._switchErrors = [])}>
      <h3>Switch to v${this._switchTarget}</h3>
      <div class="dialog-body">
        <p>
          ${current !== null ? html`v${current} stops starting new runs and drains;` : nothing} new runs start on
          v${this._switchTarget} once in-flight work has finished. Paused runs on the old version can't be continued
          afterwards.
        </p>
        <label class="check"
          ><input type="checkbox" .checked=${this._cancelRunning} @change=${(e: Event) => (this._cancelRunning = (e.target as HTMLInputElement).checked)} />
          <span>Cancel running work on the old version instead of waiting for it.</span></label
        >
      </div>
      ${this._switchErrors.length
        ? html`<div class="errors" role="alert">${this._switchErrors.map((e) => html`<p>${e}</p>`)}</div>`
        : nothing}
      <div class="dialog-actions">
        <button class="btn" @click=${() => this._dialog.close()}>Close</button>
        <button class="btn primary" ?disabled=${this._busy} @click=${this._switch}>Switch to v${this._switchTarget}</button>
      </div>
    </dialog>`;
  }

  private _openSwitch(n: number) {
    this._switchTarget = n;
    this._cancelRunning = false;
    this._switchErrors = [];
    this._dialog.showModal();
  }

  private _switch = async () => {
    this._busy = true;
    this._switchErrors = [];
    try {
      await apiPost(`/api/pipelines/${this.pipelineId}/switch`, {
        version: this._switchTarget,
        cancelRunning: this._cancelRunning,
      });
      this._dialog.close();
      this.dispatchEvent(new CustomEvent('switched'));
    } catch (e) {
      this._switchErrors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  };
}

declare global {
  interface HTMLElementTagNameMap {
    'versions-panel': VersionsPanel;
  }
}
