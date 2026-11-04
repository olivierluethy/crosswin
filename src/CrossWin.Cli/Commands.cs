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
        if (wine != null)
        {
            Line(null, "wine 32-bit", env.Wine32Available ? "available"
                : "not available (32-bit .NET Framework apps run as 64-bit where possible; Debian/Ubuntu: apt install wine32:i386)");
            string? mono = NetFramework.FindMono(env);
            Line(mono != null ? true : null, "wine mono", mono != null
                ? $"{mono} (for .NET Framework apps)"
                : "not installed; only needed for .NET Framework apps: run  ./crosswin setup");
        }
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
        EnsureFontReplacements(env);
        if (NetFramework.FindMono(env) == null)
        {
            // Only .NET Framework apps need Wine Mono; a failure here must not block modern .NET apps.
            try { NetFramework.InstallMono(env); }
            catch (CrossWinException ex) { Console.WriteLine("Warning: Wine Mono not installed (.NET Framework apps will not run): " + ex.Message); }
        }

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

    // Windows fonts that apps name but the host may lack, with host fonts to stand in for them (first available
    // wins; skipped when the host has the Windows font itself). Two WPF behaviours make this essential:
    //  - an unresolvable family falls back to "Arial", and if Arial is missing WPF calls FailFast (process dies);
    //  - the per-character fallback chain starts with "Segoe UI" / "Segoe UI Symbol"; without them even "-" can
    //    render as a box and missing glyphs are not substituted.
    private static readonly (string Windows, string[] Mac, string[] Linux)[] FontReplacements =
    {
        ("Arial", new[] { "Helvetica" }, new[] { "Liberation Sans", "DejaVu Sans", "Noto Sans" }),
        ("Times New Roman", new[] { "Times" }, new[] { "Liberation Serif", "DejaVu Serif", "Noto Serif" }),
        ("Courier New", new[] { "Courier" }, new[] { "Liberation Mono", "DejaVu Sans Mono", "Noto Sans Mono" }),
        ("Consolas", new[] { "Menlo", "Monaco" }, new[] { "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono" }),
        ("Segoe UI", new[] { "Helvetica Neue", "Arial Unicode MS", "Arial" }, new[] { "Noto Sans", "DejaVu Sans", "Liberation Sans" }),
        ("Segoe UI Light", new[] { "Helvetica Neue", "Arial" }, new[] { "Noto Sans", "DejaVu Sans", "Liberation Sans" }),
        ("Segoe UI Semibold", new[] { "Helvetica Neue", "Arial" }, new[] { "Noto Sans", "DejaVu Sans", "Liberation Sans" }),
        ("Segoe UI Symbol", new[] { "Arial Unicode MS", "Lucida Grande" }, new[] { "DejaVu Sans", "Noto Sans Symbols2", "Noto Sans" }),
    };

    /// <summary>
    /// Wine's own HKCU\Software\Wine\Fonts\Replacements is honoured by its DirectWrite (and so by WPF);
    /// the Windows FontSubstitutes key is not.
    /// </summary>
    private static void EnsureFontReplacements(HostEnv env)
    {
        var host = HostFontFamilies(env);
        var chosen = FontReplacements
            .Where(r => !host.Contains(r.Windows))
            .Select(r => (r.Windows, Host: (env.IsMac ? r.Mac : r.Linux).FirstOrDefault(host.Contains)))
            .Where(r => r.Host != null)
            .ToList();
        string marker = Path.Combine(env.Prefix, ".crosswin-font-replacements");
        string wanted = string.Join("\n", chosen.Select(r => $"{r.Windows}={r.Host}"));
        if (File.Exists(marker) && File.ReadAllText(marker) == wanted) return;
        foreach (var (windows, hostFont) in chosen)
        {
            var r = Proc.Run(env.Wine!, new[] { "reg", "add", @"HKCU\Software\Wine\Fonts\Replacements", "/v", windows,
                "/t", "REG_SZ", "/d", hostFont!, "/f" }, env.WineEnv());
            if (r.ExitCode != 0) throw new CrossWinException($"Could not set font replacement {windows} -> {hostFont}:\n" + r.Output);
            Console.WriteLine($"    font: \"{windows}\" -> host font \"{hostFont}\"");
        }
        if (!host.Contains("Arial") && !chosen.Any(c => c.Windows == "Arial"))
            Console.WriteLine("    font: WARNING no Arial substitute found; WPF apps may abort with \"Unrecoverable system error\". Install fonts-liberation.");
        File.WriteAllText(marker, wanted);
    }

    private static HashSet<string> HostFontFamilies(HostEnv env)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (env.IsMac)
        {
            // Font files of the candidates above; Wine's mac driver exposes these system fonts by family name.
            var files = new Dictionary<string, string>
            {
                ["Helvetica Neue"] = "/System/Library/Fonts/HelveticaNeue.ttc",
                ["Helvetica"] = "/System/Library/Fonts/Helvetica.ttc",
                ["Arial Unicode MS"] = "/Library/Fonts/Arial Unicode.ttf",
                ["Arial"] = "/System/Library/Fonts/Supplemental/Arial.ttf",
                ["Times New Roman"] = "/System/Library/Fonts/Supplemental/Times New Roman.ttf",
                ["Times"] = "/System/Library/Fonts/Times.ttc",
                ["Courier New"] = "/System/Library/Fonts/Supplemental/Courier New.ttf",
                ["Courier"] = "/System/Library/Fonts/Courier.ttc",
                ["Menlo"] = "/System/Library/Fonts/Menlo.ttc",
                ["Monaco"] = "/System/Library/Fonts/Monaco.ttf",
                ["Lucida Grande"] = "/System/Library/Fonts/LucidaGrande.ttc",
            };
            foreach (var (family, file) in files) if (File.Exists(file)) families.Add(family);
        }
        else
        {
            var r = Proc.Run("fc-list", new[] { ":", "family" });
            if (r.ExitCode == 0)
                foreach (string line in r.Output.Split('\n'))
                    foreach (string family in line.Split(','))
                        if (family.Trim().Length > 0) families.Add(family.Trim());
            // fc-list (package fontconfig) is not always installed; Wine still finds fonts via libfontconfig.
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NotoSans-Regular.ttf"] = "Noto Sans",
                ["DejaVuSans.ttf"] = "DejaVu Sans",
                ["LiberationSans-Regular.ttf"] = "Liberation Sans",
                ["NotoSansSymbols2-Regular.ttf"] = "Noto Sans Symbols2",
                ["LiberationSerif-Regular.ttf"] = "Liberation Serif",
                ["LiberationMono-Regular.ttf"] = "Liberation Mono",
                ["DejaVuSansMono.ttf"] = "DejaVu Sans Mono",
                ["DejaVuSerif.ttf"] = "DejaVu Serif",
                ["NotoSansMono-Regular.ttf"] = "Noto Sans Mono",
                ["NotoSerif-Regular.ttf"] = "Noto Serif",
                ["Arial.ttf"] = "Arial",
                ["arial.ttf"] = "Arial",
            };
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string dir in new[] { "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".local/share/fonts"), Path.Combine(home, ".fonts") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
                    if (files.TryGetValue(Path.GetFileName(file), out string? family)) families.Add(family);
            }
        }
        return families;
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
        bool netFramework = info.IsNetFramework || !info.SdkStyle;

        if (netFramework && NetFramework.FindMono(env) == null)
            throw new CrossWinException("This is a .NET Framework app and Wine Mono is not installed. Run  ./crosswin setup  first.");

        var session = SessionLog.Create(env, info.Name);
        string MapPath(string p) => MapToSource(p, src, sourceRoot);
        var git = GitIdentity(sourceRoot);
        session.SetInfo(o =>
        {
            o["id"] = Path.GetFileName(session.Dir);
            o["kind"] = "desktop";
            o["project"] = info.Name;
            o["projectFile"] = info.Path;
            o["sourceRoot"] = sourceRoot;
            o["git"] = git;
            o["ui"] = info.Ui.ToString();
            o["targetFramework"] = info.ChosenTfm;
            o["runtime"] = netFramework ? $"Wine Mono on {env.WineVersion} (compatibility layer)" : $".NET self-contained {rid} on {env.WineVersion} (compatibility layer)";
            o["host"] = $"{env.OsDescription} ({env.Arch})";
            o["wpfRendering"] = info.Ui is UiFramework.Wpf or UiFramework.Both ? (gpu ? "gpu" : "software") : null;
            o["support"] = assessment.Level.ToString();
            o["risks"] = new System.Text.Json.Nodes.JsonArray(info.Findings.Select(f => (System.Text.Json.Nodes.JsonNode?)f).ToArray());
            o["state"] = noBuild ? "starting" : "building";
            o["runnerPid"] = Environment.ProcessId;
            o["startedAt"] = DateTime.UtcNow.ToString("O");
        });
        session.Event("info", "session.start", $"{info.Name} ({info.Ui}, {info.ChosenTfm}) at {git?["revision"]?.ToString() ?? "no git"}");

        if (!noBuild)
        {
            Console.WriteLine($"==> Copying sources to isolated work dir {src}");
            Mirror(sourceRoot, src);
            string projectCopy = Path.Combine(src, Path.GetRelativePath(sourceRoot, info.Path));
            string log = Path.Combine(work, "build.log");
            if (netFramework)
            {
                foreach (string w in NetFramework.ShadowLegacyProjects(src)) Console.WriteLine("    warning: " + w);
                string tfm = NetFramework.ToTfm(info.ChosenTfm ?? "v4.7.2");
                var command = new List<string> { "build", projectCopy, "-c", "Debug", "-f", tfm };
                bool wants32 = info.Platform.Equals("x86", StringComparison.OrdinalIgnoreCase) || info.Prefer32Bit;
                if (wants32 && !env.Wine32Available)
                {
                    if (info.Platform.Equals("x86", StringComparison.OrdinalIgnoreCase))
                        throw new CrossWinException("This app targets x86 (32-bit) but this Wine has no 32-bit support. Install it (Debian/Ubuntu: sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install wine32:i386) and rerun.");
                    // AnyCPU + Prefer32Bit: pure managed code behaves the same as a 64-bit process.
                    command.Add("-p:Prefer32Bit=false");
                    Console.WriteLine("    warning: app prefers 32-bit but this Wine has no 32-bit support; running it as 64-bit (AnyCPU). Native 32-bit DLLs would fail; install wine32 to keep 32-bit.");
                }
                Console.WriteLine($"==> Building .NET Framework app ({tfm}) with the .NET SDK — log: {log}");
                if (!Build(env, command.ToArray(), output, log, session, MapPath))
                    return BuildFailed(session);
            }
            else
            {
                Console.WriteLine($"==> Building {rid} self-contained ({info.ChosenTfm}) — log: {log}");
                if (!Build(env, new[] { "publish", projectCopy, "-c", "Debug", "-f", info.ChosenTfm!, "-r", rid, "--self-contained", "true",
                        "-p:PublishSingleFile=false", "-p:PublishTrimmed=false" }, output, log, session, MapPath))
                    return BuildFailed(session);
            }
        }

        string? exe = FindExe(output, info);
        if (exe == null) throw new CrossWinException($"Build produced no .exe in {output}.");

        SetWpfRendering(env, gpu);
        EnsureFontReplacements(env);
        SyncUserFonts(env);
        return Launch(env, info, exe, appArgs, snapAfter, gpu, session, MapPath);
    }

    private static int BuildFailed(SessionLog session)
    {
        session.SetInfo(o => { o["state"] = "build-failed"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
        Console.Error.WriteLine("Structured errors (file/line in your source): ./crosswin events --level error");
        return 3;
    }

    /// <summary>Maps a path in the isolated work copy (host or Wine Z: form) back to the user's source tree.</summary>
    internal static string MapToSource(string path, string workSrc, string sourceRoot)
    {
        string p = path.Trim();
        if (p.StartsWith(@"Z:", StringComparison.OrdinalIgnoreCase)) p = p[2..].Replace('\\', '/');
        string prefix = workSrc.TrimEnd('/') + "/";
        return p.StartsWith(prefix, StringComparison.Ordinal) ? Path.Combine(sourceRoot, p[prefix.Length..]) : p;
    }

    internal static System.Text.Json.Nodes.JsonObject? GitIdentity(string dir)
    {
        var rev = Proc.Run("git", new[] { "-C", dir, "rev-parse", "HEAD" });
        if (rev.ExitCode != 0) return null;
        var status = Proc.Run("git", new[] { "-C", dir, "status", "--porcelain" });
        var branch = Proc.Run("git", new[] { "-C", dir, "rev-parse", "--abbrev-ref", "HEAD" });
        return new System.Text.Json.Nodes.JsonObject
        {
            ["revision"] = rev.Output.Trim(),
            ["branch"] = branch.Output.Trim(),
            ["dirty"] = status.ExitCode == 0 && status.Output.Trim().Length > 0,
        };
    }

    /// <summary>
    /// Installs user-supplied fonts into the prefix. WPF under Wine enumerates fonts through DirectWrite, which only
    /// sees fonts registered under HKLM\...\CurrentVersion\Fonts — copying the file alone is not enough.
    /// CrossWin never ships proprietary fonts itself.
    /// </summary>
    private static void SyncUserFonts(HostEnv env)
    {
        if (!Directory.Exists(env.UserFonts)) return;
        string target = Path.Combine(env.Prefix, "drive_c", "windows", "Fonts");
        string marker = Path.Combine(env.Prefix, ".crosswin-fonts");
        var registered = File.Exists(marker) ? File.ReadAllLines(marker).ToHashSet() : new HashSet<string>();
        Directory.CreateDirectory(target);
        foreach (string font in Directory.EnumerateFiles(env.UserFonts)
                     .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileName(font);
            string dest = Path.Combine(target, name);
            bool copied = !File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(font).Length;
            if (copied) File.Copy(font, dest, overwrite: true);
            if (!copied && registered.Contains(name)) continue;
            var r = Proc.Run(env.Wine!, new[] { "reg", "add", @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts",
                "/v", Path.GetFileNameWithoutExtension(name) + (name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? " (OpenType)" : " (TrueType)"),
                "/t", "REG_SZ", "/d", name, "/f" }, env.WineEnv());
            if (r.ExitCode != 0) { Console.WriteLine($"    font: could not register {name}: {r.Output.Trim()}"); continue; }
            registered.Add(name);
            Console.WriteLine($"    font: installed {name} into the Wine prefix");
        }
        File.WriteAllLines(marker, registered);
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
    internal static string SourceRoot(string path)
    {
        string dir = Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(path)! : path);
        var git = Proc.Run("git", new[] { "-C", dir, "rev-parse", "--show-toplevel" });
        return git.ExitCode == 0 ? git.Output.Trim() : dir;
    }

    private static readonly HashSet<string> Skipped = new() { "bin", "obj", ".git", ".vs", "node_modules", "packages" };

    /// <summary>One-way mirror; the user's tree is only read, never written. Skipped names (build outputs,
    /// caches) are neither copied nor deleted in the destination, so caches built there survive.</summary>
    internal static void Mirror(string from, string to, IReadOnlySet<string>? skip = null, IReadOnlySet<string>? keepTopLevel = null)
    {
        skip ??= Skipped;
        Directory.CreateDirectory(to);
        var seen = new HashSet<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string name = Path.GetFileName(entry);
            if (skip.Contains(name)) continue;
            seen.Add(name);
            string dest = Path.Combine(to, name);
            if (Directory.Exists(entry)) { Mirror(entry, dest, skip); continue; }
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
            if (seen.Contains(name) || skip.Contains(name) || keepTopLevel?.Contains(name) == true) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
        }
    }

    private static bool Build(HostEnv env, string[] command, string output, string logPath, SessionLog session, Func<string, string> mapPath)
    {
        var sw = Stopwatch.StartNew();
        session.Event("info", "build.start", "dotnet " + command[0]);
        using var log = new StreamWriter(logPath);
        var errors = new List<string>();
        int warnings = 0;
        var buildEnv = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" };
        int code = Proc.Stream(env.Dotnet, command.Concat(new[] { "-p:EnableWindowsTargeting=true", "-o", output, "-nologo" }), line =>
        {
            log.WriteLine(line);
            session.BuildLine(line, mapPath);
            if (line.Contains(": error ")) { if (!errors.Contains(line.Trim())) errors.Add(line.Trim()); }
            else if (line.Contains(": warning ")) warnings++;
            else if (line.TrimStart().StartsWith("Restored ") || line.Contains(" -> ")) Console.WriteLine("    " + line.Trim());
        }, buildEnv);
        session.Event(code == 0 ? "info" : "error", code == 0 ? "build.ok" : "build.failed",
            code == 0 ? $"build succeeded in {sw.Elapsed.TotalSeconds:0.0}s" : $"build failed (exit {code}) after {sw.Elapsed.TotalSeconds:0.0}s; full log: {logPath}",
            e => { e["durationMs"] = sw.ElapsedMilliseconds; e["warnings"] = warnings; });
        if (code == 0)
        {
            if (warnings > 0) Console.WriteLine($"    ({warnings} warning lines, see build.log)");
            return true;
        }
        Console.Error.WriteLine($"Build failed (exit {code}). First errors:");
        foreach (string e in errors.Take(15)) Console.Error.WriteLine("  " + e);
        if (errors.Any(e => e.Contains("NETSDK1045")))
            Console.Error.WriteLine("Hint: the project needs a newer .NET SDK than CrossWin installed; rerun ./crosswin after updating (see README).");
        if (errors.Any(e => e.Contains("MSB4019")))
            Console.Error.WriteLine("Hint: the project imports MSBuild targets that only exist on Windows (Visual Studio); see the warnings above.");
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
        // .NET Framework apps have no runtimeconfig; use the project's assembly name.
        string byName = Path.Combine(output, info.AssemblyName + ".exe");
        return File.Exists(byName) ? byName : null;
    }

    private static int Launch(HostEnv env, ProjectInfo info, string exe, List<string> appArgs, int? snapAfter, bool gpu, SessionLog slog, Func<string, string> mapPath)
    {
        string session = slog.Dir;
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
        slog.SetInfo(o => { o["state"] = "starting"; o["pid"] = p.Id; o["exe"] = exe; });
        slog.Event("info", "app.launch", $"launched {Path.GetFileName(exe)} under {env.WineVersion}", e => e["pid"] = p.Id);
        using var log = new StreamWriter(appLog) { AutoFlush = true };
        var crash = new StringBuilder();
        bool inCrash = false;
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (log)
            {
                log.WriteLine(line);
                slog.AppLine(line, mapPath);
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

        if (!agent) slog.SetInfo(o => o["state"] = "running");
        if (agent)
        {
            _ = Task.Run(() =>
            {
                if (!WaitFor(Path.Combine(session, "agent.ready"), TimeSpan.FromSeconds(120), p)) return;
                Console.WriteLine($"==> .NET runtime started in Wine after {stopwatch.Elapsed.TotalSeconds:0.0}s (agent attached)");
                slog.SetInfo(o => o["state"] = "running");
                slog.Event("info", "app.ready", "agent attached; ui/snap/input available", e => e["startupMs"] = stopwatch.ElapsedMilliseconds);
                if (snapAfter is { } s)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(s));
                    foreach (string line in RecaptureX11(env, RequestAgent(session, "snap", "", p))) Console.WriteLine("==> snap: " + line);
                }
            });
        }

        p.WaitForExit();
        lock (log) slog.Finish(p.ExitCode, mapPath);
        // 130/137/143 = ended by Ctrl+C / kill / SIGTERM (user or agent stopped it), not an app failure.
        bool signalled = p.ExitCode is 130 or 137 or 143;
        slog.Event(p.ExitCode == 0 || signalled ? "info" : "error", "app.exit",
            $"exit code {p.ExitCode}{(signalled ? " (stopped by signal)" : "")} after {stopwatch.Elapsed.TotalSeconds:0.0}s",
            e => { e["exitCode"] = p.ExitCode; e["crashed"] = crash.Length > 0; });
        slog.SetInfo(o => { o["state"] = crash.Length > 0 ? "crashed" : "exited"; o["exitCode"] = p.ExitCode; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
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
        var sw = Stopwatch.StartNew();
        var lines = RecaptureX11(env, RequestAgent(session, "snap", "", null));
        foreach (string line in lines) Console.WriteLine(line);
        new SessionLog(session).Event(lines.Any(l => l.StartsWith("ERROR")) ? "warn" : "info", "capture.snap",
            $"{lines.Count(l => l.Contains(".png"))} image(s)", e =>
            {
                e["durationMs"] = sw.ElapsedMilliseconds;
                e["files"] = new System.Text.Json.Nodes.JsonArray(lines.Where(l => !l.StartsWith("ERROR")).Select(l => (System.Text.Json.Nodes.JsonNode?)l.Split('\t')[0]).ToArray());
            });
        return lines.Count > 0 && !lines[0].StartsWith("ERROR") ? 0 : 1;
    }

    /// <summary>
    /// On Wine's X11 driver the agent's window-DC capture of WPF windows can be stale (shows the view before a
    /// navigation, while the X server shows the new one). X11 lets us read the real screen without permissions,
    /// so crop each window from a root capture when xwd + ImageMagick are available.
    /// </summary>
    private static List<string> RecaptureX11(HostEnv env, List<string> lines)
    {
        if (!env.IsLinux || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return lines;
        string? xwd = HostEnv.FindOnPath("xwd");
        string? convert = HostEnv.FindOnPath("magick") ?? HostEnv.FindOnPath("convert");
        if (xwd == null || convert == null)
        {
            if (lines.Any(l => !l.StartsWith("ERROR")))
                lines.Add("WARNING: X11 host capture unavailable (install x11-apps and imagemagick); WPF images may show an older state.");
            return lines;
        }
        var result = new List<string>();
        foreach (string line in lines)
        {
            string[] f = line.Split('\t');
            if (f.Length < 5 || line.StartsWith("ERROR")) { result.Add(line); continue; }
            string[] size = f[1].Split('x'), at = f[4].Split(',');
            string crop = $"{size[0]}x{size[1]}+{at[0]}+{at[1]}";
            var r = Proc.Run("/bin/sh", new[] { "-c", "\"$0\" -root -silent | \"$1\" xwd:- -crop \"$2\" +repage \"png:$3\"", xwd, convert, crop, f[0] });
            if (r.ExitCode == 0) { f[3] = "x11-screen"; result.Add(string.Join('\t', f)); }
            else result.Add(line + "\t(x11 capture failed: " + r.Output.Trim().Split('\n')[0] + ")");
        }
        return result;
    }

    public static int Input(HostEnv env, string[] commands)
    {
        string session = CurrentSession(env);
        var sw = Stopwatch.StartNew();
        var lines = RequestAgent(session, "input", string.Join('\n', commands), null);
        foreach (string line in lines) Console.WriteLine(line);
        bool failed = lines.Any(l => l.StartsWith("ERROR"));
        // Interaction history: what was sent, what the agent resolved, how long it took (typed text is redacted).
        new SessionLog(session).Event(failed ? "warn" : "info", "interaction.batch", $"{commands.Length} action(s){(failed ? ", failed" : "")}", e =>
        {
            e["durationMs"] = sw.ElapsedMilliseconds;
            e["actions"] = new System.Text.Json.Nodes.JsonArray(commands.Select(c => (System.Text.Json.Nodes.JsonNode?)SessionLog.Redact(c)).ToArray());
            e["results"] = new System.Text.Json.Nodes.JsonArray(lines.Select(l => (System.Text.Json.Nodes.JsonNode?)SessionLog.Redact(l)).ToArray());
        });
        return failed ? 1 : 0;
    }

    // ----------------------------------------------------- ui / events / session (agent-facing, issue #2)

    public static int Ui(HostEnv env, string[] args)
    {
        string session = CurrentSession(env);
        var sw = Stopwatch.StartNew();
        var lines = RequestAgent(session, "ui", "", null);
        string body = string.Join("\n", lines);
        if (lines.Count == 0 || body.StartsWith("ERROR")) { Console.Error.WriteLine(body.Length > 0 ? body : "ERROR empty answer"); return 1; }
        if (args.Contains("--json")) { Console.WriteLine(body); return 0; }
        var tree = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        bool all = args.Contains("--all");
        int shown = 0, total = 0;
        foreach (var n in tree["nodes"]!.AsArray())
        {
            total++;
            string role = n!["role"]!.ToString(), name = n["name"]?.ToString() ?? "";
            string? value = n["value"]?.ToString(), id = n["id"]?.ToString(), toggle = n["toggle"]?.ToString();
            bool offscreen = n["offscreen"] != null;
            // Compact default view: skip unnamed layout nodes and off-screen ones (use --all to see them).
            if (!all && (offscreen || (name.Length == 0 && value == null && id == null && n["depth"]!.GetValue<int>() > 0))) continue;
            shown++;
            var sb = new StringBuilder(new string(' ', 2 * n["depth"]!.GetValue<int>()));
            sb.Append(role);
            if (name.Length > 0) sb.Append($" \"{name}\"");
            if (value != null) sb.Append($" value=\"{value}\"");
            if (toggle != null) sb.Append($" [{toggle}]");
            if (id != null) sb.Append($" #{id}");
            sb.Append($"  @{n["x"]},{n["y"]} {n["w"]}x{n["h"]}");
            if (n["enabled"] != null) sb.Append(" disabled");
            if (n["focused"] != null) sb.Append(" focused");
            if (offscreen) sb.Append(" offscreen");
            if (n["window"] is { } w && w.ToString() != name) sb.Append($"  (window \"{w}\")");
            Console.WriteLine(sb.ToString());
        }
        Console.WriteLine($"-- {tree["framework"]}: {shown} of {total} elements in {sw.ElapsedMilliseconds} ms; click by label: ./crosswin input \"click <label>\"");
        return 0;
    }

    /// <summary>The current session (desktop or mobile); Android sessions pull new logcat lines first.</summary>
    private static string LatestSession(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) throw new CrossWinException("No app has been started with  ./crosswin run  or  ./crosswin mobile run  yet.");
        string dir = File.ReadAllText(env.SessionFile).Trim();
        CrossWin.Mobile.AndroidDiagnostics.Ingest(env, dir);
        return dir;
    }

    public static int Events(HostEnv env, string[] args)
    {
        string session = LatestSession(env);
        string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int minRank = SessionLog.LevelRank(Opt("--level") ?? "info");
        long since = long.TryParse(Opt("--since"), out long sn) ? sn : 0;
        int last = int.TryParse(Opt("--last"), out int ln) ? ln : 50;
        string? kind = Opt("--kind");
        var events = SessionLog.ReadEvents(session)
            .Where(e => SessionLog.LevelRank(e["level"]?.ToString()) >= minRank)
            .Where(e => e["seq"]!.GetValue<int>() > since)
            .Where(e => kind == null || e["kind"]!.ToString().StartsWith(kind))
            .ToList();
        events = events.Skip(Math.Max(0, events.Count - last)).ToList();
        if (args.Contains("--json"))
        {
            foreach (var e in events) Console.WriteLine(e.ToJsonString(SessionLog.Json));
            return 0;
        }
        foreach (var e in events)
        {
            string loc = e["file"] is { } f ? $"  {f}:{e["line"]}" : "";
            string code = e["code"] is { } c ? $"{c}: " : e["type"] is { } ty ? $"{ty}: " : "";
            string after = e["afterInteraction"] is { } ai ? $"  (after interaction #{ai})" : "";
            Console.WriteLine($"#{e["seq"],-4} {e["ms"],7}ms {e["level"],-5} {e["kind"],-20} {code}{e["msg"]}{loc}{after}");
            if (e["stack"] is System.Text.Json.Nodes.JsonArray st)
                foreach (var fr in st.Take(5)) Console.WriteLine($"{"",36}at {fr}");
        }
        if (events.Count == 0) Console.WriteLine("(no matching events)");
        return 0;
    }

    public static int SessionStatus(HostEnv env, string[] args)
    {
        string session = LatestSession(env);
        var info = SessionLog.ReadInfo(session);
        var events = SessionLog.ReadEvents(session);
        // A runner killed without cleanup leaves state "running": check the process.
        string state = info["state"]?.ToString() ?? "unknown";
        // (Desktop only: `mobile run` returns once the app is launched on the device.)
        if (info["kind"]?.ToString() != "android" && state is "running" or "starting" or "building" && info["runnerPid"] is { } rp && !Alive(rp.GetValue<int>()))
            state = "stopped (runner gone)";
        var errors = events.Where(e => e["level"]?.ToString() == "error").ToList();
        // Prefer the specific cause (build error, exception) over the generic consequence (build.failed, app.exit).
        var cause = errors.LastOrDefault(e => e["kind"]?.ToString() is not ("app.exit" or "build.failed")) ?? errors.LastOrDefault();
        var summary = new System.Text.Json.Nodes.JsonObject
        {
            ["session"] = info.DeepClone(),
            ["state"] = state,
            ["dir"] = session,
            ["agentAttached"] = File.Exists(Path.Combine(session, "agent.ready")) && state == "running",
            ["events"] = events.Count,
            ["errors"] = errors.Count,
            ["warnings"] = events.Count(e => e["level"]?.ToString() == "warn"),
            ["lastError"] = cause?.DeepClone(),
            ["lastSeq"] = events.LastOrDefault()?["seq"]?.GetValue<int>() ?? 0,
            ["logs"] = new System.Text.Json.Nodes.JsonArray(new[] { "app.log", "events.jsonl" }.Select(f => (System.Text.Json.Nodes.JsonNode?)Path.Combine(session, f)).ToArray()),
        };
        if (args.Contains("--json")) { Console.WriteLine(summary.ToJsonString(new System.Text.Json.JsonSerializerOptions(SessionLog.Json) { WriteIndented = true })); return 0; }
        Console.WriteLine($"session   {info["id"]}  [{state}]");
        if (info["kind"]?.ToString() == "android")
        {
            Console.WriteLine($"target    {info["project"]} — {info["projectKind"]}, {info["package"]}, profile {info["deviceProfile"]}");
            var tg = info["target"];
            Console.WriteLine($"runtime   {tg?["runtime"] ?? "not launched"}{(tg != null ? $" — {tg["manufacturer"]} {tg["model"]}, Android {tg["android"]} (API {tg["api"]}), {info["serial"]}" : "")}");
        }
        else
        {
            Console.WriteLine($"target    {info["project"]} — {info["ui"]}, {info["targetFramework"]}");
            Console.WriteLine($"runtime   {info["runtime"]}");
        }
        if (info["git"] is { } g) Console.WriteLine($"revision  {g["branch"]} {g["revision"]?.ToString()[..Math.Min(12, g["revision"]!.ToString().Length)]}{(g["dirty"]?.GetValue<bool>() == true ? " (uncommitted changes)" : "")}");
        Console.WriteLine($"events    {events.Count} ({errors.Count} errors, {summary["warnings"]} warnings) — ./crosswin events");
        if (cause is { } le)
            Console.WriteLine($"last err  {le["kind"]}: {le["msg"]}{(le["file"] is { } f ? $"  ({f}:{le["line"]})" : "")}");
        Console.WriteLine($"dir       {session}");
        return 0;
    }

    /// <summary>Liveness-checked state of a session (a runner killed without cleanup leaves state "running").</summary>
    public static string LiveState(System.Text.Json.Nodes.JsonObject info)
    {
        string state = info["state"]?.ToString() ?? "unknown";
        if (info["kind"]?.ToString() != "android" && state is "running" or "starting" or "building"
            && info["runnerPid"] is { } rp && int.TryParse(rp.ToString(), out int pid) && !Alive(pid))
            return "stopped";
        return state;
    }

    /// <summary>Every run this machine has recorded, newest first, with liveness-checked state and counts.
    /// Read-only overview (#2/#5/#6); does not change the current-session pointer.</summary>
    public static System.Text.Json.Nodes.JsonArray ListSessions(HostEnv env)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        string root = Path.GetDirectoryName(env.SessionFile)!;
        if (!Directory.Exists(root)) return arr;
        string? current = File.Exists(env.SessionFile) ? File.ReadAllText(env.SessionFile).Trim() : null;
        var dirs = Directory.EnumerateDirectories(root).ToList();
        var rows = new List<(DateTime When, System.Text.Json.Nodes.JsonObject Row)>();
        foreach (string dir in dirs)
        {
            var info = SessionLog.ReadInfo(dir);
            if (info.Count == 0) continue;
            var events = SessionLog.ReadEvents(dir);
            DateTime when = info["startedAt"] is { } s && DateTime.TryParse(s.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToUniversalTime() : Directory.GetCreationTimeUtc(dir);
            string state = LiveState(info);
            // "live" means we have *confirmed* the run is still going. Desktop/web runs have a runnerPid
            // LiveState already checked, so a live-ish state there is verified. Android runs are device-tracked
            // with no host pid, so a stale "running" cannot be confirmed from here: report it unverified, never
            // a confident "running now".
            bool android = info["kind"]?.ToString() == "android";
            bool liveish = state is "running" or "starting" or "building";
            rows.Add((when, new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = info["id"]?.DeepClone() ?? Path.GetFileName(dir),
                ["kind"] = info["kind"]?.DeepClone() ?? "desktop",
                ["project"] = info["project"]?.DeepClone(),
                ["state"] = state,
                ["live"] = liveish && !android,
                ["unverified"] = liveish && android,
                ["current"] = dir == current,
                ["startedAt"] = info["startedAt"]?.DeepClone(),
                ["errors"] = events.Count(e => e["level"]?.ToString() == "error"),
                ["warnings"] = events.Count(e => e["level"]?.ToString() == "warn"),
                ["dir"] = dir,
            }));
        }
        foreach (var r in rows.OrderByDescending(r => r.When)) arr.Add(r.Row);
        return arr;
    }

    public static int Sessions(HostEnv env, string[] args)
    {
        var list = ListSessions(env);
        if (args.Contains("--json")) { Console.WriteLine(list.ToJsonString(new System.Text.Json.JsonSerializerOptions(SessionLog.Json) { WriteIndented = true })); return 0; }
        if (list.Count == 0) { Console.WriteLine("No runs yet. Start one with  ./crosswin run <repo>  (or web/mobile run)."); return 0; }
        foreach (var r in list)
        {
            string mark = r!["current"]?.GetValue<bool>() == true ? "*" : " ";
            string errs = (r["errors"]!.GetValue<int>() is int e && e > 0) ? $", {e} err" : "";
            string warns = (r["warnings"]!.GetValue<int>() is int w && w > 0) ? $", {w} warn" : "";
            Console.WriteLine($"{mark} {r["id"],-26} {r["kind"],-8} {r["state"],-10} {r["project"]}{errs}{warns}");
        }
        Console.WriteLine("\n* = current session (what ui/events/snap/stop act on). Read any with  ./crosswin session.");
        return 0;
    }

    /// <summary>Stops the app of the current session (and with it the `run` process), if still running.</summary>
    public static int Stop(HostEnv env)
    {
        if (!File.Exists(env.SessionFile)) { Console.WriteLine("No session."); return 0; }
        string session = File.ReadAllText(env.SessionFile).Trim();
        var info = SessionLog.ReadInfo(session);
        bool any = false;
        foreach (string key in new[] { "pid", "runnerPid" })
        {
            if (info[key] is not { } node || node.GetValue<int>() == Environment.ProcessId) continue;
            try
            {
                var proc = Process.GetProcessById(node.GetValue<int>());
                if (proc.HasExited) continue;
                // The app first: the runner then records app.exit and ends by itself.
                if (key == "runnerPid" && proc.WaitForExit(5000)) continue;
                proc.Kill(entireProcessTree: true);
                any = true;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        if (any) new SessionLog(session).Event("info", "session.stop", "stopped on request");
        Console.WriteLine(any ? $"Stopped {info["project"]} ({info["id"]})." : "Nothing running.");
        return 0;
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; }
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

    internal static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
}
