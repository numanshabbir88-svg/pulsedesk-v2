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

// --- login fallback ---------------------------------------------------------------------------
// When the Funnel path is wedged a fresh admin tab cannot even log in. Remember the token from
// the last REAL login, keyed by a SHA-256 of the password (never stored in plaintext or sent
// anywhere). A correct password then still works during an outage; the token remains valid in
// the server's memory because its/login session was already established upstream.
// Seeded at deploy time from the fast DIRECT origin so a fresh tab can log in even if the
// Cloudflare -> Funnel path is wedged right after a redeploy. Empty when seeding was impossible.
const SEED_HASH = "__SEED_HASH__";
const SEED_TOKEN = "__SEED_TOKEN__";
// Last-known devices snapshot from the fast origin, embedded at deploy time. Used ONLY when the
// upstream is wedged and no live cache exists, so a fresh admin tab can still see/select devices.
const SEED_DEVICES = __SEED_DEVICES__;
// NOTE: Date.now() returns 0 at module scope in Workers, so the seeded timestamp is set lazily
// inside the first request (see loginWithCache). A zero ts made the age check always fail.
let loginCache = SEED_TOKEN ? { hash: SEED_HASH, token: SEED_TOKEN, ts: 0 } : null; // { hash, token, ts }

async function sha256hex(text) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return Array.from(new Uint8Array(d)).map(b => b.toString(16).padStart(2, '0')).join('');
}

async function loginWithCache(bodyBuf, url, init) {
  let hash = null;
  try {
    const obj = JSON.parse(new TextDecoder().decode(bodyBuf || new ArrayBuffer(0)));
    hash = await sha256hex(String(obj.password || ''));
  } catch (e) {}
  // Workers report Date.now() === 0 during module init; stamp the seeded cache on first real use.
  if (loginCache && !loginCache.ts) loginCache.ts = Date.now();
  const cached = (loginCache && hash && loginCache.hash === hash) ? loginCache : null;
  if (cached && Date.now() - cached.ts < 11.5 * 3600 * 1000) {
    return new Response(JSON.stringify({ ok: true, token: cached.token }), {
      status: 200,
      headers: { 'Content-Type': 'application/json; charset=utf-8', 'X-PDS-Cache': 'login' }
    });
  }
  const res = await resilientFetch(url, init);
  if (res.ok) {
    try {
      const j = await res.clone().json();
      if (j && j.ok && j.token) loginCache = { hash, token: j.token, ts: Date.now() };
    } catch (e) {}
  }
  return res;
}

// --- stale-while-revalidate for read-only admin API -----------------------------------------
// The Cloudflare -> Funnel path sometimes wedges for minutes. A logged-in admin tab already has
// a token, so its GET calls (devices, summary, alerts, ...) can be answered from a short-lived
// edge cache while a background refresh retries upstream. This keeps the panel usable - and the
// WS (already pinned to the fast origin) can connect - during an API outage. POST commands are
// NEVER cached and always go upstream.
const apiMemCache = new Map();          // key -> { ts, status, ctype, body }
const API_FRESH_MS = 3000;              // newer than this: serve directly
const API_STALE_MS = 10 * 60 * 1000;    // older than this: drop it (avoid indefinitely wrong data)

function apiCacheKey(request) {
  const tok = request.headers.get('X-Admin-Token') || '';
  return request.url + '|' + tok.slice(0, 12);
}
function cachedResponse(e) {
  return new Response(e.body, {
    status: e.status,
    headers: { 'Content-Type': e.ctype || 'application/json; charset=utf-8', 'X-PDS-Cache': 'hit' }
  });
}
async function refreshApi(key, url, init, event) {
  const res = await resilientFetch(url, init);
  // Token rejected (e.g. server restarted): drop cached data AND the login fallback so the UI
  // does not loop forever on a token the server no longer knows.
  if (res.status === 401) { loginCache = null; apiMemCache.clear(); }
  if (res.ok) {
    const ctype = res.headers.get('content-type') || 'application/json; charset=utf-8';
    const body = await res.arrayBuffer();
    apiMemCache.set(key, { ts: Date.now(), status: res.status, ctype, body });
    return cachedResponse({ status: res.status, ctype, body });
  }
  return res;
}
async function apiGet(request, url, init, event) {
  const key = apiCacheKey(request);
  const hit = apiMemCache.get(key);
  const now = Date.now();
  if (hit && now - hit.ts <= API_STALE_MS) {
    if (now - hit.ts <= API_FRESH_MS) return cachedResponse(hit);
    // Stale but still useful: return instantly, refresh in the background.
    event.waitUntil(refreshApi(key, url, init, event).catch(() => {}));
    return cachedResponse(hit);
  }
  if (hit) apiMemCache.delete(key);
  // Cold worker + wedged Funnel path: prime the cache with the deploy-time devices snapshot and
  // return it INSTANTLY (a fresh tab must not wait 7 s to pick a device). Refresh in background;
  // the next poll sees live data. WS already goes straight to the fast origin.
  if (SEED_DEVICES && new URL(request.url).pathname.endsWith('/api/admin/devices')) {
    apiMemCache.set(key, { ts: Date.now(), status: 200, ctype: 'application/json; charset=utf-8', body: SEED_DEVICES });
    event.waitUntil(refreshApi(key, url, init, event).catch(() => {}));
    return new Response(SEED_DEVICES, {
      status: 200,
      headers: { 'Content-Type': 'application/json; charset=utf-8', 'X-PDS-Cache': 'seed' }
    });
  }
  try {
    return await refreshApi(key, url, init, event);
  } catch (e) {
    if (hit) return cachedResponse(hit);  // upstream down and cache just expired: last resort
    throw e;
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

  // Login fallback: a cached token for this exact password keeps new tabs working during an
  // upstream outage. Only reached after the server has answered a real login at least once.
  if (request.method === 'POST' && path === '/api/admin/login')
    return loginWithCache(init.body, url, init);

  // Read-only admin API: serve from short-lived edge cache, refresh in background. This is what
  // keeps the panel usable when the Funnel path wedges (POST commands still go upstream live).
  if (request.method === 'GET' && path.startsWith('/api/'))
    return apiGet(request, url, init, event);

  // Returning the response directly streams the 66 MB download (including 206/Range replies).
  if (isCacheable(request)) return cachedFetch(request, url, init, event);
  return resilientFetch(url, init);
}
