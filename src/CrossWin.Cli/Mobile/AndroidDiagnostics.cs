using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CrossWin.Mobile;

/// <summary>
/// The shared session/event model (see SessionLog) for Android runs. No background collector: logcat is read
/// incrementally whenever an agent asks (`crosswin events` / `session`), from the cursor stored in session.json.
/// Classifies: Android crashes (AndroidRuntime FATAL EXCEPTION), Flutter framework errors and layout overflows,
/// unhandled Dart exceptions, React Native JS errors, other E/F lines of the app (deduplicated), app prints.
/// </summary>
public static partial class AndroidDiagnostics
{
    private const int MaxPerIngest = 300;

    public static SessionLog StartSession(HostEnv env, MobileProject p, MobileDevice device, bool release)
    {
        var session = SessionLog.Create(env, p.Name);
        var git = Commands.GitIdentity(p.Root);
        session.SetInfo(o =>
        {
            o["id"] = Path.GetFileName(session.Dir);
            o["kind"] = "android";
            o["project"] = p.Name;
            o["projectKind"] = p.KindLabel;
            o["sourceRoot"] = p.Root;
            o["git"] = git;
            o["deviceProfile"] = device.Id;
            o["build"] = release ? "release" : "debug";
            o["state"] = "building";
            o["runnerPid"] = Environment.ProcessId;
            o["startedAt"] = DateTime.UtcNow.ToString("O");
        });
        session.Event("info", "session.start", $"{p.Name} ({p.KindLabel}) as {device.Name} at {git?["revision"]?.ToString() ?? "no git"}");
        return session;
    }

    /// <summary>Records which real target runs the app — emulator vs physical phone is never blurred.</summary>
    /// <summary>Device clock (epoch seconds) — take it before launching, the app logs during am start.</summary>
    public static string DeviceNow(AndroidTarget t) => t.Shell("date", "+%s.%3N").Output.Trim();

