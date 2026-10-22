export interface Route {
  pattern: RegExp;
  params: string[];
  view: string;
}

const routes: Route[] = [
  { pattern: /^\/$/, params: [], view: 'pipeline-list-view' },
  { pattern: /^\/callback/, params: [], view: 'callback' },
  { pattern: /^\/docs\/([\w-]+)$/, params: ['page'], view: 'docs-view' },
  { pattern: /^\/docs\/?$/, params: [], view: 'docs-view' },
  { pattern: /^\/pipeline\/([^/]+)\/run\/(\d+)\/step\/([^/]+)\/logs$/, params: ['pipelineId', 'run', 'step'], view: 'step-logs-view' },
  { pattern: /^\/pipeline\/([^/]+)\/run\/(\d+)$/, params: ['pipelineId', 'run'], view: 'run-view' },
  { pattern: /^\/pipeline\/([^/]+)\/step\/([^/]+)$/, params: ['pipelineId', 'step'], view: 'step-history-view' },
  { pattern: /^\/pipeline\/([^/]+)\/runs$/, params: ['pipelineId'], view: 'pipeline-runs-view' },
  { pattern: /^\/pipeline\/([^/]+)\/versions$/, params: ['pipelineId'], view: 'pipeline-versions-view' },
  { pattern: /^\/pipeline\/([^/]+)$/, params: ['pipelineId'], view: 'pipeline-detail-view' },
];

export interface ResolvedRoute {
  view: string;
  params: Record<string, string>;
}

export function resolve(path: string): ResolvedRoute {
  for (const route of routes) {
    const match = path.match(route.pattern);
    if (match) {
      const params: Record<string, string> = {};
      route.params.forEach((name, i) => {
        params[name] = decodeURIComponent(match[i + 1]);
      });
      return { view: route.view, params };
    }
  }
  return { view: 'pipeline-list-view', params: {} };
}

export function navigate(path: string, state: unknown = null): void {
  history.pushState(state, '', path);
  window.dispatchEvent(new CustomEvent('route-changed'));
}
