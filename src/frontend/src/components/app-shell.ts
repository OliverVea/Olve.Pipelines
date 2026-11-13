import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { resolve, navigate, type ResolvedRoute } from '../router.js';
import { onLiveState, type LiveState } from '../api/events.js';
import {
  getUser,
  login,
  logout,
  handleCallback,
  onUserChanged,
  type AuthUser,
} from '../auth.js';

const LOGO = svg`<svg viewBox="0 0 20 20" aria-hidden="true">
  <rect x="1" y="7" width="6" height="6" rx="1.5" fill="var(--color-text)" />
  <rect x="13" y="1" width="6" height="6" rx="1.5" fill="var(--color-text)" />
  <path d="M7 10h3M13 4h-1.25A1.75 1.75 0 0 0 10 5.75v8.5A1.75 1.75 0 0 0 11.75 16H13" fill="none" stroke="var(--color-text)" stroke-width="1.5" />
  <rect x="13.75" y="13.75" width="4.5" height="4.5" rx="1.25" fill="none" stroke="var(--color-brand)" stroke-width="1.5" />
</svg>`;
@customElement('app-shell')
export class AppShell extends LitElement {
  static styles = css`
    :host {
      display: block;
      max-width: 1320px;
      margin: 0 auto;
      padding: 1.5rem;
    }

    header {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 2rem;
      padding-bottom: 1rem;
      border-bottom: 1px solid var(--color-border);
    }

    header h1 {
      margin: 0;
      white-space: nowrap;
      font-size: 1.25rem;
      font-weight: 600;
    }

    header a {
      display: flex;
      align-items: center;
      gap: 0.55rem;
      color: var(--color-text);
      text-decoration: none;
    }

    header h1 svg {
      width: 20px;
      height: 20px;
      flex: none;
    }

    header a:hover {
      color: var(--color-primary);
    }

    .spacer {
      flex: 1;
    }

    .stale {
      padding: 0.1rem 0.6rem;
      border-radius: 999px;
      border: 1px solid color-mix(in srgb, var(--color-warning) 50%, transparent);
      color: var(--color-warning);
      font-size: 0.78rem;
    }

    .user-info {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      font-size: 0.85rem;
      color: var(--color-text-muted);
    }

    .user-name {
      color: var(--color-text);
    }

    @media (max-width: 640px) {
      :host {
        padding: 1rem;
      }
      .user-name {
        display: none;
      }
    }

    .auth-btn {
      padding: 0.35rem 0.75rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font-size: 0.8rem;
      transition:
        background var(--transition),
        border-color var(--transition);
    }

    .auth-btn:hover {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    .loading {
      color: var(--color-text-muted);
      font-size: 0.9rem;
    }

    .signed-out {
      max-width: 28rem;
      margin: 4rem auto;
      text-align: center;
    }

    .signed-out h1 {
      display: flex;
      justify-content: center;
      align-items: center;
      gap: 0.55rem;
      margin: 0 0 1rem;
      font-size: 1.25rem;
      font-weight: 600;
    }

    .signed-out svg {
      width: 20px;
      height: 20px;
    }

    .signed-out p {
      color: var(--color-text-muted);
      margin: 0 0 1.5rem;
    }

    .sign-in {
      padding: 0.5rem 1rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-primary);
      background: var(--color-primary);
      color: var(--color-surface);
      font-size: 0.9rem;
      transition: background var(--transition);
    }

    .sign-in:hover {
      background: var(--color-primary-hover);
    }
  `;

  @state() declare private _route: ResolvedRoute;
  @state() declare private _user: AuthUser | null;
  @state() declare private _live: LiveState;
  private _unsubLive?: () => void;
  @state() declare private _authReady: boolean;

  private _unsubAuth?: () => void;

  constructor() {
    super();
    this._route = resolve(window.location.pathname);
    this._user = null;
    this._live = 'live';
    this._authReady = false;
  }

