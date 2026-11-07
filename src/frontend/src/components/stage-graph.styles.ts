import { css } from 'lit';

/** The stage graph's own styles; the component adds the shared ones. */
export const stageGraphStyles = css`
  .bar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.8rem;
    font-size: 0.75rem;
  }
  .note {
    margin-left: auto;
  }
  .scroller {
    overflow-x: auto;
    overscroll-behavior-x: contain;
    padding: 0.9rem 0 0.4rem;
  }
  .row,
  .when-body {
    display: flex;
    align-items: stretch;
    min-width: min-content;
  }
  .col {
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 0.5rem;
  }
  .barrier {
    position: relative;
    width: 1.5rem;
    flex-shrink: 0;
  }
  .barrier::after,
  .down::after {
    content: '';
    position: absolute;
    left: calc(50% - 4px);
    top: calc(50% - 4px);
    width: 7px;
    height: 7px;
    border-top: 1.5px solid var(--color-text-muted);
    border-right: 1.5px solid var(--color-text-muted);
    transform: rotate(45deg);
  }
  .when {
    position: relative;
    display: flex;
    flex-direction: column;
    align-self: center;
    padding: 0.75rem 0;
    border-bottom: 2px solid var(--color-border);
  }
  .when-label {
    position: absolute;
    bottom: 0;
    left: 50%;
    transform: translate(-50%, calc(50% + 1px));
    padding: 0 1rem;
    background: linear-gradient(
      to right,
      transparent,
      var(--graph-bg, var(--color-bg)) 0.9rem,
      var(--graph-bg, var(--color-bg)) calc(100% - 0.9rem),
      transparent
    );
    color: var(--color-text-muted);
    font-size: 0.72rem;
    line-height: 1;
    white-space: nowrap;
  }
  .lv-chips .col {
    gap: 0.4rem;
  }
  .lv-chips .barrier {
    width: 1.3rem;
  }

  .vflow {
    --rail: 2.2rem;
    padding-top: 0.9rem;
  }
  .vflow .stage {
    display: grid;
    grid-template-columns: var(--rail) minmax(0, 1fr) var(--rail);
    align-items: start;
  }
  .num {
    padding-top: 0.9rem;
    font-size: 0.72rem;
    color: var(--color-text-muted);
    font-variant-numeric: tabular-nums;
  }
  .lv-chips .num {
    padding-top: 0.45rem;
  }
  .steps {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    gap: 0.5rem;
  }
  .lv-chips .steps {
    gap: 0.4rem;
  }
  .down {
    position: relative;
    height: 1.4rem;
    margin: 0 var(--rail);
  }
  .down::after {
    top: calc(50% - 6px);
    transform: rotate(135deg);
  }
  .vwhen {
    position: relative;
    margin-top: 0.3rem;
  }
  .vwhen::before {
    content: '';
    position: absolute;
    left: calc(var(--rail) - 0.6rem);
    top: 0.55rem;
    bottom: 0.2rem;
    border-left: 2px solid var(--color-border);
  }
  .vlabel {
    display: block;
    margin: 0 var(--rail) 0.35rem;
    text-align: center;
    color: var(--color-text-muted);
    font-size: 0.72rem;
  }
  .vflow .tile {
    max-width: 100%;
  }

  .tile,
  .chip {
    position: relative;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-left: 3px solid var(--tone, var(--color-border));
    border-radius: var(--radius);
    color: var(--color-text);
    cursor: pointer;
    transition:
      opacity var(--transition),
      border-color var(--transition),
      box-shadow var(--transition);
  }
  .tile {
    width: 15.5rem;
    padding: 0.5rem 0.65rem;
  }
  .chip {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    width: var(--chip-w, 9rem);
    height: 2rem;
    padding: 0 0.5rem 0 0.45rem;
    font-size: 0.78rem;
    font-weight: 600;
  }
  .dim {
    opacity: 0.4;
  }
  .src {
    box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-primary) 55%, transparent);
  }
  .dst {
    box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-when) 60%, transparent);
  }
  .tile:hover,
  .chip:hover {
    border-color: var(--color-primary);
    border-left-color: var(--tone, var(--color-primary));
  }
  .selected {
    box-shadow: 0 0 0 2px var(--color-primary);
  }
  .plain {
    --tone: var(--color-border);
  }
  .tile .name:focus-visible,
  .chip:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
  .chip .label {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .chip .mark {
    display: inline-flex;
    color: var(--color-warning);
  }
  .chip .mark svg {
    width: 0.75rem;
    height: 0.75rem;
  }
  .chip .cnt {
    font-size: 0.66rem;
    font-weight: 500;
    color: var(--color-text-muted);
    font-variant-numeric: tabular-nums;
  }
  .chip .att {
    font-size: 0.66rem;
    color: var(--tone);
  }
  .head {
    display: flex;
    align-items: flex-start;
    gap: 0.4rem;
  }
  .name {
    flex: 1;
    min-width: 0;
    padding: 0;
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    font-weight: 600;
    font-size: 0.88rem;
    overflow-wrap: anywhere;
  }
  .badge {
    font-size: 0.66rem;
    font-weight: 650;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    padding: 0.1rem 0.35rem;
    border-radius: 3px;
    white-space: nowrap;
    color: var(--tone);
    background: color-mix(in srgb, var(--tone) 15%, transparent);
  }
  .t-idle .badge,
  .t-skip .badge,
  .t-off .badge {
    background: transparent;
    border: 1px solid color-mix(in srgb, var(--tone) 45%, transparent);
  }
  .t-run .badge {
    animation: pulse 1.6s ease-in-out infinite;
  }
  .case {
    display: inline-block;
    margin-top: 4px;
    padding: 0 6px;
    border-radius: 999px;
    font-size: 11px;
    color: var(--muted, #888);
    border: 1px solid var(--border, #ccc);
  }
  .meta {
    margin-top: 0.3rem;
    min-height: 1.15rem;
    display: flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.74rem;
    color: var(--color-text-muted);
    white-space: nowrap;
    overflow: hidden;
  }
  .meta > :not(.icon) + :not(.icon)::before {
    content: '·';
    margin-right: 0.35rem;
  }
  .run {
    color: inherit;
  }
  a.badge {
    text-decoration: none;
  }
  a.badge:hover {
    text-decoration: underline;
  }
  .run:hover {
    color: var(--color-primary);
  }
  .tile-links {
    margin-top: 0.25rem;
    min-width: 0;
  }
  .reason {
    margin-top: 0.2rem;
    font-size: 0.72rem;
    color: var(--color-text-muted);
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .block-note {
    margin-top: 0.2rem;
    font-size: 0.72rem;
    color: var(--color-warning);
  }
  .icon {
    display: inline-flex;
    visibility: hidden;
    padding: 0.15rem;
    border: 0;
    border-radius: var(--radius);
    background: transparent;
    color: var(--color-text-muted);
    opacity: 0.45;
  }
  .icon.stop,
  .icon.retry,
  .lock {
    margin-left: auto;
  }
  .icon.stop + .icon,
  .icon.retry + .lock {
    margin-left: 0;
  }
  .icon.stop {
    visibility: visible;
    opacity: 0.8;
    color: var(--color-danger);
  }
  .tile:hover .icon,
  .icon:focus-visible,
  .t-fail .retry {
    visibility: visible;
  }
  .t-fail .retry {
    opacity: 1;
    color: var(--color-danger);
  }
  @media (hover: none) {
    .icon {
      visibility: visible;
    }
  }
  .icon:hover,
  .icon:focus-visible {
    opacity: 1;
    background: var(--color-surface-hover);
  }
  .lock.closed {
    visibility: visible;
    opacity: 1;
    color: var(--color-warning);
  }
  .icon svg {
    width: 0.85rem;
    height: 0.85rem;
  }

  .hovercard {
    position: fixed;
    z-index: 20;
    pointer-events: none;
    min-width: 13rem;
    max-width: 19rem;
    padding: 0.5rem 0.65rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius);
    box-shadow: 0 6px 20px rgba(4, 32, 55, 0.18);
    font-size: 0.78rem;
    color: var(--color-text-muted);
  }
  .hc-name {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    color: var(--color-text);
    font-weight: 600;
    font-size: 0.85rem;
  }
  .hc-name .badge {
    margin-left: auto;
  }
  .hc-meta > span + span::before {
    content: '·';
    margin: 0 0.35rem;
  }

  @media (max-width: 640px) {
    .note {
      margin-left: 0;
    }
    .vflow {
      --rail: 1.6rem;
    }
    .vflow.lv-chips .chip {
      --chip-w: calc(50% - 0.2rem);
    }
  }
`;
