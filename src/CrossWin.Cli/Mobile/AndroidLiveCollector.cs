using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CrossWin.Mobile;

/// <summary>
/// Stays attached to a running Android app (like the desktop <c>run</c> command) and streams diagnostics live into
/// the shared session/event model until the app exits, the device disconnects, or the user detaches with Ctrl-C:
///   • <b>logcat</b> is read continuously (not polled), so crashes, Kotlin/Dart/Java stack traces and app prints
///     arrive immediately and nothing is lost to the small default ring buffer (E15 ring-buffer loss).
///   • for Flutter apps it connects to the <b>Dart VM service</b> and listens on the Extension stream for
///     <c>Flutter.Error</c> events — framework errors and <c>RenderFlex</c> overflows that never reach logcat
///     (E15) — and <c>Flutter.Frame</c> timings for jank. Flutter errors are classified by the same code as
///     logcat errors (<see cref="AndroidDiagnostics.ClassifyFlutter"/>), tagged <c>source: "vm-service"</c>.
/// While attached it records its own pid as <c>liveCollector</c> in session.json so <c>events</c>/<c>session</c>
/// do not read logcat a second time and double up events.
/// </summary>
public sealed partial class AndroidLiveCollector
{
    private readonly AndroidTarget _t;
    private readonly SessionLog _session;
    private readonly string _package, _root;
    private readonly HashSet<string> _pids;
    private readonly object _gate = new();
    private readonly List<string> _buffer = new();
    private DateTime _lastAppend = DateTime.MinValue, _firstPending = DateTime.MinValue;
    private string _cursor;

    private volatile bool _vmConnected;
    private ClientWebSocket? _ws;
    private int _hostPort;
    private int _frames, _janky, _jankEmitted;
    private double _maxFrameMs;

    public AndroidLiveCollector(AndroidTarget t, SessionLog session, string package, string root, IEnumerable<string> pids, string cursor)
    {
        _t = t; _session = session; _package = package; _root = root; _pids = new(pids); _cursor = cursor;
    }

    // "The Dart VM service is listening on http://127.0.0.1:12345/abcDEF=/"  (older builds say "Observatory")
    [GeneratedRegex(@"(?:Dart VM service|Observatory) is listening on https?://(?<host>[\d.]+):(?<port>\d+)/(?<token>[^/\s]*)")]
    private static partial Regex VmServiceUrl();

