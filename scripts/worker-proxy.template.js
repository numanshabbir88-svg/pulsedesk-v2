// PulseDesk stable-URL proxy.
// The hidden upstream is the fast Tailscale Funnel route. This Worker keeps one permanent public
// hostname; keep-alive.ps1 redeploys this script with the current ORIGIN. Service-worker syntax is
// used so it can be uploaded through the plain Cloudflare API.
const ORIGIN = "__ORIGIN__";
// The admin UI is embedded at deploy time so a cold Cloudflare edge never has to fetch it from
// the hidden tunnel. This removes the 20-30 s first-load delay on a new network/Wi-Fi.
const EMBEDDED_INDEX = __EMBEDDED_INDEX__;

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

// --- resilient upstream fetch ---------------------------------------------------------------
// Cloudflare -> Tailscale Funnel sometimes reuses a stale pooled socket or returns a transient
// 502/503. A single fetch() then hangs until the client gives up, which is exactly why the
// admin UI logged in one moment and "would not connect" the next even though the origin was
// healthy (direct login ~94 ms). We retry a few times with a hard per-attempt timeout and force
// a FRESH connection after the first failure. Every HTTP call routed through here (login,
// devices, stream/control/lock commands, frame upload) is idempotent, so replaying is safe.
const RETRY_HTTP = new Set([429, 502, 503, 504]);
function retryableError(err) {
  const s = String((err && (err.message || err.name)) || err);
  return /abort|timeout|timed.?out|network|socket|keep.?alive|closed|reset|econn|fetch failed|gateway/i.test(s);
}

async function resilientFetch(url, baseInit) {
  // Fail FAST. Cloudflare's pooled socket to the Funnel can stay wedged; three long attempts
  // used to burn 24 s and then return 500. The browser now retries on 5xx/network errors with
  // its own fresh connection, so a short attempt budget here recovers far quicker.
  const attempts = 2;
  const timeoutMs = 3500;
  let last;
  for (let i = 0; i < attempts; i++) {
    const headers = new Headers(baseInit.headers);
    // Note: Workers ignore hop-by-hop Connection headers (CF manages pooling itself), so this
    // is kept only as a hint. Real recovery is the short timeout + browser-side 5xx retry.
    headers.set('Connection', 'close');
    const init = Object.assign({}, baseInit, { headers });
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const res = await fetch(url, Object.assign({ signal: ctrl.signal }, init));
      clearTimeout(timer);
      if (!RETRY_HTTP.has(res.status)) return res;   // includes 401/404 -> do not replay
      last = new Error('upstream HTTP ' + res.status);
      try { if (res.body) await res.body.cancel(); } catch (e) {} // free the socket before retry
    } catch (e) {
      clearTimeout(timer);
      last = e;
      if (!retryableError(e)) throw e;
    }
    if (i < attempts - 1) await new Promise(r => setTimeout(r, 120 * (i + 1)));
  }
  throw last;
}

async function proxy(request, event) {
  const url = originUrl(request);
  if ((request.headers.get('Upgrade') || '').toLowerCase() === 'websocket')
    return proxyWebSocket(request, url);

  // Serve the admin console straight from the Worker: no tunnel round trip, instant on any colo.
  const path = new URL(request.url).pathname;
  if ((path === '/' || path === '/index.html') && EMBEDDED_INDEX) {
    return new Response(EMBEDDED_INDEX, {
      status: 200,
      headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'public, max-age=3600' }
    });
  }

  const headers = new Headers(request.headers);
  headers.delete('host'); // fetch sets Host from the target URL

  const init = {
    method: request.method,
    headers,
    redirect: 'manual', // preserve the origin's 301/302 instead of leaking its hostname
  };
  // Buffer the body once: a ReadableStream can only be consumed by a single fetch, but we may
  // need to replay it if the first attempt hits a stale Funnel socket. These bodies are small
  // (JSON commands / screenshots); the 66 MB download is a GET with no body.
  if (request.method !== 'GET' && request.method !== 'HEAD' && request.body) {
    init.body = await request.arrayBuffer();
  }

  // Returning the response directly streams the 66 MB download (including 206/Range replies).
  if (isCacheable(request)) return cachedFetch(request, url, init, event);
  return resilientFetch(url, init);
}
