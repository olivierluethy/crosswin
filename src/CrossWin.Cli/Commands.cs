using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CrossWin;

public static class Commands
{
    // Pinned macOS Wine build (Gcenx, LGPL). Homebrew's wine casks were disabled on 2026-09-01.
    private const string MacWineVersion = "11.21";
    private const string MacWineUrl = "https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.21/wine-devel-11.21-osx64.tar.xz";
    private const string MacWineSha256 = "9909b486657e10ab66c7279371ae1f990345b596b3b1b3a0cf21f28af24fa0ce";

    // ---------------------------------------------------------------- doctor

    public static int Doctor(HostEnv env)
    {
        bool ok = true;
        void Line(bool? good, string label, string detail)
        {
            string mark = good switch { true => "ok  ", false => "FAIL", null => "info" };
            Console.WriteLine($"[{mark}] {label,-14} {detail}");
            if (good == false) ok = false;
        }

        Line(null, "host", $"{env.OsDescription} ({env.Arch})");
        if (!env.IsMac && !env.IsLinux) Line(false, "host", "Only macOS and Linux hosts are supported.");
        if (env.RosettaAvailable is { } rosetta)
            Line(rosetta, "rosetta", rosetta ? "Rosetta 2 available (needed for x86_64 Wine)"
                : "missing: run  softwareupdate --install-rosetta --agree-to-license");
        if (env.IsLinux && env.Arch != Architecture.X64)
            Line(false, "arch", "Linux support currently targets x86_64 hosts; ARM Linux needs box64/FEX (not supported yet).");

        Line(File.Exists(env.Dotnet), ".NET SDK", File.Exists(env.Dotnet)
            ? Proc.Run(env.Dotnet, new[] { "--list-sdks" }).Output.Replace('\n', ' ').Trim()
            : $"missing at {env.Dotnet} (the ./crosswin script installs it)");

        string? wine = env.Wine;
        Line(wine != null, "wine", wine != null ? $"{env.WineVersion} at {wine}" : "missing: run  ./crosswin setup");
        Line(env.PrefixReady, "prefix", env.PrefixReady ? env.Prefix : $"not initialised ({env.Prefix}): run  ./crosswin setup");
        if (env.WineFontsPresent == false)
            Line(false, "fonts", "Wine's core fonts (tahoma.ttf) not found; WinForms apps may crash with \"Font cannot be found\". Debian/Ubuntu: sudo apt install fonts-wine fonts-liberation");
        Line(File.Exists(env.AgentDll), "agent", File.Exists(env.AgentDll) ? env.AgentDll : "not built: run  ./crosswin setup");
        Console.WriteLine(ok ? "\nReady." : "\nNot ready; fix the FAIL lines above.");
        return ok ? 0 : 1;
    }

    // ----------------------------------------------------------------- setup

