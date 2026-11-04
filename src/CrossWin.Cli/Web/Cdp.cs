using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CrossWin.Web;

/// <summary>
/// Minimal Chrome DevTools Protocol client over a websocket: sends commands and awaits their result by id, and
/// dispatches protocol events to <see cref="OnEvent"/>. Uses the "flatten" attach model, so each message may carry a
/// <c>sessionId</c> naming the page it belongs to. Same transport idea as the Dart VM-service collector — no Node or
/// Playwright dependency, just a websocket and JSON.
/// </summary>
public sealed class Cdp : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _id;

    /// <summary>method, params, sessionId (null for browser-level events).</summary>
    public event Action<string, JsonObject, string?>? OnEvent;

    public async Task ConnectAsync(string wsUrl, CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    public async Task<JsonObject> Send(string method, JsonObject? @params = null, string? sessionId = null, CancellationToken ct = default)
    {
        int id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { id = ++_id; _pending[id] = tcs; }
        var msg = new JsonObject { ["id"] = id, ["method"] = method };
        if (@params != null) msg["params"] = @params;
        if (sessionId != null) msg["sessionId"] = sessionId;
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);
        using (ct.Register(() => tcs.TrySetCanceled())) return await tcs.Task;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[65536];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                string text = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);
                JsonObject msg;
                try { if (JsonNode.Parse(text) is not JsonObject o) continue; msg = o; } catch (Exception) { continue; }

                if (msg["id"] is { } idNode && int.TryParse(idNode.ToString(), out int id))
                {
                    TaskCompletionSource<JsonObject>? tcs;
                    lock (_gate) { _pending.Remove(id, out tcs); }
                    if (tcs == null) continue;
                    if (msg["error"] is JsonObject err) tcs.TrySetException(new CrossWinException("CDP error: " + (err["message"]?.ToString() ?? err.ToJsonString())));
                    else tcs.TrySetResult(msg["result"] as JsonObject ?? new JsonObject());
                }
                else if (msg["method"]?.ToString() is { } method)
                {
                    try { OnEvent?.Invoke(method, msg["params"] as JsonObject ?? new JsonObject(), msg["sessionId"]?.ToString()); }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
        finally
        {
            lock (_gate) { foreach (var t in _pending.Values) t.TrySetCanceled(); _pending.Clear(); }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { }
        try { _ws.Abort(); _ws.Dispose(); } catch (Exception) { }
        _cts.Dispose();
    }
}
