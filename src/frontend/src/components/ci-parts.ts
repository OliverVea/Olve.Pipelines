import { css, html } from 'lit';
import type { CiStepState } from '@olve/api';
import { toneOf, statusLabel } from '../styles/ui.js';
import { ciLabel, ciTone, stripOf } from '../model/ci.js';

export const ciStyles = css`
  .change {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .change .title {
    color: var(--color-text);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 34rem;
  }
  .change .branch {
    font-family: var(--font-mono);
    font-size: 0.74rem;
    color: var(--color-text-muted);
  }
  .steps-strip {
    display: inline-flex;
    align-items: center;
    gap: 3px;
  }
  .steps-strip .gap {
    width: 6px;
  }
  .steps-strip .dot {
    width: 0.5rem;
    height: 0.5rem;
  }
  .toolbar {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 1rem 0 0.6rem;
  }
  .why {
    color: var(--color-text-muted);
    font-size: 0.8rem;
  }
  @media (max-width: 640px) {
    .hide-phone {
      display: none;
    }
    .change .title {
      max-width: 13rem;
    }
  }
`;

/** A status dot and label in the CI vocabulary (run statuses plus conflict and the queue's states). */
export const ciStatus = (status: string) =>
  html`<span class="st t-${ciTone(status)}"
    ><span class="dot"></span>${ciLabel(status)}</span
  >`;

/** One dot per step, stages apart. */
export const stepStrip = (steps: readonly CiStepState[]) =>
  html`<span class="steps-strip" aria-label="steps"
    >${stripOf(steps).map(
      (stage, i) =>
        html`${i ? html`<span class="gap"></span>` : ''}${stage.map(
          (s) =>
            html`<span
              class="dot t-${s.status === 'not-run' ? 'idle' : toneOf(s.status)}"
              title="${s.name}: ${statusLabel(s.status)}"
            ></span>`,
        )}`,
    )}</span
  >`;

export const commitUrl = (repo: string | null, sha: string) =>
  repo ? `https://github.com/${repo}/commit/${sha}` : null;
export const pullUrl = (repo: string | null, n: number) =>
  repo ? `https://github.com/${repo}/pull/${n}` : null;