    public static int Setup(HostEnv env, string repo)
    {
        if (env.RosettaAvailable == false)
            throw new CrossWinException("Rosetta 2 is required for Wine on Apple Silicon. Install it with:\n  softwareupdate --install-rosetta --agree-to-license\nthen rerun ./crosswin setup.");

        if (env.Wine == null)
        {
            if (env.IsMac) InstallMacWine(env);
            else throw new CrossWinException(LinuxWineHelp());
        }
        Console.WriteLine($"wine: {env.WineVersion} ({env.Wine})");

        if (!env.PrefixReady)
        {
            Console.WriteLine($"Initialising Wine prefix at {env.Prefix} (first time takes ~30-60 s)...");
            var wineEnv = env.WineEnv();
            // Skip the Wine Mono / Gecko download dialogs; modern .NET apps ship their own runtime.
            wineEnv["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
            Proc.Run(env.Wine!, new[] { "wineboot", "--init" }, wineEnv);
            WaitWineServer(env);
            if (!env.PrefixReady) throw new CrossWinException($"wineboot did not create {env.Prefix}; run with WINEDEBUG=err+all for details.");
        }
        SetWpfRendering(env, gpu: false);

        Console.WriteLine("Building CrossWin agent (in-process screenshot/input helper)...");
        var build = Proc.Run(env.Dotnet, new[] { "build", Path.Combine(repo, "tools", "CrossWin.Agent"), "-c", "Release", "-nologo",
            "-o", Path.GetDirectoryName(env.AgentDll)! });
        if (build.ExitCode != 0) throw new CrossWinException("Agent build failed:\n" + build.Output);

        Console.WriteLine();
        return Doctor(env);
    }

    private static void InstallMacWine(HostEnv env)
    {
        string dl = Path.Combine(env.Home, "dl");
        Directory.CreateDirectory(dl);
        string archive = Path.Combine(dl, Path.GetFileName(MacWineUrl));
        if (!File.Exists(archive))
        {
            Console.WriteLine($"Downloading Wine {MacWineVersion} for macOS (Gcenx build, ~200 MB)...");
            var r = Proc.Run("/usr/bin/curl", new[] { "-fSL", "-o", archive, MacWineUrl });
            if (r.ExitCode != 0) { File.Delete(archive); throw new CrossWinException("Download failed:\n" + r.Output); }
        }
        using (var fs = File.OpenRead(archive))
        {
            string hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (hash != MacWineSha256)
            {
                File.Delete(archive);
                throw new CrossWinException($"Checksum mismatch for {archive} ({hash}); deleted it. Rerun setup.");
            }
        }
        string target = Path.Combine(env.Home, "wine");
        Directory.CreateDirectory(target);
        var x = Proc.Run("/usr/bin/tar", new[] { "-xf", archive, "-C", target });
        if (x.ExitCode != 0) throw new CrossWinException("Extracting Wine failed:\n" + x.Output);
        // Downloaded with curl, so normally not quarantined; clear it anyway so Gatekeeper does not block wine.
        Proc.Run("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", target });
        if (env.Wine == null) throw new CrossWinException($"Wine was extracted to {target} but no wine binary was found.");
    }

    private static string LinuxWineHelp() => """
        Wine is not installed. Install Wine 9 or newer from your distribution or WineHQ, then rerun ./crosswin setup:
          Debian/Ubuntu:  sudo apt update && sudo apt install wine wine64   (32-bit apps: also wine32:i386)
          Fedora:         sudo dnf install wine
          Arch:           sudo pacman -S wine
        Newer builds: https://gitlab.winehq.org/wine/wine/-/wikis/Download
        Or point CrossWin at a specific build with CROSSWIN_WINE=/path/to/wine.
        """;

    private static void WaitWineServer(HostEnv env)
    {
        if (env.WineServer is { } ws) Proc.Run(ws, new[] { "-w" }, env.WineEnv());
    }

    /// <summary>WPF hardware rendering goes through Wine's D3D9 translation; software rendering is the verified path.</summary>
    private static void SetWpfRendering(HostEnv env, bool gpu)
    {
        string marker = Path.Combine(env.Prefix, ".crosswin-wpf-render");
        string wanted = gpu ? "gpu" : "software";
        if (File.Exists(marker) && File.ReadAllText(marker) == wanted) return;
        var r = Proc.Run(env.Wine!, new[] { "reg", "add", @"HKCU\Software\Microsoft\Avalon.Graphics", "/v", "DisableHWAcceleration",
            "/t", "REG_DWORD", "/d", gpu ? "0" : "1", "/f" }, env.WineEnv());
        if (r.ExitCode != 0) throw new CrossWinException("Could not set WPF render mode in the Wine registry:\n" + r.Output);
        // No wineserver -w here: it would block while any app runs; new processes see the change anyway.
        File.WriteAllText(marker, wanted);
    }

    // --------------------------------------------------------------- inspect

    public static int Inspect(HostEnv env, string path)
    {
        var projects = ProjectInspector.FindProjects(path);
        if (projects.Count == 0) throw new CrossWinException($"No .csproj found at {path}.");
        foreach (string p in projects)
        {
            var info = ProjectInspector.Inspect(p);
            var a = Assessment.For(info);
            Console.WriteLine($"{info.Name}  ({Path.GetRelativePath(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, p)})");
            Console.WriteLine($"  type:      {info.OutputType}{(info.SdkStyle ? "" : ", legacy (non-SDK) project")}");
            Console.WriteLine($"  framework: {string.Join(", ", info.TargetFrameworks.DefaultIfEmpty("unknown"))}");
            Console.WriteLine($"  ui:        {info.Ui}");
            Console.WriteLine($"  platform:  {info.Platform}");
            if (info.Packages.Count > 0) Console.WriteLine($"  packages:  {string.Join(", ", info.Packages)}");
            Console.WriteLine($"  verdict:   {a.Level} — {a.Summary}");
            foreach (string f in info.Findings) Console.WriteLine($"  risk:      {f}");
            Console.WriteLine();
        }
        Console.WriteLine($"Host: {env.OsDescription} ({env.Arch}); wine: {env.WineVersion ?? "not installed"}");
        return 0;
    }

