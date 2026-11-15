import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import {
  bindingsStatus,
  ciPullsGet,
  ciPullsRun,
  ciRunsCancel,
  ciRunsGet,
  pipelinesGet,
  type CiRun,
  type CiRunStep,
  type PullDetail,
} from '@olve/api';
import { api, errorMessages } from '../api/client.js';
import {
  sharedStyles,
  headerStyles,
  statusCardStyles,
  formatDuration,
  navClick,
  shortSha,
  pipelinePath,
  ciPath,
  ciQueuePath,
  ciRunPath,
  ciStepLogsPath,
} from '../styles/ui.js';
import { LiveController, forPipeline } from '../api/events.js';
import { navigate } from '../router.js';
import { ciManifest, runWhy } from '../model/ci.js';
import type { StepState } from './stage-graph.js';
import { ciStyles, ciStatus, stepStrip, commitUrl, pullUrl } from './ci-parts.js';
import './stage-graph.js';
import './relative-time.js';

/**
 * One CI run: what it tests (a pull request, or a queue batch), its commits and GitHub check, the
 * stage graph coloured by step status, and the other runs of its pull request. Cancel, rerun.
 */
@customElement('ci-run-view')
export class CiRunView extends LitElement {
  static styles = [
    sharedStyles,
    headerStyles,
    statusCardStyles,
    ciStyles,
    css`
      .graph {
        margin-top: 1rem;
      }
      section.runs {
        margin-top: 1.25rem;
      }
      .line .pill {
        align-self: center;
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare run: string;

  @state() declare private _run: CiRun | null;
  @state() declare private _pull: PullDetail | null;
  @state() declare private _name: string;
  @state() declare private _repo: string | null;
  @state() declare private _branch: string;
  @state() declare private _head: string | null;
  @state() declare private _error: string | null;
  @state() declare private _actionErrors: string[];
  @state() declare private _busy: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.run = '';
    this._run = null;
    this._pull = null;
    this._name = '';
    this._repo = null;
    this._branch = 'main';
    this._head = null;
    this._error = null;
    this._actionErrors = [];
    this._busy = false;
    new LiveController(
      this,
      () => this.reload(),
      forPipeline(() => this.pipelineId, 'ci-run', 'ci-attempt', 'pull'),
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') && this.pipelineId) {
      const path = { pipelineId: this.pipelineId };
      pipelinesGet({ client: api, path })
        .then((r) => {
          this._name = r.data?.name ?? '';
          this._repo = r.data?.repo ?? null;
        })
        .catch(() => {});
    }
    if (changed.has('pipelineId') || changed.has('run')) {
      this._run = null;
      this._pull = null;
      this._actionErrors = [];
      this.reload();
    }
  }

  async reload() {
    const key = `${this.pipelineId}#${this.run}`;
    const path = { pipelineId: this.pipelineId };
    try {
      const [{ data: run }, binding] = await Promise.all([
        ciRunsGet({ client: api, path: { ...path, number: Number(this.run) } }),
        bindingsStatus({ client: api, path })
          .then((r) => r.data ?? null)
          .catch(() => null),
      ]);
      const pull =
        run?.kind === 'pull' && run.pulls[0] !== undefined
          ? await ciPullsGet({ client: api, path: { ...path, pull: run.pulls[0] } })
              .then((r) => r.data ?? null)
              .catch(() => null)
          : null;
      if (key !== `${this.pipelineId}#${this.run}`) return;
      this._run = run ?? null;
      this._pull = pull;
      if (binding?.branch) this._branch = binding.branch;
      this._head = binding?.head?.sha ?? binding?.lastPushSha ?? null;
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  private async _cancel() {
    this._busy = true;
    try {
      await ciRunsCancel({
        client: api,
        path: { pipelineId: this.pipelineId, number: Number(this.run) },
      });
      this._actionErrors = [];
      await this.reload();
    } catch (e) {
      this._actionErrors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }

  private async _rerun(pull: number) {
    this._busy = true;
    try {
      const { data } = await ciPullsRun({
        client: api,
        path: { pipelineId: this.pipelineId, pull },
      });
      this._actionErrors = [];
      if (data) navigate(ciRunPath(this.pipelineId, data.number));
    } catch (e) {
      this._actionErrors = errorMessages(e);
    } finally {
      this._busy = false;
    }
  }

  private _states(r: CiRun): Map<string, StepState> {
    const states = new Map<string, StepState>();
    const state = (s: Omit<CiRunStep, 'cells'>): StepState => ({
      status: s.status,
      reason: s.reason,
      attemptCount: s.attemptCount,
      latestAttempt: s.latestAttempt,
      startedAt: s.startedAt,
      finishedAt: s.finishedAt,
      links: s.links,
      logsHref: s.latestAttempt
        ? ciStepLogsPath(this.pipelineId, r.number, s.name, s.latestAttempt)
        : undefined,
      ...(s.reusedFrom
        ? {
            runNumber: s.reusedFrom.run,
            runHref: ciRunPath(this.pipelineId, s.reusedFrom.run),
          }
        : {}),
    });
    for (const stage of r.stages)
      for (const s of stage.steps) {
        states.set(s.name, {
          ...state(s),
          ...(s.cells
            ? {
                cells: s.cells.map((c) => ({
                  cell: c.cell,
                  status: c.status,
                  reason: c.reason,
                  attemptCount: c.attemptCount,
                  latestAttempt: c.latestAttempt,
                  startedAt: c.startedAt,
                  finishedAt: c.finishedAt,
                  logsHref: c.latestAttempt
                    ? ciStepLogsPath(this.pipelineId, r.number, c.name, c.latestAttempt)
                    : undefined,
                })),
              }
            : {}),
        });
        for (const c of s.cells ?? []) states.set(c.name, state(c));
      }
    return states;
  }

  render() {
    const r = this._run;
    if (!r)
      return this._error
        ? html`<p class="error">${this._error}</p>`
        : html`<p class="muted">Loading run…</p>`;
    const queue = r.kind === 'queue';
    const pull = r.pulls[0];
    const live = r.status === 'Running';
    const stale =
      !queue && !!this._pull?.stale && this._pull.latestRun?.number === r.number;
    const why = runWhy(r);
    return html`
      <div class="crumbs">
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}
          >${this._name || 'Pipeline'}</a
        ><span>/</span><a href=${ciPath(this.pipelineId)} @click=${navClick}>CI</a
        ><span>/</span>
        ${queue
          ? html`<a href=${ciQueuePath(this.pipelineId)} @click=${navClick}>Queue</a>`
          : html`<span>#${pull}</span>`}
      </div>
      <div class="line">
        <h2>
          <span class="crumb">${queue ? 'Queue' : `#${pull}`}</span
          ><span class="sep">/</span>Run #${r.number}
        </h2>
        ${stale ? html`<span class="pill">stale</span>` : nothing} ${ciStatus(r.status)}
        ${why
          ? html`<span class="why"
              >${'supersededBy' in why
                ? html`superseded by
                    <a
                      href=${ciRunPath(this.pipelineId, why.supersededBy)}
                      @click=${navClick}
                      >#${why.supersededBy}</a
                    >`
                : why.text}</span
            >`
          : nothing}
        <span class="spacer"></span>
        ${live
          ? html`<button
              class="btn danger"
              ?disabled=${this._busy}
              @click=${this._cancel}
            >
              Cancel
            </button>`
          : nothing}
        ${!queue && pull !== undefined
          ? html`<button
              class="btn"
              ?disabled=${this._busy}
              @click=${() => this._rerun(pull)}
            >
              Rerun
            </button>`
          : nothing}
      </div>
      ${this._actionErrors.length
        ? html`<div class="errors" role="alert">
            ${this._actionErrors.map((e) => html`<p>${e}</p>`)}
          </div>`
        : nothing}
      ${r.problems.length
        ? html`<div class="errors" role="alert">
            ${r.problems.map((p) => html`<p>${p}</p>`)}
          </div>`
        : nothing}
      ${this._status(r, queue, stale)}
      ${r.stages.length
        ? html`<div class="graph">
            <stage-graph
              .manifest=${ciManifest(r)}
              .states=${this._states(r)}
            ></stage-graph>
          </div>`
        : nothing}
      ${!queue && this._pull ? this._history(this._pull) : nothing}
    `;
  }

  private _status(r: CiRun, queue: boolean, stale: boolean) {
    const head = r.pullHeads[0];
    const commit = (sha: string) => {
      const url = commitUrl(this._repo, sha);
      return url
        ? html`<a class="value mono" href=${url} target="_blank">${shortSha(sha)}</a>`
        : html`<span class="value mono">${shortSha(sha)}</span>`;
    };
    return html`<div class="status">
      ${queue
        ? html`<div class="cell grow">
            <span class="label">Pull requests</span>
            ${r.pullHeads.map(
              (p, i) =>
                html`<a
                  class="value"
                  href=${pullUrl(this._repo, p.number) ?? '#'}
                  target="_blank"
                  >${i + 1}. #${p.number} ${p.title}</a
                >`,
            )}
            <span class="sub mono">rebased onto main ${shortSha(r.mainSha)}</span>
          </div>`
        : html`<div class="cell grow">
            <span class="label">Pull request</span>
            ${head
              ? html`<a
                    class="value"
                    href=${pullUrl(this._repo, head.number) ?? '#'}
                    target="_blank"
                    >${head.title}</a
                  ><span class="sub mono">${head.branch} → ${this._branch}</span>`
              : nothing}
          </div>`}
      <div class="cell">
        <span class="label">Commit</span>
        ${queue ? commit(r.testedSha) : head ? commit(head.headSha) : nothing}
        ${queue ? html`<span class="sub mono">olve-queue/${r.number}</span>` : nothing}
      </div>
      <div class="cell">
        <span class="label">GitHub check</span>
        <span class="value">${ciStatus(r.status)}</span>
        <span class="sub mono">${queue ? 'olve/merge-queue' : 'olve/ci'}</span>
      </div>
      ${stale
        ? html`<div class="cell">
            <span class="label">Main</span>
            ${commit(r.mainSha)}
            <span class="sub mono">now ${shortSha(this._head)}</span>
          </div>`
        : nothing}
      <div class="cell">
        <span class="label">Started</span>
        <span class="value"><relative-time .value=${r.createdAt}></relative-time></span>
        <span class="sub">${formatDuration(r.createdAt, r.finishedAt)}</span>
      </div>
    </div>`;
  }

  private _history(p: PullDetail) {
    return html`<section class="runs">
      <h3>Runs of pull request #${p.number}</h3>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Run</th>
              <th>Status</th>
              <th>Commit</th>
              <th class="hide-phone">Steps</th>
              <th class="hide-phone">Started</th>
              <th class="num hide-phone">Duration</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            ${p.runs.map((h) => {
              const why = runWhy(h);
              const url = commitUrl(this._repo, h.testedSha);
              return html`<tr>
                <td>
                  <a href=${ciRunPath(this.pipelineId, h.number)} @click=${navClick}
                    ><b>#${h.number}</b></a
                  >
                </td>
                <td>${ciStatus(h.status)}</td>
                <td class="mono">
                  ${url
                    ? html`<a href=${url} target="_blank">${shortSha(h.testedSha)}</a>`
                    : shortSha(h.testedSha)}
                </td>
                <td class="hide-phone">${stepStrip(h.steps)}</td>
                <td class="hide-phone">
                  <relative-time .value=${h.createdAt}></relative-time>
                </td>
                <td class="num hide-phone">
                  ${formatDuration(h.createdAt, h.finishedAt)}
                </td>
                <td class="why">
                  ${why && 'supersededBy' in why
                    ? html`superseded by
                        <a
                          href=${ciRunPath(this.pipelineId, why.supersededBy)}
                          @click=${navClick}
                          >#${why.supersededBy}</a
                        >`
                    : (why?.text ?? nothing)}
                </td>
              </tr>`;
            })}
          </tbody>
        </table>
      </div>
    </section>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'ci-run-view': CiRunView;
  }
}
