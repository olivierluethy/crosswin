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
          crosswin web snap [--out FILE]     Screenshot the page (PNG) via the running browser
          crosswin web stop                  Close the page and the browser (and any dev server crosswin started)
          crosswin web inspect <target>      What crosswin would run and whether a browser / Node are available

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
            case "snap": return Snap(env, Opt("--out"));
            case "stop": return Stop(env);
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
        try { return RunAsync(env, session, proj, bproc, wsUrl, show).GetAwaiter().GetResult(); }
        finally { try { if (proj.DevServer is { HasExited: false }) { } } catch (Exception) { } }
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
            Save(env, new WebSession(wsUrl, targetId, bproc.Id, proj.DevServer?.Id ?? 0));
            Console.WriteLine($"Watching {proj.Url} ({(show ? "visible window" : "headless")}). Ctrl-C to stop watching.");
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
        if (bproc.HasExited)
        {
            session.Event("info", "app.exit", "the browser was closed");
            session.SetInfo(o => { o["state"] = "exited"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
            KillDevServer(proj);
            File.Delete(SessionPath(env));
            Console.WriteLine("Browser closed.");
        }
        else
        {
            // Detached: leave the browser (and dev server) running so `web snap` and a visible window persist.
            session.Event("info", "collector.detached", "stopped watching; browser still running");
            Console.WriteLine("Detached. The browser is still running — crosswin web stop closes it.");
        }
        return 0;
    }

    private static int Snap(HostEnv env, string? outPath)
    {
        var ws = Load(env);
        return WithPage(ws, async (cdp, sid) =>
        {
            var shot = await cdp.Send("Page.captureScreenshot", new JsonObject { ["format"] = "png", ["captureBeyondViewport"] = true }, sid);
            byte[] png = Convert.FromBase64String(shot["data"]?.ToString() ?? throw new CrossWinException("The browser returned no image."));
            string file = outPath ?? Path.Combine(env.Home, "web", $"snap-{DateTime.Now:HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.WriteAllBytesAsync(file, png);
            Console.WriteLine(file);
        });
    }

    private static int Stop(HostEnv env)
    {
        var ws = Load(env);
        try { WithPage(ws, async (cdp, _) => await cdp.Send("Target.closeTarget", new JsonObject { ["targetId"] = ws.TargetId })); }
        catch (Exception) { /* browser may be gone already */ }
        Kill(ws.BrowserPid);
        if (ws.DevServerPid != 0) Kill(ws.DevServerPid);
        if (File.Exists(env.SessionFile) && File.ReadAllText(env.SessionFile).Trim() is var dir
            && SessionLog.ReadInfo(dir)["kind"]?.ToString() == "web")
        {
            var log = new SessionLog(dir);
            log.Event("info", "session.stop", "stopped on request");
            log.SetInfo(o => { o["state"] = "stopped"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
        }
        try { File.Delete(SessionPath(env)); } catch (Exception) { }
        Console.WriteLine("Stopped.");
        return 0;
    }

    // ------------------------------------------------------------------ reconnect helpers

    private sealed record WebSession(string CdpUrl, string TargetId, int BrowserPid, int DevServerPid);

    private static string SessionPath(HostEnv env) => Path.Combine(env.Home, "web", "session.json");

    private static void Save(HostEnv env, WebSession s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath(env))!);
        File.WriteAllText(SessionPath(env), JsonSerializer.Serialize(s));
    }

    private static WebSession Load(HostEnv env)
    {
        if (!File.Exists(SessionPath(env))) throw new CrossWinException("No web app is running: start one with  crosswin web run <target>.");
        return JsonSerializer.Deserialize<WebSession>(File.ReadAllText(SessionPath(env)))!;
    }

    /// <summary>Reconnects to the running browser, attaches to the page, runs one operation, then disconnects.</summary>
    private static int WithPage(WebSession ws, Func<Cdp, string, Task> op)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cdp = new Cdp();
        try
        {
            cdp.ConnectAsync(ws.CdpUrl, cts.Token).GetAwaiter().GetResult();
            var attached = cdp.Send("Target.attachToTarget", new JsonObject { ["targetId"] = ws.TargetId, ["flatten"] = true }, ct: cts.Token).GetAwaiter().GetResult();
            string sid = attached["sessionId"]?.ToString() ?? throw new CrossWinException("The page is no longer open.");
            cdp.Send("Page.enable", null, sid, cts.Token).GetAwaiter().GetResult();
            op(cdp, sid).GetAwaiter().GetResult();
            return 0;
        }
        finally { cdp.Dispose(); }
    }

    private static void KillDevServer(WebProject proj) { try { if (proj.DevServer is { HasExited: false } d) d.Kill(true); } catch (Exception) { } }

    private static void Kill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(true); } catch (Exception) { }
    }
}
