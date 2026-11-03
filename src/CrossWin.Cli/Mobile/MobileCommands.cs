using System.Text.Json;

namespace CrossWin.Mobile;

/// <summary><c>crosswin mobile …</c>: preview and run Flutter, React Native and Android apps without Android Studio or Xcode.</summary>
public static class MobileCommands
{
    public const string Help = """
        crosswin mobile — try mobile apps on this computer (no Android Studio, no Xcode)

          crosswin mobile preview <repo> [--devices LIST] [--port N] [--no-open]
                Flutter: the app in your browser, side by side on iPhone, iPad, Pixel, Galaxy and moto
                screens, with iOS or Android behaviour per device. Reloads on every saved change.
          crosswin mobile setup [--phone-only] [--accept-licenses]
                Android emulator from Google's command-line tools (~2 GB, once), or with --phone-only
                just adb (~10 MB) for a real phone over USB/Wi-Fi.
          crosswin mobile run <repo> [--device ID] [--phone | --serial S] [--headless] [--release] [--grant]
                Build the APK (Flutter, React Native, Expo, Kotlin) and run it on the emulator or phone,
                shaped like the chosen device.
          crosswin mobile device <ID>          Reshape the running emulator/phone to another device; relaunch
          crosswin mobile snap [--out FILE]    Screenshot of the running Android app (PNG)
          crosswin mobile input "tap X Y" "type hello" "key BACK" "swipe X1 Y1 X2 Y2" "wait 500"
          crosswin mobile stop                 Restore the screen size and shut the emulator down
          crosswin mobile devices              List device profiles
          crosswin mobile inspect <repo>       Detect the app type and what can run here
          crosswin mobile doctor               Check Flutter, JDK, Android tools and acceleration

        LIST: device ids separated by commas, or ios, android, phones, all.
        """;

    public static int Dispatch(HostEnv env, string[] a)
    {
        if (a.Length == 0 || a[0] is "help" or "-h" or "--help") { Console.WriteLine(Help); return 0; }
        var sdk = new AndroidSdk(env);
        string? Opt(string name) => Array.IndexOf(a, name) is int i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        bool Flag(string name) => a.Contains(name);
        string Repo() => a.Length > 1 && !a[1].StartsWith("--") ? a[1] : throw new CrossWinException($"Usage: crosswin mobile {a[0]} <repo>");

        switch (a[0])
        {
            case "devices": return Devices();
            case "inspect": return Inspect(sdk, MobileProject.Detect(Repo()));
            case "doctor": return Doctor(sdk);
            case "preview":
            {
                var p = MobileProject.Detect(Repo());
                string devs = Opt("--devices") is { } list ? string.Join(",", DeviceCatalog.Resolve(list).Select(d => d.Id)) : "default";
                return new FlutterPreview(env, p).Run(Opt("--port") is { } port ? int.Parse(port) : null, !Flag("--no-open"), devs);
            }
            case "setup":
                sdk.Setup(Flag("--phone-only"), Flag("--accept-licenses"));
                return 0;
            case "run":
            {
                var p = MobileProject.Detect(Repo());
                var device = DeviceCatalog.Resolve(Opt("--device") ?? "pixel-8").Single();
                if (device.Os != MobileOs.Android)
                    throw new CrossWinException($"{device.Name} is an iOS device: use  crosswin mobile preview {Repo()} --devices {device.Id}  (Flutter).");
                string apk = AndroidBuild.Build(env, sdk, p, Flag("--release"));
                Console.WriteLine($"APK: {apk}");
                var t = AndroidTarget.Acquire(sdk, Opt("--serial"), Flag("--phone"), Flag("--headless"));
                t.ApplyProfile(device);
                var (pkg, act) = t.Install(apk, Flag("--grant"));
                t.Launch(pkg, act);
                Save(env, new Session(t.Serial, pkg, act, device.Id));
                Console.WriteLine($"Running {pkg} on {(t.IsEmulator ? "the emulator" : t.Serial)} as {device.Name} ({device.LogicalSize}).");
                if (Opt("--snap-after") is { } s && int.TryParse(s, out int secs))
                {
                    Thread.Sleep(secs * 1000);
                    Console.WriteLine("Screenshot: " + Snap(env, t, null));
                }
                foreach (string line in t.CrashLines(pkg)) Console.Error.WriteLine("  crash: " + line);
                return 0;
            }
            case "device" when a.Length > 1:
            {
                var (t, s) = Current(env, sdk);
                var d = DeviceCatalog.Resolve(a[1]).Single();
                if (d.Os != MobileOs.Android) throw new CrossWinException("The emulator reshapes to Android devices; iOS devices are in  crosswin mobile preview.");
                t.ApplyProfile(d);
                t.Launch(s.Package, s.Activity);
                Save(env, s with { Device = d.Id });
                Console.WriteLine($"Now {d.Name} ({d.LogicalSize}).");
                return 0;
            }
            case "snap":
            {
                var (t, _) = Current(env, sdk);
                Console.WriteLine(Snap(env, t, Opt("--out")));
                return 0;
            }
            case "input" when a.Length > 1:
            {
                var (t, _) = Current(env, sdk);
                foreach (string cmd in a.Skip(1)) t.Input(cmd);
                return 0;
            }
            case "stop":
            {
                var (t, _) = Current(env, sdk);
                t.Stop();
                File.Delete(SessionPath(env));
                Console.WriteLine("Stopped.");
                return 0;
            }
            default:
                throw new CrossWinException($"Unknown mobile command '{string.Join(' ', a)}'. Run 'crosswin mobile help'.");
        }
    }

