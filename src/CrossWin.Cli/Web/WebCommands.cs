using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrossWin.Web;

/// <summary>
/// <c>crosswin web …</c>: run a local web app in a real browser and stream its runtime diagnostics (console,
/// uncaught errors, failed/4xx-5xx requests) into the same session model as the desktop and mobile backends, over
/// the Chrome DevTools Protocol. No Node or Playwright is required to drive the browser; a dev server (React/Vite/
/// Next/…) needs the project's own Node toolchain, while a .html file or a static folder runs with no server at all.
/// </summary>
public static class WebCommands
{
    public const string Help = """
        crosswin web — run a local web app in a browser and see its errors (no Playwright to write)

          crosswin web run <url | file.html | dir> [--show] [--url URL]
                Open the target in a headless browser and stream console messages, uncaught exceptions
                (with source file:line) and failed network requests into the session. --show opens a visible
                window instead. A directory with package.json starts its dev server; a .html file or a folder
                with index.html runs with no server. --url skips server detection and attaches to a URL.
          crosswin web ui [--all] [--json]   The page's elements (role, text, value, position) from the DOM /
                                             accessibility tree; same format as  crosswin ui
          crosswin web input "click Save" "click X Y" "type hello" "key Enter" "scroll down" "expect Saved"
                                             Real input (CDP mouse/key events); click by visible label or pixels
          crosswin web snap [--out FILE]     Screenshot the page (PNG) via the running browser
          crosswin web stop                  Close the page and the browser (and any dev server crosswin started)
          crosswin web inspect <target>      What crosswin would run and whether a browser / Node are available

        ui / input / snap / stop act on the current web session; add  --session <id>  (from  crosswin sessions )
        to target one of several web apps running at once — each keeps its own browser connection.

        Then use the shared commands:  crosswin events --level warn · crosswin session
        """;

    public static int Dispatch(HostEnv env, string[] a)
    {
        if (a.Length == 0 || a[0] is "help" or "-h" or "--help") { Console.WriteLine(Help); return 0; }
        string? Opt(string name) => Array.IndexOf(a, name) is int i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        bool Flag(string name) => a.Contains(name);
        string Target() => a.Length > 1 && !a[1].StartsWith("--") ? a[1] : throw new CrossWinException($"Usage: crosswin web {a[0]} <url | file.html | dir>");

        switch (a[0])
        {
            case "inspect": return Inspect(Target());
            case "run": return Run(env, Target(), Flag("--show"), Opt("--url"));
            case "ui": return Ui(env, WebDir(env, Opt("--session")), Flag("--all"), Flag("--json"));
            case "input" when a.Length > 1:
            {
                var acts = a.Skip(1).ToList();
                int si = acts.IndexOf("--session");
                if (si >= 0) acts.RemoveRange(si, Math.Min(2, acts.Count - si));
                if (acts.Count == 0) throw new CrossWinException("No actions given. Example: crosswin web input \"click Save\" \"expect Saved\"");
                return Input(env, WebDir(env, Opt("--session")), acts);
            }
            case "snap": return Snap(env, WebDir(env, Opt("--session")), Opt("--out"));
            case "stop": return Stop(env, WebDir(env, Opt("--session")));
            default: throw new CrossWinException($"Unknown web command '{string.Join(' ', a)}'. Run 'crosswin web help'.");
        }
    }