    // ------------------------------------------------------------------- run

    public static int Run(HostEnv env, string repo, string path, string[] rest)
    {
        string? projectName = null;
        bool gpu = false, noBuild = false;
        int? snapAfter = null;
        var appArgs = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--project": projectName = rest[++i]; break;
                case "--gpu": gpu = true; break;
                case "--no-build": noBuild = true; break;
                case "--snap-after": snapAfter = int.Parse(rest[++i]); break;
                case "--": appArgs.AddRange(rest.Skip(i + 1)); i = rest.Length; break;
                default: throw new CrossWinException($"Unknown option {rest[i]}.");
            }
        }

        if (env.Wine == null || !env.PrefixReady || !File.Exists(env.AgentDll))
            throw new CrossWinException("CrossWin is not set up yet. Run  ./crosswin setup  first.");

        var info = SelectProject(path, projectName);
        var assessment = Assessment.For(info);
        Console.WriteLine($"==> {info.Name}: {info.Ui}, {info.ChosenTfm}; {assessment.Level} — {assessment.Summary}");
        foreach (string f in info.Findings) Console.WriteLine($"    risk: {f}");
        if (assessment.Level == SupportLevel.Unsupported) return 2;

        string sourceRoot = SourceRoot(path);
        string work = Path.Combine(env.WorkRoot, $"{info.Name}-{ShortHash(info.Path)}");
        string src = Path.Combine(work, "src");
        string output = Path.Combine(work, "out");
        string rid = info.Platform.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "win-x86" : "win-x64";

        if (!noBuild)
        {
            Console.WriteLine($"==> Copying sources to isolated work dir {src}");
            Mirror(sourceRoot, src);
            string projectCopy = Path.Combine(src, Path.GetRelativePath(sourceRoot, info.Path));
            Console.WriteLine($"==> Building {rid} self-contained ({info.ChosenTfm}) — log: {Path.Combine(work, "build.log")}");
            if (!Build(env, projectCopy, info.ChosenTfm!, rid, output, Path.Combine(work, "build.log")))
                return 3;
        }

        string? exe = FindExe(output, info);
        if (exe == null) throw new CrossWinException($"Build produced no .exe in {output}.");

        SetWpfRendering(env, gpu);
        return Launch(env, info, exe, appArgs, snapAfter, gpu);
    }

    private static ProjectInfo SelectProject(string path, string? name)
    {
        var apps = ProjectInspector.FindProjects(path).Select(ProjectInspector.Inspect)
            .Where(p => p.IsApp && p.Ui != UiFramework.None).ToList();
        if (name != null)
        {
            var match = apps.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new CrossWinException($"No WinForms/WPF app project named '{name}'. Candidates: {string.Join(", ", apps.Select(a => a.Name))}");
        }
        return apps.Count switch
        {
            0 => throw new CrossWinException($"No WinForms/WPF application project (OutputType WinExe/Exe with UseWPF/UseWindowsForms) found at {path}. Try  ./crosswin inspect {path}"),
            1 => apps[0],
            _ => throw new CrossWinException($"Several app projects found; choose one with --project: {string.Join(", ", apps.Select(a => a.Name))}"),
        };
    }

    /// <summary>Copy the whole repository so ProjectReferences and Directory.Build.props keep working.</summary>
    private static string SourceRoot(string path)
    {
        string dir = Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(path)! : path);
        var git = Proc.Run("git", new[] { "-C", dir, "rev-parse", "--show-toplevel" });
        return git.ExitCode == 0 ? git.Output.Trim() : dir;
    }

    private static readonly HashSet<string> Skipped = new() { "bin", "obj", ".git", ".vs", "node_modules", "packages" };

    /// <summary>One-way mirror; the user's tree is only read, never written.</summary>
    private static void Mirror(string from, string to)
    {
        Directory.CreateDirectory(to);
        var seen = new HashSet<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string name = Path.GetFileName(entry);
            if (Skipped.Contains(name)) continue;
            seen.Add(name);
            string dest = Path.Combine(to, name);
            if (Directory.Exists(entry)) { Mirror(entry, dest); continue; }
            var s = new FileInfo(entry);
            var d = new FileInfo(dest);
            if (!d.Exists || d.Length != s.Length || d.LastWriteTimeUtc != s.LastWriteTimeUtc)
            {
                File.Copy(entry, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, s.LastWriteTimeUtc);
            }
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(to))
        {
            string name = Path.GetFileName(entry);
            if (seen.Contains(name) || Skipped.Contains(name)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
        }
    }

    private static bool Build(HostEnv env, string project, string tfm, string rid, string output, string logPath)
    {
        using var log = new StreamWriter(logPath);
        var errors = new List<string>();
        int warnings = 0;
        var buildEnv = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" };
        int code = Proc.Stream(env.Dotnet, new[] { "publish", project, "-c", "Debug", "-f", tfm, "-r", rid, "--self-contained", "true",
            "-p:EnableWindowsTargeting=true", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-o", output, "-nologo" }, line =>
        {
            log.WriteLine(line);
            if (line.Contains(": error ")) { if (!errors.Contains(line.Trim())) errors.Add(line.Trim()); }
            else if (line.Contains(": warning ")) warnings++;
            else if (line.TrimStart().StartsWith("Restored ") || line.Contains(" -> ")) Console.WriteLine("    " + line.Trim());
        }, buildEnv);
        if (code == 0)
        {
            if (warnings > 0) Console.WriteLine($"    ({warnings} warning lines, see build.log)");
            return true;
        }
        Console.Error.WriteLine($"Build failed (exit {code}). First errors:");
        foreach (string e in errors.Take(15)) Console.Error.WriteLine("  " + e);
        if (errors.Any(e => e.Contains("NETSDK1045")))
            Console.Error.WriteLine("Hint: the project needs a newer .NET SDK than CrossWin installed; rerun ./crosswin after updating (see README).");
        if (errors.Any(e => e.Contains("MSB4019") || e.Contains("Microsoft.WinFX.targets")))
            Console.Error.WriteLine("Hint: legacy .NET Framework project files cannot be built by the .NET SDK on macOS/Linux yet.");
        return false;
    }

    private static string? FindExe(string output, ProjectInfo info)
    {
        if (!Directory.Exists(output)) return null;
        // The app's exe is the one with a matching .runtimeconfig.json (the apphost of a .NET app).
        var configs = Directory.GetFiles(output, "*.runtimeconfig.json");
        foreach (string cfg in configs.OrderByDescending(c => Path.GetFileName(c).StartsWith(info.Name + ".")))
        {
            string exe = cfg[..^".runtimeconfig.json".Length] + ".exe";
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static int Launch(HostEnv env, ProjectInfo info, string exe, List<string> appArgs, int? snapAfter, bool gpu)
    {
        string session = Path.Combine(env.Home, "session", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + info.Name);
        Directory.CreateDirectory(session);
        File.WriteAllText(env.SessionFile, session);
        string appLog = Path.Combine(session, "app.log");

        var wineEnv = env.WineEnv();
        // Wine's crash debugger keeps a crashed process alive (seen on Linux); let crashes end the run.
        string overrides = Environment.GetEnvironmentVariable("WINEDLLOVERRIDES") ?? "";
        wineEnv["WINEDLLOVERRIDES"] = overrides.Length > 0 ? overrides + ";winedbg.exe=d" : "winedbg.exe=d";
        bool agent = ProjectInspector.ModernVersion(info.ChosenTfm!) >= 6;
        if (agent)
        {
            wineEnv["DOTNET_STARTUP_HOOKS"] = HostEnv.ToWinePath(env.AgentDll);
            wineEnv["CROSSWIN_AGENT_DIR"] = HostEnv.ToWinePath(session);
        }

        Console.WriteLine($"==> Launching under {env.WineVersion}{(info.Ui is UiFramework.Wpf or UiFramework.Both ? $", WPF rendering: {(gpu ? "GPU (unverified)" : "software")}" : "")}");
        Console.WriteLine($"    session: {session}");
        Console.WriteLine("    close the app window (or press Ctrl+C) to stop.");

        var psi = Proc.Start(env.Wine!, new[] { exe }.Concat(appArgs), wineEnv, Path.GetDirectoryName(exe));
        var stopwatch = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        using var log = new StreamWriter(appLog) { AutoFlush = true };
        var crash = new StringBuilder();
        bool inCrash = false;
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (log)
            {
                log.WriteLine(line);
                if (!inCrash && line.Contains("Unhandled exception."))
                {
                    inCrash = true;
                    // Safety net if the process still lingers after an unhandled .NET exception.
                    _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
                    {
                        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    });
                }
                if (inCrash && crash.Length < 4000) crash.AppendLine(line);
                Console.WriteLine("[app] " + line);
            }
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        };

        if (agent)
        {
            _ = Task.Run(() =>
            {
                if (!WaitFor(Path.Combine(session, "agent.ready"), TimeSpan.FromSeconds(120), p)) return;
                Console.WriteLine($"==> .NET runtime started in Wine after {stopwatch.Elapsed.TotalSeconds:0.0}s (agent attached)");
                if (snapAfter is { } s)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(s));
                    foreach (string line in RequestAgent(session, "snap", "", p)) Console.WriteLine("==> snap: " + line);
                }
            });
        }

        p.WaitForExit();
        Console.WriteLine($"==> App exited with code {p.ExitCode} after {stopwatch.Elapsed.TotalSeconds:0.0}s. Log: {appLog}");
        if (crash.Length > 0)
        {
            Console.WriteLine("==> The app crashed with an unhandled .NET exception (first lines above, full text in the log).");
            string text = crash.ToString();
            if (text.Contains("Font '") && text.Contains("cannot be found"))
                Console.WriteLine("    Cause: a font is missing and Wine found no fallback. Install Wine's core fonts (Debian/Ubuntu: sudo apt install fonts-wine fonts-liberation) and rerun ./crosswin doctor.");
            else
                Console.WriteLine("    If it works on Windows, this is likely a Wine compatibility gap; please report the exception type and top frames.");
        }
        return p.ExitCode == 0 ? 0 : 4;
    }

    // ------------------------------------------------------- snap / input

    public static int Snap(HostEnv env)
    {
        string session = CurrentSession(env);
        var lines = RequestAgent(session, "snap", "", null);
        foreach (string line in lines) Console.WriteLine(line);
        return lines.Count > 0 && !lines[0].StartsWith("ERROR") ? 0 : 1;
    }

    public static int Input(HostEnv env, string[] commands)
    {
        string session = CurrentSession(env);
        var lines = RequestAgent(session, "input", string.Join('\n', commands), null);
        foreach (string line in lines) Console.WriteLine(line);
        return lines.Any(l => l.StartsWith("ERROR")) ? 1 : 0;
    }

    private static string CurrentSession(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) throw new CrossWinException("No app has been started with  ./crosswin run  yet.");
        string session = File.ReadAllText(env.SessionFile).Trim();
        if (!File.Exists(Path.Combine(session, "agent.ready")))
            throw new CrossWinException($"The CrossWin agent is not attached in session {session} (app not started, already exited, or older than .NET 6).");
        return session;
    }

    /// <summary>File-based request/response with the in-process agent (see tools/CrossWin.Agent).</summary>
    private static List<string> RequestAgent(string session, string name, string body, Process? app)
    {
        string done = Path.Combine(session, name + ".done");
        File.Delete(done);
        File.WriteAllText(Path.Combine(session, name + ".request.tmp"), body);
        File.Move(Path.Combine(session, name + ".request.tmp"), Path.Combine(session, name + ".request"), overwrite: true);
        if (!WaitFor(done, TimeSpan.FromSeconds(20), app))
            return new List<string> { "ERROR no answer from the agent within 20 s (is the app still running and responsive?)" };
        Thread.Sleep(50);
        return File.ReadAllLines(done).Where(l => l.Length > 0)
            .Select(l => l.StartsWith(@"Z:\") ? l[2..].Replace('\\', '/') : l).ToList();
    }

    private static bool WaitFor(string file, TimeSpan timeout, Process? app)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(file)) return true;
            if (app is { HasExited: true }) return false;
            Thread.Sleep(200);
        }
        return false;
    }

    private static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
