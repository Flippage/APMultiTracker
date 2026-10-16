using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace APMultiTracker;

/// <summary>The WebSocket link to the engine. Messages are raised on the UI thread.</summary>
public sealed class EngineClient
{
    private readonly Dispatcher _ui;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private int _nextReq;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string EngineVersion { get; private set; } = "";
    public string UtVersion { get; private set; } = "";
    public string ApVersion { get; private set; } = "";

    public event Action<JsonObject>? Message;
    public event Action? Disconnected;

    public EngineClient(Dispatcher ui) => _ui = ui;

    /// <summary>Connects and waits for the engine's hello. Returns false if nothing answers.</summary>
    public async Task<bool> ConnectAsync(int port, string token, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            _ws = ws;
            await SendAsync(new JsonObject { ["cmd"] = "hello", ["token"] = token });
            var hello = await ReceiveAsync(ws, timeout.Token);
            if (hello?["type"]?.GetValue<string>() != "hello") { ws.Dispose(); _ws = null; return false; }
            EngineVersion = hello["engine"]?.GetValue<string>() ?? "";
            UtVersion = hello["ut"]?.GetValue<string>() ?? "";
            ApVersion = hello["ap"]?.GetValue<string>() ?? "";
        }
        catch
        {
            ws.Dispose();
            if (ReferenceEquals(_ws, ws)) _ws = null;
            return false;
        }
        _ = Task.Run(() => ReceiveLoopAsync(ws));
        return true;
    }

    public async Task CloseAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws == null) return;
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        ws.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveAsync(ws, CancellationToken.None);
                if (msg == null) break;
                if (msg["req"] is JsonNode r && r.GetValueKind() == JsonValueKind.Number
                    && _pending.TryRemove(r.GetValue<int>(), out var tcs))
                {
                    tcs.TrySetResult(msg);
                    continue;
                }
                _ = _ui.InvokeAsync(() => Message?.Invoke(msg));
            }
        }
        catch (Exception ex) { Log.Warn("engine", "Connection dropped: " + ex.Message); }
        if (ReferenceEquals(_ws, ws)) _ws = null;
        ws.Dispose();
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        _ = _ui.InvokeAsync(() => Disconnected?.Invoke());
    }

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    public async Task SendAsync(JsonObject msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Warn("engine", "Send failed: " + ex.Message); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Sends a request and waits for the reply carrying the same "req" number.</summary>
    public async Task<JsonObject?> RequestAsync(JsonObject msg, TimeSpan timeout)
    {
        int req = Interlocked.Increment(ref _nextReq);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = tcs;
        msg["req"] = req;
        await SendAsync(msg);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        _pending.TryRemove(req, out _);
        return done == tcs.Task && tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
    }

    // ---- commands

    public Task TrackAsync(string id, Multiworld mw, SlotRef slot) => SendAsync(new JsonObject
    {
        ["cmd"] = "track", ["id"] = id, ["server"] = mw.Server.Trim(), ["slot"] = slot.Name.Trim(),
        ["password"] = string.IsNullOrEmpty(mw.Password) ? null : mw.Password, ["yaml_folder"] = mw.YamlFolder,
        ["room"] = mw.Id,
    });

    /// <summary>Sends a chat message or server command (e.g. !hint) as one of the tracked slots. Returns an error or null.</summary>
    public async System.Threading.Tasks.Task<string?> SayAsync(string id, string text)
    {
        var reply = await RequestAsync(new JsonObject { ["cmd"] = "say", ["id"] = id, ["text"] = text }, TimeSpan.FromSeconds(10));
        if (reply == null) return "No answer from the engine.";
        return reply["error"]?.GetValue<string>();
    }

    public Task ClearFeedAsync(string room) => SendAsync(new JsonObject { ["cmd"] = "clear_feed", ["room"] = room });

    public Task UntrackAsync(string id) => SendAsync(new JsonObject { ["cmd"] = "untrack", ["id"] = id });

    public Task RefreshAsync(string? id) => SendAsync(new JsonObject { ["cmd"] = "refresh", ["id"] = id });

    public Task SetIgnoredAsync(string id, long[] locations, bool ignored)
    {
        var arr = new JsonArray();
        foreach (var l in locations) arr.Add(l);
        return SendAsync(new JsonObject { ["cmd"] = "ignore", ["id"] = id, ["locations"] = arr, ["ignored"] = ignored });
    }

    /// <summary>Pretend to have these items (UT's /manually_collect); empty clears. Returns an error or null.</summary>
    public async Task<string?> SetManualAsync(string id, IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        var reply = await RequestAsync(new JsonObject { ["cmd"] = "manual", ["id"] = id, ["items"] = arr }, TimeSpan.FromSeconds(10));
        if (reply == null) return "No answer from the engine.";
        return reply["error"]?.GetValue<string>();
    }

    /// <summary>Every item name in a tracked slot's game (for suggestions).</summary>
    public async Task<IReadOnlyList<string>> ItemNamesAsync(string id)
    {
        var reply = await RequestAsync(new JsonObject { ["cmd"] = "item_names", ["id"] = id }, TimeSpan.FromSeconds(10));
        return (reply?["names"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList()
               ?? new List<string>();
    }

    public Task ShutdownAsync() => SendAsync(new JsonObject { ["cmd"] = "shutdown" });
}
