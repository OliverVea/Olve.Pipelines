import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { client, errorMessages } from '../api.js';
import type {
  ConfigCheckResponse,
  ConfigSaveResponse,
  ConfigSnapshotInfo,
  PendingVersionInfo,
} from '@olve/olve-pipelines-client/src/models/index.js';
import { sharedStyles, navClick, secretsPath } from '../v2/ui.js';
import { loadVersion, toActivation, currentVersion } from '../v2/versions.js';
import { configEntries, documentSchema, type ConfigEntry } from '../v2/config-schema.js';
import { diffLines, foldDiff, type DiffHunk } from '../v2/diff.js';
import type { JsonSchema } from '../v2/manifest.js';
import type { ConfigEditor } from './config-editor.js';
import './config-editor.js';
import './diff-view.js';
import './relative-time.js';
import './pipeline-header.js';

type Target = { version: number; role: 'active' | 'pending'; entries: ConfigEntry[] };

/**
 * A pipeline's config document: a YAML editor validated against the active (or pending) version's
 * schema, the result of the last save, the snapshot history with rollback, and a diff between two snapshots.
 */
@customElement('pipeline-config-view')
export class PipelineConfigView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .row {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.5rem 0.9rem;
        margin: 0.6rem 0;
        font-size: 0.85rem;
      }
      .banner {
        border: 1px solid color-mix(in srgb, var(--color-warning) 55%, transparent);
        background: color-mix(in srgb, var(--color-warning) 12%, transparent);
        border-radius: var(--radius);
        padding: 0.5rem 0.75rem;
        font-size: 0.85rem;
        margin: 0.6rem 0;
      }
      .banner ul,
      .result ul {
        margin: 0.25rem 0 0;
        padding-left: 1.2rem;
      }
      .result {
        font-size: 0.85rem;
        margin: 0.6rem 0;
      }
      .ok {
        color: var(--color-success);
      }
      .bad {
        color: var(--color-danger);
      }
      .muted {
        color: var(--color-text-muted);
      }
      select {
        padding: 0.2rem 0.4rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-surface);
        color: var(--color-text);
      }
      dl.entries {
        display: grid;
        grid-template-columns: max-content 1fr;
        gap: 0.2rem 0.8rem;
        font-size: 0.8rem;
        margin: 0.4rem 0 0;
      }
      dl.entries dt {
        font-family: var(--font-mono);
      }
      dl.entries dd {
        margin: 0;
        color: var(--color-text-muted);
      }
      pre.effective {
        font-size: 0.75rem;
        background: var(--color-code-bg);
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        padding: 0.5rem 0.75rem;
        overflow: auto;
        max-height: 20rem;
      }
    `,
  ];

  @property() declare pipelineId: string;

  @state() declare private _yaml: string;
  @state() declare private _snapshot: ConfigSnapshotInfo | null;
  @state() declare private _history: ConfigSnapshotInfo[];
  @state() declare private _targets: Target[];
  @state() declare private _target: number | null;
  @state() declare private _pending: PendingVersionInfo | null;
  @state() declare private _check: ConfigCheckResponse | null;
  @state() declare private _saved: ConfigSaveResponse | null;
  @state() declare private _errors: string[];
  @state() declare private _dirty: boolean;
  @state() declare private _busy: boolean;
  @state() declare private _from: number | null;
  @state() declare private _to: number | null;
  @state() declare private _diff: DiffHunk[] | null;
  @state() declare private _error: string | null;
  @query('config-editor') declare private _editor: ConfigEditor;

  constructor() {
    super();
    this.pipelineId = '';
    this._yaml = '';
    this._snapshot = null;
    this._history = [];
    this._targets = [];
    this._target = null;
    this._pending = null;
    this._check = null;
    this._saved = null;
    this._errors = [];
    this._dirty = false;
    this._busy = false;
    this._from = null;
    this._to = null;
    this._diff = null;
    this._error = null;
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId')) this.reload(true);
    if (changed.has('_from') || changed.has('_to')) this._computeDiff();
  }

  async reload(replaceText: boolean) {
    const p = client.api.pipelines.byId(this.pipelineId);
    try {
      const [doc, history, versions, health] = await Promise.all([
        p.config.get(),
        p.config.history.get(),
        p.versions.get(),
        p.health.get(),
      ]);
      this._snapshot = doc?.snapshot ?? null;
      if (replaceText || !this._dirty) {
        this._yaml = doc?.yaml ?? '';
        this._dirty = false;
      }
      this._history = history ?? [];
      this._pending = health?.pending?.version != null ? health.pending : null;
      this._targets = await this._loadTargets(
        currentVersion(toActivation(versions?.activation)),
        this._pending,
      );
      if (this._target === null || !this._targets.some((t) => t.version === this._target))
        this._target =
          (this._targets.find((t) => t.role === 'pending') ?? this._targets[0])
            ?.version ?? null;
      if (this._from === null && this._history.length > 1) {
        this._to = this._history[0].number ?? null;
        this._from = this._history[1].number ?? null;
      }
      this._check =
        this._target === null
          ? null
          : ((await p.config.check.get({ queryParameters: { version: this._target } })) ??
            null);
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  private async _loadTargets(
    active: number | null,
    pending: PendingVersionInfo | null,
  ): Promise<Target[]> {
    const out: Target[] = [];
    if (pending && !pending.held && pending.version != null) {
      const v = await loadVersion(this.pipelineId, pending.version);
      out.push({
        version: pending.version,
        role: 'pending',
        entries: configEntries(v?.manifest),
      });
    }
    if (active !== null) {
      const v = await loadVersion(this.pipelineId, active);
      out.push({ version: active, role: 'active', entries: configEntries(v?.manifest) });
    }
    return out;
  }

  private get _schema(): JsonSchema | null {
    const t = this._targets.find((x) => x.version === this._target);
    return t ? documentSchema(t.entries) : null;
  }

  private async _save() {
    this._busy = true;
    this._errors = [];
    try {
      this._saved =
        (await client.api.pipelines
          .byId(this.pipelineId)
          .config.put({ yaml: this._editor.text })) ?? null;
      this._dirty = false;
      await this.reload(true);
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }

  private async _rollback(n: number) {
    if (!confirm(`Save config #${n} again as the newest snapshot?`)) return;
    this._busy = true;
    this._errors = [];
    try {
      this._saved =
        (await client.api.pipelines
          .byId(this.pipelineId)
          .config.rollback.post({ snapshot: n })) ?? null;
      await this.reload(true);
    } catch (e) {
      this._errors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }

  private async _computeDiff() {
    const from = this._from;
    const to = this._to;
    if (from === null || to === null) return;
    this._diff = null;
    try {
      const p = client.api.pipelines.byId(this.pipelineId);
      const [a, b] = await Promise.all([
        p.config.get({ queryParameters: { snapshot: from } }),
        p.config.get({ queryParameters: { snapshot: to } }),
      ]);
      if (from !== this._from || to !== this._to) return;
      this._diff = foldDiff(diffLines(a?.yaml ?? '', b?.yaml ?? ''), 3);
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  render() {
    const target = this._targets.find((t) => t.version === this._target);
    return html`
      <pipeline-header .pipelineId=${this.pipelineId} tab="config"></pipeline-header>
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${this._renderPending()}
      <section>
        <div class="row">
          <span
            >${this._snapshot
              ? html`config #${this._snapshot.number}, saved by ${this._snapshot.author}
                  <relative-time .value=${this._snapshot.createdAt}></relative-time>`
              : html`<span class="muted">No config saved yet.</span>`}</span
          >
          ${this._targets.length
            ? html`<label
                >Validate against
                <select
                  @change=${(e: Event) =>
                    this._selectTarget(Number((e.target as HTMLSelectElement).value))}
                >
                  ${this._targets.map(
                    (t) =>
                      html`<option
                        value=${t.version}
                        ?selected=${t.version === this._target}
                      >
                        v${t.version} (${t.role})
                      </option>`,
                  )}
                </select></label
              >`
            : nothing}
        </div>
        <config-editor
          .value=${this._yaml}
          .schema=${this._schema}
          @change=${() => (this._dirty = true)}
        ></config-editor>
        <div class="row">
          <button class="btn" ?disabled=${this._busy} @click=${() => this._save()}>
            Save as a new snapshot
          </button>
          ${this._dirty ? html`<span class="muted">unsaved changes</span>` : nothing}
        </div>
        ${this._errors.length
          ? html`<div class="result bad">
              Not saved:
              <ul>
                ${this._errors.map((e) => html`<li>${e}</li>`)}
              </ul>
            </div>`
          : nothing}
        ${this._renderSaved()} ${target ? this._renderEntries(target) : nothing}
      </section>
      ${this._renderCheck()} ${this._renderHistory()}
    `;
  }

  private async _selectTarget(version: number) {
    this._target = version;
    try {
      this._check =
        (await client.api.pipelines
          .byId(this.pipelineId)
          .config.check.get({ queryParameters: { version } })) ?? null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  private _renderPending() {
    const p = this._pending;
    if (!p) return nothing;
    return html`<div class="banner">
      <b>${p.held ? `v${p.version} is held` : `v${p.version} is pending`}</b>
      ${p.held
        ? ': new runs wait until its config is satisfied again.'
        : ': it activates as soon as its config and secrets are satisfied.'}
      <ul>
        ${(p.reasons ?? []).map((r) => html`<li>${r}</li>`)}
        ${(p.changedDefaults ?? []).map(
          (c) =>
            html`<li>
              v${p.version} changes the default of <code>${c.path}</code> (${c.from} →
              ${c.to})
            </li>`,
        )}
      </ul>
      ${(p.reasons ?? []).some((r) => r.startsWith('secret '))
        ? html`<a href=${secretsPath(this.pipelineId)} @click=${navClick}>Set secrets</a>`
        : nothing}
    </div>`;
  }

  private _renderSaved() {
    const s = this._saved;
    if (!s) return nothing;
    return html`<div class="result">
      <span class="ok">Saved config #${s.snapshot?.number}.</span>
      ${s.activatedVersion != null ? html` Activated v${s.activatedVersion}.` : nothing}
      ${s.pending && !s.pending.satisfied
        ? html` v${s.pending.version} still waits: ${this._reasons(s.pending).join('; ')}.`
        : nothing}
    </div>`;
  }

  private _renderEntries(t: Target) {
    if (!t.entries.length)
      return html`<p class="muted">v${t.version} declares no config entries.</p>`;
    return html`<details>
      <summary>Entries of v${t.version}</summary>
      <dl class="entries">
        ${t.entries.map(
          (e) =>
            html`<dt>${e.name}${e.required ? '' : '?'}</dt>
              <dd>${e.description}</dd>`,
        )}
      </dl>
    </details>`;
  }

  private _reasons(c: ConfigCheckResponse): string[] {
    return [
      ...(c.problems ?? []).map((p) => `config ${p}`),
      ...(c.missingSecrets ?? []).map((s) => `secret ${s} not set`),
    ];
  }

  private _renderCheck() {
    const c = this._check;
    if (!c) return nothing;
    return html`<section>
      <h3>Saved config against v${c.version}</h3>
      <p class=${c.satisfied ? 'ok' : 'bad'}>
        ${c.satisfied ? 'Satisfied.' : this._reasons(c).join('; ')}
      </p>
      ${(c.changedDefaults ?? []).length
        ? html`<ul>
            ${c.changedDefaults!.map(
              (d) =>
                html`<li>
                  v${c.version} changes the default of <code>${d.path}</code> (${d.from} →
                  ${d.to})
                </li>`,
            )}
          </ul>`
        : nothing}
      ${c.effective
        ? html`<details>
            <summary>Effective config (defaults applied)</summary>
            <pre class="effective">
${JSON.stringify(JSON.parse(c.effective), null, 2)}</pre
            >
          </details>`
        : nothing}
    </section>`;
  }

  private _renderHistory() {
    if (!this._history.length) return nothing;
    return html`<section>
      <h3>History</h3>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Config</th>
              <th>Author</th>
              <th>Saved</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            ${this._history.map(
              (s, i) =>
                html`<tr>
                  <td>
                    <b class="mono">#${s.number}</b>${s.restoredFrom != null
                      ? html` <span class="muted">rollback to #${s.restoredFrom}</span>`
                      : nothing}
                  </td>
                  <td>${s.author}</td>
                  <td><relative-time .value=${s.createdAt}></relative-time></td>
                  <td>
                    ${i === 0
                      ? html`<span class="muted">current</span>`
                      : html`<button
                          class="btn small"
                          ?disabled=${this._busy}
                          @click=${() => this._rollback(s.number!)}
                        >
                          Roll back to #${s.number}
                        </button>`}
                  </td>
                </tr>`,
            )}
          </tbody>
        </table>
      </div>
      ${this._history.length > 1
        ? html`<div class="row">
              <span>Compare</span>
              ${this._select(this._from, (n) => (this._from = n), 'from')}
              <span>→</span>
              ${this._select(this._to, (n) => (this._to = n), 'to')}
            </div>
            ${this._from === this._to
              ? html`<p class="muted">Pick two different snapshots.</p>`
              : this._diff && !this._diff.some((h) => 'lines' in h)
                ? html`<p class="muted">The snapshots are identical.</p>`
                : this._diff
                  ? html`<diff-view label="Config diff" .hunks=${this._diff}></diff-view>`
                  : nothing}`
        : nothing}
    </section>`;
  }

  private _select(value: number | null, set: (n: number) => void, label: string) {
    return html`<select
      aria-label="Compare ${label}"
      @change=${(e: Event) => set(Number((e.target as HTMLSelectElement).value))}
    >
      ${this._history.map(
        (s) =>
          html`<option value=${s.number ?? 0} ?selected=${s.number === value}>
            #${s.number}
          </option>`,
      )}
    </select>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'pipeline-config-view': PipelineConfigView;
  }
}
