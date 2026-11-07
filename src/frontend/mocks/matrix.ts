/**
 * Matrix builds, mocked on fixture data: `npm run dev`, then /mocks/matrix.html. Nothing here talks
 * to a server; the components it renders are the real ones.
 */
import { LitElement, css, html, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import type { Manifest, ManifestStep } from '../src/model/manifest.js';
import {
  type Cell,
  type CellState,
  type Matrix,
  cellAddress,
  cellLabel,
  matrixOf,
  rollUp,
} from '../src/model/matrix.js';
import type { StepState } from '../src/components/stage-graph.js';
import { sharedStyles, statusLabel, toneOf } from '../src/styles/ui.js';
import '../src/components/stage-graph.js';
import '../src/components/matrix-cells.js';

const step = (name: string, matrix?: Matrix): ManifestStep => ({
  name,
  kind: 'custom',
  image: 'oven/bun:1.4.3-slim',
  input: {},
  inputTypes: {},
  output: null,
  secrets: [],
  ...(matrix ? { matrix } : {}),
});

const cross = (axes: Record<string, string[]>, exclude: Cell[] = []): Matrix => {
  let cells: Cell[] = [{}];
  for (const [axis, values] of Object.entries(axes))
    cells = cells.flatMap((c) => values.map((v) => ({ ...c, [axis]: v })));
  return {
    axes,
    cells: cells.filter(
      (c) => !exclude.some((x) => Object.entries(x).every(([k, v]) => c[k] === v)),
    ),
  };
};

const buildCli = cross({ os: ['linux', 'windows', 'macos'], arch: ['x64', 'arm64'] }, [
  { os: 'windows', arch: 'arm64' },
]);
const testSdk = cross({ typescript: ['7.0', '5.9'] });
const e2e = cross({ browser: ['chromium', 'firefox', 'webkit'] });

const manifest = {
  apiVersion: 'olve.pipelines/v2',
  name: 'Olve.Pipelines v2',
  input: 'DeployInput',
  models: {},
  secrets: [],
  triggers: [],
  failureHandlers: [],
  stages: [
    { steps: [step('source')] },
    {
      steps: [
        step('compile-server'),
        step('build-cli', buildCli),
        step('test-sdk', testSdk),
      ],
    },
    { steps: [step('deploy-beta')] },
    { steps: [step('e2e', e2e), step('beta-test')] },
    { steps: [step('deploy-prod')] },
  ],
} as unknown as Manifest;

type Scenario = 'running' | 'failed' | 'cascaded' | 'green';
const SCENARIOS: [Scenario, string][] = [
  ['running', 'Running'],
  ['failed', 'A cell failed (keepRunning)'],
  ['cascaded', 'Failure cascaded (cascadeFailure)'],
  ['green', 'All green'],
];
const WINDOWS_FAILED = 'bun build --compile: target bun-windows-x64 not found';

const ago = (s: number) => new Date(Date.now() - s * 1000);

function cellStates(
  m: Matrix,
  pick: (c: Cell, i: number) => Partial<CellState>,
): CellState[] {
  return m.cells.map((cell, i) => ({
    cell,
    status: 'succeeded',
    latestAttempt: 1,
    attemptCount: 1,
    startedAt: ago(300 - i * 10),
    finishedAt: ago(200 - i * 10),
    logsHref: '#logs',
    ...pick(cell, i),
  }));
}

/** The step's reason: the failure, and for a cascade only the failure that caused it. */
function matrixReason(cells: CellState[]): string | null {
  const failed = cells.filter((c) => c.status === 'failed');
  if (failed.length === 0) return null;
  const first = `${cellLabel(failed[0]!.cell)} failed: ${failed[0]!.reason}`;
  return failed.length > 1 ? `${failed.length} cells failed. ${first}` : first;
}

function statesFor(scenario: Scenario): Map<string, StepState> {
  const done = (startedS: number, tookS: number): StepState => ({
    status: 'succeeded',
    runNumber: 204,
    startedAt: ago(startedS),
    finishedAt: ago(startedS - tookS),
    logsHref: '#logs',
  });
  const failed = scenario === 'failed' || scenario === 'cascaded';
  const cascaded = 'cancelled: windows · x64 failed';
  const cli = cellStates(buildCli, (c) => {
    if (scenario === 'green') return {};
    if (c.os === 'windows' && failed) return { status: 'failed', reason: WINDOWS_FAILED };
    if (c.os !== 'macos') return {};
    if (scenario === 'cascaded')
      return c.arch === 'x64'
        ? { status: 'cancelled', reason: cascaded }
        : {
            status: 'cancelled',
            reason: cascaded,
            startedAt: null,
            finishedAt: null,
            latestAttempt: null,
            logsHref: undefined,
          };
    return c.arch === 'arm64'
      ? {
          status: 'scheduled',
          startedAt: null,
          finishedAt: null,
          latestAttempt: null,
          logsHref: undefined,
        }
      : { status: 'in-progress', finishedAt: null };
  });
  const sdk = cellStates(testSdk, () => ({}));
  const matrixState = (cells: CellState[]): StepState => ({
    status: rollUp(cells),
    reason: matrixReason(cells),
    runNumber: 204,
    startedAt: cells[0]?.startedAt,
    finishedAt: rollUp(cells) === 'succeeded' ? cells.at(-1)?.finishedAt : null,
    cells,
  });
  const states = new Map<string, StepState>([
    ['source', done(400, 5)],
    ['compile-server', done(395, 48)],
    ['build-cli', matrixState(cli)],
    ['test-sdk', matrixState(sdk)],
  ]);
  if (scenario === 'green') {
    const browsers = cellStates(e2e, (_, i) => ({
      startedAt: ago(120),
      finishedAt: ago(120 - 20 - i * 6),
    }));
    states.set('deploy-beta', done(150, 16));
    states.set('e2e', matrixState(browsers));
    states.set('beta-test', done(130, 52));
    states.set('deploy-prod', done(60, 17));
  }
  return states;
}

@customElement('matrix-mock')
export class MatrixMock extends LitElement {
  static styles = [
    sharedStyles,
    css`
      :host {
        display: block;
        max-width: 72rem;
        margin: 0 auto;
        padding: 1.5rem 1rem 4rem;
      }
      h1 {
        font-size: 1.2rem;
        margin: 0 0 0.25rem;
      }
      .intro {
        color: var(--color-text-muted);
        margin: 0 0 1rem;
        max-width: 48rem;
      }
      .seg {
        margin-bottom: 1rem;
      }
      .sheet {
        margin-top: 1.25rem;
        padding: 1rem;
        border: 1px solid var(--color-border);
        border-radius: var(--radius);
        background: var(--color-surface);
      }
      .sheet h2 {
        display: flex;
        align-items: center;
        gap: 0.5rem;
        margin: 0 0 0.25rem;
        font-size: 1rem;
      }
      .addr {
        margin: 0 0 0.75rem;
        color: var(--color-text-muted);
        font-size: 0.8rem;
      }
      .badge {
        font-size: 0.66rem;
        font-weight: 650;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        padding: 0.1rem 0.35rem;
        border-radius: 3px;
        color: var(--tone);
        background: color-mix(in srgb, var(--tone) 15%, transparent);
      }
      .cell-logs {
        margin-top: 0.75rem;
        padding: 0.6rem 0.75rem;
        border-radius: var(--radius);
        background: var(--color-bg);
        font-size: 0.8rem;
        color: var(--color-text-muted);
      }
    `,
  ];

  @state() declare private _scenario: Scenario;
  @state() declare private _step: string;
  @state() declare private _cell: Cell | null;

  constructor() {
    super();
    this._scenario = 'failed';
    this._step = 'build-cli';
    this._cell = null;
  }

  render() {
    const states = statesFor(this._scenario);
    return html`<h1>Matrix builds (mock)</h1>
      <p class="intro">
        <code>build-cli</code> runs on <code>os × arch</code> without windows · arm64,
        <code>test-sdk</code> on two TypeScript versions and <code>e2e</code> in three
        browsers. A matrix step is one step in its stage: the barrier waits for every
        cell, and any failed cell fails the step. Chips show the cells finished; cards
        show each cell. Pick a step or a cell.
      </p>
      <span class="seg" role="group" aria-label="Scenario">
        ${SCENARIOS.map(
          ([v, label]) =>
            html`<button
              aria-pressed=${this._scenario === v}
              @click=${() => (this._scenario = v)}
            >
              ${label}
            </button>`,
        )}
      </span>
      <stage-graph
        .manifest=${manifest}
        .states=${states}
        .selected=${this._step}
        stepControls
        @step-select=${(e: CustomEvent<{ step: string; cell?: Cell }>) => {
          this._step = e.detail.step;
          this._cell = e.detail.cell ?? null;
        }}
      ></stage-graph>
      ${this._sheet(states)}`;
  }

  private _sheet(states: Map<string, StepState>) {
    const s = manifest.stages
      .flatMap((x) => ('steps' in x ? x.steps : []))
      .find((x) => x.name === this._step);
    if (!s) return nothing;
    const st = states.get(s.name);
    const matrix = matrixOf(s);
    const status = st?.status ?? 'not-run';
    return html`<section class="sheet">
      <h2>
        ${s.name}
        <span class="badge t-${toneOf(status)}">${statusLabel(status)}</span>
      </h2>
      <p class="addr mono">
        Olve.Pipelines v2#204/${this._cell ? cellAddress(s.name, this._cell) : s.name}
      </p>
      ${matrix
        ? html`<matrix-cells
              .matrix=${matrix}
              .cells=${st?.cells ?? null}
              .step=${s.name}
              .selected=${this._cell}
              @cell-select=${(e: CustomEvent<{ cell: Cell }>) =>
                (this._cell = e.detail.cell)}
              @cell-rerun=${(e: CustomEvent<{ cell: Cell }>) =>
                alert(
                  `pl step rerun "Olve.Pipelines v2#204/${cellAddress(s.name, e.detail.cell)}"`,
                )}
            ></matrix-cells>
            ${this._cell
              ? html`<div class="cell-logs">
                  The logs, output and files of
                  <strong>${cellLabel(this._cell)}</strong> go here, as for any step.
                </div>`
              : nothing}`
        : html`<p class="muted">Not a matrix step: the usual step sheet.</p>`}
    </section>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'matrix-mock': MatrixMock;
  }
}
