import { LitElement, html, css, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { navigate } from '../router.js';

// The setup guide lives as raw Markdown served by the backend at /docs/<page>.md (the
// LLM/agent surface — see Program.cs). This component is the *human* surface: it fetches
// the same raw file and renders it. We deliberately do NOT add a markdown-library
// dependency — the docs use a small, known subset (headings, lists, fenced code,
// blockquotes, inline code/bold/links), so a focused renderer keeps the bundle lean.

const DEFAULT_PAGE = 'index';

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

    .doc {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      padding: 1.5rem 2rem;
      line-height: 1.6;
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

    .doc p {
      margin: 0.75rem 0;
    }

    .doc ul,
    .doc ol {
      margin: 0.75rem 0;
      padding-left: 1.5rem;
    }

    .doc li {
      margin: 0.3rem 0;
    }

    .doc code {
      background: var(--color-surface-hover);
      border-radius: var(--radius);
      padding: 0.1rem 0.3rem;
    }

    .doc pre {
      margin: 1rem 0;
    }

    .doc pre code {
      background: none;
      padding: 0;
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

  @state() private declare _blocks: TemplateResult[];
  @state() private declare _loading: boolean;
  @state() private declare _error: string | null;

  constructor() {
    super();
    this.page = DEFAULT_PAGE;
    this._blocks = [];
    this._loading = true;
    this._error = null;
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
      this._blocks = renderMarkdown(text);
    } catch (e) {
      this._error = e instanceof Error ? e.message : String(e);
    } finally {
      this._loading = false;
    }
  }

  render() {
    return html`
      <div class="doc-nav">
        <a href="/" @click=${this._navHome}>&larr; Back to pipelines</a>
      </div>
      ${this._loading
        ? html`<p class="loading">Loading docs…</p>`
        : this._error
          ? html`<p class="error">${this._error}</p>`
          : html`<article class="doc" @click=${this._onLinkClick}>${this._blocks}</article>`}
    `;
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

// ---- Minimal Markdown renderer (block-level) ----

function renderMarkdown(src: string): TemplateResult[] {
  const lines = src.replace(/\r\n/g, '\n').split('\n');
  const blocks: TemplateResult[] = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    // Fenced code block
    const fence = line.match(/^```(.*)$/);
    if (fence) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !lines[i].startsWith('```')) {
        code.push(lines[i]);
        i++;
      }
      i++; // closing fence
      blocks.push(html`<pre><code>${code.join('\n')}</code></pre>`);
      continue;
    }

    // Heading
    const heading = line.match(/^(#{1,3})\s+(.*)$/);
    if (heading) {
      const text = inline(heading[2]);
      blocks.push(
        heading[1].length === 1
          ? html`<h1>${text}</h1>`
          : heading[1].length === 2
            ? html`<h2>${text}</h2>`
            : html`<h3>${text}</h3>`
      );
      i++;
      continue;
    }

    // Horizontal rule
    if (/^---+$/.test(line.trim())) {
      blocks.push(html`<hr />`);
      i++;
      continue;
    }

    // Blockquote
    if (line.startsWith('>')) {
      const quote: string[] = [];
      while (i < lines.length && lines[i].startsWith('>')) {
        quote.push(lines[i].replace(/^>\s?/, ''));
        i++;
      }
      blocks.push(html`<blockquote>${paragraphs(quote)}</blockquote>`);
      continue;
    }

    // Lists (unordered or ordered)
    const listMatch = line.match(/^(\s*)([-*]|\d+\.)\s+(.*)$/);
    if (listMatch) {
      const ordered = /\d+\./.test(listMatch[2]);
      const items: TemplateResult[] = [];
      while (i < lines.length) {
        const m = lines[i].match(/^(\s*)([-*]|\d+\.)\s+(.*)$/);
        if (!m) break;
        items.push(html`<li>${inline(m[3])}</li>`);
        i++;
      }
      blocks.push(ordered ? html`<ol>${items}</ol>` : html`<ul>${items}</ul>`);
      continue;
    }

    // Blank line
    if (line.trim() === '') {
      i++;
      continue;
    }

    // Paragraph — gather until blank line or block boundary
    const para: string[] = [];
    while (
      i < lines.length &&
      lines[i].trim() !== '' &&
      !lines[i].startsWith('```') &&
      !lines[i].startsWith('>') &&
      !/^#{1,3}\s/.test(lines[i]) &&
      !/^(\s*)([-*]|\d+\.)\s+/.test(lines[i])
    ) {
      para.push(lines[i]);
      i++;
    }
    blocks.push(html`<p>${inline(para.join(' '))}</p>`);
  }

  return blocks;
}

function paragraphs(lines: string[]): TemplateResult[] {
  const out: TemplateResult[] = [];
  let buf: string[] = [];
  const flush = () => {
    if (buf.length) {
      out.push(html`<p>${inline(buf.join(' '))}</p>`);
      buf = [];
    }
  };
  for (const l of lines) {
    if (l.trim() === '') flush();
    else buf.push(l);
  }
  flush();
  return out;
}

// Inline tokens: `code`, **bold**, [text](href). Tokenized so we never inject raw HTML —
// each piece becomes a Lit value, keeping it XSS-safe.
function inline(text: string): (TemplateResult | string)[] {
  const out: (TemplateResult | string)[] = [];
  const re = /(`[^`]+`)|(\*\*[^*]+\*\*)|(\[[^\]]+\]\([^)]+\))/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const token = m[0];
    if (token.startsWith('`')) {
      out.push(html`<code>${token.slice(1, -1)}</code>`);
    } else if (token.startsWith('**')) {
      out.push(html`<strong>${token.slice(2, -2)}</strong>`);
    } else {
      const linkMatch = token.match(/^\[([^\]]+)\]\(([^)]+)\)$/)!;
      out.push(renderLink(linkMatch[1], linkMatch[2]));
    }
    last = re.lastIndex;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function renderLink(label: string, href: string): TemplateResult {
  const resolved = resolveDocHref(href);
  const external = resolved.startsWith('http://') || resolved.startsWith('https://');
  return external
    ? html`<a href=${resolved} target="_blank" rel="noopener">${label}</a>`
    : html`<a href=${resolved}>${label}</a>`;
}

// Inter-doc links in the source are relative (e.g. "getting-started.md",
// "config-reference.md#anchor"). Rewrite them to client routes (/docs/<slug>) so the
// in-app viewer chains naturally instead of downloading the raw .md.
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
