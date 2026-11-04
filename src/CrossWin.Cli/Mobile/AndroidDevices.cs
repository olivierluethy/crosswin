using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CrossWin.Mobile;

/// <summary>
/// Runs apps on one Android target: the CrossWin emulator or a real phone over adb. Different phones are reproduced
/// on the same target by overriding its display size and density (<c>wm size</c> / <c>wm density</c>), which is what
/// Android apps lay out against — one system image and one boot instead of an emulator per model.
/// </summary>
public sealed class AndroidTarget
{
    public const string AvdName = "crosswin-phone";
    private const int EmulatorPort = 5554;
    public const string EmulatorSerial = "emulator-5554";

    private readonly AndroidSdk _sdk;
    public string Serial { get; }
    public bool IsEmulator => Serial.StartsWith("emulator-");

    private AndroidTarget(AndroidSdk sdk, string serial) { _sdk = sdk; Serial = serial; }

    private string Adb => _sdk.Adb ?? throw new CrossWinException("adb missing: run  crosswin mobile setup  (or  --phone-only).");

    public Proc.Result Shell(params string[] args) => Proc.Run(Adb, new[] { "-s", Serial, "shell" }.Concat(args), _sdk.BuildEnv());
    public Proc.Result AdbCmd(params string[] args) => Proc.Run(Adb, new[] { "-s", Serial }.Concat(args), _sdk.BuildEnv());

    /// <summary>Connected devices in state "device" (serial, model).</summary>
    public static List<(string Serial, string Model)> Connected(AndroidSdk sdk)
    {
        if (sdk.Adb is not { } adb) return new();
        var r = Proc.Run(adb, new[] { "devices", "-l" }, sdk.BuildEnv());
        var list = new List<(string, string)>();
        foreach (string line in r.Output.Split('\n').Skip(1))
        {
            var m = Regex.Match(line, @"^(\S+)\s+device\b(.*)$");
            if (!m.Success) continue;
            var model = Regex.Match(m.Groups[2].Value, @"model:(\S+)");
            list.Add((m.Groups[1].Value, model.Success ? model.Groups[1].Value.Replace('_', ' ') : "?"));
        }
        return list;
    }

    /// <summary>Every device adb sees, including ones not usable yet (unauthorized, offline, no permissions).</summary>
    public static List<(string Serial, string State, string Model)> All(AndroidSdk sdk)
    {
        if (sdk.Adb is not { } adb) return new();
        var r = Proc.Run(adb, new[] { "devices", "-l" }, sdk.BuildEnv());
        var list = new List<(string, string, string)>();
        foreach (string line in r.Output.Split('\n').Skip(1))
        {
            // "SERIAL  unauthorized usb:1-1 transport_id:3" / "SERIAL  no permissions (user in plugdev group; …)"
            var m = Regex.Match(line.TrimEnd(), @"^(\S+)\s+(device|unauthorized|offline|authorizing|connecting|recovery|sideload|bootloader|no permissions)\b(.*)$");
            if (!m.Success) continue;
            var model = Regex.Match(m.Groups[3].Value, @"model:(\S+)");
            list.Add((m.Groups[1].Value, m.Groups[2].Value, model.Success ? model.Groups[1].Value.Replace('_', ' ') : "?"));
        }
        return list;
    }

    /// <summary>Plain-language next step for a device that adb sees but cannot use. CrossWin never bypasses the
    /// phone's trust prompt; the person holding the phone has to allow it.</summary>
    public static string? StateHelp(string state) => state switch
    {
        "unauthorized" => "Unlock the phone and tap \"Allow\" on \"Allow USB debugging?\" (tick \"Always allow from this computer\" to skip it next time). No prompt? Unplug and plug in again, or in Developer options tap \"Revoke USB debugging authorizations\" and reconnect.",
        "authorizing" or "connecting" => "The phone is still connecting; wait a few seconds and try again.",
        "offline" => "The phone is connected but not answering. Unplug and plug it in again (use a data cable, not a charge-only cable); if it stays offline, restart adb with  adb kill-server.",
        "no permissions" => OperatingSystem.IsLinux()
            ? "Linux does not allow this user to open the phone's USB device. Install the udev rules (Debian/Ubuntu: sudo apt install android-sdk-platform-tools-common), add yourself to the plugdev group, then unplug and plug in again."
            : "This user may not access the phone over USB; unplug and plug it in again.",
        "recovery" or "sideload" or "bootloader" => "The phone is in recovery/bootloader mode; restart it normally.",
        _ => null,
    };

