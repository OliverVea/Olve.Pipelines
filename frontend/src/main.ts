import './style.css'
import { client } from './api.js'
import type { Pipeline, PipelineBuilder, PipelineSource, ProcessingStep, ScriptVerification, Verification } from '@olve/olve-pipelines-client/src/models/index.js'

const app = document.querySelector<HTMLDivElement>('#app')!;

function showError(error: unknown) {
  const msg = error instanceof Error ? error.message : String(error);
  app.innerHTML = `<p><a href="/" data-link>&larr; Back</a></p><pre style="color:red;white-space:pre-wrap">${msg}</pre>`;
}

function fill(selector: string, html: string) {
  const target = document.querySelector(selector);
  if (target) target.innerHTML = html;
}

async function navigate() {
  const path = window.location.pathname;
  const m = path.match(/^\/pipeline\/([^/]+)$/);
  try {
    if (m) await renderPipeline(m[1]);
    else await renderList();
  } catch (e) { showError(e); }
}

// --- Pipeline List ---

async function renderList() {
  const pipelines = await client.api.pipelines.get() ?? [];
  app.innerHTML = `
    <h1>Pipelines</h1>
    ${pipelines.map(p => `
      <div class="card">
        <a href="/pipeline/${p.id}" data-link><strong>${p.name}</strong></a>
        <button class="delete-pipeline" data-id="${p.id}">Delete</button>
      </div>
    `).join('')}
    ${pipelines.length === 0 ? '<p>No pipelines yet.</p>' : ''}
    <form id="create-pipeline" class="inline-form">
      <input type="text" name="name" placeholder="New pipeline name" required />
      <button type="submit">Create</button>
    </form>
  `;
  on('create-pipeline', 'submit', async (e) => {
    e.preventDefault();
    await client.api.pipelines.post({ queryParameters: { name: fd(e).get('name') as string } });
    await navigate();
  });
  onAll('.delete-pipeline', 'click', async (e) => {
    await client.api.pipelines.byId(data(e).id!).delete();
    await navigate();
  });
}

// --- Pipeline Detail ---

