import './style.css'
import { client } from './api.js'
import { isUntypedNode, isUntypedString } from '@microsoft/kiota-abstractions'
import type { Parsable } from '@microsoft/kiota-abstractions'

const app = document.querySelector<HTMLDivElement>('#app')!;

function id(entity: Parsable & { id?: unknown }): string {
  const v = entity.id;
  if (isUntypedNode(v) && isUntypedString(v)) return String(v.value);
  return String(v);
}

function showError(error: unknown) {
  const msg = error instanceof Error ? error.message : String(error);
  app.innerHTML = `<p><a href="/" data-link>&larr; Back</a></p><pre style="color:red;white-space:pre-wrap">${msg}</pre>`;
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
        <a href="/pipeline/${id(p)}" data-link><strong>${p.name}</strong></a>
        <button class="delete-pipeline" data-id="${id(p)}">Delete</button>
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
    await client.api.pipelines.byId((e.currentTarget as HTMLElement).dataset.id!).delete();
    await navigate();
  });
}

// --- Pipeline Detail ---

async function renderPipeline(pid: string) {
  const p = client.api.pipelines.byId(pid);
  const [pipeline, sources, builders, processing] = await Promise.all([
    p.get(), p.sources.get(), p.builders.get(), p.processing.get(),
  ]);
  if (!pipeline) { app.innerHTML = `<p>Not found.</p><p><a href="/" data-link>Back</a></p>`; return; }

  // Load artifacts per builder and verifications per processing step in parallel
  const builderArtifacts = await Promise.all(
    (builders ?? []).map(async b => ({
      builder: b,
      artifacts: await p.builders.byBuilderId(id(b)).artifacts.get() ?? [],
    }))
  );
  const processingVerifications = await Promise.all(
    (processing ?? []).map(async s => ({
      step: s,
      verifications: await p.processing.byProcessingId(id(s)).verifications.get() ?? [],
    }))
  );

  app.innerHTML = `
    <p><a href="/" data-link>&larr; Pipelines</a></p>
    <h1>${pipeline.name}</h1>
    <code>${id(pipeline)}</code>

    <section>
      <h2>Sources</h2>
      ${(sources ?? []).map(s => `
        <div class="card">
          <strong>${s.name}</strong> <code>${id(s)}</code>
          <button class="delete-source" data-id="${id(s)}">Delete</button>
        </div>
      `).join('') || '<p>None.</p>'}
      <form class="inline-form" id="add-source">
        <input type="text" name="name" placeholder="Source name" required />
        <button type="submit">Add Source</button>
      </form>
    </section>

    <section>
      <h2>Builders</h2>
      ${builderArtifacts.map(({ builder: b, artifacts }) => `
        <div class="card">
          <strong>${b.name}</strong> <code>${id(b)}</code>
          <button class="delete-builder" data-id="${id(b)}">Delete</button>
          <div class="sub-items">
            <h3>Artifacts</h3>
            ${artifacts.map(a => `
              <span class="chip">${a.name} <button class="delete-artifact" data-builder="${id(b)}" data-id="${id(a)}">&times;</button></span>
            `).join('') || '<em>None</em>'}
            <form class="inline-form add-artifact" data-builder="${id(b)}">
              <input type="text" name="name" placeholder="Artifact name" required />
              <button type="submit">Add</button>
            </form>
          </div>
        </div>
      `).join('') || '<p>None.</p>'}
      <form class="inline-form" id="add-builder">
        <input type="text" name="name" placeholder="Builder name" required />
        <button type="submit">Add Builder</button>
      </form>
    </section>

    <section>
      <h2>Processing</h2>
      ${processingVerifications.map(({ step: s, verifications }) => `
        <div class="card">
          <strong>${s.name}</strong> <code>${id(s)}</code>
          <button class="delete-processing" data-id="${id(s)}">Delete</button>
          <div class="sub-items">
            <h3>Verifications</h3>
            ${verifications.map(v => `
              <span class="chip">${v.name} <button class="delete-verification" data-processing="${id(s)}" data-id="${id(v)}">&times;</button></span>
            `).join('') || '<em>None</em>'}
            <form class="inline-form add-verification" data-processing="${id(s)}">
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
    </section>
  `;

  // Sources
  on('add-source', 'submit', async (e) => {
    e.preventDefault();
    await post(`/api/pipelines/${pid}/sources`, { name: fd(e).get('name') });
    await navigate();
  });
  onAll('.delete-source', 'click', async (e) => {
    await p.sources.bySourceId(data(e).id!).delete();
    await navigate();
  });

  // Builders
  on('add-builder', 'submit', async (e) => {
    e.preventDefault();
    await p.builders.post({ name: fd(e).get('name') as string });
    await navigate();
  });
  onAll('.delete-builder', 'click', async (e) => {
    await p.builders.byBuilderId(data(e).id!).delete();
    await navigate();
  });

  // Artifacts
  onAll('.add-artifact', 'submit', async (e) => {
    e.preventDefault();
    const builderId = (e.target as HTMLFormElement).dataset.builder!;
    await p.builders.byBuilderId(builderId).artifacts.post({ name: fd(e).get('name') as string });
    await navigate();
  });
  onAll('.delete-artifact', 'click', async (e) => {
    const d = data(e);
    await p.builders.byBuilderId(d.builder!).artifacts.byArtifactId(d.id!).delete();
    await navigate();
  });

  // Processing
  on('add-processing', 'submit', async (e) => {
    e.preventDefault();
    await p.processing.post({ name: fd(e).get('name') as string });
    await navigate();
  });
  onAll('.delete-processing', 'click', async (e) => {
    await p.processing.byProcessingId(data(e).id!).delete();
    await navigate();
  });

  // Verifications
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
}

// --- Helpers ---

function on(id: string, event: string, handler: (e: Event) => Promise<void>) {
  document.getElementById(id)?.addEventListener(event, (e) => handler(e).catch(showError));
}
function onAll(sel: string, event: string, handler: (e: Event) => Promise<void>) {
  document.querySelectorAll(sel).forEach(el => el.addEventListener(event, (e) => handler(e).catch(showError)));
}
function fd(e: Event) { return new FormData(e.target as HTMLFormElement); }
function data(e: Event) { return (e.currentTarget as HTMLElement).dataset; }
async function post(url: string, body: object) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
}

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