    /// <summary>
    /// --serial wins; "--phone" picks the single connected real device; otherwise the CrossWin emulator
    /// (created and booted when needed).
    /// </summary>
    public static AndroidTarget Acquire(AndroidSdk sdk, string? serial, bool phone, bool headless)
    {
        var connected = Connected(sdk);
        if (serial != null)
        {
            if (!connected.Any(c => c.Serial == serial))
            {
                var known = All(sdk).FirstOrDefault(d => d.Serial == serial);
                if (known.Serial != null && StateHelp(known.State) is { } help)
                    throw new CrossWinException($"Device {serial} is {known.State}. {help}");
                throw new CrossWinException($"Device {serial} is not connected. Connected: {Describe(connected)}");
            }
            return new AndroidTarget(sdk, serial);
        }
        if (phone)
        {
            var phones = connected.Where(c => !c.Serial.StartsWith("emulator-")).ToList();
            if (phones.Count == 0 && All(sdk).FirstOrDefault(d => !d.Serial.StartsWith("emulator-") && d.State != "device") is { Serial: not null } waiting)
                throw new CrossWinException($"Phone {waiting.Serial} is connected but {waiting.State}. {StateHelp(waiting.State) ?? ""}".TrimEnd());
            return phones.Count switch
            {
                1 => new AndroidTarget(sdk, phones[0].Serial),
                0 => throw new CrossWinException("No phone connected. Enable USB debugging (Settings → About phone → tap 'Build number' 7×, then Developer options → USB debugging) and plug it in, or pair over Wi-Fi with  adb pair <ip:port>."),
                _ => throw new CrossWinException($"Several phones connected; pick one with --serial: {Describe(phones)}"),
            };
        }
        var t = new AndroidTarget(sdk, EmulatorSerial);
        if (!connected.Any(c => c.Serial == EmulatorSerial)) t.BootEmulator(headless);
        return t;
    }

    private static string Describe(List<(string Serial, string Model)> l) =>
        l.Count == 0 ? "none" : string.Join(", ", l.Select(c => $"{c.Serial} ({c.Model})"));

    // ------------------------------------------------------------------ emulator

