using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CrossWin.Web;

/// <summary>
/// Maps Chrome DevTools Protocol events of one page into the shared, bounded session/event model (kind "web"),
/// so <c>crosswin events</c> / <c>session</c> show a web app's console errors, uncaught exceptions (with a source
/// file mapped back into the project where possible) and failed/4xx-5xx network requests — the same way they show a
/// desktop app's build errors and exceptions. Secrets are redacted by SessionLog.
/// </summary>
public sealed partial class WebDiagnostics
{
    private readonly SessionLog _session;
    private readonly string? _root;
    private readonly Dictionary<string, string> _requestUrls = new(); // requestId -> url (bounded)

    public WebDiagnostics(SessionLog session, string? root) { _session = session; _root = root; }

    public static SessionLog Start(HostEnv env, WebProject p, string browser, bool headless)
    {
        var s = SessionLog.Create(env, p.Name);
        var git = p.Root != null ? Commands.GitIdentity(p.Root) : null;
        s.SetInfo(o =>
        {
            o["id"] = Path.GetFileName(s.Dir);
            o["kind"] = "web";
            o["project"] = p.Name;
            o["projectKind"] = p.Kind;
            o["url"] = p.Url;
            if (p.Root != null) o["sourceRoot"] = p.Root;
            if (git != null) o["git"] = git;
            o["runtime"] = $"{Path.GetFileName(browser)} via Chrome DevTools Protocol{(headless ? " (headless)" : " (visible)")}";
            o["state"] = "starting";
            o["runnerPid"] = Environment.ProcessId;
            o["startedAt"] = DateTime.UtcNow.ToString("O");
        });
        s.Event("info", "session.start", $"{p.Name} at {p.Url}");
        return s;
    }

    /// <summary>Records a batch of web interactions with per-action results (typed text redacted), like the other backends.</summary>
    public static void Interaction(string dir, IReadOnlyList<string> actions, IReadOnlyList<string> results, bool failed, long ms)
    {
        if (SessionLog.ReadInfo(dir)["kind"]?.ToString() != "web") return;
        new SessionLog(dir).Event(failed ? "warn" : "info", "interaction.batch", $"{actions.Count} action(s){(failed ? ", failed" : "")}", e =>
        {
            e["durationMs"] = ms;
            e["actions"] = new JsonArray(actions.Select(x => (JsonNode?)SessionLog.Redact(x)).ToArray());
            e["results"] = new JsonArray(results.Select(r => (JsonNode?)SessionLog.Redact(r)).ToArray());
        });
    }

    /// <summary>http://localhost:PORT/src/App.tsx → &lt;root&gt;/src/App.tsx when that file exists (best effort).</summary>
    private string MapUrl(string url)
    {
        if (_root == null || url.Length == 0) return url;
        if (url.StartsWith("file://")) { try { return new Uri(url).LocalPath; } catch (Exception) { return url; } }
        var m = Regex.Match(url, @"^https?://[^/]+/(?<path>[^?#]*)");
        if (!m.Success) return url;
        string rel = m.Groups["path"].Value.TrimStart('/');
        if (rel.Length == 0 || rel.StartsWith("@") || rel.StartsWith("node_modules")) return url;
        string candidate = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(candidate) ? candidate : url;
    }

    public void Handle(string method, JsonObject p)
    {
        switch (method)
        {
            case "Runtime.consoleAPICalled": OnConsole(p); break;
            case "Runtime.exceptionThrown": OnException(p); break;
            case "Log.entryAdded": OnLog(p); break;
            case "Network.requestWillBeSent": OnRequest(p); break;
            case "Network.loadingFailed": OnNetFailed(p); break;
            case "Network.responseReceived": OnResponse(p); break;
            case "Page.loadEventFired": _session.Event("info", "page.load", "page finished loading"); break;
        }
    }

    private void OnConsole(JsonObject p)
    {
        string type = p["type"]?.ToString() ?? "log";
        string level = type switch { "error" or "assert" => "error", "warning" => "warn", _ => "info" };
        string msg = ArgsText(p["args"] as JsonArray);
        _session.Event(level, "console." + type, msg.Length > 0 ? msg : $"(empty console.{type})", e => AddTop(e, p["stackTrace"] as JsonObject));
    }

