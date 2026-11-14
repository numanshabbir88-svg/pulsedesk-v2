using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>In-memory broker: agent sockets -> admin viewers.</summary>
public sealed class Live
{
    private readonly ConcurrentDictionary<string, WebSocket> _agents = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, byte>> _viewers = new();

    public void AddAgent(string id, WebSocket ws) => _agents[id] = ws;

    public void RemoveAgent(string id, WebSocket ws)
    {
        if (_agents.TryGetValue(id, out var cur) && ReferenceEquals(cur, ws)) _agents.TryRemove(id, out _);
    }

    public void AddViewer(string id, WebSocket ws) => _viewers.GetOrAdd(id, _ => new()).TryAdd(ws, 0);

    public void RemoveViewer(string id, WebSocket ws)
    {
        if (!_viewers.TryGetValue(id, out var set)) return;
        set.TryRemove(ws, out _);
        if (set.IsEmpty) _viewers.TryRemove(id, out _);
    }

    public async Task FanoutFrame(string id, byte[] buf, int count)
    {
        if (!_viewers.TryGetValue(id, out var set) || set.IsEmpty) return;
        var seg = new ArraySegment<byte>(buf, 0, count);
        foreach (var ws in set.Keys)
        {
            if (ws.State != WebSocketState.Open) { set.TryRemove(ws, out _); continue; }
            try { await ws.SendAsync(seg, WebSocketMessageType.Binary, true, CancellationToken.None); }
            catch { set.TryRemove(ws, out _); }
        }
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
