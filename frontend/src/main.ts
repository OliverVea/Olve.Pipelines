import './style.css'
import { client } from './api.js'
import { isUntypedNode, isUntypedString } from '@microsoft/kiota-abstractions'
import type { Parsable } from '@microsoft/kiota-abstractions'

const app = document.querySelector<HTMLDivElement>('#app')!;

function getIdStr(entity: Parsable & { id?: unknown }): string {
  const id = entity.id;
  if (isUntypedNode(id) && isUntypedString(id)) return String(id.value);
  return String(id);
}

function showError(error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  app.innerHTML = `
    <p><a href="/" data-link>&larr; Back</a></p>
    <pre style="color: red; white-space: pre-wrap;">${message}</pre>
  `;
}

// --- Routing ---

const routes: [RegExp, (...args: string[]) => Promise<void>][] = [
  [/^\/pipeline\/([^/]+)\/build\/([^/]+)$/, renderBuild],
  [/^\/pipeline\/([^/]+)\/processing\/([^/]+)$/, renderProcessing],
  [/^\/pipeline\/([^/]+)$/, renderPipeline],
  [/^\/$/, renderPipelineList],
];

async function navigate() {
  const path = window.location.pathname;
  try {
    for (const [pattern, handler] of routes) {
      const match = path.match(pattern);
      if (match) {
        await handler(...match.slice(1));
        return;
      }
    }
    await renderPipelineList();
  } catch (e) {
    showError(e);
  }
}

// --- Pipeline List ---

async function renderPipelineList() {
  const pipelines = await client.api.pipelines.get() ?? [];

  app.innerHTML = `
    <h1>Pipelines</h1>
    <ul>
      ${pipelines.map(p => `<li><a href="/pipeline/${getIdStr(p)}" data-link>${p.name}</a></li>`).join('')}
    </ul>
    ${pipelines.length === 0 ? '<p>No pipelines yet.</p>' : ''}
    <h2>Create Pipeline</h2>
    <form id="create-pipeline">
      <input type="text" name="name" placeholder="Pipeline name" required />
      <button type="submit">Create</button>
    </form>
  `;

  on('create-pipeline', 'submit', async (e) => {
    e.preventDefault();
    const name = formData(e).get('name') as string;
    await client.api.pipelines.post({ queryParameters: { name } });
    await navigate();
  });
}

// --- Pipeline Detail ---

async function renderPipeline(pipelineId: string) {
  const pipeline = await client.api.pipelines.byId(pipelineId).get();
  if (!pipeline) return notFound('Pipeline');

  const sources = await client.api.pipelines.byId(pipelineId).sources.get() ?? [];
  const builds = await client.api.pipelines.byId(pipelineId).builds.get() ?? [];
  const processing = await client.api.pipelines.byId(pipelineId).processing.get() ?? [];

  app.innerHTML = `
    <p><a href="/" data-link>&larr; Pipelines</a></p>
    <h1>${pipeline.name}</h1>
    <p><code>${getIdStr(pipeline)}</code></p>
    <button id="delete-pipeline">Delete Pipeline</button>

    <hr />
    <h2>Sources</h2>
    <ul>
      ${sources.map(s => `<li>${s.name ?? 'unnamed'} — ${s.additionalData?.['owner'] ?? ''}/${s.additionalData?.['repository'] ?? ''} (${s.additionalData?.['branch'] ?? ''}) <button class="delete-source" data-id="${getIdStr(s)}">Delete</button></li>`).join('')}
    </ul>
    ${sources.length === 0 ? '<p>No sources.</p>' : ''}
    <details>
      <summary>Add GitHub Source</summary>
      <form id="add-source">
        <input type="text" name="name" placeholder="Source name" required />
        <input type="text" name="owner" placeholder="Owner" required />
        <input type="text" name="repository" placeholder="Repository" required />
        <input type="text" name="branch" placeholder="Branch" value="main" required />
        <button type="submit">Add</button>
      </form>
    </details>

    <hr />
    <h2>Builds</h2>
    <ul>
      ${builds.map(b => `<li><a href="/pipeline/${pipelineId}/build/${getIdStr(b)}" data-link>${b.name}</a> <button class="delete-build" data-id="${getIdStr(b)}">Delete</button></li>`).join('')}
    </ul>
    ${builds.length === 0 ? '<p>No builds.</p>' : ''}
    <details>
      <summary>Add Build</summary>
      <form id="add-build">
        <input type="text" name="name" placeholder="Build name" required />
        <button type="submit">Add</button>
      </form>
    </details>

    <hr />
    <h2>Processing Steps</h2>
    <ul>
      ${processing.map(p => `<li><a href="/pipeline/${pipelineId}/processing/${getIdStr(p)}" data-link>${p.name}</a> <button class="delete-processing" data-id="${getIdStr(p)}">Delete</button></li>`).join('')}
    </ul>
    ${processing.length === 0 ? '<p>No processing steps.</p>' : ''}
    <details>
      <summary>Add Processing Step</summary>
      <form id="add-processing">
        <input type="text" name="name" placeholder="Step name" required />
        <button type="submit">Add</button>
      </form>
    </details>
  `;

  on('delete-pipeline', 'click', async () => {
    await client.api.pipelines.byId(pipelineId).delete();
    history.pushState(null, '', '/');
    await navigate();
  });

  onAll('.delete-source', 'click', async (e) => {
    const sourceId = (e.currentTarget as HTMLElement).dataset.id!;
    await client.api.pipelines.byId(pipelineId).sources.bySourceId(sourceId).delete();
    await navigate();
  });

  on('add-source', 'submit', async (e) => {
    e.preventDefault();
    const d = formData(e);
    await fetch(`/api/pipelines/${pipelineId}/sources`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        type: 'github',
        name: d.get('name'),
        owner: d.get('owner'),
        repository: d.get('repository'),
        branch: d.get('branch'),
      }),
    });
    await navigate();
  });

  onAll('.delete-build', 'click', async (e) => {
    const buildId = (e.currentTarget as HTMLElement).dataset.id!;
    await client.api.pipelines.byId(pipelineId).builds.byBuildId(buildId).delete();
    await navigate();
  });

  on('add-build', 'submit', async (e) => {
    e.preventDefault();
    await client.api.pipelines.byId(pipelineId).builds.post({ name: formData(e).get('name') as string });
    await navigate();
  });

  onAll('.delete-processing', 'click', async (e) => {
    const processingId = (e.currentTarget as HTMLElement).dataset.id!;
    await client.api.pipelines.byId(pipelineId).processing.byProcessingId(processingId).delete();
    await navigate();
  });

  on('add-processing', 'submit', async (e) => {
    e.preventDefault();
    await client.api.pipelines.byId(pipelineId).processing.post({ name: formData(e).get('name') as string });
    await navigate();
  });
}