  connectedCallback(): void {
    super.connectedCallback();
    window.addEventListener('route-changed', this._onRouteChanged);
    window.addEventListener('popstate', this._onRouteChanged);
    this._unsubAuth = onUserChanged((user) => (this._user = user));
    this._unsubLive = onLiveState((state) => (this._live = state));
    this._initAuth();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    window.removeEventListener('route-changed', this._onRouteChanged);
    window.removeEventListener('popstate', this._onRouteChanged);
    this._unsubAuth?.();
    this._unsubLive?.();
  }

  private async _initAuth(): Promise<void> {
    if (window.location.pathname === '/callback') {
      try {
        const returnPath = await handleCallback();
        this._user = await getUser();
        this._authReady = true;
        navigate(returnPath);
      } catch (e) {
        console.error('OIDC callback failed:', e);
        this._authReady = true;
        navigate('/');
      }
      return;
    }

    this._user = await getUser();
    this._authReady = true;

    if (!this._user || this._user.expired) {
      await login();
    }
  }

  private _onRouteChanged = (): void => {
    this._route = resolve(window.location.pathname);
  };

  render() {
    if (!this._authReady) {
      return html`<p class="loading">Signing in...</p>`;
    }

    if (!this._user) {
      return html`<div class="signed-out">
        <h1>${LOGO}Olve Pipelines</h1>
        <p>Your session has ended. Sign in again to carry on where you were.</p>
        <button class="sign-in" @click=${() => void login()}>Sign in with GitHub</button>
      </div>`;
    }

    return html`
      <header>
        <h1><a href="/" @click=${this._handleNav}>${LOGO}Olve Pipelines</a></h1>
        <span class="spacer"></span>
        ${this._live === 'reconnecting'
          ? html`<span
              class="stale"
              role="status"
              title="Live updates are disconnected; pages may be out of date until they reconnect."
              >Reconnecting…</span
            >`
          : nothing}
        <div class="user-info">
          <span class="user-name"
            >${this._user.profile.preferred_username ??
            this._user.profile.name ??
            'User'}</span
          >
          <button class="auth-btn" @click=${this._logout}>Log out</button>
        </div>
      </header>
      <main>${this._renderView()}</main>
    `;
  }

  private _renderView() {
    switch (this._route.view) {
      case 'pipeline-detail-view':
        return html`<pipeline-detail-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-detail-view>`;
      case 'pipeline-runs-view':
        return html`<pipeline-runs-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-runs-view>`;
      case 'pipeline-versions-view':
        return html`<pipeline-versions-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-versions-view>`;
      case 'pipeline-config-view':
        // Loaded on demand: the YAML editor is the bulk of its size.
        void import('./pipeline-config-view.js');
        return html`<pipeline-config-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-config-view>`;
      case 'pipeline-secrets-view':
        return html`<pipeline-secrets-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-secrets-view>`;
      case 'pipeline-binding-view':
        return html`<pipeline-binding-view
          .pipelineId=${this._route.params.pipelineId}
        ></pipeline-binding-view>`;
      case 'run-view':
        return html`<run-view
          .pipelineId=${this._route.params.pipelineId}
          .run=${this._route.params.run}
        ></run-view>`;
      case 'step-logs-view':
        return html`<step-logs-view
          .pipelineId=${this._route.params.pipelineId}
          .run=${this._route.params.run}
          .step=${this._route.params.step}
        ></step-logs-view>`;
      case 'critical-path-view':
        return html`<critical-path-view
          .pipelineId=${this._route.params.pipelineId}
        ></critical-path-view>`;
      case 'reliability-view':
        return html`<reliability-view
          .pipelineId=${this._route.params.pipelineId}
        ></reliability-view>`;
      case 'step-runs-view':
        return html`<step-runs-view
          .pipelineId=${this._route.params.pipelineId}
          .step=${this._route.params.step}
        ></step-runs-view>`;
      case 'docs-view':
        return html`<docs-view .page=${this._route.params.page ?? 'index'}></docs-view>`;
      default:
        return html`<pipeline-list-view></pipeline-list-view>`;
    }
  }

  private _handleNav(e: Event) {
    e.preventDefault();
    const anchor = e.currentTarget as HTMLAnchorElement;
    history.pushState(null, '', anchor.href);
    this._route = resolve(window.location.pathname);
  }

  private async _logout(): Promise<void> {
    await logout();
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'app-shell': AppShell;
  }
}
