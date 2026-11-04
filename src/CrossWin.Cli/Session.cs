using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CrossWin;

/// <summary>
/// Structured, bounded, privacy-aware diagnostics for one run session (issue #2):
///   session.json  — target identity + state (project, framework, runtime, host, git revision, pid, …)
///   events.jsonl  — one JSON event per line: seq, t, ms, level, kind, msg (+ file/line/code/stack/data)
/// Raw logs (build.log, app.log) stay next to it as evidence. Events are observed facts; hints are labelled.
/// </summary>
public sealed partial class SessionLog
{
    public const int MaxEvents = 5000, MaxMessage = 2000;
    public string Dir { get; }
    private readonly string events;
    private readonly DateTime started;
    private readonly object gate = new();
    private int wineFixmes;
    private readonly HashSet<string> seenWine = new();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public SessionLog(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        events = Path.Combine(dir, "events.jsonl");
        // ms is relative to the session start, also for events appended later by other crosswin processes.
        started = ReadInfo(dir)["startedAt"] is { } t && DateTime.TryParse(t.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime() : DateTime.UtcNow;
    }

    public static SessionLog Create(HostEnv env, string name)
    {
        string dir = Path.Combine(env.Home, "session", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name);
        var s = new SessionLog(dir);
        File.WriteAllText(env.SessionFile, dir);
        return s;
    }

    /// <summary>Never throws: diagnostics must not take down the runner (a failure is reported once on stderr).</summary>
    public void Event(string level, string kind, string msg, Action<JsonObject>? extra = null)
    {
        try { Write(level, kind, msg, extra); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            if (!reportedFailure) { reportedFailure = true; Console.Error.WriteLine($"crosswin: could not record session event ({ex.Message})"); }
        }
    }

    private bool reportedFailure;

    private void Write(string level, string kind, string msg, Action<JsonObject>? extra)
    {
        lock (gate)
        {
            // Several processes append (the runner, ui/input/snap calls): take an exclusive file lock and
            // derive seq from the existing line count so it stays monotonic across processes.
            using var fs = OpenExclusive(events);
            int seq = 0;
            using (var reader = new StreamReader(fs, leaveOpen: true)) { while (reader.ReadLine() != null) seq++; }
            if (seq >= MaxEvents) return;
            var now = DateTime.UtcNow;
            var e = new JsonObject
            {
                ["seq"] = ++seq,
                ["t"] = now.ToString("O"),
                ["ms"] = (long)(now - started).TotalMilliseconds,
                ["level"] = level,
                ["kind"] = kind,
                ["msg"] = Redact(msg.Length > MaxMessage ? msg[..MaxMessage] + "…" : msg),
            };
            extra?.Invoke(e);
            if (seq == MaxEvents) { e["level"] = "warn"; e["kind"] = "session.truncated"; e["msg"] = $"event limit {MaxEvents} reached; see raw logs"; }
            fs.Seek(0, SeekOrigin.End);
            var bytes = System.Text.Encoding.UTF8.GetBytes(e.ToJsonString(Json) + "\n");
            fs.Write(bytes);
        }
    }

    private static FileStream OpenExclusive(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { Thread.Sleep(10); }
        }
    }

