// PulseDesk stable-URL proxy.
// The quick-tunnel origin changes whenever the home internet changes. This Worker keeps one
// permanent public hostname; keep-alive.ps1 redeploys this script with the current origin.
// Service-worker syntax is used so it can be uploaded through the plain Cloudflare API.
const ORIGIN = "__ORIGIN__";

addEventListener('fetch', event => {
  event.respondWith(proxy(event.request));
});

function originUrl(request) {
  const target = new URL(ORIGIN);
  const url = new URL(request.url);
  url.protocol = target.protocol;
  url.host = target.host;
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

  // The browser's onopen fires as soon as we accept the client (line below), which is before
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

async function proxy(request) {
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
  return fetch(url, init);
}
