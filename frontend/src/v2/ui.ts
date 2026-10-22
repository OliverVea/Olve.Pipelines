import { css } from 'lit';
import { navigate } from '../router.js';

/**
 * Status vocabulary of the v2 views. Step statuses are the attempt
 * discriminators plus the run view's derived `held` / `pending` / `not-run`;
 * run statuses are capitalised enums from the API and normalised here.
 */
export type Tone = 'ok' | 'fail' | 'run' | 'wait' | 'skip' | 'idle' | 'off';

export function toneOf(status: string | null | undefined): Tone {
  switch ((status ?? '').toLowerCase()) {
    case 'succeeded':
    case 'healthy':
      return 'ok';
    case 'failed':
    case 'unhealthy':
    case 'running (unhealthy)':
      return 'fail';
    case 'in-progress':
    case 'running':
    case 'running (healthy)':
    case 'scheduled':
      return 'run';
    case 'held':
    case 'blocked':
    case 'draining':
      return 'wait';
    case 'skipped':
      return 'skip';
    case 'cancelled':
    case 'obsolete':
    case 'superseded':
      return 'off';
    default:
      return 'idle';
  }
}

export function statusLabel(status: string | null | undefined): string {
  const s = (status ?? '').toLowerCase();
  switch (s) {
    case 'in-progress':
      return 'running';
    case 'not-run':
      return 'not run';
    case '':
      return 'unknown';
    default:
      return s;
  }
}

export function isActiveStatus(status: string | null | undefined): boolean {
  const s = (status ?? '').toLowerCase();
  return s === 'running' || s === 'in-progress' || s === 'scheduled' || s === 'pending' || s === 'held';
}

