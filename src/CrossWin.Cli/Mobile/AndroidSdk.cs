using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CrossWin.Mobile;

/// <summary>
/// A command-line Android SDK — no Android Studio. Uses an existing SDK when one is configured, otherwise a private
/// one under CROSSWIN_HOME/android/sdk. Also finds (or installs) a JDK 17+, which sdkmanager and Gradle need.
/// </summary>
public sealed class AndroidSdk
{
    // Google's command-line tools 12.0 (cmdline-tools;12.0). sdkmanager updates itself afterwards if needed.
    private const string CmdlineToolsBuild = "11076708";

    private readonly HostEnv _env;
    public AndroidSdk(HostEnv env) => _env = env;

    public static bool IsWindows => OperatingSystem.IsWindows();
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;
    private static string Bat(string name) => IsWindows ? name + ".bat" : name;

    public string PrivateRoot => Path.Combine(_env.Home, "android", "sdk");
    public string AvdHome => Path.Combine(_env.Home, "android", "avd");
    public string PrivateJdk => Path.Combine(_env.Home, "android", "jdk");

    /// <summary>ANDROID_HOME, ANDROID_SDK_ROOT, the usual install locations, then the private SDK.</summary>
    public string? Root
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string?[] candidates =
            {
                Environment.GetEnvironmentVariable("CROSSWIN_ANDROID_SDK"),
                Environment.GetEnvironmentVariable("ANDROID_HOME"),
                Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                PrivateRoot,
                Path.Combine(home, "Library", "Android", "sdk"),
                "/opt/homebrew/share/android-commandlinetools",
                "/usr/local/share/android-commandlinetools",
                Path.Combine(home, "Android", "Sdk"),
                Path.Combine(local, "Android", "Sdk"),
            };
            return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && SdkManagerIn(c!) != null);
        }
    }

    private static string? SdkManagerIn(string root)
    {
        foreach (string dir in new[] { Path.Combine(root, "cmdline-tools", "latest", "bin"), Path.Combine(root, "tools", "bin") })
        {
            string p = Path.Combine(dir, Bat("sdkmanager"));
            if (File.Exists(p)) return p;
        }
        if (Directory.Exists(Path.Combine(root, "cmdline-tools")))
            foreach (string v in Directory.GetDirectories(Path.Combine(root, "cmdline-tools")))
                if (File.Exists(Path.Combine(v, "bin", Bat("sdkmanager")))) return Path.Combine(v, "bin", Bat("sdkmanager"));
        return null;
    }

    public string? SdkManager => Root is { } r ? SdkManagerIn(r) : null;
    public string? AvdManager => SdkManager is { } s ? Path.Combine(Path.GetDirectoryName(s)!, Bat("avdmanager")) : null;
    public string? Adb => Tool(Path.Combine("platform-tools", Exe("adb")));
    public string? Emulator => Tool(Path.Combine("emulator", Exe("emulator")));

    private string? Tool(string rel)
    {
        // Platform-tools alone (phone-only setup) may live in the private SDK without cmdline-tools.
        foreach (string? r in new[] { Root, PrivateRoot })
            if (r != null && File.Exists(Path.Combine(r, rel))) return Path.Combine(r, rel);
        return null;
    }

    public string? Aapt2
    {
        get
        {
            if (Root is not { } r || !Directory.Exists(Path.Combine(r, "build-tools"))) return null;
            return Directory.GetDirectories(Path.Combine(r, "build-tools"))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version())
                .Select(d => Path.Combine(d, Exe("aapt2"))).FirstOrDefault(File.Exists);
        }
    }

    public bool LicensesAccepted => Root is { } r && File.Exists(Path.Combine(r, "licenses", "android-sdk-license"));

    /// <summary>The emulator image ABI that runs hardware-accelerated on this host.</summary>
    public string Abi => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";
    public const int ApiLevel = 36;
    public string SystemImage => $"system-images;android-{ApiLevel};google_apis;{Abi}";
    public bool SystemImageInstalled => Root is { } r &&
        File.Exists(Path.Combine(r, "system-images", $"android-{ApiLevel}", "google_apis", Abi, "system.img"));

    // ------------------------------------------------------------------ Java

    /// <summary>JAVA_HOME if it is 17+, a JDK next to Flutter's configuration, Homebrew's openjdk@17, then the private one.</summary>
    public string? JavaHome
    {
        get
        {
            string?[] candidates =
            {
                Environment.GetEnvironmentVariable("JAVA_HOME"),
                PrivateJdkHome(),
                "/opt/homebrew/opt/openjdk@17", "/opt/homebrew/opt/openjdk@21", "/opt/homebrew/opt/openjdk",
                "/usr/local/opt/openjdk@17", "/usr/lib/jvm/java-17-openjdk-amd64", "/usr/lib/jvm/java-21-openjdk-amd64",
                "/usr/lib/jvm/java-17-openjdk", "/usr/lib/jvm/java-21-openjdk",
            };
            return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && JavaMajor(c!) >= 17);
        }
    }

    private string? PrivateJdkHome()
    {
        if (!Directory.Exists(PrivateJdk)) return null;
        foreach (string d in Directory.GetDirectories(PrivateJdk))
        {
            string mac = Path.Combine(d, "Contents", "Home");
            if (Directory.Exists(mac)) return mac;
            if (File.Exists(Path.Combine(d, "bin", Exe("java")))) return d;
        }
        return null;
    }

    public static int JavaMajor(string javaHome)
    {
        string java = Path.Combine(javaHome, "bin", Exe("java"));
        if (!File.Exists(java)) return 0;
        string release = Path.Combine(javaHome, "release");
        if (File.Exists(release))
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"(\\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        var v = System.Text.RegularExpressions.Regex.Match(Proc.Run(java, new[] { "-version" }).Output, "version \"(\\d+)");
        return v.Success ? int.Parse(v.Groups[1].Value) : 0;
    }

    /// <summary>Environment for sdkmanager, Gradle, Flutter and React Native builds.</summary>
    public Dictionary<string, string> BuildEnv()
    {
        var e = new Dictionary<string, string> { ["ANDROID_AVD_HOME"] = AvdHome };
        if (Root is { } r) { e["ANDROID_HOME"] = r; e["ANDROID_SDK_ROOT"] = r; }
        if (JavaHome is { } j)
        {
            e["JAVA_HOME"] = j;
            e["PATH"] = Path.Combine(j, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        return e;
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Installs what is missing. <paramref name="phoneOnly"/>: adb only (~10 MB), for a real phone.</summary>
    public void Setup(bool phoneOnly, bool acceptLicenses)
    {
        if (phoneOnly)
        {
            if (Adb != null) { Console.WriteLine($"adb already present: {Adb}"); return; }
            string os = IsWindows ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            Download($"https://dl.google.com/android/repository/platform-tools-latest-{os}.zip", PrivateRoot, "platform-tools");
            Console.WriteLine($"adb installed: {Adb}");
            Console.WriteLine("Connect a phone with USB debugging on (Settings → Developer options), or pair over Wi-Fi with 'adb pair'.");
            return;
        }

        if (JavaHome == null) InstallJdk();
        if (Root == null) InstallCmdlineTools();
        string sdk = Root ?? throw new CrossWinException("Android command-line tools could not be installed.");
        Console.WriteLine($"Android SDK: {sdk}");
        Console.WriteLine($"JDK:         {JavaHome}");

        if (!LicensesAccepted)
        {
            if (!acceptLicenses)
            {
                Console.WriteLine("The Android SDK licenses must be accepted once. Read them now (interactive):");
                RunInteractive(SdkManager!, new[] { $"--sdk_root={sdk}", "--licenses" });
            }
            else
            {
                Console.WriteLine("Accepting Android SDK licenses (--accept-licenses) ...");
                RunWithInput(SdkManager!, new[] { $"--sdk_root={sdk}", "--licenses" }, string.Concat(Enumerable.Repeat("y\n", 20)));
            }
            if (!LicensesAccepted) throw new CrossWinException("Android SDK licenses not accepted; nothing installed.");
        }

        var packages = new List<string>();
        if (Adb == null) packages.Add("platform-tools");
        if (Emulator == null) packages.Add("emulator");
        if (!SystemImageInstalled) packages.Add(SystemImage);
        if (!Directory.Exists(Path.Combine(sdk, "platforms", $"android-{ApiLevel}"))) packages.Add($"platforms;android-{ApiLevel}");
        if (Aapt2 == null) packages.Add("build-tools;36.0.0");
        if (packages.Count == 0) { Console.WriteLine("Emulator, system image and platform tools are installed."); }
        else
        {
            Console.WriteLine($"Installing {string.Join(", ", packages)} (one-time, ~2 GB) ...");
            int code = Proc.Stream(SdkManager!, new[] { $"--sdk_root={sdk}" }.Concat(packages), line =>
            {
                if (!line.Contains('[') || line.Contains("100%")) Console.WriteLine("    " + line.Trim());
            }, BuildEnv());
            if (code != 0) throw new CrossWinException($"sdkmanager failed (exit {code}).");
        }
        Console.WriteLine(AccelerationHint() ?? "Hardware acceleration: available.");
    }

    /// <summary>Why the emulator would be slow or refuse to start, or null.</summary>
    public string? AccelerationHint()
    {
        if (OperatingSystem.IsLinux())
        {
            if (!File.Exists("/dev/kvm")) return "No /dev/kvm: enable virtualization (VT-x/AMD-V) in the BIOS; inside a VM enable nested virtualization.";
            var r = Proc.Run("/bin/sh", new[] { "-c", "test -r /dev/kvm -a -w /dev/kvm" });
            if (r.ExitCode != 0) return "No access to /dev/kvm: run  sudo usermod -aG kvm $USER  and log in again.";
        }
        if (IsWindows && Emulator is { } emu)
        {
            var r = Proc.Run(emu, new[] { "-accel-check" });
            if (r.ExitCode != 0)
                return "Hardware acceleration missing: enable 'Windows Hypervisor Platform' (Windows Features) and reboot.\n  " + r.Output.Trim();
        }
        return null;
    }

    private void InstallJdk()
    {
        string os = IsWindows ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        Console.WriteLine("No JDK 17+ found: installing a private Eclipse Temurin 17 (~190 MB) ...");
        Download($"https://api.adoptium.net/v3/binary/latest/17/ga/{os}/{arch}/jdk/hotspot/normal/eclipse", PrivateJdk, null,
            archiveIsZip: IsWindows);
        if (JavaHome == null) throw new CrossWinException($"JDK install failed; set JAVA_HOME to a JDK 17+.");
    }

    private void InstallCmdlineTools()
    {
        string os = IsWindows ? "win" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        Console.WriteLine("Installing Android command-line tools (~150 MB, no Android Studio) ...");
        string tmp = Path.Combine(_env.Home, "android", "dl-cmdline");
        Download($"https://dl.google.com/android/repository/commandlinetools-{os}-{CmdlineToolsBuild}_latest.zip", tmp, null);
        // The zip holds "cmdline-tools/"; sdkmanager expects it at <sdk>/cmdline-tools/latest.
        string dest = Path.Combine(PrivateRoot, "cmdline-tools", "latest");
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(Path.Combine(tmp, "cmdline-tools"), dest);
        Directory.Delete(tmp, true);
    }

    /// <summary>Downloads and unpacks a .zip or .tar.gz into <paramref name="dir"/>; skips if <paramref name="marker"/> exists.</summary>
    private static void Download(string url, string dir, string? marker, bool archiveIsZip = true)
    {
        if (marker != null && Directory.Exists(Path.Combine(dir, marker))) return;
        Directory.CreateDirectory(dir);
        bool zip = archiveIsZip;
        string file = Path.Combine(dir, zip ? "download.zip" : "download.tar.gz");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            if (!resp.IsSuccessStatusCode) throw new CrossWinException($"Download failed ({(int)resp.StatusCode}): {url}");
            using var fs = File.Create(file);
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }
        if (zip)
        {
            ZipFile.ExtractToDirectory(file, dir, overwriteFiles: true);
            if (!OperatingSystem.IsWindows()) RestoreExecBits(dir);
        }
        else
        {
            using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, overwriteFiles: true);
        }
        File.Delete(file);
    }

    /// <summary>Zip extraction may drop the executable bit of the Android tools' launchers.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RestoreExecBits(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string n = Path.GetFileName(f);
            bool exec = !n.Contains('.') || n.EndsWith(".sh") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            if (exec) File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private void RunInteractive(string file, string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in BuildEnv()) psi.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private void RunWithInput(string file, string[] args, string input)
    {
        var psi = Proc.Start(file, args, BuildEnv(), null);
        psi.RedirectStandardInput = true;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}