    private static int Inspect(string target)
    {
        string? browser = Browser.Find();
        var p = WebProject.Resolve(target, urlOverride: null, launchServer: false);
        Console.WriteLine($"{p.Name}: {p.Kind}");
        if (p.Root != null) Console.WriteLine($"  source   {p.Root}");
        Console.WriteLine($"  open     {(p.Kind == "dev-server" ? "dev server (started on run; needs the app's Node toolchain)" : p.Url)}");
        Console.WriteLine($"  browser  {browser ?? "none found — install Google Chrome or Chromium"}");
        if (p.Kind == "dev-server")
            Console.WriteLine($"  node     {HostEnv.FindOnPath("node") ?? "not on PATH (needed to start the dev server; or pass --url)"}");
        return browser == null ? 1 : 0;
    }

    private static int Run(HostEnv env, string target, bool show, string? urlOverride)
    {
        string binary = Browser.Find() ?? throw new CrossWinException("No Chrome/Chromium/Edge/Brave found. Install Google Chrome or Chromium to run web apps.");
        var proj = WebProject.Resolve(target, urlOverride);
        var session = WebDiagnostics.Start(env, proj, binary, headless: !show);
        string profile = Path.Combine(env.Home, "web", "profile-" + Path.GetFileName(session.Dir));
        var (bproc, wsUrl) = Browser.Launch(binary, headless: !show, profile);
        return RunAsync(env, session, proj, bproc, wsUrl, show).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(HostEnv env, SessionLog session, WebProject proj, Process bproc, string wsUrl, bool show)
    {
        using var cts = new CancellationTokenSource();
        var cdp = new Cdp();
        string targetId = "", sessionId = "";
        try
        {
            await cdp.ConnectAsync(wsUrl, cts.Token);
            var created = await cdp.Send("Target.createTarget", new JsonObject { ["url"] = proj.Url }, ct: cts.Token);
            targetId = created["targetId"]?.ToString() ?? throw new CrossWinException("The browser did not create a page target.");
            var attached = await cdp.Send("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, ct: cts.Token);
            sessionId = attached["sessionId"]?.ToString() ?? throw new CrossWinException("Could not attach to the page.");

            var diag = new WebDiagnostics(session, proj.Root);
            cdp.OnEvent += (m, pr, sid) =>
            {
                if (sid == sessionId) diag.Handle(m, pr);
                else if (m == "Target.targetDestroyed" && pr["targetId"]?.ToString() == targetId) cts.Cancel();
            };
            foreach (string domain in new[] { "Runtime.enable", "Log.enable", "Network.enable", "Page.enable" })
                await cdp.Send(domain, null, sessionId, cts.Token);
            await cdp.Send("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct: cts.Token);

            session.SetInfo(o =>
            {
                o["state"] = "running";
                o["cdpUrl"] = wsUrl;
                o["targetId"] = targetId;
                o["browserPid"] = bproc.Id;
                if (proj.DevServer != null) o["devServerPid"] = proj.DevServer.Id;
            });
            session.Event("info", "app.ready", "browser attached; console, errors and network streaming");
            // The connection (cdpUrl/targetId/pids) is in this session's own info above, so several web sessions
            // can run at once and each be reconnected by its session id — no single shared session file.
            Console.WriteLine($"Watching {proj.Url} ({(show ? "visible window" : "headless")}). Ctrl-C stops the browser; run again to reopen.");
            Console.WriteLine("Diagnostics: crosswin events --level warn · crosswin session · crosswin web snap");
        }
        catch (Exception ex) when (ex is CrossWinException or OperationCanceledException)
        {
            session.Event("error", "app.exit", "could not attach to the browser: " + ex.Message.Split('\n')[0]);
            session.SetInfo(o => { o["state"] = "error"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
            cdp.Dispose();
            try { if (!bproc.HasExited) bproc.Kill(true); } catch (Exception) { }
            KillDevServer(proj);
            throw;
        }

        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!cts.IsCancellationRequested && !bproc.HasExited) await Task.Delay(200);
        }
        finally { Console.CancelKeyPress -= onCancel; }

        cdp.Dispose();
        // Ctrl-C (or the browser closing) ends the session. On an interactive Ctrl-C the terminal already delivers
        // SIGINT to the child browser, so "leave it running" was never true here — stop everything we started and
        // clean up, so no stale session points at a dead browser. (MCP's background `web run` gets no SIGINT and
        // keeps running until crosswin_stop.)
        bool browserGone = bproc.HasExited;
        try { if (!bproc.HasExited) bproc.Kill(true); } catch (Exception) { }
        KillDevServer(proj);
        session.Event("info", "app.exit", browserGone ? "the browser was closed" : "stopped (Ctrl-C)");
        // Clear cdpUrl so this session no longer reads as a live web app once its browser is gone.
        session.SetInfo(o => { o["state"] = browserGone ? "exited" : "stopped"; o.Remove("cdpUrl"); o["endedAt"] = DateTime.UtcNow.ToString("O"); });
        Console.WriteLine(browserGone ? "Browser closed." : "Stopped (browser and any dev server closed).");
        return 0;
    }

    private static int Ui(HostEnv env, string dir, bool all, bool json)
    {
        var ws = Load(dir);
        return WithPage(dir, ws, async (cdp, sid) =>
        {
            var sw = Stopwatch.StartNew();
            var nodes = await WebPage.Tree(cdp, sid, all);
            if (json) { Console.WriteLine(new JsonObject { ["framework"] = "web", ["nodes"] = nodes }.ToJsonString(SessionLog.Json)); return 0; }
            int shown = 0, total = 0;
            foreach (var node in nodes)
            {
                total++;
                if (node is not JsonObject nn) continue;
                string name = nn["name"]?.ToString() ?? "";
                bool interesting = name.Length > 0 || nn["value"] != null || nn["toggle"] != null || nn["clickable"] != null;
                if (!all && !interesting) continue;
                shown++;
                var line = new System.Text.StringBuilder(new string(' ', Math.Min(2 * (nn["depth"]?.GetValue<int>() ?? 0), 40)));
                line.Append(nn["role"]);
                if (name.Length > 0) line.Append($" \"{name}\"");
                if (nn["value"] is { } v) line.Append($" value=\"{v}\"");
                if (nn["toggle"] is { } tg) line.Append($" [{tg}]");
                if (nn["id"] is { } id) line.Append($" #{id}");
                line.Append($"  @{nn["x"]},{nn["y"]} {nn["w"]}x{nn["h"]}");
                if (nn["enabled"] != null) line.Append(" disabled");
                if (nn["focused"] != null) line.Append(" focused");
                Console.WriteLine(line.ToString());
            }
            Console.WriteLine($"-- web: {shown} of {total} elements in {sw.ElapsedMilliseconds} ms; click by label: crosswin web input \"click <label>\"");
            return 0;
        });
    }

    private static int Input(HostEnv env, string dir, List<string> actions)
    {
        var ws = Load(dir);
        return WithPage(dir, ws, async (cdp, sid) =>
        {
            var results = new List<string>();
            bool failed = false;
            var sw = Stopwatch.StartNew();
            foreach (string cmd in actions)
            {
                try { await WebPage.Do(cdp, sid, cmd); results.Add("ok: " + cmd); }
                catch (CrossWinException ex) { results.Add("ERROR " + ex.Message.Split('\n')[0]); failed = true; break; }
            }
            foreach (string r in results) Console.WriteLine(r);
            if (!failed) Console.WriteLine($"-- {actions.Count} action(s) in {sw.ElapsedMilliseconds} ms");
            WebDiagnostics.Interaction(dir, actions, results, failed, sw.ElapsedMilliseconds);
            return failed ? 1 : 0;
        });
    }

    private static int Snap(HostEnv env, string dir, string? outPath)
    {
        var ws = Load(dir);
        return WithPage(dir, ws, async (cdp, sid) =>
        {
            var shot = await cdp.Send("Page.captureScreenshot", new JsonObject { ["format"] = "png", ["captureBeyondViewport"] = true }, sid);
            byte[] png = Convert.FromBase64String(shot["data"]?.ToString() ?? throw new CrossWinException("The browser returned no image."));
            string file = outPath ?? Path.Combine(env.Home, "web", $"snap-{DateTime.Now:HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.WriteAllBytesAsync(file, png);
            Console.WriteLine(file);
            return 0;
        });
    }

    private static int Stop(HostEnv env, string dir)
    {
        var ws = Load(dir);
        try { WithPage(dir, ws, async (cdp, _) => { await cdp.Send("Target.closeTarget", new JsonObject { ["targetId"] = ws.TargetId }); return 0; }); }
        catch (Exception) { /* browser may be gone already */ }
        Kill(ws.BrowserPid);
        if (ws.DevServerPid != 0) Kill(ws.DevServerPid);
        if (SessionLog.ReadInfo(dir)["kind"]?.ToString() == "web")
        {
            var log = new SessionLog(dir);
            log.Event("info", "session.stop", "stopped on request");
            log.SetInfo(o => { o["state"] = "stopped"; o.Remove("cdpUrl"); o["endedAt"] = DateTime.UtcNow.ToString("O"); });
        }
        Console.WriteLine("Stopped.");
        return 0;
    }

    // ------------------------------------------------------------------ reconnect helpers

    private sealed record WebSession(string CdpUrl, string TargetId, int BrowserPid, int DevServerPid);

    /// <summary>The web session to act on: the one named by --session &lt;id&gt; (id or unique prefix), else the
    /// current pointer. Each web run records its own CDP connection in its session info, so several can run and be
    /// driven at once (multi-session, #2/#5/#6). Throws if the resolved session isn't a live web run.</summary>
    private static string WebDir(HostEnv env, string? sel)
    {
        if (sel != null) return Commands.ResolveSession(env, sel);
        if (!File.Exists(env.SessionFile)) throw new CrossWinException("No web app is running: start one with  crosswin web run <target>.");
        return File.ReadAllText(env.SessionFile).Trim();
    }

    /// <summary>Reconstructs the CDP connection from the session's own info (written by `web run`).</summary>
    private static WebSession Load(string dir)
    {
        var info = SessionLog.ReadInfo(dir);
        if (info["kind"]?.ToString() != "web" || info["cdpUrl"] == null)
            throw new CrossWinException("No web app is running in this session: start one with  crosswin web run <target>.");
        return new WebSession(info["cdpUrl"]!.ToString(), info["targetId"]?.ToString() ?? "",
            info["browserPid"]?.GetValue<int>() ?? 0, info["devServerPid"]?.GetValue<int>() ?? 0);
    }

    private static void MarkClosed(string dir)
    {
        try { new SessionLog(dir).SetInfo(o => { if (o["state"]?.ToString() is "running" or "starting") o["state"] = "exited"; o.Remove("cdpUrl"); o["endedAt"] = DateTime.UtcNow.ToString("O"); }); }
        catch (Exception) { }
    }

    /// <summary>Reconnects to the running browser, attaches to the page, runs one operation, then disconnects.</summary>
    private static int WithPage(string dir, WebSession ws, Func<Cdp, string, Task<int>> op)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var cdp = new Cdp();
        try
        {
            cdp.ConnectAsync(ws.CdpUrl, cts.Token).GetAwaiter().GetResult();
            var attached = cdp.Send("Target.attachToTarget", new JsonObject { ["targetId"] = ws.TargetId, ["flatten"] = true }, ct: cts.Token).GetAwaiter().GetResult();
            string sid = attached["sessionId"]?.ToString() ?? throw new CrossWinException("The page is no longer open — restart with  crosswin web run <target>.");
            cdp.Send("Page.enable", null, sid, cts.Token).GetAwaiter().GetResult();
            return op(cdp, sid).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Unreachable(ex))
        {
            // The browser for this session is gone (e.g. closed, or killed by Ctrl-C on an interactive `web run`).
            // Mark this session closed so the next call reports "no web app running" rather than failing to connect.
            MarkClosed(dir);
            throw new CrossWinException("The browser for this web session has closed. Start a new one with  crosswin web run <target>.");
        }
        finally { cdp.Dispose(); }
    }

    private static bool Unreachable(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException)
                return true;
        return false;
    }

    private static void KillDevServer(WebProject proj) { try { if (proj.DevServer is { HasExited: false } d) d.Kill(true); } catch (Exception) { } }

    private static void Kill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(true); } catch (Exception) { }
    }
}
