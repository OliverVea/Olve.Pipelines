import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { api, errorMessages } from '../api/client.js';
import {
  ciRunsAttempts,
  pipelinesGet,
  runsAttempts,
  runStepsCancel,
  type AttemptState,
  type Attempt,
  type CiAttempt,
} from '@olve/api';
import {
  sharedStyles,
  toneOf,
  statusLabel,
  formatDuration,
  navClick,
  runPath,
  ciRunPath,
  pipelinePath,
  isActiveStatus,
  isLiveStatus,
} from '../styles/ui.js';
import { LiveController } from '../api/events.js';
import './log-pane.js';

type AttemptStatusFields = {
  type?: AttemptState;
  startedAt?: string | null;
  finishedAt?: string | null;
  cancelledAt?: string | null;
  reason?: string | null;
};

const statusOf = (a: Attempt | CiAttempt) => a.status as AttemptStatusFields;

/** The log of one step's attempt in a run (a CI run with `ci`), full height. */
@customElement('step-logs-view')
export class StepLogsView extends LitElement {
  static styles = [
    sharedStyles,
    css`
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 0.4rem 0.8rem;
        margin-bottom: 0.6rem;
      }
      .head h2 {
        margin: 0;
      }
      .head .cancel {
        margin-left: auto;
      }
      .meta {
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      .attempts {
        display: flex;
        flex-wrap: wrap;
        gap: 0.35rem;
        margin-bottom: 0.6rem;
      }
      .reason {
        margin: 0 0 0.6rem;
        font-size: 0.82rem;
        color: var(--color-text-muted);
      }
      log-pane {
        --log-max-height: calc(100vh - 16rem);
      }
    `,
  ];

  @property() declare pipelineId: string;
  @property() declare run: string;
  @property() declare step: string;
  @property({ type: Boolean }) declare ci: boolean;

  @state() declare private _name: string;
  @state() declare private _attempts: (Attempt | CiAttempt)[] | null;
  @state() declare private _attempt: number | null;
  @state() declare private _error: string | null;
  @state() declare private _busy: boolean;

  constructor() {
    super();
    this.pipelineId = '';
    this.run = '';
    this.step = '';
    this.ci = false;
    this._name = '';
    this._attempts = null;
    this._attempt = null;
    this._error = null;
    this._busy = false;
    new LiveController(
      this,
      () => this.reload(),
      (n) =>
        n.pipelineId === this.pipelineId &&
        (n.type === (this.ci ? 'ci-run' : 'run') ||
          (n.type === (this.ci ? 'ci-attempt' : 'attempt') && n.step === this.step)) &&
        String(n.run) === this.run,
    );
  }

  updated(changed: Map<string, unknown>) {
    if (changed.has('pipelineId') || changed.has('run') || changed.has('step')) {
      const a = new URLSearchParams(location.search).get('attempt');
      this._attempt = a ? Number(a) : null;
      this._attempts = null;
      this.reload();
    }
  }

  async reload() {
    const path = { pipelineId: this.pipelineId };
    try {
      const [pipeline, attempts] = await Promise.all([
        this._name ? null : pipelinesGet({ client: api, path }).then((r) => r.data),
        (this.ci ? ciRunsAttempts : runsAttempts)({
          client: api,
          path: { ...path, number: Number(this.run) },
        }).then((r) => r.data as (Attempt | CiAttempt)[] | undefined),
      ]);
      if (pipeline) this._name = pipeline.name ?? '';
      this._attempts = (attempts ?? [])
        .filter((a) => a.stepName === this.step)
        .sort((a, b) => (a.number ?? 0) - (b.number ?? 0));
      this._error = null;
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    }
  }

  private _selected(): Attempt | CiAttempt | null {
    const list = this._attempts ?? [];
    return list.find((a) => a.number === this._attempt) ?? list[list.length - 1] ?? null;
  }

  private _cancel = async () => {
    if (
      !confirm(
        `Cancel ${this.step} in run #${this.run}? The run ends Cancelled once its stage settles.`,
      )
    )
      return;
    this._busy = true;
    try {
      await runStepsCancel({
        client: api,
        path: { pipelineId: this.pipelineId, number: Number(this.run), step: this.step },
      });
      await this.reload();
    } catch (e) {
      this._error = errorMessages(e).join(' ');
    } finally {
      this._busy = false;
    }
  };

  private _select(n: number | null | undefined) {
    this._attempt = n ?? null;
    history.replaceState(history.state, '', `${location.pathname}?attempt=${n}`);
  }

  render() {
    const run = (this.ci ? ciRunPath : runPath)(this.pipelineId, Number(this.run));
    const list = this._attempts;
    const a = this._selected();
    const st = a ? statusOf(a) : null;
    return html`
      <div class="crumbs">
        <a href="/" @click=${navClick}>Pipelines</a><span>/</span>
        <a href=${pipelinePath(this.pipelineId)} @click=${navClick}
          >${this._name || 'Pipeline'}</a
        ><span>/</span>
        <a href="${run}?step=${encodeURIComponent(this.step)}" @click=${navClick}
          >Run #${this.run}</a
        >
      </div>
      <div class="head">
        <h2>${this.step}</h2>
        ${st
          ? html`<span class="st t-${toneOf(st.type)}"
              ><span class="dot"></span>${statusLabel(st.type)}</span
            >`
          : nothing}
        ${st?.startedAt
          ? html`<span class="meta"
              >${formatDuration(st.startedAt, st.finishedAt ?? st.cancelledAt)}</span
            >`
          : nothing}
        ${isActiveStatus(st?.type) && !this.ci
          ? html`<button
              class="btn danger small cancel"
              ?disabled=${this._busy}
              @click=${this._cancel}
            >
              Cancel
            </button>`
          : nothing}
      </div>
      ${list && list.length > 1
        ? html`<div class="attempts">
            ${list.map(
              (x) =>
                html`<button
                  class="btn small ${x.id === a?.id ? 'primary' : ''}"
                  @click=${() => this._select(x.number)}
                >
                  @${x.number} ${statusLabel(statusOf(x).type)}
                </button>`,
            )}
          </div>`
        : nothing}
      ${st?.reason ? html`<p class="reason">${st.reason}</p>` : nothing}
      ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
      ${a
        ? html`<log-pane
            .address=${{
              pipelineId: this.pipelineId,
              number: Number(this.run),
              step: this.step,
              attempt: a.number,
              ci: this.ci,
            }}
            ?live=${isLiveStatus(st?.type)}
          ></log-pane>`
        : list
          ? html`<p class="muted">This step has no attempt in run #${this.run}.</p>`
          : html`<p class="muted">Loading…</p>`}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'step-logs-view': StepLogsView;
  }
}
