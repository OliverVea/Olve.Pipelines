import './components/app-shell.js';
import './components/pipeline-list-view.js';
import './components/pipeline-detail-view.js';
import './components/docs-view.js';
import './components/run-view.js';
import './components/step-runs-view.js';
import './components/critical-path-view.js';
import './components/reliability-view.js';
import './components/step-logs-view.js';
import './components/pipeline-runs-view.js';
import './components/pipeline-versions-view.js';
import './components/pipeline-secrets-view.js';
import './components/pipeline-binding-view.js';

// Shows the offline page when the server can't be reached.
if ('serviceWorker' in navigator && window.isSecureContext)
  navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(() => {});
