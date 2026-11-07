import { LitElement, css, html, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import {
  type Cell,
  type CellState,
  type Matrix,
  cellAddress,
  cellLabel,
  gridOf,
  sameCell,
} from '../model/matrix.js';
import { formatDuration, sharedStyles, statusLabel, toneOf } from '../styles/ui.js';

/**
 * A matrix step's cells. `compact` is the card's view: one pill per cell, or a grid when the matrix
 * has two axes. Otherwise a table for the step sheet, one row per cell with its logs and a rerun.
 * Events: `cell-select` {cell}, `cell-rerun` {cell}.
 */
@customElement('matrix-cells')
export class MatrixCells extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        display: block;
      }
      .pills {
        display: flex;
        flex-wrap: wrap;
        gap: 0.25rem;
        margin-top: 0.35rem;
      }
      .pill {
        display: inline-flex;
        align-items: center;
        gap: 0.3rem;
        max-width: 100%;
        padding: 0.05rem 0.4rem 0.05rem 0.3rem;
        border: 1px solid var(--color-border);
        border-radius: 999px;
        background: none;
        color: var(--color-text);
        font: inherit;
        font-size: 0.7rem;
        cursor: pointer;
      }
      .pill:hover,
      .sq:hover {
        border-color: var(--color-primary);
      }
      .pill .dot,
      td .dot {
        flex: none;
        width: 0.5rem;
        height: 0.5rem;
        border-radius: 50%;
        background: var(--tone, var(--color-pending));
      }
      .pill .v {
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .t-run .dot,
      .t-run.sq {
        animation: pulse 1.6s ease-in-out infinite;
      }
      .grid {
        margin-top: 0.4rem;
        border-collapse: separate;
        border-spacing: 3px;
        font-size: 0.66rem;
        color: var(--color-text-muted);
      }
      .grid th,
      .grid td {
        border: 0;
        padding: 0;
        background: none;
      }
      .grid th {
        font-weight: 500;
        padding: 0 0.15rem;
        text-align: left;
        white-space: nowrap;
      }
      .grid thead th {
        text-align: center;
      }
      .sq {
        display: block;
        width: 1.1rem;
        height: 1.1rem;
        margin: auto;
        padding: 0;
        border: 1px solid transparent;
        border-radius: 3px;
        background: var(--tone, var(--color-pending));
        cursor: pointer;
      }
      .t-idle.sq {
        background: transparent;
        border-color: var(--color-border);
      }
      .hole {
        display: block;
        width: 1.1rem;
        height: 1.1rem;
        margin: auto;
        border-radius: 3px;
        background: repeating-linear-gradient(
          45deg,
          transparent 0 3px,
          var(--color-border) 3px 4px
        );
      }
      .axes {
        margin: 0 0 0.5rem;
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
      .axes code {
        font-size: 0.78rem;
      }
      td.cell {
        white-space: nowrap;
      }
      td.cell .in {
        display: flex;
        align-items: center;
        gap: 0.45rem;
      }
      tr.selected td {
        background: color-mix(in srgb, var(--color-primary) 8%, transparent);
      }
      .reason {
        color: var(--color-text-muted);
        font-size: 0.78rem;
      }
    `,
  ];

  @property({ attribute: false }) declare matrix: Matrix | null;
  @property({ attribute: false }) declare cells: CellState[] | null;
  @property() declare step: string;
  @property({ type: Boolean }) declare compact: boolean;
  @property({ attribute: false }) declare selected: Cell | null;

  constructor() {
    super();
    this.matrix = null;
    this.cells = null;
    this.step = '';
    this.compact = false;
    this.selected = null;
  }

  private _state(cell: Cell): CellState {
    return this.cells?.find((c) => sameCell(c.cell, cell)) ?? { cell, status: 'not-run' };
  }

  private _emit(e: Event, name: string, cell: Cell) {
    e.stopPropagation();
    this.dispatchEvent(
      new CustomEvent(name, { detail: { cell }, bubbles: true, composed: true }),
    );
  }

  private _title(s: CellState) {
    return `${cellLabel(s.cell)}: ${statusLabel(s.status)}${s.reason ? ` — ${s.reason}` : ''}`;
  }

  render() {
    const m = this.matrix;
    if (!m) return nothing;
    return this.compact ? this._compact(m) : this._table(m);
  }

  private _compact(m: Matrix) {
    const g = gridOf(m);
    if (!g)
      return html`<div class="pills">
        ${m.cells.map((cell) => {
          const s = this._state(cell);
          return html`<button
            class="pill t-${toneOf(s.status)}"
            title=${this._title(s)}
            aria-label=${this._title(s)}
            @click=${(e: Event) => this._emit(e, 'cell-select', cell)}
          >
            <span class="dot"></span><span class="v">${cellLabel(cell)}</span>
          </button>`;
        })}
      </div>`;
    const rows = m.axes[g.rowAxis]!;
    const cols = m.axes[g.colAxis]!;
    return html`<table class="grid" aria-label="cells by ${g.rowAxis} and ${g.colAxis}">
      <thead>
        <tr>
          <th></th>
          ${cols.map((c) => html`<th scope="col">${c}</th>`)}
        </tr>
      </thead>
      <tbody>
        ${rows.map(
          (r) =>
            html`<tr>
              <th scope="row">${r}</th>
              ${cols.map((c) => {
                const cell = m.cells.find(
                  (x) => x[g.rowAxis] === r && x[g.colAxis] === c,
                );
                if (!cell)
                  return html`<td>
                    <span class="hole" title="${r} · ${c}: excluded"></span>
                  </td>`;
                const s = this._state(cell);
                return html`<td>
                  <button
                    class="sq t-${toneOf(s.status)}"
                    title=${this._title(s)}
                    aria-label=${this._title(s)}
                    @click=${(e: Event) => this._emit(e, 'cell-select', cell)}
                  ></button>
                </td>`;
              })}
            </tr>`,
        )}
      </tbody>
    </table>`;
  }

  private _table(m: Matrix) {
    return html`<p class="axes">
        ${Object.entries(m.axes).map(
          ([axis, values], i) =>
            html`${i > 0 ? ' × ' : ''}<code>${axis}</code> ${values.join(', ')}`,
        )}
        · ${m.cells.length} cells
      </p>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Cell</th>
              <th>Status</th>
              <th>Attempt</th>
              <th class="num">Took</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            ${m.cells.map((cell) => {
              const s = this._state(cell);
              const picked = this.selected && sameCell(this.selected, cell);
              return html`<tr class=${picked ? 'selected' : ''}>
                <td class="cell t-${toneOf(s.status)}">
                  <div class="in">
                    <span class="dot"></span>
                    <span class="mono" title=${cellAddress(this.step, cell)}
                      >${cellLabel(cell)}</span
                    >
                  </div>
                  ${s.reason ? html`<div class="reason">${s.reason}</div>` : nothing}
                </td>
                <td>${statusLabel(s.status)}</td>
                <td>${s.latestAttempt ? `@${s.latestAttempt}` : ''}</td>
                <td class="num">
                  ${s.startedAt ? formatDuration(s.startedAt, s.finishedAt) : ''}
                </td>
                <td>
                  ${s.logsHref ? html`<a href=${s.logsHref}>Logs</a>` : nothing}
                  ${s.status === 'failed' || s.status === 'cancelled'
                    ? html`<button
                        class="btn small"
                        @click=${(e: Event) => this._emit(e, 'cell-rerun', cell)}
                      >
                        Rerun
                      </button>`
                    : nothing}
                </td>
              </tr>`;
            })}
          </tbody>
        </table>
      </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'matrix-cells': MatrixCells;
  }
}