    private void EnsureAvd()
    {
        string config = Path.Combine(_sdk.AvdHome, AvdName + ".avd", "config.ini");
        if (File.Exists(config)) return;
        if (!_sdk.SystemImageInstalled || _sdk.AvdManager is not { } avdmanager)
            throw new CrossWinException("Emulator not installed: run  crosswin mobile setup");
        Directory.CreateDirectory(_sdk.AvdHome);
        Console.WriteLine($"Creating emulator '{AvdName}' ({_sdk.SystemImage}) ...");
        var psi = Proc.Start(avdmanager, new[] { "create", "avd", "-n", AvdName, "-k", _sdk.SystemImage, "-d", "pixel_7", "--force" },
            _sdk.BuildEnv(), null);
        psi.RedirectStandardInput = true;
        using (var p = Process.Start(psi)!)
        {
            p.StandardInput.WriteLine("no"); // "Do you wish to create a custom hardware profile?"
            p.StandardInput.Close();
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(config)) throw new CrossWinException("avdmanager failed:\n" + output.Trim());
        }
        // Base screen: a large 1440x3120 @560 panel, so every profile is a downscale of real pixels.
        var overrides = new Dictionary<string, string>
        {
            ["hw.lcd.width"] = "1440", ["hw.lcd.height"] = "3120", ["hw.lcd.density"] = "560",
            ["skin.name"] = "1440x3120", ["skin.path"] = "_no_skin", ["showDeviceFrame"] = "no",
            ["hw.keyboard"] = "yes", ["hw.ramSize"] = "2048", ["disk.dataPartition.size"] = "6G",
            ["hw.gpu.enabled"] = "yes", ["hw.gpu.mode"] = "auto", ["hw.camera.back"] = "emulated",
        };
        var lines = File.ReadAllLines(config).Where(l => !overrides.Keys.Any(k => l.StartsWith(k + "="))).ToList();
        lines.AddRange(overrides.Select(kv => $"{kv.Key}={kv.Value}"));
        File.WriteAllLines(config, lines);
    }

    private void BootEmulator(bool headless)
    {
        if (_sdk.Emulator is not { } emu) throw new CrossWinException("Emulator not installed: run  crosswin mobile setup");
        if (_sdk.AccelerationHint() is { } hint) Console.Error.WriteLine("warning: " + hint);
        EnsureAvd();
        var args = new List<string> { "-avd", AvdName, "-port", EmulatorPort.ToString(), "-no-boot-anim", "-netdelay", "none", "-netspeed", "full" };
        if (headless) args.AddRange(new[] { "-no-window", "-gpu", "swiftshader_indirect", "-no-audio" });
        string log = Path.Combine(Path.GetDirectoryName(_sdk.AvdHome)!, "emulator.log");
        Console.WriteLine($"Starting emulator{(headless ? " (headless)" : "")} ... log: {log}");
        StartDetached(emu, args, log, _sdk.BuildEnv());

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(4))
        {
            Thread.Sleep(2000);
            if (Shell("getprop", "sys.boot_completed").Output.Trim() == "1") break;
            if (File.Exists(log) && File.ReadAllText(log) is var text && Regex.IsMatch(text, @"FATAL|PANIC|cannot run in|failed to initialize HAX|requires hardware acceleration"))
                throw new CrossWinException("Emulator failed to start:\n" + string.Join('\n', text.Split('\n').Where(l => Regex.IsMatch(l, "FATAL|PANIC|ERROR|acceleration")).Take(8)));
        }
        if (Shell("getprop", "sys.boot_completed").Output.Trim() != "1") throw new CrossWinException($"Emulator did not finish booting within 4 minutes; see {log}");
        Shell("input", "keyevent", "82"); // dismiss the lock screen
        Console.WriteLine($"Emulator ready in {sw.Elapsed.TotalSeconds:0} s.");
    }

    /// <summary>Starts a long-running process that outlives the CLI, with output in <paramref name="log"/>.</summary>
    public static void StartDetached(string file, IEnumerable<string> args, string log, IDictionary<string, string> env)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"start \"crosswin\" /b \"{file}\" {string.Join(' ', args.Select(a => $"\"{a}\""))} > \"{log}\" 2>&1");
            foreach (var (k, v) in env) psi.Environment[k] = v;
            Process.Start(psi);
            return;
        }
        var sh = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        sh.ArgumentList.Add("-c");
        sh.ArgumentList.Add("nohup \"$@\" > \"$CROSSWIN_LOG\" 2>&1 < /dev/null &");
        sh.ArgumentList.Add("sh");
        sh.ArgumentList.Add(file);
        foreach (string a in args) sh.ArgumentList.Add(a);
        foreach (var (k, v) in env) sh.Environment[k] = v;
        sh.Environment["CROSSWIN_LOG"] = log;
        Process.Start(sh)!.WaitForExit();
    }

    public void Stop()
    {
        ResetProfile();
        if (IsEmulator) AdbCmd("emu", "kill");
    }

    // ------------------------------------------------------------------ device profiles

    /// <summary>Makes the target look like <paramref name="d"/> to apps: its pixel size and density.</summary>
    public void ApplyProfile(MobileDevice d)
    {
        Shell("wm", "size", $"{d.WidthPx}x{d.HeightPx}");
        Shell("wm", "density", d.Density.ToString());
        Shell("settings", "put", "system", "user_rotation", "0");
        Thread.Sleep(800); // SystemUI relayout
    }

    public void ResetProfile()
    {
        Shell("wm", "size", "reset");
        Shell("wm", "density", "reset");
    }

    /// <summary>Turns off system animations, so screenshots are taken of settled screens.</summary>
    public void QuietAnimations()
    {
        foreach (string k in new[] { "window_animation_scale", "transition_animation_scale", "animator_duration_scale" })
            Shell("settings", "put", "global", k, "0");
    }

    // ------------------------------------------------------------------ apps

    public (string Package, string? Activity) Install(string apk, bool grantPermissions)
    {
        var args = new List<string> { "install", "-r", "-t" };
        if (grantPermissions) args.Add("-g");
        args.Add(apk);
        var r = AdbCmd(args.ToArray());
        if (r.ExitCode != 0 || !r.Output.Contains("Success"))
        {
            if (r.Output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") && PackageOf(apk) is { } pkg)
            {
                AdbCmd("uninstall", pkg); // signed with another key before: start clean
                return Install(apk, grantPermissions);
            }
            throw new CrossWinException("adb install failed:\n" + r.Output.Trim());
        }
        return (PackageOf(apk) ?? throw new CrossWinException("Could not read the package name from the APK."), LaunchableActivity(apk));
    }

    private string? Badging(string apk) => _sdk.Aapt2 is { } aapt ? Proc.Run(aapt, new[] { "dump", "badging", apk }).Output : null;

    public string? PackageOf(string apk) => Badging(apk) is { } b && Regex.Match(b, @"package: name='([^']+)'") is { Success: true } m ? m.Groups[1].Value : null;
    private string? LaunchableActivity(string apk) =>
        Badging(apk) is { } b && Regex.Match(b, @"launchable-activity: name='([^']+)'") is { Success: true } m ? m.Groups[1].Value : null;

    public void Launch(string package, string? activity)
    {
        Shell("am", "force-stop", package);
        var r = activity != null
            ? Shell("am", "start", "-W", "-n", $"{package}/{activity}")
            : Shell("monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1");
        if (r.ExitCode != 0 || r.Output.Contains("Error")) throw new CrossWinException($"Could not start {package}:\n{r.Output.Trim()}");
    }

    public byte[] Screenshot()
    {
        var psi = Proc.Start(Adb, new[] { "-s", Serial, "exec-out", "screencap", "-p" }, _sdk.BuildEnv(), null);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        byte[] png = ms.ToArray();
        if (p.ExitCode != 0 || png.Length < 8 || png[1] != (byte)'P') throw new CrossWinException("Screenshot failed: " + err.Result.Trim());
        return png;
    }

    /// <summary>Visible labelled elements (text or accessibility label) with their centre, from the accessibility tree.</summary>
    public List<(string Label, int X, int Y)> Elements()
    {
        Shell("uiautomator", "dump", "/sdcard/crosswin-ui.xml");
        string xml = Shell("cat", "/sdcard/crosswin-ui.xml").Output;
        var list = new List<(string, int, int)>();
        foreach (Match m in Regex.Matches(xml, "<node [^>]*>"))
        {
            string node = m.Value;
            string label = new[] { "text", "content-desc" }
                .Select(a => Regex.Match(node, "\\b" + a + "=\"([^\"]*)\"").Groups[1].Value)
                .FirstOrDefault(v => v.Length > 0) ?? "";
            var b = Regex.Match(node, "bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"");
            if (label.Length == 0 || !b.Success) continue;
            label = System.Net.WebUtility.HtmlDecode(label).Replace('\n', ' ');
            int x1 = int.Parse(b.Groups[1].Value), y1 = int.Parse(b.Groups[2].Value);
            int x2 = int.Parse(b.Groups[3].Value), y2 = int.Parse(b.Groups[4].Value);
            list.Add((label, (x1 + x2) / 2, (y1 + y2) / 2));
        }
        return list;
    }

    /// <summary>
    /// The accessibility hierarchy as the same node schema as the desktop agent (depth, role, name, id, value,
    /// toggle, bounds, enabled, focused). Pixels are device pixels (same space as "tap X Y" and snap).
    /// Password fields are redacted (Android already masks them; the value is dropped anyway).
    /// </summary>
    public System.Text.Json.Nodes.JsonObject Tree()
    {
        Shell("uiautomator", "dump", "/sdcard/crosswin-ui.xml");
        string xml = Shell("cat", "/sdcard/crosswin-ui.xml").Output;
        int start = xml.IndexOf("<?xml", StringComparison.Ordinal) is var i && i >= 0 ? i : xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        if (start < 0) throw new CrossWinException("uiautomator returned no hierarchy (screen locked or a secure window?): " + xml.Trim().Split('\n')[0]);
        var doc = System.Xml.Linq.XDocument.Parse(xml[start..]);
        var nodes = new System.Text.Json.Nodes.JsonArray();
        void Walk(System.Xml.Linq.XElement e, int depth)
        {
            foreach (var child in e.Elements("node"))
            {
                string A(string n) => (string?)child.Attribute(n) ?? "";
                var b = Regex.Match(A("bounds"), @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
                string cls = A("class");
                string role = cls.Contains('.') ? cls[(cls.LastIndexOf('.') + 1)..] : cls;
                string text = A("text"), desc = A("content-desc"), rid = A("resource-id");
                bool password = A("password") == "true";
                bool editable = role.Contains("EditText");
                var n = new System.Text.Json.Nodes.JsonObject { ["depth"] = depth, ["role"] = role };
                n["name"] = editable ? desc : (desc.Length > 0 ? desc : text);
                if (rid.Length > 0) n["id"] = rid.Contains(":id/") ? rid[(rid.IndexOf(":id/") + 4)..] : rid;
                if (editable && text.Length > 0) n["value"] = password ? "<redacted>" : (text.Length > 200 ? text[..200] + "…" : text);
                if (A("checkable") == "true") n["toggle"] = A("checked") == "true" ? "On" : "Off";
                if (b.Success)
                {
                    int x1 = int.Parse(b.Groups[1].Value), y1 = int.Parse(b.Groups[2].Value);
                    n["x"] = x1; n["y"] = y1; n["w"] = int.Parse(b.Groups[3].Value) - x1; n["h"] = int.Parse(b.Groups[4].Value) - y1;
                }
                if (A("enabled") == "false") n["enabled"] = false;
                if (A("focused") == "true") n["focused"] = true;
                if (A("clickable") == "true") n["clickable"] = true;
                if (A("scrollable") == "true") n["scrollable"] = true;
                if (depth == 0) n["window"] = A("package");
                nodes.Add(n);
                Walk(child, depth + 1);
            }
        }
        Walk(doc.Root!, 0);
        return new System.Text.Json.Nodes.JsonObject { ["framework"] = "android", ["nodes"] = nodes };
    }

    /// <summary>The element labelled <paramref name="text"/> (exact, then first word, prefix, contains; case-insensitive).</summary>
    public (string Label, int X, int Y)? Find(string text)
    {
        var all = Elements();
        foreach (Func<string, bool> match in new Func<string, bool>[]
        {
            l => l.Equals(text, StringComparison.OrdinalIgnoreCase),
            l => l.Split(' ')[0].Equals(text, StringComparison.OrdinalIgnoreCase),
            l => l.StartsWith(text, StringComparison.OrdinalIgnoreCase),
            l => l.Contains(text, StringComparison.OrdinalIgnoreCase),
        })
            foreach (var e in all) if (match(e.Label)) return e;
        return null;
    }

    /// <summary>
    /// "tap X Y" or "tap TEXT" (an element's text or accessibility label), "longpress X Y|TEXT", "swipe X1 Y1 X2 Y2 [MS]",
    /// "scroll down|up", "type TEXT", "key BACK|HOME|ENTER|…", "wait MS", "wait-for TEXT", "rotate".
    /// </summary>
    public void Input(string command)
    {
        string[] p = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        string verb = p[0].ToLowerInvariant();
        string rest = p.Length > 1 ? p[1].Trim().Trim('"') : "";
        string[] n = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool numeric = n.Length == 2 && n.All(x => int.TryParse(x, out _));
        (string Label, int X, int Y) Need(string text) => Find(text)
            ?? throw new CrossWinException($"No element '{text}' on screen. Visible: {string.Join(" | ", Elements().Select(e => e.Label).Distinct().Take(25))}");
        Proc.Result? r = verb switch
        {
            "tap" or "click" when numeric => Shell("input", "tap", n[0], n[1]),
            "tap" or "click" when rest.Length > 0 => TapAt(Need(rest)),
            "longpress" when numeric => Shell("input", "swipe", n[0], n[1], n[0], n[1], "800"),
            "longpress" when rest.Length > 0 => Need(rest) is var e ? Shell("input", "swipe", $"{e.X}", $"{e.Y}", $"{e.X}", $"{e.Y}", "800") : null,
            "wait-for" => WaitFor(rest),
            "scroll" => rest == "up"
                ? Shell("input", "swipe", "540", "700", "540", "1700", "350")
                : Shell("input", "swipe", "540", "1700", "540", "700", "350"),
            "swipe" when n.Length >= 4 => Shell("input", "swipe", n[0], n[1], n[2], n[3], n.Length > 4 ? n[4] : "300"),
            // adb's input text needs spaces as %s and shell metacharacters escaped.
            "type" => Shell("input", "text", "'" + rest.Replace("'", "'\\''").Replace(" ", "%s") + "'"),
            "key" => Shell("input", "keyevent", rest.StartsWith("KEYCODE_") ? rest : "KEYCODE_" + rest.ToUpperInvariant()),
            "wait" when int.TryParse(rest, out int ms) => Wait(ms),
            "rotate" => Shell("settings", "put", "system", "user_rotation",
                Shell("settings", "get", "system", "user_rotation").Output.Trim() == "1" ? "0" : "1"),
            _ => throw new CrossWinException($"Unknown input '{command}'. Use: tap X Y | tap TEXT | longpress X Y|TEXT | swipe X1 Y1 X2 Y2 [MS] | scroll down|up | type TEXT | key BACK|HOME|ENTER | wait MS | wait-for TEXT | rotate"),
        };
        if (r is { ExitCode: not 0 }) throw new CrossWinException($"'{command}' failed: {r.Output.Trim()}");
    }

    private Proc.Result TapAt((string Label, int X, int Y) e) => Shell("input", "tap", e.X.ToString(), e.Y.ToString());

    private static Proc.Result? Wait(int ms) { Thread.Sleep(ms); return null; }

    private Proc.Result? WaitFor(string text)
    {
        for (int i = 0; i < 30; i++)
        {
            if (Find(text) != null) return null;
            Thread.Sleep(500);
        }
        throw new CrossWinException($"'{text}' did not appear within 15 s.");
    }

    /// <summary>Crash lines of <paramref name="package"/> since the app was started.</summary>
    public List<string> CrashLines(string package)
    {
        var r = AdbCmd("logcat", "-d", "-b", "crash", "-v", "brief");
        return r.Output.Split('\n').Where(l => l.Contains(package) || l.Contains("FATAL EXCEPTION")).Take(20).ToList();
    }
}
