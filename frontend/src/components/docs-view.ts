import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import MiniSearch from 'minisearch';
import { navigate } from '../router.js';

interface DocRecord {
  slug: string;
  title: string;
  headings: string;
  text: string;
  excerpt: string;
}

interface DocHit {
  slug: string;
  title: string;
  excerpt: string;
}

// The setup guide lives as raw Markdown served by the backend at /docs/<page>.md (the
// LLM/agent surface — see Program.cs). This component is the *human* surface: it fetches
// the same raw file and renders it. We parse the full GitHub-flavoured Markdown grammar
// (tables, task lists, strikethrough, nested lists, autolinks, …) with `marked`, then run
// the output through DOMPurify so the rendered HTML is XSS-safe before it hits the DOM.

const DEFAULT_PAGE = 'index';

// GitHub-flavoured Markdown: tables, strikethrough, task lists, autolinks.
marked.setOptions({ gfm: true, breaks: false });

// Single global hook (DOMPurify hooks are process-wide): rewrite inter-doc links and
// harden external links. Inter-doc links in the source are relative (e.g.
// "getting-started.md", "config-reference.md#anchor") — rewrite them to client routes
// (/docs/<slug>) so the in-app viewer chains naturally instead of downloading the raw
// .md. External links open in a new tab with noopener.
DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName !== 'A') return;
  const el = node as HTMLAnchorElement;
  const href = el.getAttribute('href') ?? '';
  const resolved = resolveDocHref(href);
  if (resolved !== href) el.setAttribute('href', resolved);
  if (/^https?:\/\//.test(resolved)) {
    el.setAttribute('target', '_blank');
    el.setAttribute('rel', 'noopener noreferrer');
  }
});

@customElement('docs-view')
export class DocsView extends LitElement {
  static styles = css`
    :host {
      display: block;
    }

    .doc-nav {
      margin-bottom: 1.5rem;
      font-size: 0.85rem;
    }

    .doc-nav a {
      color: var(--color-text-muted);
    }

    .doc-top {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
      margin-bottom: 1.5rem;
    }

    .doc-top .doc-nav {
      margin-bottom: 0;
    }

    .doc-search {
      position: relative;
      width: min(22rem, 50%);
    }

    .doc-search input {
      width: 100%;
      box-sizing: border-box;
      padding: 0.45rem 0.7rem;
      font: inherit;
      font-size: 0.85rem;
      color: var(--color-text);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
    }

    .doc-search input:focus {
      outline: none;
      border-color: var(--color-primary);
    }

    .doc-results {
      position: absolute;
      top: calc(100% + 0.3rem);
      left: 0;
      right: 0;
      z-index: 10;
      max-height: 22rem;
      overflow-y: auto;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      box-shadow: var(--shadow);
      padding: 0.3rem;
    }

    .doc-results button {
      display: block;
      width: 100%;
      text-align: left;
      padding: 0.45rem 0.6rem;
      border: none;
      border-radius: var(--radius);
      background: none;
      color: inherit;
      font: inherit;
      cursor: pointer;
    }

    .doc-results button:hover,
    .doc-results button.active {
      background: var(--color-surface-hover);
    }

    .doc-results .hit-title {
      font-size: 0.85rem;
      font-weight: 600;
    }

    .doc-results .hit-excerpt {
      font-size: 0.75rem;
      color: var(--color-text-muted);
      margin-top: 0.1rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .doc-results .empty {
      padding: 0.5rem 0.6rem;
      font-size: 0.8rem;
      color: var(--color-text-muted);
    }

    .doc {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      padding: 1.5rem 2rem;
      line-height: 1.6;
      overflow-wrap: break-word;
    }

    .doc h1 {
      font-size: 1.6rem;
      margin: 0 0 1rem;
      padding-bottom: 0.5rem;
      border-bottom: 1px solid var(--color-border);
    }

    .doc h2 {
      font-size: 1.25rem;
      margin: 1.75rem 0 0.75rem;
    }

    .doc h3 {
      font-size: 1.05rem;
      margin: 1.25rem 0 0.5rem;
    }

    .doc h4 {
      font-size: 0.95rem;
      margin: 1rem 0 0.5rem;
    }

    .doc h5,
    .doc h6 {
      font-size: 0.9rem;
      margin: 1rem 0 0.5rem;
      color: var(--color-text-muted);
    }

    .doc p {
      margin: 0.75rem 0;
    }

    .doc a {
      color: var(--color-primary);
    }

    .doc ul,
    .doc ol {
      margin: 0.75rem 0;
      padding-left: 1.5rem;
    }

    .doc li {
      margin: 0.3rem 0;
    }

    .doc li > ul,
    .doc li > ol {
      margin: 0.3rem 0;
    }

    /* GFM task lists */
    .doc li.task-list-item {
      list-style: none;
      margin-left: -1.2rem;
    }

    .doc li.task-list-item input {
      margin-right: 0.4rem;
    }

    .doc code {
      background: var(--color-surface-hover);
      border-radius: var(--radius);
      padding: 0.1rem 0.3rem;
      font-size: 0.9em;
    }

    .doc pre {
      margin: 1rem 0;
      padding: 0.85rem 1rem;
      background: var(--color-surface-hover);
      border: 1px solid var(--color-border);
      border-radius: var(--radius);
      overflow-x: auto;
    }

    .doc pre code {
      background: none;
      padding: 0;
      font-size: 0.85rem;
    }

    .doc blockquote {
      margin: 1rem 0;
      padding: 0.5rem 1rem;
      border-left: 3px solid var(--color-primary);
      background: var(--color-surface-hover);
      border-radius: 0 var(--radius) var(--radius) 0;
      color: var(--color-text-muted);
    }

    .doc blockquote p {
      margin: 0.25rem 0;
    }

    .doc hr {
      border: none;
      border-top: 1px solid var(--color-border);
      margin: 1.5rem 0;
    }

    .doc img {
      max-width: 100%;
      height: auto;
      border-radius: var(--radius);
    }

    .doc del {
      color: var(--color-text-muted);
    }

    /* GFM tables */
    .doc table {
      width: 100%;
      margin: 1rem 0;
      border-collapse: collapse;
      font-size: 0.9rem;
      display: block;
      overflow-x: auto;
    }

    .doc th,
    .doc td {
      border: 1px solid var(--color-border);
      padding: 0.4rem 0.7rem;
      text-align: left;
    }

    .doc th {
      background: var(--color-surface-hover);
      font-weight: 600;
    }

    .doc tbody tr:nth-child(even) {
      background: var(--color-surface-hover);
    }

    .loading,
    .error {
      padding: 2rem;
      color: var(--color-text-muted);
    }

    .error {
      color: var(--color-danger);
    }
  `;