    /// <summary>Blocks until the app exits, the device disconnects, or <paramref name="ct"/> is cancelled (Ctrl-C).</summary>
    public void Run(CancellationToken ct)
    {
        _session.SetInfo(o => o["liveCollector"] = Environment.ProcessId);
        // Grow the logcat ring buffer so a burst (e.g. a full crash dump) is not dropped before we read it.
        try { _t.AdbCmd("logcat", "-G", "2M"); } catch (Exception) { /* best effort */ }

        var logcat = _t.StreamAdb("logcat", "-b", "main,crash", "-v", "epoch", "-T", _cursor);
        logcat.OutputDataReceived += (_, e) => OnLine(e.Data);
        logcat.BeginOutputReadLine();
        logcat.BeginErrorReadLine();

        string reason = "detached";
        try
        {
            int emptyPolls = 0;
            var pidCheck = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                if (logcat.HasExited) { reason = "device-disconnected"; break; }
                MaybeFlush();
                if (pidCheck.ElapsedMilliseconds >= 2000)
                {
                    pidCheck.Restart();
                    string alive = Pidof();
                    if (alive.Length == 0) { if (++emptyPolls >= 2) { reason = "exited"; break; } }
                    else { emptyPolls = 0; foreach (string pid in alive.Split(' ', StringSplitOptions.RemoveEmptyEntries)) _pids.Add(pid); }
                }
                Thread.Sleep(250);
            }
        }
        finally
        {
            try { if (!logcat.HasExited) logcat.Kill(entireProcessTree: true); } catch (Exception) { }
            FlushNow();
            CloseVm();
            EmitSummary();
            Finish(reason);
        }
    }

    private string Pidof()
    {
        try { return _t.Shell("pidof", _package).Output.Trim(); } catch (Exception) { return ""; }
    }

    // ------------------------------------------------------------------ logcat

    private void OnLine(string? line)
    {
        if (line == null) return;
        var now = DateTime.UtcNow;
        lock (_gate) { if (_buffer.Count == 0) _firstPending = now; _buffer.Add(line); _lastAppend = now; }
        if (!_vmConnected && VmServiceUrl().Match(line) is { Success: true } m)
            ConnectVm(m.Groups["port"].Value, m.Groups["token"].Value);
    }

    // Flush once the stream has been quiet briefly (so a multi-line crash/error block is complete), but don't let a
    // continuously chatty app starve the flush or grow the buffer without bound: also flush on age or size.
    private void MaybeFlush()
    {
        bool ready;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            ready = _buffer.Count > 0 && ((now - _lastAppend).TotalMilliseconds >= 400
                || (now - _firstPending).TotalMilliseconds >= 2000 || _buffer.Count >= 200);
        }
        if (ready) FlushNow();
    }

    private void FlushNow()
    {
        List<string> batch;
        lock (_gate) { if (_buffer.Count == 0) return; batch = new(_buffer); _buffer.Clear(); }
        try
        {
            string? last = AndroidDiagnostics.Parse(_session, batch, _pids, _package, _root);
            if (last != null)
            {
                _cursor = AndroidDiagnostics.AdvanceCursor(last);
                var pids = _pids.ToArray();
                _session.SetInfo(o =>
                {
                    o["logcatCursor"] = _cursor;
                    o["pids"] = new JsonArray(pids.Select(x => (JsonNode?)x).ToArray());
                });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException) { }
    }

    // ------------------------------------------------------------------ Dart VM service (Flutter)

    private void ConnectVm(string devicePort, string token)
    {
        lock (_gate) { if (_vmConnected) return; _vmConnected = true; }
        _ = Task.Run(async () =>
        {
            try
            {
                // Map a free host port to the device's loopback VM-service port, then open the JSON-RPC websocket.
                string hp = _t.AdbCmd("forward", "tcp:0", "tcp:" + devicePort).Output.Trim();
                if (!int.TryParse(hp, out _hostPort)) return;
                var ws = new ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_hostPort}/{token}/ws"), CancellationToken.None);
                await SendRpc(ws, "1", "Extension");
                _session.Event("info", "vm.connected", "Dart VM service attached: Flutter framework errors and frame timings", e => e["source"] = "vm-service");
                await Receive(ws);
            }
            catch (Exception ex)
            {
                _session.Event("debug", "vm.error", "could not attach to the Dart VM service (" + ex.Message.Split('\n')[0] + ")");
            }
        });
    }

    private static async Task SendRpc(ClientWebSocket ws, string id, string streamId)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "streamListen", ["params"] = new JsonObject { ["streamId"] = streamId } };
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task Receive(ClientWebSocket ws)
    {
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) break;
            ms.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            string text = Encoding.UTF8.GetString(ms.ToArray());
            ms.SetLength(0);
            try { if (JsonNode.Parse(text) is JsonObject m) Handle(m); } catch (Exception) { }
        }
    }

    private void Handle(JsonObject m)
    {
        if (m["method"]?.ToString() != "streamNotify" || m["params"]?["event"] is not JsonObject ev) return;
        string? ekind = ev["extensionKind"]?.ToString();
        if (ekind == "Flutter.Error" && ev["extensionData"] is JsonObject err) OnFlutterError(err);
        else if (ekind == "Flutter.Frame" && ev["extensionData"] is JsonObject frame) OnFrame(frame);
    }

    private void OnFlutterError(JsonObject data)
    {
        var lines = DiagText(data);
        if (lines.Count == 0) return;
        var at = DateTime.UtcNow;
        var (level, kind, msg, extra) = AndroidDiagnostics.ClassifyFlutter(lines, "flutter.error", _root);
        _session.Event(level, kind, msg, e =>
        {
            e["t"] = at.ToString("O");
            e["ms"] = _session.MsAt(at);
            e["source"] = "vm-service";
            extra(e);
        });
    }

    // Prefer the fully rendered error text (first error only, per E15); otherwise flatten description + properties.
    private static List<string> DiagText(JsonObject data)
    {
        if (data["renderedErrorText"]?.ToString() is { Length: > 0 } rendered)
            return rendered.Replace("\r", "").Split('\n').ToList();
        var lines = new List<string>();
        if (data["description"]?.ToString() is { Length: > 0 } d) lines.Add(d);
        void Walk(JsonObject n)
        {
            if (n["properties"] is JsonArray props)
                foreach (var p in props)
                    if (p is JsonObject po)
                    {
                        if (po["description"]?.ToString() is { Length: > 0 } pd) lines.Add(pd);
                        Walk(po);
                    }
        }
        Walk(data);
        if (data["stackTrace"]?.ToString() is { Length: > 0 } st)
            lines.AddRange(st.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(15));
        return lines;
    }

    private void OnFrame(JsonObject data)
    {
        double Num(string k) => data[k] is { } v && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double x) ? x : 0;
        double us = Num("elapsed");
        if (us <= 0) return;
        _frames++;
        double ms = us / 1000.0;
        if (ms > _maxFrameMs) _maxFrameMs = ms;
        if (ms > 100 && _jankEmitted < 25)
        {
            _janky++;
            _jankEmitted++;
            double build = Num("build") / 1000.0, raster = Num("raster") / 1000.0;
            var at = DateTime.UtcNow;
            _session.Event("warn", "perf.jank", $"slow frame {ms:0} ms (build {build:0} ms, raster {raster:0} ms)", e =>
            {
                e["t"] = at.ToString("O");
                e["ms"] = _session.MsAt(at);
                e["source"] = "vm-service";
                e["frameMs"] = Math.Round(ms, 1);
            });
        }
        else if (ms > 100) _janky++;
    }

    private void EmitSummary()
    {
        if (_frames == 0) return;
        _session.Event("info", "perf.frames", $"{_frames} frames rendered, {_janky} janky (>100 ms), slowest {_maxFrameMs:0} ms", e =>
        {
            e["source"] = "vm-service";
            e["frames"] = _frames;
            e["janky"] = _janky;
            e["slowestMs"] = Math.Round(_maxFrameMs, 1);
        });
    }

    private void CloseVm()
    {
        try { _ws?.Abort(); _ws?.Dispose(); } catch (Exception) { }
        if (_hostPort > 0) { try { _t.AdbCmd("forward", "--remove", "tcp:" + _hostPort); } catch (Exception) { } }
    }

    // ------------------------------------------------------------------ shutdown

    private void Finish(string reason)
    {
        _session.SetInfo(o =>
        {
            o.Remove("liveCollector");
            // Don't clobber a terminal state another command (e.g. `mobile stop`) already set.
            if (o["state"]?.ToString() is not ("running" or null)) return;
            if (reason == "exited") { o["state"] = "app-not-running"; o["endedAt"] = DateTime.UtcNow.ToString("O"); }
            else if (reason == "device-disconnected") o["state"] = "device-disconnected";
        });
        if (reason == "exited")
        {
            _session.Event("info", "app.exit", "the app process ended");
            Console.WriteLine("App closed.");
        }
        else if (reason == "device-disconnected")
        {
            _session.Event("warn", "device.disconnected", $"{_t.Serial} is no longer connected");
            Console.WriteLine("Device disconnected.");
        }
        else Console.WriteLine("Detached. The app is still running — crosswin mobile stop closes it.");
    }
}
