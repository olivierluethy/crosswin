using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossWin;

/// <summary>Facts about the host machine and the CrossWin-managed toolchain under CROSSWIN_HOME.</summary>
public sealed class HostEnv
{
    public string Home { get; } = Environment.GetEnvironmentVariable("CROSSWIN_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crosswin");

    public bool IsMac => OperatingSystem.IsMacOS();
    public bool IsLinux => OperatingSystem.IsLinux();
    public Architecture Arch => RuntimeInformation.OSArchitecture;
    public string OsDescription => RuntimeInformation.OSDescription;

    public string Dotnet => Path.Combine(Home, "dotnet", "dotnet");
    public string Prefix => Environment.GetEnvironmentVariable("WINEPREFIX") ?? Path.Combine(Home, "prefix");
    public string AgentDll => Path.Combine(Home, "tools", "agent", "CrossWin.Agent.dll");
    public string WorkRoot => Path.Combine(Home, "work");
    public string SessionFile => Path.Combine(Home, "session", "current");
    /// <summary>User-supplied fonts (e.g. from a licensed Windows) copied into the prefix before each run.</summary>
    public string UserFonts => Path.Combine(Home, "fonts");

    /// <summary>Wine launcher: CROSSWIN_WINE, the CrossWin-managed macOS build, then PATH.</summary>
    public string? Wine
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("CROSSWIN_WINE");
            if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
            if (IsMac)
            {
                string managed = Path.Combine(Home, "wine", "Wine Devel.app", "Contents", "Resources", "wine", "bin", "wine");
                if (File.Exists(managed)) return managed;
            }
            return FindOnPath("wine") ?? FindOnPath("wine64");
        }
    }

    public string? WineServer => Wine is { } w && File.Exists(Path.Combine(Path.GetDirectoryName(w)!, "wineserver"))
        ? Path.Combine(Path.GetDirectoryName(w)!, "wineserver")
        : FindOnPath("wineserver");

    /// <summary>On Apple Silicon, x86_64 Wine needs Rosetta 2.</summary>
    public bool? RosettaAvailable
    {
        get
        {
            if (!IsMac || Arch != Architecture.Arm64) return null;
            return Proc.Run("/usr/bin/arch", new[] { "-x86_64", "/usr/bin/true" }).ExitCode == 0;
        }
    }

    // Distro Wine may print warnings (e.g. "wine32 is missing") around the version line.
    public string? WineVersion => Wine is { } w
        ? Proc.Run(w, new[] { "--version" }, WineEnv()).Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("wine-")) ?? "unknown"
        : null;

    public bool PrefixReady => File.Exists(Path.Combine(Prefix, "system.reg"));

    public Dictionary<string, string> WineEnv() => new()
    {
        ["WINEPREFIX"] = Prefix,
        ["WINEDEBUG"] = Environment.GetEnvironmentVariable("WINEDEBUG") ?? "-all",
        // MoltenVK prints Vulkan extension lists on every start; hide unless the user asked for them.
        ["MVK_CONFIG_LOG_LEVEL"] = Environment.GetEnvironmentVariable("MVK_CONFIG_LOG_LEVEL") ?? "0",
    };

    /// <summary>
    /// Wine's own core fonts (Tahoma etc.). Without them GDI+ cannot fall back for missing fonts such as
    /// "Segoe UI" and WinForms throws "Font '?' cannot be found". Debian/Ubuntu ship them in fonts-wine.
    /// </summary>
    public bool? WineFontsPresent
    {
        get
        {
            if (Wine is not { } w) return null;
            string bin = Path.GetDirectoryName(Path.GetFullPath(w))!;
            string[] dirs =
            {
                Path.Combine(bin, "..", "share", "wine", "fonts"),
                "/usr/share/wine/fonts", "/usr/local/share/wine/fonts", "/opt/wine-stable/share/wine/fonts",
                "/opt/wine-devel/share/wine/fonts", "/opt/wine-staging/share/wine/fonts",
            };
            return dirs.Any(d => File.Exists(Path.Combine(d, "tahoma.ttf")));
        }
    }

    public static string ToWinePath(string unixPath) => "Z:" + Path.GetFullPath(unixPath).Replace('/', '\\');

    public static string? FindOnPath(string name)
    {
        // Windows: ';'-separated PATH and launchers with an extension (flutter.bat, npx.cmd, adb.exe).
        string[] exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".bat", ".cmd", "" } : new[] { "" };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (string ext in exts)
            {
                string candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

public static class Proc
{
    public record Result(int ExitCode, string Output);

    public static Result Run(string file, IEnumerable<string> args, IDictionary<string, string>? env = null, string? cwd = null)
    {
        var psi = Start(file, args, env, cwd);
        try
        {
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new Result(p.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new Result(-1, ex.Message);
        }
    }

    /// <summary>Runs a process, streaming each output line to <paramref name="onLine"/>.</summary>
    public static int Stream(string file, IEnumerable<string> args, Action<string> onLine, IDictionary<string, string>? env = null, string? cwd = null)
    {
        using var p = Process.Start(Start(file, args, env, cwd))!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    public static ProcessStartInfo Start(string file, IEnumerable<string> args, IDictionary<string, string>? env, string? cwd)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }
}
