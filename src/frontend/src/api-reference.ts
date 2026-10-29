// Its own page, embedded by docs-view: Redoc styles the document head, which the app's shadow DOM never sees.
import { init } from 'redoc/bundles/redoc.standalone.js';

init('/docs/openapi.json', {}, document.getElementById('redoc'));