// --- Build Detail ---

async function renderBuild(pipelineId: string, buildId: string) {
  const build = await client.api.pipelines.byId(pipelineId).builds.byBuildId(buildId).get();
  if (!build) return notFound('Build');

  const artifacts = await client.api.pipelines.byId(pipelineId).builds.byBuildId(buildId).artifacts.get() ?? [];

  app.innerHTML = `
    <p><a href="/pipeline/${pipelineId}" data-link>&larr; Pipeline</a></p>
    <h1>Build: ${build.name}</h1>
    <p><code>${getIdStr(build)}</code></p>

    <hr />
    <h2>Artifacts</h2>
    <ul>
      ${artifacts.map(a => `<li>${a.name} <code>${getIdStr(a)}</code> <button class="delete-artifact" data-id="${getIdStr(a)}">Delete</button></li>`).join('')}
    </ul>
    ${artifacts.length === 0 ? '<p>No artifacts.</p>' : ''}
    <details>
      <summary>Add Artifact</summary>
      <form id="add-artifact">
        <input type="text" name="name" placeholder="Artifact name" required />
        <button type="submit">Add</button>
      </form>
    </details>
  `;

  onAll('.delete-artifact', 'click', async (e) => {
    const artifactId = (e.currentTarget as HTMLElement).dataset.id!;
    await client.api.pipelines.byId(pipelineId).builds.byBuildId(buildId).artifacts.byArtifactId(artifactId).delete();
    await navigate();
  });

  on('add-artifact', 'submit', async (e) => {
    e.preventDefault();
    await client.api.pipelines.byId(pipelineId).builds.byBuildId(buildId).artifacts.post({ name: formData(e).get('name') as string });
    await navigate();
  });
}

// --- Processing Detail ---

async function renderProcessing(pipelineId: string, processingId: string) {
  const step = await client.api.pipelines.byId(pipelineId).processing.byProcessingId(processingId).get();
  if (!step) return notFound('Processing step');

  const verifications = await client.api.pipelines.byId(pipelineId).processing.byProcessingId(processingId).verifications.get() ?? [];

  app.innerHTML = `
    <p><a href="/pipeline/${pipelineId}" data-link>&larr; Pipeline</a></p>
    <h1>Processing: ${step.name}</h1>
    <p><code>${getIdStr(step)}</code></p>

    <hr />
    <h2>Verifications</h2>
    <ul>
      ${verifications.map(v => `<li>${v.name} <code>${getIdStr(v)}</code> <button class="delete-verification" data-id="${getIdStr(v)}">Delete</button></li>`).join('')}
    </ul>
    ${verifications.length === 0 ? '<p>No verifications.</p>' : ''}
    <details>
      <summary>Add Verification</summary>
      <form id="add-verification">
        <input type="text" name="name" placeholder="Verification name" required />
        <button type="submit">Add</button>
      </form>
    </details>
  `;

  onAll('.delete-verification', 'click', async (e) => {
    const verificationId = (e.currentTarget as HTMLElement).dataset.id!;
    await client.api.pipelines.byId(pipelineId).processing.byProcessingId(processingId).verifications.byVerificationId(verificationId).delete();
    await navigate();
  });

  on('add-verification', 'submit', async (e) => {
    e.preventDefault();
    await client.api.pipelines.byId(pipelineId).processing.byProcessingId(processingId).verifications.post({ name: formData(e).get('name') as string });
    await navigate();
  });
}

// --- Helpers ---

function notFound(entity: string) {
  app.innerHTML = `<p>${entity} not found.</p><p><a href="/" data-link>Back</a></p>`;
}

function on(id: string, event: string, handler: (e: Event) => Promise<void>) {
  document.getElementById(id)?.addEventListener(event, (e) => handler(e).catch(showError));
}

function onAll(selector: string, event: string, handler: (e: Event) => Promise<void>) {
  document.querySelectorAll(selector).forEach(el =>
    el.addEventListener(event, (e) => handler(e).catch(showError))
  );
}

function formData(e: Event): FormData {
  return new FormData(e.target as HTMLFormElement);
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