    public static void Launched(SessionLog session, AndroidTarget t, string package, MobileDevice device, string cursorBeforeLaunch)
    {
        string Prop(string name) => t.Shell("getprop", name).Output.Trim();
        string pid = t.Shell("pidof", package).Output.Trim();
        // Cursor = device clock just before the launch, so the first ingest covers startup but no old history.
        string deviceNow = cursorBeforeLaunch;
        session.SetInfo(o =>
        {
            o["state"] = "running";
            o["package"] = package;
            o["serial"] = t.Serial;
            o["deviceProfile"] = device.Id;
            o["target"] = new JsonObject
            {
                ["type"] = t.IsEmulator ? "emulator" : "physical-device",
                ["runtime"] = t.IsEmulator ? "Android emulator (Google system image; real Android, not vendor firmware)" : "physical Android device over adb",
                ["manufacturer"] = Prop("ro.product.manufacturer"),
                ["model"] = Prop("ro.product.model"),
                ["android"] = Prop("ro.build.version.release"),
                ["api"] = Prop("ro.build.version.sdk"),
                ["profile"] = $"{device.Name} {device.WidthPx}x{device.HeightPx} @{device.Density}dpi" + (t.IsEmulator ? " (wm size/density)" : ""),
            };
            o["pids"] = new JsonArray(pid.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)x).ToArray());
            if (Regex.IsMatch(deviceNow, @"^\d+\.\d+$")) o["logcatCursor"] = deviceNow;
        });
        session.Event("info", "app.launch", $"{package} on {(t.IsEmulator ? "emulator" : "phone")} {t.Serial}", e => e["pid"] = pid);
    }

    /// <summary>Appends new logcat events of the session's app since the last call. Never throws.</summary>
    public static void Ingest(HostEnv env, string dir)
    {
        var info = SessionLog.ReadInfo(dir);
        if (info["kind"]?.ToString() != "android" || info["serial"] is not { } serialNode || info["package"] is not { } pkgNode) return;
        // A live collector (attached `mobile run`) owns logcat while it runs; don't read it twice.
        if (info["liveCollector"] is { } lc && int.TryParse(lc.ToString(), out int lcPid) && lcPid != Environment.ProcessId && ProcessAlive(lcPid)) return;
        string serial = serialNode.ToString(), package = pkgNode.ToString();
        var session = new SessionLog(dir);
        try
        {
            var sdk = new AndroidSdk(env);
            if (!AndroidTarget.Connected(sdk).Any(c => c.Serial == serial))
            {
                if (info["state"]?.ToString() == "running")
                {
                    session.Event("warn", "device.disconnected", $"{serial} is no longer connected (unplugged, unauthorized or emulator stopped)");
                    session.SetInfo(o => o["state"] = "device-disconnected");
                }
                return;
            }
            var t = AndroidTarget.Acquire(sdk, serial, false, true);
            // The app may have restarted (new pid): keep every pid seen, so earlier crash lines still match.
            var pids = new HashSet<string>((info["pids"] as JsonArray ?? new JsonArray()).Select(x => x!.ToString()));
            foreach (string pid in t.Shell("pidof", package).Output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) pids.Add(pid);

            var args = new List<string> { "logcat", "-d", "-b", "main,crash", "-v", "epoch" };
            if (info["logcatCursor"]?.ToString() is { } cursor) { args.Add("-T"); args.Add(cursor); }
            var r = t.AdbCmd(args.ToArray());
            if (r.ExitCode != 0) return;
            string? last = Parse(session, r.Output.Split('\n'), pids, package, info["sourceRoot"]?.ToString() ?? "");
            session.SetInfo(o =>
            {
                o["pids"] = new JsonArray(pids.Select(x => (JsonNode?)x).ToArray());
                if (last != null) o["logcatCursor"] = AdvanceCursor(last);
                if (pids.Count > 0 && t.Shell("pidof", package).Output.Trim().Length == 0 && o["state"]?.ToString() == "running") o["state"] = "app-not-running";
            });
        }
        catch (Exception ex) when (ex is CrossWinException or IOException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"crosswin: could not read device logs ({ex.Message.Split('\n')[0]})");
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    // -T is inclusive: move 1 ms past the last line read.
    internal static string AdvanceCursor(string epoch) =>
        (decimal.Parse(epoch, CultureInfo.InvariantCulture) + 0.001m).ToString("0.000", CultureInfo.InvariantCulture);

    // 1730700000.123  1234  1250 E Tag     : message
    [GeneratedRegex(@"^\s*(?<t>\d+\.\d+)\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<lvl>[VDIWEFA])\s+(?<tag>.*?)\s*:\s?(?<msg>.*)$")]
    private static partial Regex LogLine();
    // at com.x.MainActivity.onCreate(MainActivity.kt:42)
    [GeneratedRegex(@"\((?<file>[\w$.-]+\.(kt|java)):(?<line>\d+)\)")]
    private static partial Regex JvmFrame();
    // #0      MyWidget.build (package:my_app/main.dart:42:7)
    [GeneratedRegex(@"\((?<uri>(package:[\w]+|file://)[^:)]*\.dart):(?<line>\d+)(:(?<col>\d+))?\)")]
    private static partial Regex DartFrame();

    private sealed class Block
    {
        public string Kind = "", Pid = "";
        public DateTime At;
        public List<string> Lines = new();
    }

    /// <summary>Returns the epoch of the last parsed line.</summary>
    internal static string? Parse(SessionLog session, IEnumerable<string> lines, HashSet<string> pids, string package, string root)
    {
        string? last = null;
        int emitted = 0;
        var seen = new HashSet<string>();
        Block? block = null;

        void Emit(string level, string kind, string msg, DateTime at, Action<JsonObject>? extra = null)
        {
            if (emitted++ >= MaxPerIngest) return;
            session.Event(level, kind, msg, e =>
            {
                e["t"] = at.ToString("O");
                e["ms"] = session.MsAt(at);
                e["source"] = "logcat";
                extra?.Invoke(e);
            });
        }

        void Flush()
        {
            if (block == null) return;
            var b = block; block = null;
            if (b.Kind == "android.crash")
            {
                // FATAL EXCEPTIONs of other apps are not ours.
                if (!pids.Contains(b.Pid) && !b.Lines.Any(l => l.StartsWith("Process: " + package + ","))) return;
                pids.Add(b.Pid);
                // FATAL EXCEPTION: main / Process: pkg, PID: n / java.lang.X: msg / \tat frames / Caused by: …
                string head = b.Lines.FirstOrDefault(l => Regex.IsMatch(l, @"^[\w.$]+(Exception|Error)\b")) ?? b.Lines.FirstOrDefault() ?? "crash";
                int colon = head.IndexOf(": ", StringComparison.Ordinal);
                var frames = b.Lines.Where(l => l.TrimStart().StartsWith("at ")).Select(l => l.Trim()[3..]).ToList();
                // Top frame in the app's own code: its package first, else the first non-platform frame.
                string[] platform = { "android.", "androidx.", "java.", "javax.", "kotlin.", "kotlinx.", "com.android.", "dalvik.", "libcore.", "sun." };
                string? appFrame = frames.FirstOrDefault(f => f.StartsWith(package + "."))
                    ?? frames.FirstOrDefault(f => !platform.Any(f.StartsWith) && JvmFrame().IsMatch(f));
                var loc = appFrame != null ? JvmFrame().Match(appFrame) : null;
                string? cause = b.Lines.LastOrDefault(l => l.StartsWith("Caused by: "));
                Emit("error", "exception.crash", colon > 0 ? head[(colon + 2)..] : head, b.At, e =>
                {
                    e["type"] = colon > 0 ? head[..colon] : head;
                    if (cause != null) e["cause"] = cause["Caused by: ".Length..];
                    if (loc is { Success: true }) { e["file"] = FindSource(root, loc.Groups["file"].Value); e["line"] = int.Parse(loc.Groups["line"].Value); }
                    e["stack"] = new JsonArray(frames.Take(20).Select(f => (JsonNode?)f).ToArray());
                });
                return;
            }
            // Flutter error block or unhandled Dart exception
            var (level, kind, outMsg, extra) = ClassifyFlutter(b.Lines, b.Kind, root);
            Emit(level, kind, outMsg, b.At, extra);
        }

        foreach (string raw in lines)
        {
            var m = LogLine().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            last = m.Groups["t"].Value;
            string pid = m.Groups["pid"].Value, tag = m.Groups["tag"].Value, lvl = m.Groups["lvl"].Value, msg = m.Groups["msg"].Value;
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(decimal.Parse(last, CultureInfo.InvariantCulture) * 1000)).UtcDateTime;
            bool mine = pids.Contains(pid) || (tag == "AndroidRuntime" && msg.Contains(package));

            if (block != null)
            {
                bool continues = block.Kind == "android.crash" ? tag == "AndroidRuntime" && pid == block.Pid
                    : tag == "flutter" && pids.Contains(pid) && (block.Kind == "flutter.error" ? !msg.StartsWith("════════") : msg.TrimStart().StartsWith("#") || msg.Trim().Length == 0);
                if (continues) { block.Lines.Add(msg); continue; }
                if (block.Kind == "flutter.error" && msg.StartsWith("════════")) { Flush(); continue; }
                Flush();
            }
            if (tag == "AndroidRuntime" && msg.StartsWith("FATAL EXCEPTION"))
            {
                block = new Block { Kind = "android.crash", At = at, Pid = pid };
                continue; // "Process: <package>, PID: n" in the next lines decides whether it is our app (see Flush)
            }
            if (!mine) continue;
            if (tag == "flutter" && msg.Contains("EXCEPTION CAUGHT BY")) { block = new Block { Kind = "flutter.error", At = at, Lines = { msg } }; continue; }
            if (msg.Contains("Unhandled Exception:")) { block = new Block { Kind = "flutter.unhandled", At = at, Lines = { msg } }; continue; }
            if (tag == "ReactNativeJS" && lvl is "E" or "F") { Emit("error", "js.error", msg, at); continue; }
            if (tag == "flutter" && lvl == "I") { Emit("info", "app.output", msg, at); continue; }
            if (lvl is "E" or "F" or "A")
            {
                string key = tag + ":" + Regex.Replace(msg, @"0x[0-9a-f]+|\d+", "#");
                if (seen.Add(key)) Emit("warn", "logcat.error", $"{tag}: {msg}", at, e => e["tag"] = tag);
            }
        }
        Flush();
        return last;
    }

    /// <summary>
    /// Classifies a Flutter error block (from logcat's <c>flutter:</c> dump or the Dart VM service's
    /// <c>Flutter.Error</c> rendered text) into a structured event: level, kind (flutter.error / flutter.overflow /
    /// flutter.unhandled), a short message and the extra fields (type, library, context, source file:line, widget,
    /// stack). Shared so logcat and the live VM collector emit identical events.
    /// </summary>
    internal static (string Level, string Kind, string Msg, Action<JsonObject> Extra) ClassifyFlutter(IReadOnlyList<string> lines, string blockKind, string root)
    {
        string text = string.Join('\n', lines);
        string msg = lines.Select(l => l.Trim().Trim('═', '╡', '╞', ' '))
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("EXCEPTION CAUGHT BY") && !l.StartsWith("The following") && !l.StartsWith("#"))
            ?? lines.FirstOrDefault() ?? "Flutter error";
        if (blockKind == "flutter.unhandled" && lines.Count > 0) msg = Regex.Replace(lines[0], @"^.*Unhandled Exception:\s*", "");
        var overflow = Regex.Match(text, @"A (?<w>\w+) overflowed by (?<px>[\d.]+) pixels on the (?<side>\w+)");
        var frame = DartFrame().Matches(text).FirstOrDefault(m => !m.Value.Contains("package:flutter/"));
        var library = Regex.Match(text, @"EXCEPTION CAUGHT BY (?<lib>[A-Z ]+?) ╞");
        var thrown = Regex.Match(text, @"The following (?<type>[\w<>.]+) was thrown(?<ctx>[^:]*)");
        string kind = overflow.Success ? "flutter.overflow" : blockKind;
        return (overflow.Success ? "warn" : "error", kind, overflow.Success ? overflow.Value + "." : msg.Trim(), e =>
        {
            if (thrown.Success) e["type"] = thrown.Groups["type"].Value;
            if (library.Success) e["library"] = library.Groups["lib"].Value.Trim().ToLowerInvariant();
            if (thrown.Success && thrown.Groups["ctx"].Value.Trim().Length > 0) e["context"] = thrown.Groups["ctx"].Value.Trim().TrimEnd(':');
            if (frame != null) { e["file"] = DartSource(root, frame.Groups["uri"].Value); e["line"] = int.Parse(frame.Groups["line"].Value); }
            var widget = Regex.Match(text, @"The relevant error-causing widget was:\s*\n?\s*(?<w>[\w<>]+)");
            if (widget.Success) e["widget"] = widget.Groups["w"].Value;
            e["stack"] = new JsonArray(lines.Where(l => l.TrimStart().StartsWith("#")).Take(15).Select(l => (JsonNode?)l.Trim()).ToArray());
        });
    }

    /// <summary>package:my_app/screens/home.dart → &lt;root&gt;/lib/screens/home.dart</summary>
    private static string DartSource(string root, string uri)
    {
        var m = Regex.Match(uri, @"^package:[\w]+/(?<rest>.+)$");
        if (m.Success && root.Length > 0) return Path.Combine(root, "lib", m.Groups["rest"].Value);
        return uri.StartsWith("file://") ? uri[7..] : uri;
    }

    /// <summary>JVM frames only carry a file name; find it in the user's tree (first match outside build output).</summary>
    private static string FindSource(string root, string file)
    {
        if (root.Length == 0 || !Directory.Exists(root)) return file;
        try
        {
            return Directory.EnumerateFiles(root, file, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12 })
                .FirstOrDefault(f => !f.Contains("/build/") && !f.Contains("/.gradle/") && !f.Contains("/node_modules/")) ?? file;
        }
        catch (IOException) { return file; }
    }

    /// <summary>Records a batch of mobile actions with per-action results (typed text redacted).</summary>
    public static void Interaction(string dir, IReadOnlyList<string> actions, IReadOnlyList<string> results, bool failed, long ms)
    {
        if (SessionLog.ReadInfo(dir)["kind"]?.ToString() != "android") return;
        new SessionLog(dir).Event(failed ? "warn" : "info", "interaction.batch", $"{actions.Count} action(s){(failed ? ", failed" : "")}", e =>
        {
            e["durationMs"] = ms;
            e["actions"] = new JsonArray(actions.Select(a => (JsonNode?)SessionLog.Redact(a)).ToArray());
            e["results"] = new JsonArray(results.Select(r => (JsonNode?)SessionLog.Redact(r)).ToArray());
        });
    }
}