    private void OnException(JsonObject p)
    {
        var d = p["exceptionDetails"] as JsonObject;
        string text = d?["exception"]?["description"]?.ToString() ?? d?["text"]?.ToString() ?? "Uncaught exception";
        string first = text.Split('\n')[0];
        _session.Event("error", "exception.unhandled", first, e =>
        {
            e["stack"] = new JsonArray(text.Split('\n').Skip(1).Take(20).Select(l => (JsonNode?)l.Trim()).ToArray());
            string? url = d?["url"]?.ToString();
            if (d?["lineNumber"] is { } ln && int.TryParse(ln.ToString(), out int line))
            {
                if (!string.IsNullOrEmpty(url)) e["file"] = MapUrl(url!);
                e["line"] = line + 1; // CDP line/column are 0-based
                if (d?["columnNumber"] is { } cn && int.TryParse(cn.ToString(), out int col)) e["col"] = col + 1;
            }
        });
    }

    private void OnLog(JsonObject p)
    {
        var entry = p["entry"] as JsonObject;
        string source = entry?["source"]?.ToString() ?? "other";
        if (source == "console-api") return; // already covered by Runtime.consoleAPICalled
        string lvl = entry?["level"]?.ToString() ?? "info";
        _session.Event(lvl == "error" ? "error" : lvl == "warning" ? "warn" : "info", "browser." + source, entry?["text"]?.ToString() ?? "",
            e => { if (entry?["url"]?.ToString() is { Length: > 0 } u) e["url"] = u; });
    }

    private void OnRequest(JsonObject p)
    {
        string id = p["requestId"]?.ToString() ?? "";
        string url = p["request"]?["url"]?.ToString() ?? "";
        if (id.Length == 0) return;
        if (_requestUrls.Count > 1000) _requestUrls.Clear();
        _requestUrls[id] = url;
    }

    private void OnNetFailed(JsonObject p)
    {
        if (p["canceled"]?.ToString() == "true") return; // navigations/aborts are not failures
        string id = p["requestId"]?.ToString() ?? "";
        string url = _requestUrls.GetValueOrDefault(id, "");
        string err = p["errorText"]?.ToString() ?? "failed";
        _session.Event("warn", "network.failed", $"{err}  {url}".Trim(), e =>
        {
            if (url.Length > 0) e["url"] = url;
            e["errorText"] = err;
            if (p["type"]?.ToString() is { } t) e["resourceType"] = t;
        });
    }

    private void OnResponse(JsonObject p)
    {
        var resp = p["response"] as JsonObject;
        if (resp?["status"] is not { } st || !int.TryParse(st.ToString(), out int status) || status < 400) return;
        string url = resp["url"]?.ToString() ?? "";
        _session.Event(status >= 500 ? "error" : "warn", "network.error", $"{status} {resp["statusText"]}  {url}".Trim(), e =>
        {
            e["status"] = status;
            if (url.Length > 0) e["url"] = url;
        });
    }

    // ------------------------------------------------------------------ helpers

    private void AddTop(JsonObject e, JsonObject? stack)
    {
        if (stack?["callFrames"] is not JsonArray frames || frames.Count == 0 || frames[0] is not JsonObject f) return;
        if (f["url"]?.ToString() is { Length: > 0 } url) e["file"] = MapUrl(url);
        if (f["lineNumber"] is { } ln && int.TryParse(ln.ToString(), out int line)) e["line"] = line + 1;
        if (f["columnNumber"] is { } cn && int.TryParse(cn.ToString(), out int col)) e["col"] = col + 1;
    }

    private static string ArgsText(JsonArray? args)
    {
        if (args == null) return "";
        var parts = args.Select(a => a as JsonObject).Where(a => a != null).Select(a =>
            a!["value"]?.ToString() ?? a["description"]?.ToString() ?? a["unserializableValue"]?.ToString()
            ?? (a["preview"]?["description"]?.ToString()) ?? a["type"]?.ToString() ?? "");
        return string.Join(' ', parts).Trim();
    }
}