    private static int Devices()
    {
        foreach (var group in DeviceCatalog.All.GroupBy(d => d.Os))
        {
            Console.WriteLine(group.Key == MobileOs.Ios ? "iPhone / iPad (preview with iOS behaviour)" : "Android (preview, emulator or phone)");
            foreach (var d in group) Console.WriteLine($"  {d.Id,-19} {d.Name,-34} {d.LogicalSize,-14} {d.WidthPx}x{d.HeightPx} px");
        }
        return 0;
    }

    private static int Inspect(AndroidSdk sdk, MobileProject p)
    {
        Console.WriteLine($"{p.Name}: {p.KindLabel}");
        Console.WriteLine($"  source   {p.Root}");
        foreach (string n in p.Notes) Console.WriteLine($"  note     {n}");
        Console.WriteLine($"  preview  {(p.Kind == MobileKind.Flutter ? "yes: crosswin mobile preview (browser, iOS + Android behaviour)" : "no (Flutter only)")}");
        Console.WriteLine($"  android  {(p.HasAndroid ? "yes: crosswin mobile run" + (sdk.Emulator == null ? " (run crosswin mobile setup first)" : "") : "no")}");
        Console.WriteLine($"  ios      {(p.Kind == MobileKind.IosNative ? "Swift/SwiftUI on Linux: not available yet (planned)" : p.Kind == MobileKind.Flutter ? "iOS behaviour in the preview" : "not on this computer")}");
        return 0;
    }

    private static int Doctor(AndroidSdk sdk)
    {
        void Line(bool? ok, string what, string detail) =>
            Console.WriteLine($"[{(ok switch { true => "ok  ", false => "miss", null => "info" })}] {what,-14} {detail}");
        string? flutter = HostEnv.FindOnPath("flutter");
        Line(flutter != null, "flutter", flutter ?? "not on PATH (needed for Flutter apps and the preview)");
        Line(HostEnv.FindOnPath("node") != null, "node", HostEnv.FindOnPath("node") ?? "not on PATH (only for React Native)");
        Line(sdk.JavaHome != null, "jdk 17+", sdk.JavaHome ?? "missing (crosswin mobile setup installs one)");
        Line(sdk.Root != null, "android sdk", sdk.Root ?? "missing (crosswin mobile setup)");
        Line(sdk.Adb != null, "adb", sdk.Adb ?? "missing");
        Line(sdk.Emulator != null, "emulator", sdk.Emulator ?? "missing (crosswin mobile setup)");
        Line(sdk.SystemImageInstalled, "system image", sdk.SystemImage + (sdk.SystemImageInstalled ? "" : " (missing)"));
        Line(sdk.Root == null ? null : sdk.LicensesAccepted, "licenses", sdk.LicensesAccepted ? "accepted" : "not accepted yet");
        if (sdk.Emulator != null) { string? h = sdk.AccelerationHint(); Line(h == null, "acceleration", h ?? "available"); }
        var phones = AndroidTarget.Connected(sdk);
        Line(null, "connected", phones.Count == 0 ? "no device" : string.Join(", ", phones.Select(p => $"{p.Serial} ({p.Model})")));
        return 0;
    }

    // ------------------------------------------------------------------ session

    public sealed record Session(string Serial, string Package, string? Activity, string Device);

    private static string SessionPath(HostEnv env) => Path.Combine(env.Home, "mobile", "session.json");

    private static void Save(HostEnv env, Session s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath(env))!);
        File.WriteAllText(SessionPath(env), JsonSerializer.Serialize(s));
    }

    private static (AndroidTarget, Session) Current(HostEnv env, AndroidSdk sdk)
    {
        if (!File.Exists(SessionPath(env))) throw new CrossWinException("Nothing is running: start an app with  crosswin mobile run <repo>.");
        var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(SessionPath(env)))!;
        return (AndroidTarget.Acquire(sdk, s.Serial, false, true), s);
    }

    private static string Snap(HostEnv env, AndroidTarget t, string? outPath)
    {
        string file = outPath ?? Path.Combine(env.Home, "mobile", $"snap-{DateTime.Now:HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllBytes(file, t.Screenshot());
        return file;
    }
}
