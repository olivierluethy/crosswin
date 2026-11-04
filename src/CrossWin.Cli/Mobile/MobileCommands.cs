using System.Globalization;
using System.Text.Json;
using CrossWin.Mobile.Fixtures;

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
          crosswin mobile run <repo> [--device ID] [--phone | --serial S] [--headless] [--release] [--ask-permissions]
                Build the APK (Flutter, React Native, Expo, Kotlin) and run it on the emulator or phone,
                shaped like the chosen device.
                Puts test photos, videos, contacts and files on the emulator the first time (--no-seed skips);
                a real phone keeps its screen size and data unless you pass --device / --seed.
          crosswin mobile seed [options]       Test data and device state for the running emulator/phone:
                --photos N         a large library of N photos instead of the ~70 everyday ones
                --date YYYY-MM-DD  set the device date (e.g. 2026-10-31 for Halloween); --date now restores it
                --timezone ZONE    e.g. Europe/Zurich      --locale de-CH   language and region
                --location PLACE   zurich, rome, tokyo … or LAT,LON (emulator GPS)
                --photo-access full|limited|none           what the app may see of the photo library
                --no-videos --no-contacts --no-files       leave those out;  --reset removes all test data
                Your own files in ~/.crosswin/fixtures/user/ (e.g. photos with faces) are added too.
          crosswin mobile device <ID>          Reshape the running emulator/phone to another device; relaunch
          crosswin mobile snap [--out FILE]    Screenshot of the running Android app (PNG)
          crosswin mobile input "tap Skip" "tap X Y" "type hello" "key BACK" "scroll down" "wait-for Done"
                                               (tap by visible text/label, or by pixels of the snap image)
          crosswin mobile ui [--all] [--json]  The screen's elements (role, text, value, id, position) from the
                                               accessibility tree; same format as  crosswin ui
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
                var session = AndroidDiagnostics.StartSession(env, p, device, Flag("--release"));
                // Target first: its CPU architecture lets Flutter build only what this device needs.
                var t = AndroidTarget.Acquire(sdk, Opt("--serial"), Flag("--phone"), Flag("--headless"));
                string apk = AndroidBuild.Build(env, sdk, p, Flag("--release"), session, t.Abi());
                Console.WriteLine($"APK: {apk}");
                // A real phone keeps its own screen unless a profile is asked for explicitly; the emulator is
                // always shaped to the chosen device.
                bool reshape = t.IsEmulator || Opt("--device") != null;
                if (reshape) t.ApplyProfile(device);
                else device = t.NativeProfile();
                var installClock = System.Diagnostics.Stopwatch.StartNew();
                var (pkg, act) = t.Install(apk, grantPermissions: !Flag("--ask-permissions"));
                session.Event("info", "app.install", $"installed {Path.GetFileName(apk)} ({new FileInfo(apk).Length / 1048576.0:0.0} MB) in {installClock.ElapsedMilliseconds} ms",
                    e => { e["durationMs"] = installClock.ElapsedMilliseconds; e["apkBytes"] = new FileInfo(apk).Length; });
                var seeder = new AndroidSeeder(env, t);
                // Test photos/contacts/files go onto emulators by default, onto a real phone only with --seed.
                bool seed = t.IsEmulator ? !Flag("--no-seed") : Flag("--seed");
                if (seed && !seeder.Seeded)
                {
                    Console.WriteLine("Adding test data (photos, videos, contacts, files) ...");
                    seeder.Seed(new AndroidSeeder.Options(Package: pkg));
                }
                string cursor = AndroidDiagnostics.DeviceNow(t);
                var launchClock = System.Diagnostics.Stopwatch.StartNew();
                t.Launch(pkg, act);
                AndroidDiagnostics.Launched(session, t, pkg, device, cursor);
                session.Event("info", "app.started", $"am start returned after {launchClock.ElapsedMilliseconds} ms", e => e["durationMs"] = launchClock.ElapsedMilliseconds);
                Save(env, new Session(t.Serial, pkg, act, device.Id));
                Console.WriteLine($"Running {pkg} on {(t.IsEmulator ? "the emulator" : t.Serial)} as {device.Name} ({device.LogicalSize}).");
                if (Opt("--snap-after") is { } s && int.TryParse(s, out int secs))
                {
                    Thread.Sleep(secs * 1000);
                    Console.WriteLine("Screenshot: " + Snap(env, t, null));
                }
                foreach (string line in t.CrashLines(pkg)) Console.Error.WriteLine("  crash: " + line);
                Console.WriteLine("Diagnostics: crosswin session · crosswin events --level warn · crosswin mobile ui");
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
            case "seed":
            {
                AndroidTarget t;
                Session? s = File.Exists(SessionPath(env)) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(SessionPath(env))) : null;
                t = AndroidTarget.Acquire(sdk, s?.Serial, Flag("--phone"), true);
                var seeder = new AndroidSeeder(env, t);
                if (Flag("--reset")) { seeder.Remove(); Console.WriteLine("Test data removed."); return 0; }
                DateTime? date = null;
                if (Opt("--date") is { } d)
                {
                    if (d == "now") { seeder.RestoreTime(); Console.WriteLine("Automatic date and time restored."); }
                    else date = DateTime.Parse(d, CultureInfo.InvariantCulture);
                }
                Console.WriteLine($"Seeding {(t.IsEmulator ? "the emulator" : t.Serial)} ...");
                seeder.Seed(new AndroidSeeder.Options(
                    PhotoCount: Opt("--photos") is { } n ? int.Parse(n) : null,
                    Videos: !Flag("--no-videos"), Contacts: !Flag("--no-contacts"), Files: !Flag("--no-files"),
                    Date: date, TimeZone: Opt("--timezone"), Locale: Opt("--locale"), Location: Opt("--location"),
                    PhotoAccess: Opt("--photo-access"), Package: s?.Package));
                if (s != null) t.Launch(s.Package, s.Activity);
                return 0;
            }
            case "ui":
            {
                var (t, _) = Current(env, sdk);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tree = t.Tree();
                if (Flag("--json")) { Console.WriteLine(tree.ToJsonString(SessionLog.Json)); return 0; }
                int shown = 0, total = 0;
                foreach (var n in tree["nodes"]!.AsArray())
                {
                    total++;
                    string name = n!["name"]?.ToString() ?? "";
                    bool interesting = name.Length > 0 || n["value"] != null || n["toggle"] != null || n["clickable"] != null || n["scrollable"] != null;
                    if (!Flag("--all") && !interesting) continue;
                    shown++;
                    var line = new System.Text.StringBuilder(new string(' ', Math.Min(2 * n["depth"]!.GetValue<int>(), 40)));
                    line.Append(n["role"]);
                    if (name.Length > 0) line.Append($" \"{name}\"");
                    if (n["value"] is { } v) line.Append($" value=\"{v}\"");
                    if (n["toggle"] is { } tg) line.Append($" [{tg}]");
                    if (n["id"] is { } id) line.Append($" #{id}");
                    line.Append($"  @{n["x"]},{n["y"]} {n["w"]}x{n["h"]}");
                    if (n["enabled"] != null) line.Append(" disabled");
                    if (n["focused"] != null) line.Append(" focused");
                    if (n["scrollable"] != null) line.Append(" scrollable");
                    Console.WriteLine(line.ToString());
                }
                Console.WriteLine($"-- android: {shown} of {total} elements in {sw.ElapsedMilliseconds} ms; tap by label: crosswin mobile input \"tap <label>\"");
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
                var actions = a.Skip(1).ToList();
                var results = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { results = t.InputBatch(actions); }
                catch (AndroidTarget.BatchFailure ex)
                {
                    results = ex.Done.Append("ERROR " + ex.Message.Split('\n')[0]).ToList();
                    foreach (string r in results) Console.WriteLine(r);
                    AndroidDiagnostics.Interaction(env, actions, results, true, sw.ElapsedMilliseconds);
                    return 1;
                }
                foreach (string r in results) Console.WriteLine(r);
                Console.WriteLine($"-- {actions.Count} action(s) in {sw.ElapsedMilliseconds} ms");
                AndroidDiagnostics.Interaction(env, actions, results, false, sw.ElapsedMilliseconds);
                return 0;
            }
            case "stop":
            {
                var (t, current) = Current(env, sdk);
                // A real phone that was never reshaped keeps any display settings of its owner.
                t.Stop(resetProfile: t.IsEmulator || current.Device != "native");
                File.Delete(SessionPath(env));
                if (File.Exists(env.SessionFile) && File.ReadAllText(env.SessionFile).Trim() is var dir
                    && SessionLog.ReadInfo(dir)["kind"]?.ToString() == "android")
                {
                    AndroidDiagnostics.Ingest(env, dir); // keep the last logs before the target goes away
                    var log = new SessionLog(dir);
                    log.Event("info", "session.stop", "stopped on request");
                    log.SetInfo(o => { o["state"] = "stopped"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
                }
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
        var devices = AndroidTarget.All(sdk);
        if (devices.Count == 0) Line(null, "connected", "no device (phone: Settings → About phone → tap \"Build number\" 7×, then Developer options → USB debugging, and plug it in)");
        foreach (var d in devices)
        {
            string kind = d.Serial.StartsWith("emulator-") ? "emulator" : "phone";
            if (d.State == "device") Line(true, kind, $"{d.Serial} ({d.Model}) ready");
            else Line(false, kind, $"{d.Serial} {d.State}: {AndroidTarget.StateHelp(d.State) ?? "not usable yet"}");
        }
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