  /** The doc page slug, e.g. "index", "getting-started". */
  @property() declare page: string;

  @state() private declare _html: string;
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;
  @state() private declare _query: string;
  @state() private declare _hits: DocHit[];
  @state() private declare _activeHit: number;

  /** Lazily built on first search (and cached) from /docs-index.json. */
  private _search: MiniSearch<DocRecord> | null = null;
  private _searchLoading = false;

  constructor() {
    super();
    this.page = DEFAULT_PAGE;
    this._html = '';
    this._loading = true;
    this._error = null;
    this._query = '';
    this._hits = [];
    this._activeHit = -1;
  }

  connectedCallback(): void {
    super.connectedCallback();
    this._load();
  }

  updated(changed: Map<string, unknown>): void {
    if (changed.has('page')) this._load();
  }

  private async _load(): Promise<void> {
    this._loading = true;
    this._error = null;
    const slug = this.page || DEFAULT_PAGE;
    try {
      const res = await fetch(`/docs/${slug}.md`);
      if (!res.ok) throw new Error(`Failed to load docs page "${slug}" (${res.status})`);
      const text = await res.text();
      const parsed = marked.parse(text, { async: false });
      this._html = DOMPurify.sanitize(parsed, { ADD_ATTR: ['target'] });
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    return html`
      <div class="doc-top">
        <div class="doc-nav">
          <a href="/" @click=${this._navHome}>&larr; Back to pipelines</a>
        </div>
        ${this._renderSearch()}
      </div>
      ${this._loading
        ? html`<p class="loading">Loading docs…</p>`
        : this._error
          ? html`<p class="error">${this._error}</p>`
          : html`<article class="doc" @click=${this._onLinkClick}>${unsafeHTML(this._html)}</article>`}
    `;
  }

  private _renderSearch() {
    const showResults = this._query.trim().length > 0;
    return html`
      <div class="doc-search">
        <input
          type="search"
          placeholder="Search docs…"
          .value=${this._query}
          @focus=${this._ensureIndex}
          @input=${this._onSearchInput}
          @keydown=${this._onSearchKeydown}
          @blur=${this._onSearchBlur}
          aria-label="Search docs"
        />
        ${showResults
          ? html`<div class="doc-results">
              ${this._hits.length === 0
                ? html`<div class="empty">No matches</div>`
                : this._hits.map(
                    (hit, i) => html`
                      <button
                        class=${i === this._activeHit ? 'active' : ''}
                        @mousedown=${(e: Event) => this._openHit(e, hit.slug)}
                      >
                        <div class="hit-title">${hit.title}</div>
                        <div class="hit-excerpt">${hit.excerpt}</div>
                      </button>
                    `
                  )}
            </div>`
          : null}
      </div>
    `;
  }

  // MiniSearch index is loaded once, on first focus, so the index payload isn't fetched
  // until the user actually intends to search.
  private async _ensureIndex(): Promise<void> {
    if (this._search || this._searchLoading) return;
    this._searchLoading = true;
    try {
      const res = await fetch('/docs-index.json');
      if (!res.ok) return;
      const docs = (await res.json()) as DocRecord[];
      const search = new MiniSearch<DocRecord>({
        idField: 'slug',
        fields: ['title', 'headings', 'text'],
        storeFields: ['slug', 'title', 'excerpt'],
        searchOptions: { boost: { title: 3, headings: 2 }, prefix: true, fuzzy: 0.2 },
      });
      search.addAll(docs);
      this._search = search;
      if (this._query.trim()) this._runQuery();
    } finally {
      this._searchLoading = false;
    }
  }

  private _onSearchInput(e: Event): void {
    this._query = (e.target as HTMLInputElement).value;
    this._activeHit = -1;
    this._runQuery();
  }

  private _runQuery(): void {
    const q = this._query.trim();
    if (!q || !this._search) {
      this._hits = [];
      return;
    }
    this._hits = this._search.search(q).slice(0, 8) as unknown as DocHit[];
  }

  private _onSearchKeydown(e: KeyboardEvent): void {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      this._activeHit = Math.min(this._activeHit + 1, this._hits.length - 1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      this._activeHit = Math.max(this._activeHit - 1, 0);
    } else if (e.key === 'Enter') {
      const hit = this._hits[this._activeHit] ?? this._hits[0];
      if (hit) {
        e.preventDefault();
        this._goto(hit.slug);
      }
    } else if (e.key === 'Escape') {
      this._clearSearch();
    }
  }

  // Delay so a result click (mousedown) registers before the dropdown unmounts.
  private _onSearchBlur(): void {
    setTimeout(() => this._clearSearch(), 120);
  }

  private _openHit(e: Event, slug: string): void {
    e.preventDefault();
    this._goto(slug);
  }

  private _goto(slug: string): void {
    this._clearSearch();
    navigate(`/docs/${slug}`);
  }

  private _clearSearch(): void {
    this._query = '';
    this._hits = [];
    this._activeHit = -1;
  }

  private _navHome(e: Event): void {
    e.preventDefault();
    navigate('/');
  }

  // Inter-doc links render as <a href="/docs/<slug>">; intercept clicks so they route
  // client-side instead of triggering a full reload.
  private _onLinkClick(e: Event): void {
    const target = (e.target as HTMLElement).closest('a');
    if (!target) return;
    const href = target.getAttribute('href');
    if (href && href.startsWith('/docs/')) {
      e.preventDefault();
      navigate(href);
    }
  }
}

// Rewrite a relative "<slug>.md(#anchor)?" reference to the client route /docs/<slug>.
// Leaves absolute URLs, anchors, and anything else untouched.
function resolveDocHref(href: string): string {
  if (/^https?:\/\//.test(href) || href.startsWith('#')) return href;
  const localMd = href.match(/^([\w-]+)\.md(#.*)?$/);
  if (localMd) return `/docs/${localMd[1]}`;
  return href;
}

declare global {
  interface HTMLElementTagNameMap {
    'docs-view': DocsView;
  }
}