export function formatDuration(from: Date | string | null | undefined, to: Date | string | null | undefined): string {
  if (!from) return '';
  const a = new Date(from).getTime();
  const b = to ? new Date(to).getTime() : Date.now();
  if (Number.isNaN(a) || Number.isNaN(b)) return '';
  let s = Math.max(0, Math.round((b - a) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  s %= 60;
  if (m < 60) return `${m}m${String(s).padStart(2, '0')}s`;
  return `${Math.floor(m / 60)}h${String(m % 60).padStart(2, '0')}m`;
}

export function shortSha(sha: string | null | undefined): string {
  return sha ? sha.slice(0, 7) : '';
}

export function formatSize(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}

/** Click handler for in-app links: client-side navigation on plain left clicks. */
export function navClick(e: MouseEvent): void {
  if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
  const a = (e.currentTarget as HTMLElement).closest('a');
  const href = a?.getAttribute('href');
  if (!href || a?.target === '_blank') return;
  e.preventDefault();
  navigate(href);
}

export const pipelinePath = (id: string) => `/pipeline/${id}`;
export const runPath = (id: string, n: number | null | undefined) => `/pipeline/${id}/run/${n ?? ''}`;
export const runsPath = (id: string) => `/pipeline/${id}/runs`;
export const versionsPath = (id: string) => `/pipeline/${id}/versions`;
export const secretsPath = (id: string) => `/pipeline/${id}/secrets`;
export const bindingPath = (id: string) => `/pipeline/${id}/binding`;
export const stepLogsPath = (id: string, run: number | null | undefined, step: string, attempt?: number | null) =>
  `/pipeline/${id}/run/${run ?? ''}/step/${encodeURIComponent(step)}/logs${attempt ? `?attempt=${attempt}` : ''}`;
export const stepPath = (id: string, step: string) => `/pipeline/${id}/step/${encodeURIComponent(step)}`;
export const stepRunsPath = (id: string, step: string) => `${stepPath(id, step)}/runs`;

/** Shared styles for the v2 views (buttons, pills, status dots, panels). */
export const sharedStyles = css`
  :host {
    display: block;
  }
  *,
  *::before,
  *::after {
    box-sizing: border-box;
  }
  a {
    color: var(--color-primary);
    text-decoration: none;
  }
  a:hover {
    text-decoration: underline;
  }
  code,
  .mono {
    font-family: var(--font-mono);
    font-size: 0.86em;
  }
  .muted {
    color: var(--color-text-muted);
  }
  .error {
    color: var(--color-danger);
  }
  .errors {
    margin: 0.5rem 0;
    padding: 0.5rem 0.75rem;
    border-left: 3px solid var(--color-danger);
    background: color-mix(in srgb, var(--color-danger) 8%, transparent);
    color: var(--color-text);
    font-size: 0.85rem;
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .errors p {
    margin: 0.15rem 0;
  }

  h2 {
    margin: 0;
    font-size: 1.35rem;
    font-weight: 650;
    letter-spacing: -0.01em;
  }
  h3 {
    margin: 0 0 0.6rem;
    font-size: 0.95rem;
    font-weight: 650;
  }

  .btn {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.3rem 0.7rem;
    border-radius: var(--radius);
    border: 1px solid var(--color-border);
    background: var(--color-surface);
    color: var(--color-text);
    font-size: 0.82rem;
    line-height: 1.4;
    white-space: nowrap;
    transition:
      background var(--transition),
      border-color var(--transition);
  }
  .btn:hover:not(:disabled) {
    background: var(--color-surface-hover);
    border-color: var(--color-primary);
  }
  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
  .btn.primary {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: var(--color-surface);
  }
  .btn.primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
    border-color: var(--color-primary-hover);
  }
  .btn.danger:hover:not(:disabled) {
    border-color: var(--color-danger);
    color: var(--color-danger);
  }
  .btn.small {
    padding: 0.15rem 0.5rem;
    font-size: 0.75rem;
  }
  .btn:focus-visible,
  a:focus-visible,
  select:focus-visible,
  input:focus-visible,
  textarea:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 1px;
  }

  .pill {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    padding: 0.05rem 0.5rem;
    border-radius: 999px;
    font-size: 0.75rem;
    line-height: 1.5;
    border: 1px solid var(--color-border);
    color: var(--color-text-muted);
    background: var(--color-surface);
    white-space: nowrap;
  }

  /* Status: a dot plus a word. Tone drives colour; shape distinguishes the
     "nothing happened" states so colour isn't the only signal. */
  .st {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.8rem;
    white-space: nowrap;
  }
  .dot {
    width: 0.6rem;
    height: 0.6rem;
    border-radius: 50%;
    flex-shrink: 0;
    background: var(--tone, var(--color-pending));
    border: 1.5px solid var(--tone, var(--color-pending));
  }
  .t-ok {
    --tone: var(--color-success);
  }
  .t-fail {
    --tone: var(--color-danger);
  }
  .t-run {
    --tone: var(--color-primary);
  }
  .t-wait {
    --tone: var(--color-warning);
  }
  .t-skip {
    --tone: var(--color-success);
  }
  .t-skip .dot,
  .dot.t-skip {
    background: transparent;
  }
  .t-idle {
    --tone: var(--color-pending);
  }
  .t-idle .dot,
  .dot.t-idle {
    background: transparent;
    border-style: dashed;
  }
  .t-off {
    --tone: var(--color-text-muted);
  }
  .t-off .dot,
  .dot.t-off {
    background: transparent;
  }
  .t-run .dot,
  .dot.t-run {
    animation: pulse 1.6s ease-in-out infinite;
  }
  @keyframes pulse {
    50% {
      opacity: 0.35;
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .t-run .dot,
    .dot.t-run {
      animation: none;
    }
  }

  .panel {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    padding: 0.9rem 1rem;
  }
  section + section {
    margin-top: 1.5rem;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
  }
  th,
  td {
    text-align: left;
    padding: 0.35rem 0.5rem;
    border-bottom: 1px solid var(--color-border);
    vertical-align: middle;
  }
  th {
    color: var(--color-text-muted);
    font-weight: 500;
    font-size: 0.78rem;
  }
  tbody tr:hover {
    background: var(--color-surface-hover);
  }
  .table-wrap {
    overflow-x: auto;
  }
  .num {
    font-variant-numeric: tabular-nums;
  }

  .back {
    color: var(--color-text-muted);
    font-size: 0.85rem;
  }
  .back:hover {
    color: var(--color-primary);
  }
  .crumbs {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    font-size: 0.85rem;
    color: var(--color-text-muted);
    margin-bottom: 0.4rem;
  }
  .crumbs a {
    color: var(--color-text-muted);
  }
  .crumbs a:hover {
    color: var(--color-primary);
  }

  dialog {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 1.1rem 1.25rem;
    max-width: min(560px, calc(100vw - 2rem));
    width: 100%;
    box-shadow: 0 10px 30px rgba(4, 32, 55, 0.25);
  }
  dialog::backdrop {
    background: rgba(4, 32, 55, 0.35);
  }
  .dialog-actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
    margin-top: 1rem;
  }
`;
