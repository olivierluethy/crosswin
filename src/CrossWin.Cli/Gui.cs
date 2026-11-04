using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossWin.Mobile;

namespace CrossWin;

/// <summary>
/// Local, loopback-only control surface for non-developers (#4, #5, #6). Serves a single-page app and a
/// small JSON API over the same engine the CLI uses: check a project (ProjectInspector + Assessment),
/// report host/setup readiness honestly, run an app (spawns the existing `crosswin run`) and read back the
/// shared session/diagnostics. No AI agent, no terminal knowledge required to use it.
/// Opening the GUI never restarts a running session — it only reads the session files the runner writes.
/// </summary>
public static class Gui
{
    public static int Serve(HostEnv env, string repo, string[] args)
    {
        int wanted = 0;
        bool open = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": wanted = int.Parse(args[++i]); break;
                case "--no-open": open = false; break;
                default: throw new CrossWinException($"Unknown option {args[i]} for 'gui'.");
            }
        }

        var (listener, port) = StartListener(wanted);
        string url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"CrossWin is running at {url}");
        Console.WriteLine("Open that address in your browser. Press Ctrl-C here to stop.");
        if (open) TryOpenBrowser(url);

        using var stopping = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Set(); };

        // One request at a time is plenty for a single local user; keep the model simple and predictable.
        _ = Task.Run(() =>
        {
            while (!stopping.IsSet)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; } // listener stopped
                try { Handle(env, repo, ctx); }
                catch (Exception ex) { TrySend(ctx, 500, Json(new JsonObject { ["error"] = ex.Message })); }
            }
        });

        stopping.Wait();
        Console.WriteLine("\nStopped the CrossWin control surface. Any app you started keeps running (use 'crosswin stop' to close it).");
        try { listener.Stop(); } catch { }
        return 0;
    }

    // ------------------------------------------------------------------- HTTP

    private static void Handle(HostEnv env, string repo, HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        string method = ctx.Request.HttpMethod;

        if (method == "GET" && path is "/" or "/index.html") { SendIndex(ctx); return; }
        if (path == "/api/host" && method == "GET") { Send(ctx, 200, Json(HostStatus(env))); return; }
        if (path == "/api/session" && method == "GET") { Send(ctx, 200, Json(SessionStatus(env))); return; }
        if (path == "/api/events" && method == "GET") { Send(ctx, 200, Json(EventsStatus(env, ctx.Request))); return; }
        if (path == "/api/recent" && method == "GET") { Send(ctx, 200, Json(new JsonObject { ["recent"] = Recent(env) })); return; }
        if (path == "/api/devices" && method == "GET") { Send(ctx, 200, Json(Devices(env))); return; }
        if (path == "/api/check" && method == "POST") { Send(ctx, 200, Json(Check(env, Body(ctx)))); return; }
        if (path == "/api/run" && method == "POST") { Send(ctx, 200, Json(RunApp(env, repo, Body(ctx)))); return; }
        if (path == "/api/snap" && method == "POST") { Send(ctx, 200, Json(Snap(env))); return; }
        if (path == "/api/ui" && method == "GET") { Send(ctx, 200, Json(Ui(env))); return; }
        if (path == "/api/stop" && method == "POST") { Send(ctx, 200, Json(StopApp(env))); return; }

        Send(ctx, 404, Json(new JsonObject { ["error"] = "Not found." }));
    }

    private static JsonObject Body(HttpListenerContext ctx)
    {
        using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        string s = r.ReadToEnd();
        if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
        return JsonNode.Parse(s)?.AsObject() ?? new JsonObject();
    }

    // ------------------------------------------------------------------- API

    private static JsonObject HostStatus(HostEnv env)
    {
        bool wine = env.Wine != null, agent = File.Exists(env.AgentDll);
        bool ready = wine && env.PrefixReady && agent;
        return new JsonObject
        {
            ["os"] = env.IsMac ? "macOS" : env.IsLinux ? "Linux" : "this system",
            ["osDetail"] = env.OsDescription,
            ["arch"] = env.Arch.ToString(),
            ["appleSilicon"] = env.IsMac && env.Arch == System.Runtime.InteropServices.Architecture.Arm64,
            ["wine"] = wine,
            ["wineVersion"] = wine ? env.WineVersion : null,
            ["prefix"] = env.PrefixReady,
            ["agent"] = agent,
            ["ready"] = ready,
            ["setupCommand"] = "./crosswin setup",
        };
    }

    private static JsonObject Check(HostEnv env, JsonObject body)
    {
        string raw = (body["path"]?.ToString() ?? "").Trim();
        if (raw.Length == 0) return new JsonObject { ["error"] = "Type the folder (or web address) of the app you want to run." };

        // A web address needs no filesystem; everything else is a path on disk.
        bool isUrl = System.Text.RegularExpressions.Regex.IsMatch(raw, @"^https?://");
        string path = isUrl ? raw : ExpandHome(raw);
        if (!isUrl && !File.Exists(path) && !Directory.Exists(path))
            return new JsonObject { ["error"] = $"There's nothing at \"{path}\". Check the folder path and try again." };

        // 1) Windows desktop apps (the core mission) win when present.
        if (!isUrl)
        {
            var files = ProjectInspector.FindProjects(path);
            var apps = files.Select(ProjectInspector.Inspect).Where(p => p.IsApp && p.Ui != UiFramework.None).ToList();
            if (files.Count > 0) return DesktopResult(path, files, apps);
        }

        // 2) Mobile (Flutter / React Native / Android) — pure detection, nothing is built or launched.
        //    Only when THIS folder holds a project marker, so detection never escalates to an unrelated git
        //    root (which would e.g. find a sibling Android module for a plain static web folder).
        string[] mobileMarkers = { "pubspec.yaml", "package.json", "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts" };
        if (!isUrl && Directory.Exists(path) && mobileMarkers.Any(m => File.Exists(Path.Combine(path, m))))
        {
            try { return MobileResult(MobileProject.Detect(path), path); }
            catch (CrossWinException) { /* not a mobile project; try web */ }
        }

        // 3) Web (a URL, an .html file, a static folder, or a dev-server project).
        try { return WebResult(env, Web.WebProject.Resolve(path, null, launchServer: false), path); }
        catch (CrossWinException)
        {
            return new JsonObject { ["error"] = "CrossWin didn't recognise an app here. Point it at a folder with a Windows app (.csproj), a Flutter/React Native app, or a website (a folder, an .html file, or a web address)." };
        }
    }

    private static JsonObject DesktopResult(string path, IReadOnlyList<string> files, List<ProjectInfo> apps)
    {
        string baseDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var projects = new JsonArray();
        foreach (string file in files)
        {
            var info = ProjectInspector.Inspect(file);
            var a = Assessment.For(info);
            bool runnable = info.IsApp && info.Ui != UiFramework.None && a.Level != SupportLevel.Unsupported;
            var spec = new JsonArray();
            if (info.IsApp && info.Ui != UiFramework.None)
            {
                spec.Add(SpecItem("Kind", UiName(info.Ui)));
                spec.Add(SpecItem("Needs", FriendlyRuntime(info)));
                spec.Add(SpecItem("Build", info.Platform == "AnyCPU" ? "Any (64-bit)" : info.Platform));
            }
            projects.Add(new JsonObject
            {
                ["type"] = "desktop",
                ["runKind"] = "desktop",
                ["name"] = info.Name,
                ["rel"] = Path.GetRelativePath(baseDir, file),
                ["isApp"] = info.IsApp,
                ["spec"] = spec,
                ["level"] = a.Level.ToString(),
                ["verdict"] = VerdictWord(a.Level, info),
                ["summary"] = FriendlySummary(a, info),
                ["risks"] = new JsonArray(info.Findings.Select(f => (JsonNode?)FriendlyRisk(f)).ToArray()),
                ["runnable"] = runnable,
            });
        }
        return new JsonObject { ["path"] = path, ["type"] = "desktop", ["projects"] = projects, ["appCount"] = apps.Count, ["defaultApp"] = apps.Count == 1 ? apps[0].Name : null };
    }

    private static JsonObject MobileResult(MobileProject m, string path)
    {
        bool ios = m.Kind == MobileKind.IosNative;
        bool runnable = !ios && m.HasAndroid;
        var spec = new JsonArray { SpecItem("Kind", m.KindLabel) };
        if (m.HasAndroid || !ios) spec.Add(SpecItem("Runs on", "an Android phone or emulator"));
        string verdict = ios ? "Needs a Mac" : runnable ? "Needs a device" : "Not ready";
        string summary = ios
            ? "This is an iOS app. Apple's tools are needed to build and sign it, so CrossWin can't run it here."
            : runnable
                ? $"This is a {m.KindLabel} app. CrossWin can build and run it — connect an Android phone (and allow it on the phone) or start an emulator, then run it."
                : $"This is a {m.KindLabel} app but it has no Android target yet. Add one (the notes below say how), then it can run.";
        var project = new JsonObject
        {
            ["type"] = "mobile",
            ["runKind"] = "mobile",
            ["name"] = m.Name,
            ["isApp"] = true,
            ["spec"] = spec,
            ["level"] = runnable ? "Experimental" : "Unsupported",
            ["verdict"] = verdict,
            ["summary"] = summary,
            ["risks"] = new JsonArray(m.Notes.Select(n => (JsonNode?)n).ToArray()),
            ["runnable"] = runnable,
        };
        return new JsonObject { ["path"] = path, ["type"] = "mobile", ["projects"] = new JsonArray { project }, ["appCount"] = runnable ? 1 : 0, ["defaultApp"] = m.Name };
    }

    private static JsonObject WebResult(HostEnv env, Web.WebProject w, string path)
    {
        bool needsNode = w.Kind == "dev-server";
        bool nodeReady = HostEnv.FindOnPath("node") != null;
        var spec = new JsonArray { SpecItem("Kind", WebKindLabel(w.Kind)) };
        spec.Add(SpecItem("Opens", w.Kind == "url" ? w.Url : w.Kind == "dev-server" ? "its own dev server" : (w.Name + (w.Kind == "file" ? ".html" : "/"))));
        bool runnable = !needsNode || nodeReady;
        string verdict = runnable ? "Ready to run" : "Needs Node.js";
        string summary = w.Kind switch
        {
            "url" => "This is a web address. CrossWin opens it in a browser and watches for errors, failed requests and console messages.",
            "file" or "static" => "This is a local web page. CrossWin opens it in a browser (no server needed) and watches for errors and failed requests.",
            _ when nodeReady => "This is a web app with its own dev server. CrossWin starts the server and opens it in a browser, watching for errors, failed requests and console messages.",
            _ => "This is a web app that needs Node.js to start its dev server. Install Node.js, or start the server yourself and give CrossWin the address.",
        };
        var risks = new JsonArray();
        if (needsNode && !nodeReady) risks.Add("Node.js isn't installed, so CrossWin can't start this app's dev server here.");
        var project = new JsonObject
        {
            ["type"] = "web",
            ["runKind"] = "web",
            ["name"] = w.Name,
            ["isApp"] = true,
            ["spec"] = spec,
            ["level"] = runnable ? "Supported" : "Experimental",
            ["verdict"] = verdict,
            ["summary"] = summary,
            ["risks"] = risks,
            ["runnable"] = runnable,
        };
        return new JsonObject { ["path"] = isUrlOrFile(path, w), ["type"] = "web", ["projects"] = new JsonArray { project }, ["appCount"] = runnable ? 1 : 0, ["defaultApp"] = w.Name };
    }

    private static string isUrlOrFile(string path, Web.WebProject w) => w.Kind == "url" ? w.Url : path;

    private static JsonObject SpecItem(string k, string v) => new() { ["k"] = k, ["v"] = v };

    private static string WebKindLabel(string kind) => kind switch
    {
        "url" => "Website", "file" => "Local web page", "static" => "Local website", "dev-server" => "Web app (dev server)", _ => "Website",
    };

    private static JsonObject RunApp(HostEnv env, string repo, JsonObject body)
    {
        string raw = (body["path"]?.ToString() ?? "").Trim();
        string? project = body["project"]?.ToString();
        string runKind = body["runKind"]?.ToString() ?? "desktop";
        bool isUrl = System.Text.RegularExpressions.Regex.IsMatch(raw, @"^https?://");
        string path = isUrl ? raw : ExpandHome(raw);
        if (path.Length == 0 || (!isUrl && !File.Exists(path) && !Directory.Exists(path)))
            return new JsonObject { ["error"] = "Pick an app to run first." };

        // Only the Windows desktop backend needs Wine + the agent; web/mobile have their own prerequisites.
        if (runKind == "desktop" && (env.Wine == null || !env.PrefixReady || !File.Exists(env.AgentDll)))
            return new JsonObject { ["error"] = "CrossWin isn't set up yet.", ["needsSetup"] = true, ["setupCommand"] = "./crosswin setup" };

        string wrapper = Path.Combine(repo, "crosswin");
        if (!File.Exists(wrapper))
            return new JsonObject { ["error"] = "Could not find the crosswin launcher to start the app." };

        Remember(env, path, project);

        // Spawn the existing attached runner for this backend in the background; it writes the shared session
        // files the GUI then polls. Redirect to a log via a shell so no pipe needs draining and it outlives us.
        var cmd = new StringBuilder("exec ").Append(Sh(wrapper));
        switch (runKind)
        {
            case "web":
                cmd.Append(" web run ").Append(Sh(path));
                if (string.Equals(body["show"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)) cmd.Append(" --show");
                break;
            case "mobile":
                cmd.Append(" mobile run ").Append(Sh(path));
                string? device = body["device"]?.ToString();
                if (device == "phone") cmd.Append(" --phone");
                else if (!string.IsNullOrEmpty(device) && device != "emulator") cmd.Append(" --serial ").Append(Sh(device));
                break;
            default:
                cmd.Append(" run ").Append(Sh(path));
                if (!string.IsNullOrEmpty(project)) cmd.Append(" --project ").Append(Sh(project));
                break;
        }
        string log = Path.Combine(env.Home, "gui-run.log");
        cmd.Append(" >> ").Append(Sh(log)).Append(" 2>&1");

        var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(cmd.ToString());
        try { Process.Start(psi); }
        catch (Exception ex) { return new JsonObject { ["error"] = $"Could not start the app: {ex.Message}" }; }

        return new JsonObject { ["started"] = true, ["logPath"] = log };
    }

    private static JsonObject Snap(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) return new JsonObject { ["error"] = "No app is running yet." };
        string kind = SessionKind(env);
        var images = new JsonArray();

        if (kind is "web" or "android")
        {
            // web/mobile write a single PNG; ask for a known path and read it back.
            string outDir = Path.Combine(env.Home, "gui");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, $"preview-{kind}.png");
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            var (ex, so, se) = kind == "web"
                ? RunCli(env, "web", "snap", "--out", outFile)
                : RunCli(env, "mobile", "snap", "--out", outFile);
            if (File.Exists(outFile))
            {
                try { images.Add(ImageNode(File.ReadAllBytes(outFile), kind == "web" ? "Page" : "Screen", "")); } catch { }
            }
            if (images.Count > 0) return new JsonObject { ["images"] = images };
            return new JsonObject { ["error"] = CleanCli((so + "\n" + se).Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "No preview available yet.") };
        }

        // Desktop: snap prints one tab-separated line per window, PNG path first.
        var (exit, stdout, stderr) = RunCli(env, "snap");
        foreach (string line in stdout.Split('\n'))
        {
            string[] f = line.Trim().Split('\t');
            if (f.Length < 3 || f[0].StartsWith("ERROR") || !f[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f[0]) || images.Count >= 4) continue;
            try { images.Add(ImageNode(File.ReadAllBytes(f[0]), f.Length > 2 ? f[2] : "", f.Length > 1 ? f[1] : "")); } catch { }
        }
        if (images.Count > 0) return new JsonObject { ["images"] = images };
        // Snap can legitimately be unavailable (GPU WPF, .NET Framework/Mono apps, app not ready yet).
        string why = (stdout + "\n" + stderr).Split('\n').FirstOrDefault(l => l.Contains("ERROR") || l.Contains("not available") || l.Contains("No app")) ?? "";
        return new JsonObject { ["error"] = why.Length > 0 ? CleanCli(why) : "No preview available yet — the app may still be starting, or this app can't be captured." };
    }

    private static JsonObject ImageNode(byte[] png, string title, string size) => new()
    {
        ["title"] = title,
        ["size"] = size,
        ["dataUri"] = "data:image/png;base64," + Convert.ToBase64String(png),
    };

    private static string SessionKind(HostEnv env)
    {
        try
        {
            string dir = File.ReadAllText(env.SessionFile).Trim();
            return SessionLog.ReadInfo(dir)["kind"]?.ToString() ?? "desktop";
        }
        catch { return "desktop"; }
    }

    private static JsonObject Ui(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) return new JsonObject { ["error"] = "No app is running yet." };
        string kind = SessionKind(env);
        var (exit, stdout, stderr) = kind switch
        {
            "web" => RunCli(env, "web", "ui", "--json"),
            "android" => RunCli(env, "mobile", "ui", "--json"),
            _ => RunCli(env, "ui", "--json"),
        };
        string body = stdout.Trim();
        if (exit != 0 || body.Length == 0 || body.StartsWith("ERROR"))
            return new JsonObject { ["error"] = CleanCli((body.Length > 0 ? body : stderr).Split('\n').FirstOrDefault() ?? "Couldn't read the app's controls.") };
        try { return JsonNode.Parse(body)!.AsObject(); }
        catch { return new JsonObject { ["error"] = "Couldn't read the app's controls." }; }
    }

    private static JsonObject Devices(HostEnv env)
    {
        var sdk = new AndroidSdk(env);
        bool adb = sdk.Adb != null;
        var arr = new JsonArray();
        if (adb)
        {
            foreach (var d in AndroidTarget.All(sdk))
            {
                bool phone = !d.Serial.StartsWith("emulator-");
                bool ready = d.State == "device";
                arr.Add(new JsonObject
                {
                    ["serial"] = d.Serial,
                    ["kind"] = phone ? "phone" : "emulator",
                    ["model"] = d.Model == "?" ? (phone ? "Android phone" : "emulator") : d.Model,
                    ["state"] = d.State,
                    ["ready"] = ready,
                    ["help"] = ready ? null : AndroidTarget.StateHelp(d.State),
                });
            }
        }
        return new JsonObject
        {
            ["adb"] = adb,
            ["emulatorAvailable"] = sdk.Emulator != null && sdk.SystemImageInstalled,
            ["setupHint"] = adb ? null : "Run  ./crosswin mobile setup  once to install the Android tools (adb and an emulator).",
            ["devices"] = arr,
        };
    }

    private static JsonObject StopApp(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) return new JsonObject { ["stopped"] = true };
        try { Commands.Stop(env); } catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
        return new JsonObject { ["stopped"] = true };
    }

    private static JsonObject SessionStatus(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) return new JsonObject { ["none"] = true };
        string dir = File.ReadAllText(env.SessionFile).Trim();
        if (!Directory.Exists(dir)) return new JsonObject { ["none"] = true };
        var info = SessionLog.ReadInfo(dir);
        var events = SessionLog.ReadEvents(dir);
        int errors = events.Count(e => e["level"]?.ToString() == "error");
        int warns = events.Count(e => e["level"]?.ToString() == "warn");
        return new JsonObject
        {
            ["id"] = info["id"]?.DeepClone() ?? Path.GetFileName(dir),
            ["project"] = info["project"]?.DeepClone(),
            ["kind"] = info["kind"]?.DeepClone(),
            ["state"] = info["state"]?.DeepClone(),
            ["ui"] = info["ui"]?.DeepClone(),
            ["runtime"] = info["runtime"]?.DeepClone(),
            ["support"] = info["support"]?.DeepClone(),
            ["eventCount"] = events.Count,
            ["errors"] = errors,
            ["warnings"] = warns,
        };
    }

    private static JsonObject EventsStatus(HostEnv env, HttpListenerRequest req)
    {
        if (!File.Exists(env.SessionFile)) return new JsonObject { ["none"] = true, ["events"] = new JsonArray() };
        string dir = File.ReadAllText(env.SessionFile).Trim();
        if (!Directory.Exists(dir)) return new JsonObject { ["none"] = true, ["events"] = new JsonArray() };
        int since = int.TryParse(req.QueryString["since"], out int s) ? s : 0;
        string? level = req.QueryString["level"];
        int minRank = SessionLog.LevelRank(level);
        var selected = SessionLog.ReadEvents(dir)
            .Where(e => (e["seq"]?.GetValue<int>() ?? 0) > since)
            .Where(e => minRank == 0 || SessionLog.LevelRank(e["level"]?.ToString()) >= minRank)
            .TakeLast(200)
            .Select(e => e.DeepClone())
            .ToArray();
        return new JsonObject { ["events"] = new JsonArray(selected) };
    }

    // ------------------------------------------------------------------- plain-language mapping

    private static string VerdictWord(SupportLevel level, ProjectInfo info) => level switch
    {
        SupportLevel.Supported => "Ready to run",
        SupportLevel.Experimental => "Worth a try",
        _ when !info.IsApp || info.Ui == UiFramework.None => "Not an app",
        _ => "Not a match",
    };

    private static string FriendlySummary(Assessment a, ProjectInfo info) => a.Level switch
    {
        SupportLevel.Supported => $"This is a {UiName(info.Ui)} app. CrossWin has run apps like it, so it should start and respond to clicks and typing.",
        SupportLevel.Experimental when info.IsNetFramework || !info.SdkStyle =>
            "This is an older .NET Framework app. CrossWin will try to run it, but some parts may look wrong and the live screenshot/click tools aren't available for it.",
        SupportLevel.Experimental when info.Findings.Count > 0 =>
            $"This {UiName(info.Ui)} app should start, but it uses a few features that can behave differently here. See what to expect below.",
        SupportLevel.Experimental => $"This {UiName(info.Ui)} app targets an older .NET version; it may build and run, but it isn't a supported path.",
        _ when !info.IsApp => "This project is a code library, not an app you can open — pick the app project instead.",
        _ when info.Ui == UiFramework.None => "This project isn't a WinForms or WPF app, so CrossWin can't run it as a desktop window.",
        _ => "CrossWin can't run this one.",
    };

    private static string FriendlyRuntime(ProjectInfo info)
    {
        if (info.ChosenTfm is not { } t) return "unknown";
        int? v = ProjectInspector.ModernVersion(t);
        if (v is >= 5) return $".NET {v}";
        if (info.IsNetFramework) return ".NET Framework";
        return t;
    }

    private static string UiName(UiFramework ui) => ui switch
    {
        UiFramework.Wpf => "WPF", UiFramework.WinForms => "Windows Forms", UiFramework.Both => "WPF + Windows Forms", _ => "desktop",
    };

    /// <summary>Strip the "Package: " / "path: " prefix the CLI uses so the GUI shows a clean sentence.</summary>
    private static string FriendlyRisk(string finding)
    {
        int colon = finding.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon < 60 ? finding[(colon + 2)..] : finding;
    }

    // ------------------------------------------------------------------- recent folders

    private static JsonArray Recent(HostEnv env)
    {
        string file = Path.Combine(env.Home, "gui", "recent.json");
        if (!File.Exists(file)) return new JsonArray();
        try
        {
            var arr = JsonNode.Parse(File.ReadAllText(file))?.AsArray() ?? new JsonArray();
            // Drop entries whose folder no longer exists.
            var live = arr.Where(n => n?["path"] is { } p && (Directory.Exists(p.ToString()) || File.Exists(p.ToString())))
                .Select(n => n!.DeepClone()).ToArray();
            return new JsonArray(live);
        }
        catch { return new JsonArray(); }
    }

    private static void Remember(HostEnv env, string path, string? project)
    {
        try
        {
            string dir = Path.Combine(env.Home, "gui");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "recent.json");
            var arr = File.Exists(file) ? (JsonNode.Parse(File.ReadAllText(file))?.AsArray() ?? new JsonArray()) : new JsonArray();
            var kept = arr.Where(n => n?["path"]?.ToString() != path).Select(n => n!.DeepClone()).ToList();
            kept.Insert(0, new JsonObject
            {
                ["path"] = path,
                ["name"] = project ?? Path.GetFileName(path.TrimEnd('/', '\\')),
                ["at"] = DateTime.UtcNow.ToString("O"),
            });
            File.WriteAllText(file, new JsonArray(kept.Take(8).ToArray()).ToJsonString(SessionLog.Json));
        }
        catch { /* recent list is a convenience; never fail a run over it */ }
    }

    // ------------------------------------------------------------------- plumbing

    /// <summary>Run a quick CLI command in-place (snap/ui — not an app launch) and capture stdout/stderr separately.</summary>
    private static (int exit, string stdout, string stderr) RunCli(HostEnv env, params string[] args)
    {
        string? dll = Assembly.GetEntryAssembly()?.Location;
        var psi = new ProcessStartInfo
        {
            FileName = dll != null && File.Exists(env.Dotnet) ? env.Dotnet : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (dll != null && File.Exists(env.Dotnet)) { psi.ArgumentList.Add(dll); foreach (string a in args) psi.ArgumentList.Add(a); }
        else { psi.ArgumentList.Add("-lc"); psi.ArgumentList.Add("exec crosswin " + string.Join(' ', args)); }
        try
        {
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o, e);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    /// <summary>Drop the agent's "ERROR " prefix and the "crosswin: " noise so the GUI shows a plain sentence.</summary>
    private static string CleanCli(string s)
    {
        s = s.Trim();
        if (s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) s = s[5..].TrimStart(' ', ':', '\t');
        if (s.StartsWith("crosswin:", StringComparison.OrdinalIgnoreCase)) s = s["crosswin:".Length..].Trim();
        return s.Length > 0 ? s : "Something went wrong.";
    }

    private static (HttpListener, int) StartListener(int wanted)
    {
        int[] candidates = wanted != 0 ? new[] { wanted } : new[] { 8787, 8788, 8789, 8790, 8791, 8080, 0 };
        foreach (int c in candidates)
        {
            int port = c == 0 ? FreePort() : c;
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); return (l, port); }
            catch (HttpListenerException) { try { l.Close(); } catch { } }
            catch (System.Net.Sockets.SocketException) { try { l.Close(); } catch { } }
        }
        throw new CrossWinException("Could not open a local port for the CrossWin control surface. Try  ./crosswin gui --port 8899");
    }

    private static int FreePort()
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        t.Start();
        int port = ((IPEndPoint)t.LocalEndpoint).Port;
        t.Stop();
        return port;
    }

    private static void SendIndex(HttpListenerContext ctx)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("gui/index.html");
        if (stream == null) { Send(ctx, 500, Json(new JsonObject { ["error"] = "GUI resource missing." })); return; }
        using var r = new StreamReader(stream);
        SendBytes(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(r.ReadToEnd()));
    }

    private static string Json(JsonNode node) => node.ToJsonString(SessionLog.Json);

    private static void Send(HttpListenerContext ctx, int status, string body)
        => SendBytes(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body));

    private static void SendBytes(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
    {
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    private static void TrySend(HttpListenerContext ctx, int status, string body)
    {
        try { Send(ctx, status, body); } catch { }
    }

    private static string ExpandHome(string p)
        => p == "~" || p.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 1 ? p[2..] : "")
            : p;

    /// <summary>Single-quote an argument for /bin/bash -lc.</summary>
    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static void TryOpenBrowser(string url)
    {
        try
        {
            string opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener, url) { UseShellExecute = false });
        }
        catch { /* the URL is printed; opening is best-effort */ }
    }
}
