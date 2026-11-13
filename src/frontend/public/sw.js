// Served at /sw.js so its scope is the whole site. When opening a page fails for want of a
// connection, it shows /offline.html (cached on install) instead of the browser's error. Only page
// loads: API calls, streams and assets are untouched.

const OFFLINE_CACHE = 'pipelines-offline-v1';
const OFFLINE_PAGE = '/offline.html';

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(OFFLINE_CACHE)
      .then((cache) => cache.add(new Request(OFFLINE_PAGE, { cache: 'reload' })))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      for (const key of await caches.keys())
        if (key !== OFFLINE_CACHE) await caches.delete(key);
      await self.clients.claim();
    })(),
  );
});

self.addEventListener('fetch', (event) => {
  if (event.request.mode !== 'navigate') return;
  event.respondWith(
    fetch(event.request).catch(
      async () => (await caches.match(OFFLINE_PAGE)) ?? Response.error(),
    ),
  );
});
