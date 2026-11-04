using System.Diagnostics;

namespace CrossWin.Web;

/// <summary>Finds and launches a Chromium-family browser with CDP enabled on a throwaway profile.</summary>
public static class Browser
{
    /// <summary>A Chrome/Chromium/Edge/Brave executable, or null if none is installed.</summary>
    public static string? Find()
    {
        if (OperatingSystem.IsMacOS())
            foreach (string p in new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
            })
                if (File.Exists(p)) return p;
        foreach (string n in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser", "chrome" })
            if (HostEnv.FindOnPath(n) is { } f) return f;
        return null;
    }

    /// <summary>
    /// Starts the browser with a fresh profile and remote debugging on a free port, then returns the process and the
    /// browser-level CDP websocket URL (read from the profile's <c>DevToolsActivePort</c> file Chromium writes on start).
    /// </summary>
    public static (Process Proc, string WsUrl) Launch(string binary, bool headless, string profileDir)
    {
        // A stale profile would keep an old DevToolsActivePort; start clean.
        try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true); } catch (IOException) { }
        Directory.CreateDirectory(profileDir);
        var args = new List<string>
        {
            "--remote-debugging-port=0", "--user-data-dir=" + profileDir,
            "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
            "--disable-sync", "--disable-features=Translate", "--disable-extensions", "--mute-audio",
            "--window-size=1280,900", "about:blank",
        };
        if (headless) { args.Insert(0, "--headless=new"); args.Add("--disable-gpu"); }
        var psi = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine();  // drain pipes (no handler = discarded) so the browser never blocks on a full stderr
        p.BeginErrorReadLine();

        string portFile = Path.Combine(profileDir, "DevToolsActivePort");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (File.Exists(portFile))
            {
                try
                {
                    string[] lines = File.ReadAllLines(portFile);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out int port))
                        return (p, $"ws://127.0.0.1:{port}{lines[1]}");
                }
                catch (IOException) { }
            }
            if (p.HasExited) throw new CrossWinException($"The browser exited before it was ready (exit {p.ExitCode}). Try without --show, or check the browser install.");
            Thread.Sleep(100);
        }
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
        throw new CrossWinException("Timed out waiting for the browser's DevTools endpoint.");
    }
}
