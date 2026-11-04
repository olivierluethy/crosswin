using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrossWin.Web;

/// <summary>
/// Resolves what the user pointed at into a URL the browser can open, starting a dev server when the target is a
/// web project directory: a http(s) URL is used as-is; a <c>.html</c> file or a static directory with
/// <c>index.html</c> becomes a <c>file://</c> URL (no server, no Node needed); a directory with a
/// <c>package.json</c> starts its dev script and waits for the local URL it prints.
/// </summary>
public sealed partial class WebProject
{
    public string Url { get; private set; } = "";
    public string Name { get; private set; } = "web";
    public string? Root { get; private set; }
    public Process? DevServer { get; private set; }
    public string Kind { get; private set; } = "url";

    [GeneratedRegex(@"https?://(localhost|127\.0\.0\.1|\[::1\]):\d+\S*")]
    private static partial Regex LocalUrl();

    public static WebProject Resolve(string target, string? urlOverride, bool launchServer = true)
    {
        var p = new WebProject();
        if (Regex.IsMatch(target, @"^https?://"))
        {
            p.Url = target; p.Name = new Uri(target).Host; p.Kind = "url";
            return p;
        }
        string full = Path.GetFullPath(target);
        if (File.Exists(full) && (full.EndsWith(".html") || full.EndsWith(".htm")))
        {
            p.Root = Path.GetDirectoryName(full);
            p.Name = Path.GetFileNameWithoutExtension(full);
            p.Url = new Uri(full).AbsoluteUri;
            p.Kind = "file";
            return p;
        }
        if (!Directory.Exists(full)) throw new CrossWinException($"'{target}' is not a URL, an .html file or a directory.");
        p.Root = full;
        p.Name = Path.GetFileName(full.TrimEnd('/', '\\'));
        string pkg = Path.Combine(full, "package.json");
        string index = Path.Combine(full, "index.html");
        if (!File.Exists(pkg))
        {
            if (File.Exists(index)) { p.Url = new Uri(index).AbsoluteUri; p.Kind = "static"; return p; }
            throw new CrossWinException($"{target} has no package.json and no index.html. Pass a URL, an .html file, or a web project directory.");
        }
        p.Kind = "dev-server";
        if (urlOverride != null) { p.Url = urlOverride; return p; }
        if (launchServer) p.StartDevServer(full, pkg);
        return p;
    }

    private void StartDevServer(string dir, string pkgPath)
    {
        var (pm, pmPath) = DetectPackageManager(dir);
        if (pmPath == null)
            throw new CrossWinException($"This looks like a web app ({Name}) but '{pm}' is not installed. Install Node.js and the app's dependencies, or start the dev server yourself and pass its URL: crosswin web {dir} --url http://localhost:PORT");
        string script = DevScript(pkgPath);
        Console.WriteLine($"Starting the dev server: {pm} run {script} ...");
        var psi = new ProcessStartInfo(pmPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (string a in new[] { "run", script }) psi.ArgumentList.Add(a);
        psi.Environment["BROWSER"] = "none"; // CRA: don't auto-open a browser
        psi.Environment["CI"] = "true";
        var p = Process.Start(psi)!;
        DevServer = p;
        string? found = null;
        void Scan(string? line)
        {
            if (line == null) return;
            Console.WriteLine("[dev] " + line);
            if (found == null && LocalUrl().Match(line) is { Success: true } m) found = m.Value.TrimEnd('/', '.', ')');
        }
        p.OutputDataReceived += (_, e) => Scan(e.Data);
        p.ErrorDataReceived += (_, e) => Scan(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        while (found == null && sw.Elapsed < TimeSpan.FromSeconds(90) && !p.HasExited) Thread.Sleep(200);
        if (found == null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
            throw new CrossWinException($"The dev server did not print a local URL within 90 s. Start it yourself and pass the URL: crosswin web {dir} --url http://localhost:PORT");
        }
        Url = found;
    }

    private static (string Pm, string? Path) DetectPackageManager(string dir)
    {
        if (File.Exists(Path.Combine(dir, "pnpm-lock.yaml"))) return ("pnpm", HostEnv.FindOnPath("pnpm"));
        if (File.Exists(Path.Combine(dir, "yarn.lock"))) return ("yarn", HostEnv.FindOnPath("yarn"));
        if (File.Exists(Path.Combine(dir, "bun.lockb"))) return ("bun", HostEnv.FindOnPath("bun"));
        return ("npm", HostEnv.FindOnPath("npm"));
    }

    private static string DevScript(string pkgPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
            if (doc.RootElement.TryGetProperty("scripts", out var s))
                foreach (string name in new[] { "dev", "start", "serve" })
                    if (s.TryGetProperty(name, out _)) return name;
        }
        catch (JsonException) { }
        return "dev";
    }
}
