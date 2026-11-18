using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>In-memory broker: agent sockets -> admin viewers.</summary>
public sealed class Live
{
    /// <summary>
    /// One admin viewer. Frames are "latest wins": a slow connection (mobile data, long
    /// distance) drops old frames instead of queueing them, so the video stays live and
    /// control stays responsive. Text messages (lock status) are queued and never dropped.
    /// </summary>
    private sealed class Viewer
    {
        private readonly WebSocket _ws;
        private readonly object _gate = new();
        private byte[]? _frame;
        private readonly Queue<string> _texts = new();
        private bool _pumping;

        public Viewer(WebSocket ws) => _ws = ws;

        public WebSocket Socket => _ws;

        public void PushFrame(byte[] buffer, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            lock (_gate) { _frame = copy; }
            Start();
        }

        public void PushText(string text)
        {
            lock (_gate) { _texts.Enqueue(text); }
            Start();
        }

        private void Start()
        {
            lock (_gate)
            {
                if (_pumping) return;
                _pumping = true;
            }
            _ = Task.Run(Pump);
        }

        private async Task Pump()
        {
            while (true)
            {
                string? text = null;
                byte[]? frame = null;
                lock (_gate)
                {
                    if (_texts.Count > 0) text = _texts.Dequeue();
                    else if (_frame != null) { frame = _frame; _frame = null; }
                    else { _pumping = false; return; }
                }

                try
                {
                    if (text != null)
                        await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true,
                            CancellationToken.None);
                    else if (frame != null)
                        await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch
                {
                    lock (_gate) { _frame = null; _texts.Clear(); _pumping = false; }
                    try { _ws.Abort(); } catch { }
                    return;
                }
            }
        }
    }

    private readonly ConcurrentDictionary<string, WebSocket> _agents = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, Viewer>> _viewers = new();
    // A machine can briefly have two agents connected (e.g. an orphaned service-session copy
    // alongside the real desktop agent). The service one only ever captures a black desktop,
    // so it sends ~0 KB JPEGs. Remember the largest frame each socket produced and let a real
    // frame take over streaming - that way a stale copy can never show old/black pictures.
    private readonly ConcurrentDictionary<WebSocket, int> _agentFrameBytes = new();

    public void AddAgent(string id, WebSocket ws)
    {
        _agentFrameBytes[ws] = 0;
        _agents[id] = ws;   // newest connection starts active; a real frame can switch it below
    }

    public void RemoveAgent(string id, WebSocket ws)
    {
        _agentFrameBytes.TryRemove(ws, out _);
        if (!_agents.TryGetValue(id, out var cur) || !ReferenceEquals(cur, ws)) return;
        WebSocket? best = null; int bestBytes = 0;
        foreach (var pair in _agentFrameBytes)
        {
            if (pair.Key.State == WebSocketState.Open && pair.Value > bestBytes)
            { best = pair.Key; bestBytes = pair.Value; }
        }
        if (best != null) _agents[id] = best;
        else _agents.TryRemove(id, out _);
    }

    /// <summary>Called for every agent frame so the live socket follows the real desktop.</summary>
    public void ObserveAgentFrame(string id, WebSocket ws, int count)
    {
        if (count <= 1024) return;   // black / unchanged frames must not win
        _agentFrameBytes[ws] = count;
        if (!_agents.TryGetValue(id, out var cur) || ReferenceEquals(cur, ws)) return;
        _agentFrameBytes.TryGetValue(cur, out var curBytes);
        if (curBytes <= 1024) _agents[id] = ws;
    }

    public void AddViewer(string id, WebSocket ws) =>
        _viewers.GetOrAdd(id, _ => new()).TryAdd(ws, new Viewer(ws));

    public void RemoveViewer(string id, WebSocket ws)
    {
        if (!_viewers.TryGetValue(id, out var set)) return;
        set.TryRemove(ws, out _);
        if (set.IsEmpty) _viewers.TryRemove(id, out _);
    }

    public bool HasViewers(string id) =>
        _viewers.TryGetValue(id, out var set) && !set.IsEmpty;

    public void FanoutFrame(string id, byte[] buf, int count)
    {
        if (!_viewers.TryGetValue(id, out var set) || set.IsEmpty) return;
        foreach (var v in set.Values) v.PushFrame(buf, count);
    }

    /// <summary>Small text frames from the agent (lock status) forwarded to the admin viewers.</summary>
    public void FanoutText(string id, string text)
    {
        if (!_viewers.TryGetValue(id, out var set) || set.IsEmpty) return;
        foreach (var v in set.Values) v.PushText(text);
    }

    public async Task NotifyAgent(string id, string json)
    {
        if (!_agents.TryGetValue(id, out var ws) || ws.State != WebSocketState.Open) return;
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { _agents.TryRemove(id, out _); }
    }

    public Task SendCommand(string id, string command) =>
        NotifyAgent(id, JsonSerializer.Serialize(new { cmd = command }));
}
