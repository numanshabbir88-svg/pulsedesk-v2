// PulseDesk Admin - minimal service worker.
// Its only job is to make the console installable on a phone ("Add to home screen") and to
// keep the shell available if the connection blips. API calls and the live WebSocket are
// never cached. All URLs are relative to this file, so the console also works when it is
// served from a sub-path (e.g. /pulsedesk/).
const SHELL = ['index.html', 'manifest.webmanifest', 'icon-192.png', 'icon-512.png'];
const CACHE = 'pulsedesk-shell-v7';

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE)
      .then(c => c.addAll(SHELL).catch(() => null))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', event => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);
  if (url.origin !== location.origin) return;
  // never cache live / private data
  if (/(^|\/)(api|ws|shots|download|mobile|join)\//.test(url.pathname)) return;

  // network first, fall back to the cached shell (never for live data)
  event.respondWith(
    fetch(req).then(res => {
      if (res && res.ok && req.mode === 'navigate') {
        const copy = res.clone();
        caches.open(CACHE).then(c => c.put('index.html', copy)).catch(() => {});
      }
      return res;
    }).catch(() => caches.match(req).then(hit => hit || caches.match('index.html')))
  );
});
