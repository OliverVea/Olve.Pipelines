import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { resolve, navigate, type ResolvedRoute } from '../router.js';
import {
  getUser,
  login,
  logout,
  handleCallback,
  onUserChanged,
  type AuthUser,
} from '../auth.js';

@customElement('app-shell')
export class AppShell extends LitElement {
  static styles = css`
    :host {
      display: block;
      max-width: 1200px;
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
      font-size: 1.25rem;
      font-weight: 600;
    }

    header a {
      color: var(--color-text);
      text-decoration: none;
    }

    header a:hover {
      color: var(--color-primary);
    }

    .spacer {
      flex: 1;
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

    .auth-btn {
      padding: 0.35rem 0.75rem;
      border-radius: var(--radius);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      color: var(--color-text);
      font-size: 0.8rem;
      transition: background var(--transition), border-color var(--transition);
    }

    .auth-btn:hover {
      background: var(--color-surface-hover);
      border-color: var(--color-primary);
    }

    .loading {
      color: var(--color-text-muted);
      font-size: 0.9rem;
    }
  `;

  @state() private declare _route: ResolvedRoute;
  @state() private declare _user: AuthUser | null;
  @state() private declare _authReady: boolean;

  private _unsubAuth?: () => void;

  constructor() {
    super();
    this._route = resolve(window.location.pathname);
    this._user = null;
    this._authReady = false;
  }

  connectedCallback(): void {
    super.connectedCallback();
    window.addEventListener('route-changed', this._onRouteChanged);
    window.addEventListener('popstate', this._onRouteChanged);
    this._unsubAuth = onUserChanged((user) => (this._user = user));
    this._initAuth();
  }

  disconnectedCallback(): void {
    super.disconnectedCallback();
    window.removeEventListener('route-changed', this._onRouteChanged);
    window.removeEventListener('popstate', this._onRouteChanged);
    this._unsubAuth?.();
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
      return html`<p class="loading">Redirecting to login...</p>`;
    }

    return html`
      <header>
        <h1><a href="/" @click=${this._handleNav}>Olve Pipelines</a></h1>
        <span class="spacer"></span>
        <div class="user-info">
          <span class="user-name">${this._user.profile.preferred_username ?? this._user.profile.name ?? 'User'}</span>
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
      case 'job-logs-view':
        return html`<job-logs-view
          .pipelineId=${this._route.params.pipelineId}
          .jobId=${this._route.params.jobId}
        ></job-logs-view>`;
      case 'docs-view':
        return html`<docs-view .page=${this._route.params.page ?? 'index'}></docs-view>`;
      case 'step-detail-view':
        return html`<step-detail-view
          .pipelineId=${this._route.params.pipelineId}
          .stepId=${this._route.params.stepId}
        ></step-detail-view>`;
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