    /// <summary>Records target identity; merged into session.json (later calls update fields such as state).</summary>
    public void SetInfo(Action<JsonObject> update)
    {
        lock (gate)
        {
            string path = Path.Combine(Dir, "session.json");
            var o = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            update(o);
            File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions(Json) { WriteIndented = true }));
        }
    }

    // --------------------------------------------------------------------- classification of raw lines

    // C:\x\File.cs(12,5): error CS1002: ; expected [C:\x\App.csproj]
    [GeneratedRegex(@"^\s*(?<file>.+?)\((?<line>\d+)(,(?<col>\d+))?\):\s*(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.*?)(\s+\[[^\]]+\])?$")]
    private static partial Regex MsBuildDiag();

    [GeneratedRegex(@"^\s+at (?<frame>.+?)( in (?<file>.+):line (?<line>\d+))?$")]
    private static partial Regex StackFrame();

    public void BuildLine(string line, Func<string, string> mapPath)
    {
        var m = MsBuildDiag().Match(line);
        if (!m.Success || m.Groups["sev"].Value != "error") return;
        Event("error", "build.error", m.Groups["msg"].Value, e =>
        {
            e["code"] = m.Groups["code"].Value;
            e["file"] = mapPath(m.Groups["file"].Value);
            e["line"] = int.Parse(m.Groups["line"].Value);
            if (m.Groups["col"].Success) e["col"] = int.Parse(m.Groups["col"].Value);
        });
    }

    private List<string>? exception;

    /// <summary>App stdout/stderr: .NET exceptions become one structured event; Wine noise is aggregated.</summary>
    public void AppLine(string line, Func<string, string> mapPath)
    {
        if (exception != null)
        {
            if (line.StartsWith("   at ") || line.StartsWith(" ---> ") || line.StartsWith("   --- End")) { exception.Add(line); return; }
            FlushException(mapPath);
        }
        if (line.StartsWith("Unhandled exception.")) { exception = new List<string> { line }; return; }

        // Wine debug channels: "0024:fixme:ole:..." / "0024:err:module:..."
        var w = Regex.Match(line, @"^[0-9a-f]{4}:(?<cls>fixme|err|warn|trace):(?<ch>[a-z0-9_]+):");
        if (w.Success)
        {
            if (w.Groups["cls"].Value == "fixme") { wineFixmes++; return; }
            string key = Regex.Replace(line[5..], @"0x[0-9a-f]+|\d+", "#");
            if (!seenWine.Add(key)) return; // deduplicate repeats
            Event(w.Groups["cls"].Value == "err" ? "warn" : "debug", "wine." + w.Groups["ch"].Value, line[5..]);
            return;
        }
        if (line.Trim().Length > 0) Event("info", "app.output", line);
    }

    public void FlushException(Func<string, string> mapPath)
    {
        if (exception == null) return;
        var lines = exception;
        exception = null;
        string head = lines[0]["Unhandled exception.".Length..].Trim();
        int colon = head.IndexOf(": ", StringComparison.Ordinal);
        var frames = new JsonArray();
        string? file = null; int? lineNo = null;
        foreach (string l in lines.Skip(1))
        {
            var m = StackFrame().Match(l);
            if (!m.Success) continue;
            if (frames.Count < 20) frames.Add(m.Groups["frame"].Value + (m.Groups["file"].Success ? $" ({mapPath(m.Groups["file"].Value)}:{m.Groups["line"].Value})" : ""));
            if (file == null && m.Groups["file"].Success) { file = mapPath(m.Groups["file"].Value); lineNo = int.Parse(m.Groups["line"].Value); }
        }
        // Correlation (observed timing, not proof of cause): the interaction batch that just preceded it.
        // Read before Event(), which holds the events file locked.
        int? after = LastInteraction(TimeSpan.FromSeconds(10));
        Event("error", "exception.unhandled", colon > 0 ? head[(colon + 2)..] : head, e =>
        {
            e["type"] = colon > 0 ? head[..colon] : head;
            if (file != null) { e["file"] = file; e["line"] = lineNo; }
            e["stack"] = frames;
            if (after is { } seq) e["afterInteraction"] = seq;
        });
    }

    private int? LastInteraction(TimeSpan within)
    {
        var last = ReadEvents(Dir).LastOrDefault(x => x["kind"]?.ToString() == "interaction.batch");
        if (last == null || !DateTime.TryParse(last["t"]!.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return null;
        return DateTime.UtcNow - t.ToUniversalTime() <= within ? last["seq"]!.GetValue<int>() : null;
    }

    public void Finish(int exitCode, Func<string, string> mapPath)
    {
        FlushException(mapPath);
        if (wineFixmes > 0) Event("debug", "wine.fixme", $"{wineFixmes} Wine 'fixme' lines (unimplemented API notes; usually harmless) — see app.log");
    }

    // --------------------------------------------------------------------- privacy

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)(\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s""'),;\]}]+)")]
    private static partial Regex SecretAssign();
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[a-z0-9._~+/=-]{12,}")]
    private static partial Regex AuthHeader();

    public static string Redact(string s) =>
        AuthHeader().Replace(SecretAssign().Replace(s, "$1$2<redacted>"), "$1 <redacted>");

    // --------------------------------------------------------------------- reading

    public static List<JsonObject> ReadEvents(string dir)
    {
        string path = Path.Combine(dir, "events.jsonl");
        if (!File.Exists(path)) return new();
        return File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
    }

    public static JsonObject ReadInfo(string dir)
    {
        string path = Path.Combine(dir, "session.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
    }

    public static int LevelRank(string? level) => level switch { "error" => 3, "warn" => 2, "info" => 1, _ => 0 };
}