async function renderPipeline(pid: string) {
  const p = client.api.pipelines.byId(pid);

  app.innerHTML = `
    <p><a href="/" data-link>&larr; Pipelines</a></p>
    <div id="pipeline-header">Loading...</div>
    <section><h2>Sources</h2><div id="sources-content" class="loading">Loading...</div></section>
    <section><h2>Builders</h2><div id="builders-content" class="loading">Loading...</div></section>
    <section><h2>Processing</h2><div id="processing-content" class="loading">Loading...</div></section>
    <section><h2>Triggers</h2><div id="triggers-content"></div></section>
  `;

  // Pipeline header
  p.get().then((pipeline: Pipeline | undefined) => {
    if (!pipeline) { app.innerHTML = `<p>Not found.</p><p><a href="/" data-link>Back</a></p>`; return; }
    fill('#pipeline-header', `<h1>${pipeline.name}</h1>`);
  }).catch((e: unknown) => fill('#pipeline-header', err(e)));

  // Sources
  p.sources.get().then(async (sources: PipelineSource[] | undefined) => {
    const list = sources ?? [];
    const withConfig = await Promise.all(list.map(async (s: PipelineSource) => ({
      source: s,
      github: await p.sources.bySourceId(s.id!).github.get().catch(() => null),
      hardcoded: await p.sources.bySourceId(s.id!).hardcoded.get().catch(() => null),
    })));
    fill('#sources-content', `
      ${withConfig.map(({ source: s, github: gh, hardcoded: hc }) => {
        const files = hc?.files?.additionalData as Record<string, unknown> | undefined;
        const fileEntries = files ? Object.entries(files) : [];
        return `
        <div class="card">
          <strong>${s.name}</strong>
          <button class="delete-source" data-id="${s.id}">Delete</button>
          ${gh ? `<div class="attachment">GitHub: ${gh.owner}/${gh.repository} (${gh.branch})</div>` : ''}
          ${hc ? `<div class="attachment">Hardcoded: ${fileEntries.length} file(s)<ul class="file-list">${fileEntries.map(([path]) => `<li><code>${esc(path)}</code></li>`).join('')}</ul></div>` : ''}
          ${!gh && !hc ? `
            <div class="sub-items">
              <details><summary>Set GitHub</summary>
                <form class="inline-form set-github" data-id="${s.id}">
                  <input type="text" name="owner" placeholder="Owner" required />
                  <input type="text" name="repository" placeholder="Repository" required />
                  <input type="text" name="branch" placeholder="Branch" value="main" required />
                  <button type="submit">Set</button>
                </form>
              </details>
              <details><summary>Set Hardcoded</summary>
                <form class="set-hardcoded" data-id="${s.id}">
                  <div class="file-entries">
                    <div class="file-entry inline-form">
                      <input type="text" name="path" placeholder="path/to/file" required />
                      <textarea name="content" placeholder="File content" rows="2" required></textarea>
                    </div>
                  </div>
                  <button type="button" class="add-file-entry" data-id="${s.id}">+ Add file</button>
                  <button type="submit">Set</button>
                </form>
              </details>
            </div>
          ` : ''}
        </div>
      `}).join('') || '<p>None.</p>'}
      <form class="inline-form" id="add-source">
        <input type="text" name="name" placeholder="Source name" required />
        <button type="submit">Add Source</button>
      </form>
    `);
    on('add-source', 'submit', async (e) => {
      e.preventDefault();
      await p.sources.post({ name: fd(e).get('name') as string });
      await navigate();
    });
    onAll('.delete-source', 'click', async (e) => {
      await p.sources.bySourceId(data(e).id!).delete();
      await navigate();
    });
    onAll('.set-github', 'submit', async (e) => {
      e.preventDefault();
      const d = fd(e);
      const sid = (e.target as HTMLFormElement).dataset.id!;
      await p.sources.bySourceId(sid).github.put({
        owner: d.get('owner') as string,
        repository: d.get('repository') as string,
        branch: d.get('branch') as string,
      });
      await navigate();
    });
    onAll('.add-file-entry', 'click', (e) => {
      const form = (e.currentTarget as HTMLElement).closest('form')!;
      const entries = form.querySelector('.file-entries')!;
      entries.insertAdjacentHTML('beforeend', `
        <div class="file-entry inline-form">
          <input type="text" name="path" placeholder="path/to/file" required />
          <textarea name="content" placeholder="File content" rows="2" required></textarea>
        </div>
      `);
      return Promise.resolve();
    });
    onAll('.set-hardcoded', 'submit', async (e) => {
      e.preventDefault();
      const sid = (e.target as HTMLFormElement).dataset.id!;
      const form = e.target as HTMLFormElement;
      const paths = form.querySelectorAll<HTMLInputElement>('input[name="path"]');
      const contents = form.querySelectorAll<HTMLTextAreaElement>('textarea[name="content"]');
      const files: Record<string, string> = {};
      paths.forEach((input, i) => { files[input.value] = contents[i].value; });
      // Use fetch for the dict — Kiota can't serialize additionalData properly
      await fetch(`/api/pipelines/${pid}/sources/${sid}/hardcoded`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ files }),
      });
      await navigate();
    });
  }).catch((e: unknown) => fill('#sources-content', err(e)));

  // Builders
  p.builders.get().then(async (builders: PipelineBuilder[] | undefined) => {
    const list = builders ?? [];
    const withScript = await Promise.all(list.map(async (b: PipelineBuilder) => ({
      builder: b,
      script: await p.builders.byBuilderId(b.id!).script.get().catch(() => null),
    })));
    fill('#builders-content', `
      ${withScript.map(({ builder: b, script }) => `
        <div class="card">
          <strong>${b.name}</strong>
          <button class="delete-builder" data-id="${b.id}">Delete</button>
          ${script ? `<div class="attachment">Script: <code>${esc(script.script ?? '')}</code></div>` : `
            <details><summary>Set Script</summary>
              <form class="inline-form set-builder-script" data-id="${b.id}">
                <input type="text" name="script" placeholder="Script command" required />
                <button type="submit">Set</button>
              </form>
            </details>
          `}
        </div>
      `).join('') || '<p>None.</p>'}
      <form class="inline-form" id="add-builder">
        <input type="text" name="name" placeholder="Builder name" required />
        <button type="submit">Add Builder</button>
      </form>
    `);
    on('add-builder', 'submit', async (e) => {
      e.preventDefault();
      await p.builders.post({ name: fd(e).get('name') as string });
      await navigate();
    });
    onAll('.delete-builder', 'click', async (e) => {
      await p.builders.byBuilderId(data(e).id!).delete();
      await navigate();
    });
    onAll('.set-builder-script', 'submit', async (e) => {
      e.preventDefault();
      const bid = (e.target as HTMLFormElement).dataset.id!;
      await p.builders.byBuilderId(bid).script.put({ script: fd(e).get('script') as string });
      await navigate();
    });
  }).catch((e: unknown) => fill('#builders-content', err(e)));

  // Processing
  p.processing.get().then(async (steps: ProcessingStep[] | undefined) => {
    const list = steps ?? [];
    const withDetails = await Promise.all(list.map(async (s: ProcessingStep) => ({
      step: s,
      script: await p.processing.byProcessingId(s.id!).script.get().catch(() => null),
      verifications: await Promise.all(
        ((await p.processing.byProcessingId(s.id!).verifications.get()) ?? []).map(async (v: Verification) => ({
          verification: v,
          script: await p.processing.byProcessingId(s.id!).verifications.byVerificationId(v.id!).script.get().catch(() => null),
        }))
      ),
    })));
    fill('#processing-content', `
      ${withDetails.map(({ step: s, script, verifications }) => `
        <div class="card">
          <strong>${s.name}</strong>
          <button class="delete-processing" data-id="${s.id}">Delete</button>
          ${script ? `<div class="attachment">Script: <code>${esc(script.script ?? '')}</code></div>` : `
            <details><summary>Set Script</summary>
              <form class="inline-form set-processing-script" data-id="${s.id}">
                <input type="text" name="script" placeholder="Script command" required />
                <button type="submit">Set</button>
              </form>
            </details>
          `}
          <div class="sub-items">
            <h3>Verifications</h3>
            ${verifications.map(({ verification: v, script: vs }: { verification: Verification; script: ScriptVerification | null | undefined }) => `
              <div class="chip-row">
                <span class="chip">${v.name}${vs ? ` — <code>${esc(vs.script ?? '')}</code>` : ''}
                  <button class="delete-verification" data-processing="${s.id}" data-id="${v.id}">&times;</button>
                </span>
                ${!vs ? `
                  <form class="inline-form set-verification-script" data-processing="${s.id}" data-id="${v.id}">
                    <input type="text" name="script" placeholder="Script" required />
                    <button type="submit">Set</button>
                  </form>
                ` : ''}
              </div>
            `).join('') || '<em>None</em>'}
            <form class="inline-form add-verification" data-processing="${s.id}">
              <input type="text" name="name" placeholder="Verification name" required />
              <button type="submit">Add</button>
            </form>
          </div>
        </div>
      `).join('') || '<p>None.</p>'}
      <form class="inline-form" id="add-processing">
        <input type="text" name="name" placeholder="Step name" required />
        <button type="submit">Add Step</button>
      </form>
    `);
    on('add-processing', 'submit', async (e) => {
      e.preventDefault();
      await p.processing.post({ name: fd(e).get('name') as string });
      await navigate();
    });
    onAll('.delete-processing', 'click', async (e) => {
      await p.processing.byProcessingId(data(e).id!).delete();
      await navigate();
    });
    onAll('.set-processing-script', 'submit', async (e) => {
      e.preventDefault();
      const sid = (e.target as HTMLFormElement).dataset.id!;
      await p.processing.byProcessingId(sid).script.put({ script: fd(e).get('script') as string });
      await navigate();
    });
    onAll('.add-verification', 'submit', async (e) => {
      e.preventDefault();
      const processingId = (e.target as HTMLFormElement).dataset.processing!;
      await p.processing.byProcessingId(processingId).verifications.post({ name: fd(e).get('name') as string });
      await navigate();
    });
    onAll('.delete-verification', 'click', async (e) => {
      const d = data(e);
      await p.processing.byProcessingId(d.processing!).verifications.byVerificationId(d.id!).delete();
      await navigate();
    });
    onAll('.set-verification-script', 'submit', async (e) => {
      e.preventDefault();
      const form = e.target as HTMLFormElement;
      await p.processing.byProcessingId(form.dataset.processing!).verifications.byVerificationId(form.dataset.id!).script.put({
        script: fd(e).get('script') as string,
      });
      await navigate();
    });
  }).catch((e: unknown) => fill('#processing-content', err(e)));

  // Triggers
  fill('#triggers-content', `
    <div class="card">
      <button id="trigger-sourcing">Run Sourcing</button>
      <button id="trigger-building">Run Building</button>
    </div>
  `);
  on('trigger-sourcing', 'click', async () => {
    await p.trigger.sourcing.post();
    alert('Sourcing triggered');
  });
  on('trigger-building', 'click', async () => {
    await p.trigger.building.post();
    alert('Building triggered');
  });
}

// --- Helpers ---

function err(e: unknown) { return `<pre style="color:red">${e}</pre>`; }
function esc(s: string) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function on(id: string, event: string, handler: (e: Event) => Promise<void>) {
  document.getElementById(id)?.addEventListener(event, (e) => handler(e).catch(showError));
}
function onAll(sel: string, event: string, handler: (e: Event) => Promise<void>) {
  document.querySelectorAll(sel).forEach(el => el.addEventListener(event, (e) => handler(e).catch(showError)));
}
function fd(e: Event) { return new FormData(e.target as HTMLFormElement); }
function data(e: Event) { return (e.currentTarget as HTMLElement).dataset; }

// --- Navigation ---

document.addEventListener('click', (e) => {
  const target = (e.target as HTMLElement).closest<HTMLAnchorElement>('a[data-link]');
  if (!target) return;
  e.preventDefault();
  history.pushState(null, '', target.href);
  navigate();
});
window.addEventListener('popstate', navigate);
navigate();
