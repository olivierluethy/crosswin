using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrossWin;

/// <summary>
/// Optional MCP server (stdio, JSON-RPC 2.0) — a thin adapter over the same commands the CLI and GUI use.
/// No state of its own: sessions live in ~/.crosswin/session, so a CLI user, a GUI and any MCP host see the
/// same session. Host config: command "/path/to/crosswin/crosswin", args ["mcp"].
/// stdout carries only protocol messages; command output is captured per call, diagnostics go to stderr.
/// </summary>
public static class Mcp
{
    private const string ProtocolVersion = "2025-06-18";

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<HostEnv, string, JsonObject, (int Code, string Text)> Call);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            var prop = new JsonObject { ["description"] = p.Description };
            if (p.Type == "string[]") { prop["type"] = "array"; prop["items"] = new JsonObject { ["type"] = "string" }; }
            else prop["type"] = p.Type;
            properties[p.Name] = prop;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray()),
        };
    }

    private static string? SessionKind(HostEnv env) =>
        File.Exists(env.SessionFile) ? SessionLog.ReadInfo(File.ReadAllText(env.SessionFile).Trim())["kind"]?.ToString() : null;

    /// <summary>The current session is an Android run (`crosswin mobile run`): route device tools there.</summary>
    private static bool Android(HostEnv env) => SessionKind(env) == "android";
    /// <summary>The current session is a web run (`crosswin web run`): route ui/input/snap/stop there.</summary>
    private static bool Web(HostEnv env) => SessionKind(env) == "web";

    private static string? Str(JsonObject a, string name) => a[name]?.GetValue<string>();
    private static bool Flag(JsonObject a, string name) => a[name]?.GetValue<bool>() ?? false;

    private static readonly Tool[] Tools =
    {
        new("crosswin_inspect",
            "Inspect a WinForms/WPF repository or .csproj before building: UI framework, target framework, risky APIs, support verdict.",
            Schema(("path", "string", "Repository directory or .csproj path", true)),
            (env, repo, a) => Capture(() => Commands.Inspect(env, Str(a, "path")!))),
        new("crosswin_run",
            "Build the app in an isolated copy and launch it under Wine (the user's source is never modified). Returns when the app is ready, the build failed or the app exited. Previous CrossWin app instances are stopped first.",
            Schema(("path", "string", "Repository directory or .csproj path", true),
                   ("project", "string", "Project name when the repository contains several apps", false),
                   ("noBuild", "boolean", "Reuse the previous build output", false)),
            (env, repo, a) => StartRun(env, Str(a, "path")!, Str(a, "project"), Flag(a, "noBuild"))),
        new("crosswin_devices",
            "List Android targets for a mobile run: connected phones and the CrossWin emulator with their readiness (ready, unauthorized, offline, missing), plus whether the mobile toolchain (adb, JDK, SDK, emulator) is installed.",
            Schema(),
            (env, repo, a) => Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "doctor" }))),
        new("crosswin_mobile_run",
            "Build a mobile app (Flutter, React Native, Expo, Kotlin) and launch it on the CrossWin emulator or an authorized phone, then stay attached in the background streaming diagnostics (logcat, and for Flutter the Dart VM service: framework errors, layout overflows and frame jank) into the same session. Returns when the app has launched or the build failed. Then use crosswin_ui / crosswin_input / crosswin_events / crosswin_snap, and crosswin_stop to close it.",
            Schema(("path", "string", "Repository or app directory", true),
                   ("device", "string", "Device profile to shape the screen to, e.g. pixel-8 (default); a real phone keeps its own screen unless set", false),
                   ("phone", "boolean", "Use the single connected physical phone instead of the emulator", false),
                   ("serial", "string", "Target a specific adb serial (several devices connected)", false),
                   ("release", "boolean", "Build a release APK instead of debug", false),
                   ("headless", "boolean", "Boot the emulator without a window", false)),
            (env, repo, a) => StartMobileRun(env, a)),
        new("crosswin_web_run",
            "Open a local web app in a real browser (Chrome DevTools Protocol) and stream its console messages, uncaught exceptions (with source file:line) and failed/4xx-5xx network requests into the same session. The target is a URL, an .html file, a static folder, or a web project directory (its dev server is started). Returns once the page is attached. Then use crosswin_ui / crosswin_input (click by label, type, expect) / crosswin_events / crosswin_snap, and crosswin_stop to close it.",
            Schema(("path", "string", "URL, .html file, static folder, or web project directory", true),
                   ("url", "string", "Attach to this URL instead of detecting/starting a dev server", false),
                   ("show", "boolean", "Open a visible browser window instead of headless", false)),
            (env, repo, a) => StartWebRun(env, a)),
        new("crosswin_session",
            "Current session: target identity (project, git revision, runtime), state, error/warning counts, most recent error with file:line.",
            Schema(),
            (env, repo, a) => Capture(() => Commands.SessionStatus(env, new[] { "--json" }))),
        new("crosswin_compare",
            "Structural diff of two UI snapshots in the shared node schema (each from crosswin_ui json=true, for any backend): an authorized reference vs the current implementation. Reports elements missing from / extra in the implementation and matched elements whose value, toggle or enabled state changed (set geometry=true to also flag bounds moves). Structural/behavioural, not pixel similarity. Each input is a file path or inline JSON.",
            Schema(("reference", "string", "Reference UI snapshot: a file path or inline {\"nodes\":[…]} JSON", true),
                   ("current", "string", "Current UI snapshot: a file path or inline JSON (capture with crosswin_ui json=true)", true),
                   ("geometry", "boolean", "Also report position/size changes beyond a few pixels", false)),
            (env, repo, a) => Capture(() =>
            {
                var diff = Compare.Diff(Compare.LoadSnapshot(Str(a, "reference")!), Compare.LoadSnapshot(Str(a, "current")!), Flag(a, "geometry"), 6);
                Console.WriteLine(diff.ToJsonString(SessionLog.Json));
                var s = diff["summary"]!.AsObject();
                return s["missing"]!.GetValue<int>() + s["extra"]!.GetValue<int>() + s["changed"]!.GetValue<int>() == 0 ? 0 : 2;
            })),
        new("crosswin_events",
            "Structured session events as JSON lines: build errors and unhandled exceptions with file/line in the user's source, app output, Wine errors, interactions. Use 'since' with the last seq you saw to get only new events.",
            Schema(("level", "string", "Minimum level: debug, info, warn, error (default info)", false),
                   ("kind", "string", "Event kind prefix, e.g. build, exception, interaction", false),
                   ("since", "integer", "Only events with seq greater than this", false),
                   ("last", "integer", "At most this many events (default 50)", false)),
            (env, repo, a) =>
            {
                var args = new List<string> { "--json" };
                foreach (string k in new[] { "level", "kind", "since", "last" })
                    if (a[k] is { } v) { args.Add("--" + k); args.Add(v.ToString()); }
                return Capture(() => Commands.Events(env, args.ToArray()));
            }),
        new("crosswin_ui",
            "Semantic UI tree of the running app (role, name, value, toggle state, bounds in client pixels) from WPF automation peers / WinForms accessibility. Prefer this over screenshots.",
            Schema(("all", "boolean", "Include unnamed and off-screen elements", false),
                   ("json", "boolean", "Raw JSON instead of the compact text tree", false)),
            (env, repo, a) =>
            {
                var flags = new[] { Flag(a, "all") ? "--all" : "", Flag(a, "json") ? "--json" : "" }.Where(s => s.Length > 0).ToArray();
                return Web(env) ? Capture(() => CrossWin.Web.WebCommands.Dispatch(env, new[] { "ui" }.Concat(flags).ToArray()))
                    : Android(env) ? Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "ui" }.Concat(flags).ToArray()))
                    : Capture(() => Commands.Ui(env, flags));
            }),
        new("crosswin_input",
            "Send a batch of real input actions, executed in order; stops at the first failure. Desktop: 'click <label>' or 'click X Y', 'type <text>', 'key Enter|Tab|Escape|Up|Down|…', 'wait <ms>', 'expect <text>' (element with that text appears within 3 s). Android sessions: 'tap <label>|X Y', 'longpress', 'swipe X1 Y1 X2 Y2', 'scroll down|up', 'type', 'key BACK|HOME|ENTER', 'wait', 'wait-for <text>' (expect is mapped to it), 'rotate'.",
            Schema(("actions", "string[]", "Actions, e.g. [\"click Save\", \"expect Saved\"]", true)),
            (env, repo, a) =>
            {
                var actions = a["actions"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                return Web(env) ? Capture(() => CrossWin.Web.WebCommands.Dispatch(env, new[] { "input" }.Concat(actions).ToArray()))
                    : Android(env) ? Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "input" }.Concat(actions.Select(x => x.StartsWith("expect ") ? "wait-for " + x[7..] : x)).ToArray()))
                    : Capture(() => Commands.Input(env, actions));
            }),
        new("crosswin_snap",
            "Screenshot the app windows to PNG files (paths returned). Use for visual questions the UI tree cannot answer.",
            Schema(),
            (env, repo, a) => Web(env) ? Capture(() => CrossWin.Web.WebCommands.Dispatch(env, new[] { "snap" }))
                : Android(env) ? Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "snap" }))
                : Capture(() => Commands.Snap(env))),
        new("crosswin_stop",
            "Stop the running app of the current session.",
            Schema(),
            (env, repo, a) => Web(env) ? Capture(() => CrossWin.Web.WebCommands.Dispatch(env, new[] { "stop" }))
                : Android(env) ? Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "stop" }))
                : Capture(() => Commands.Stop(env))),
    };

    public static int Serve(HostEnv env, string repo)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        // Anything a command prints must never reach the protocol stream.
        Console.SetOut(Console.Error);
        Console.Error.WriteLine("crosswin mcp: ready (stdio)");
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? reply;
            try { reply = Handle(env, repo, JsonNode.Parse(line)!.AsObject()); }
            catch (JsonException ex) { reply = Error(null, -32700, "Parse error: " + ex.Message); }
            if (reply != null) lock (stdout) stdout.WriteLine(reply.ToJsonString(SessionLog.Json));
        }
        return 0;
    }

    private static JsonObject? Handle(HostEnv env, string repo, JsonObject msg)
    {
        JsonNode? id = msg["id"]?.DeepClone();
        string method = msg["method"]?.GetValue<string>() ?? "";
        if (id == null) return null; // notifications (e.g. notifications/initialized) need no answer
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "crosswin", ["version"] = "0.1.0" },
                    ["instructions"] = "CrossWin runs existing WinForms/WPF desktop apps (under Wine on macOS/Linux) and mobile apps (Flutter/React Native/Kotlin on an emulator or authorized phone). Desktop loop: crosswin_run -> crosswin_ui -> crosswin_input (click by label, expect text) -> crosswin_events level=error -> edit source -> crosswin_run again. Mobile loop: crosswin_devices -> crosswin_mobile_run -> crosswin_ui -> crosswin_input (tap by label) -> crosswin_events level=warn (Flutter framework errors and layout overflows arrive live) -> crosswin_stop. Web loop: crosswin_web_run (URL/.html/dev-server) -> crosswin_ui -> crosswin_input (click by label, type, expect) -> crosswin_events level=warn (console errors, uncaught exceptions with file:line, failed requests) -> crosswin_stop. crosswin_ui/input/snap/stop auto-route to whichever session (desktop, mobile or web) is current. Prefer ui/events over screenshots.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(Tools.Select(t => (JsonNode?)new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
                    }).ToArray()),
                });
            case "tools/call":
            {
                string name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
                var tool = Tools.FirstOrDefault(t => t.Name == name);
                if (tool == null) return Error(id, -32602, $"Unknown tool {name}");
                var sw = Stopwatch.StartNew();
                int code; string text;
                try { (code, text) = tool.Call(env, repo, args); }
                catch (CrossWinException ex) { (code, text) = (1, ex.Message); }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException or IOException)
                {
                    (code, text) = (1, $"{ex.GetType().Name}: {ex.Message}");
                }
                Console.Error.WriteLine($"crosswin mcp: {name} -> {code} in {sw.ElapsedMilliseconds} ms");
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "(no output)" }),
                    ["isError"] = code != 0,
                });
            }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static readonly object ConsoleGate = new();

    /// <summary>Runs a CLI command and returns what it printed (stdout and stderr) plus its exit code.</summary>
    private static (int, string) Capture(Func<int> command)
    {
        lock (ConsoleGate)
        {
            var buffer = new StringWriter();
            TextWriter oldOut = Console.Out, oldErr = Console.Error;
            Console.SetOut(buffer);
            Console.SetError(buffer);
            try { int code = command(); return (code, buffer.ToString().TrimEnd()); }
            catch (CrossWinException ex) { return (1, (buffer + ex.Message).TrimEnd()); }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        }
    }

    /// <summary>
    /// `run` blocks until the app exits, so start it as a separate crosswin process and wait for a decisive event
    /// in the new session (ready / build failed / exited).
    /// </summary>
    private static (int, string) StartRun(HostEnv env, string path, string? project, bool noBuild)
    {
        Capture(() => Commands.Stop(env));
        string before = File.Exists(env.SessionFile) ? File.ReadAllText(env.SessionFile).Trim() : "";
        var args = new List<string> { "run", path };
        if (project != null) { args.Add("--project"); args.Add(project); }
        if (noBuild) args.Add("--no-build");
        var psi = SelfInvocation(args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var output = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        string? session = null;
        while (sw.Elapsed < TimeSpan.FromMinutes(10))
        {
            Thread.Sleep(300);
            if (session == null && File.Exists(env.SessionFile))
            {
                string now = File.ReadAllText(env.SessionFile).Trim();
                if (now != before) session = now;
            }
            if (session != null)
            {
                var kinds = SessionLog.ReadEvents(session).Select(e => e["kind"]?.ToString()).ToHashSet();
                if (kinds.Contains("app.ready") || kinds.Contains("build.failed") || kinds.Contains("app.exit")) break;
                // .NET Framework apps have no agent; "running" without app.ready is as good as it gets.
                if (SessionLog.ReadInfo(session)["state"]?.ToString() == "running" && kinds.Contains("app.launch") && sw.Elapsed > TimeSpan.FromSeconds(20)) break;
            }
            if (p.HasExited) break;
        }
        string log;
        lock (output) log = output.ToString();
        var (_, status) = Capture(() => Commands.SessionStatus(env, new[] { "--json" }));
        bool failed = p.HasExited && p.ExitCode != 0;
        string tail = string.Join('\n', log.Split('\n').TakeLast(25));
        return (failed ? 1 : 0, $"{status}\n\n-- crosswin run output (last lines) --\n{tail}");
    }

    /// <summary>
    /// `mobile run` stays attached (the live collector runs in the foreground), so start it as a separate crosswin
    /// process that keeps feeding the session, and return once the app has launched or the build failed.
    /// </summary>
    private static (int, string) StartMobileRun(HostEnv env, JsonObject a)
    {
        Capture(() => Mobile.MobileCommands.Dispatch(env, new[] { "stop" })); // clear any previous mobile session
        string before = File.Exists(env.SessionFile) ? File.ReadAllText(env.SessionFile).Trim() : "";
        var args = new List<string> { "mobile", "run", Str(a, "path")! };
        if (Str(a, "device") is { } dev) { args.Add("--device"); args.Add(dev); }
        if (Flag(a, "phone")) args.Add("--phone");
        if (Str(a, "serial") is { } ser) { args.Add("--serial"); args.Add(ser); }
        if (Flag(a, "release")) args.Add("--release");
        if (Flag(a, "headless")) args.Add("--headless");
        var psi = SelfInvocation(args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var output = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        string? session = null;
        while (sw.Elapsed < TimeSpan.FromMinutes(8))
        {
            Thread.Sleep(300);
            if (session == null && File.Exists(env.SessionFile))
            {
                string now = File.ReadAllText(env.SessionFile).Trim();
                if (now != before) session = now;
            }
            if (session != null)
            {
                var kinds = SessionLog.ReadEvents(session).Select(e => e["kind"]?.ToString()).ToHashSet();
                if (kinds.Contains("app.launch") || kinds.Contains("build.failed") || kinds.Contains("build.error")) break;
            }
            if (p.HasExited) break; // --detach was not passed, so an early exit means the build/launch failed
        }
        string log;
        lock (output) log = output.ToString();
        var (_, status) = Capture(() => Commands.SessionStatus(env, new[] { "--json" }));
        bool launched = session != null && SessionLog.ReadEvents(session).Any(e => e["kind"]?.ToString() == "app.launch");
        string tail = string.Join('\n', log.Split('\n').TakeLast(25));
        return (launched ? 0 : 1, $"{status}\n\n-- crosswin mobile run output (last lines) --\n{tail}");
    }

    /// <summary>
    /// `web run` stays attached (it streams diagnostics in the foreground), so start it as a separate crosswin
    /// process that keeps feeding the session, and return once the page has attached (app.ready) or it failed.
    /// </summary>
    private static (int, string) StartWebRun(HostEnv env, JsonObject a)
    {
        if (Web(env)) Capture(() => CrossWin.Web.WebCommands.Dispatch(env, new[] { "stop" })); // clear a previous web session
        string before = File.Exists(env.SessionFile) ? File.ReadAllText(env.SessionFile).Trim() : "";
        var args = new List<string> { "web", "run", Str(a, "path")! };
        if (Str(a, "url") is { } url) { args.Add("--url"); args.Add(url); }
        if (Flag(a, "show")) args.Add("--show");
        var psi = SelfInvocation(args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var output = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        string? session = null;
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            Thread.Sleep(200);
            if (session == null && File.Exists(env.SessionFile))
            {
                string now = File.ReadAllText(env.SessionFile).Trim();
                if (now != before) session = now;
            }
            if (session != null)
            {
                var kinds = SessionLog.ReadEvents(session).Select(e => e["kind"]?.ToString()).ToHashSet();
                if (kinds.Contains("app.ready") || kinds.Contains("app.exit")) break;
            }
            if (p.HasExited) break;
        }
        string log;
        lock (output) log = output.ToString();
        var (_, status) = Capture(() => Commands.SessionStatus(env, new[] { "--json" }));
        bool ready = session != null && SessionLog.ReadEvents(session).Any(e => e["kind"]?.ToString() == "app.ready");
        string tail = string.Join('\n', log.Split('\n').TakeLast(25));
        return (ready ? 0 : 1, $"{status}\n\n-- crosswin web run output (last lines) --\n{tail}");
    }

    private static ProcessStartInfo SelfInvocation(IEnumerable<string> args)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Started as "dotnet crosswin.dll" by the bootstrap script: pass the dll again.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            psi.ArgumentList.Add(typeof(Mcp).Assembly.Location);
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
