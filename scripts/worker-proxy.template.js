// PulseDesk stable-URL proxy.
// The hidden upstream is the fast Tailscale Funnel route. This Worker keeps one permanent public
// hostname; keep-alive.ps1 redeploys this script with the current ORIGIN. Service-worker syntax is
// used so it can be uploaded through the plain Cloudflare API.
const ORIGIN = "__ORIGIN__";

addEventListener('fetch', event => {
  event.respondWith(proxy(event.request, event));
});

function originUrl(request) {
  const target = new URL(ORIGIN);
  const url = new URL(request.url);
  // ORIGIN may carry a base path (e.g. the existing Tailscale Funnel /pulsedesk route).
  // Keep it on every proxied request so the public URL stays unchanged for employees.
  const base = target.pathname.replace(/\/+$/, '');
  url.protocol = target.protocol;
  url.host = target.host;
  url.pathname = base + url.pathname;
  return url.toString();
}

async function sendTo(ws, data) {
  try {
    if (typeof data === 'string') ws.send(data);
    else if (data instanceof ArrayBuffer || ArrayBuffer.isView(data)) ws.send(data);
    else if (data && typeof data.arrayBuffer === 'function') ws.send(await data.arrayBuffer());
    else ws.send(String(data));
  } catch (e) { /* peer gone; its close handler tears down the other side */ }
}

async function proxyWebSocket(request, url) {
  // `fetch(url, {headers:{Upgrade:'websocket'}})` gets a 400 from the Cloudflare edge here;
  // the Workers WebSocket client connects to the tunnel cleanly. Bridge it to the browser
  // socket explicitly so admin clicks and agent JPEGs actually flow both ways.
  const wsUrl = url.replace(/^https:/i, 'wss:').replace(/^http:/i, 'ws:');
  const upstream = new WebSocket(wsUrl);

  const pair = new WebSocketPair();
  const client = pair[0];     // returned to the browser
  const downstream = pair[1]; // handled here
  downstream.accept({ allowHalfOpen: true });

  // The browser's onopen fires as soon as we accept the client, which is before
  // the upstream tunnel socket finishes connecting. Command frames sent in that window (the
  // initial {"cmd":"status"} probe and stream/control commands) used to be dropped, producing
  // a false "AGENT OLD" pill and lost control commands. Queue them and flush once upstream is open.
  const pending = [];
  upstream.addEventListener('open', () => {
    for (const m of pending) sendTo(upstream, m);
    pending.length = 0;
  });
  upstream.addEventListener('message', e => sendTo(downstream, e.data));
  downstream.addEventListener('message', e => {
    if (upstream.readyState === 1) sendTo(upstream, e.data);
    else pending.push(e.data);
  });
  upstream.addEventListener('close', () => { pending.length = 0; try { downstream.close(); } catch (e) {} });
  downstream.addEventListener('close', () => { try { upstream.close(); } catch (e) {} });
  upstream.addEventListener('error', () => { pending.length = 0; try { downstream.close(); } catch (e) {} });
  downstream.addEventListener('error', () => { try { upstream.close(); } catch (e) {} });

  // Returning immediately lets the handshake finish while the upstream socket connects.
  return new Response(null, { status: 101, webSocket: client });
}

// Static pages must not pay the Cloudflare -> Funnel round trip on every load. The Funnel fetch
// can take 3-26 s from the edge; caching the immutable UI/invite HTML at the Worker edge keeps
// page loads instant. API, WebSocket and the 66 MB download are never cached.
function isCacheable(request) {
  if (request.method !== 'GET' && request.method !== 'HEAD') return false;
  const p = new URL(request.url).pathname;
  if (p === '/' || p === '/index.html' || p === '/favicon.ico') return true;
  if (p.startsWith('/join/')) return true;
  if (p.startsWith('/css/') || p.startsWith('/js/') || p.startsWith('/lib/') ||
      p.startsWith('/assets/') || p.startsWith('/static/')) return true;
  return false;
}

async function cachedFetch(request, url, init, event) {
  try {
    const cache = caches.default;
    if (!cache) return fetch(url, init);
    const key = new Request(request.url, { method: 'GET' });
    const hit = await cache.match(key);
    if (hit) return hit;

    const res = await fetch(url, init);
    if (res.ok) {
      const stored = res.clone();
      event.waitUntil(cache.put(key, stored).catch(() => {}));
    }
    return res;
  } catch (e) {
    return fetch(url, init);
  }
}

async function proxy(request, event) {
  const url = originUrl(request);
  if ((request.headers.get('Upgrade') || '').toLowerCase() === 'websocket')
    return proxyWebSocket(request, url);

  const headers = new Headers(request.headers);
  headers.delete('host'); // fetch sets Host from the target URL

  const init = {
    method: request.method,
    headers,
    redirect: 'manual', // preserve the origin's 301/302 instead of leaking its hostname
  };
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    init.body = request.body;
  }

  // Returning the response directly streams the 66 MB download (including 206/Range replies).
  if (isCacheable(request)) return cachedFetch(request, url, init, event);
  return fetch(url, init);
}
