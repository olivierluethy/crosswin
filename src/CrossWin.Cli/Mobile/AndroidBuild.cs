namespace CrossWin.Mobile;

/// <summary>Builds an installable APK from a Flutter, React Native/Expo or Gradle project in an isolated work copy.</summary>
public static class AndroidBuild
{
    public static string WorkDir(HostEnv env, MobileProject p) =>
        Path.Combine(env.Home, "work", $"mobile-{p.Name}-{Commands.ShortHash(p.Root)}");

    /// <summary>Mirrors the project and builds it; returns the APK path.</summary>
    public static string Build(HostEnv env, AndroidSdk sdk, MobileProject p, bool release, SessionLog? session = null, string? abi = null)
    {
        if (!p.HasAndroid) throw new CrossWinException($"{p.Name} has no Android target ({string.Join("; ", p.Notes)}).");
        if (sdk.Root == null) throw new CrossWinException("Android SDK missing: run  crosswin mobile setup");
        if (sdk.JavaHome == null) throw new CrossWinException("JDK 17+ missing: run  crosswin mobile setup");

        string work = WorkDir(env, p);
        string src = Path.Combine(work, "src");
        Console.WriteLine($"Mirroring {p.Root} -> {src}");
        Commands.Mirror(p.Root, src, MobileProject.MirrorSkip);
        string log = Path.Combine(work, "build.log");
        var benv = sdk.BuildEnv();
        Console.WriteLine($"Building {p.KindLabel} APK ({(release ? "release" : "debug")}) ... log: {log}");
        // Compiler paths point into the work copy (or are relative to it): report them in the user's tree.
        string MapPath(string f) => Path.IsPathRooted(f) ? Commands.MapToSource(f, src, p.Root) : Path.Combine(p.Root, f);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        session?.Event("info", "build.start", $"{p.KindLabel} APK ({(release ? "release" : "debug")})");

        using var w = new StreamWriter(log);
        int Run(string file, IEnumerable<string> args, string cwd)
        {
            w.WriteLine($"$ {file} {string.Join(' ', args)}   (in {cwd})");
            return Proc.Stream(file, args, line =>
            {
                w.WriteLine(line);
                session?.ToolchainLine(line, MapPath);
                string t = line.Trim();
                if (t.StartsWith("✓") || t.StartsWith("BUILD ") || t.Contains("error:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("FAILURE") || t.StartsWith("e: "))
                    Console.WriteLine("    " + t);
            }, benv, cwd);
        }
        void Check(int code, string what)
        {
            if (code == 0) return;
            w.Flush();
            session?.Event("error", "build.failed", $"{what} failed (exit {code}) after {clock.Elapsed.TotalSeconds:0.0}s; full log: {log}",
                e => e["durationMs"] = clock.ElapsedMilliseconds);
            session?.SetInfo(o => { o["state"] = "build-failed"; o["endedAt"] = DateTime.UtcNow.ToString("O"); });
            var tail = File.ReadLines(log).TakeLast(25);
            throw new CrossWinException($"{what} failed (exit {code}). Last lines of {log}:\n  " + string.Join("\n  ", tail));
        }

        string Done(string apk)
        {
            session?.Event("info", "build.ok", $"build succeeded in {clock.Elapsed.TotalSeconds:0.0}s", e =>
            {
                e["durationMs"] = clock.ElapsedMilliseconds;
                e["artifact"] = apk;
                e["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(apk))).ToLowerInvariant();
            });
            return apk;
        }

        switch (p.Kind)
        {
            case MobileKind.Flutter:
            {
                string flutter = HostEnv.FindOnPath("flutter")
                    ?? throw new CrossWinException("Flutter SDK not found on PATH. Install it (https://docs.flutter.dev/get-started/install) — Android Studio is not needed.");
                var args = new List<string> { "build", "apk", release ? "--release" : "--debug" };
                // One ABI instead of all four: smaller APK, faster build and install.
                string? platform = abi switch
                {
                    "arm64-v8a" => "android-arm64", "armeabi-v7a" => "android-arm", "x86_64" => "android-x64", _ => null,
                };
                if (platform != null) { args.Add("--target-platform"); args.Add(platform); }
                Check(Run(flutter, args, src), "flutter build apk");
                return Done(Newest(Path.Combine(src, "build", "app", "outputs", "flutter-apk"), release ? "app-release.apk" : "app-debug.apk"));
            }
            case MobileKind.ReactNative:
            case MobileKind.Expo:
            {
                NodeInstall(src, Run, Check);
                if (!Directory.Exists(Path.Combine(src, "android")))
                {
                    string npx = HostEnv.FindOnPath("npx") ?? throw new CrossWinException("npx (Node.js) not found on PATH.");
                    Check(Run(npx, new[] { "expo", "prebuild", "--platform", "android", "--no-install" }, src), "expo prebuild");
                }
                // Release bundles the JavaScript, so the app starts without a Metro server; the templates sign it
                // with the debug keystore.
                string android = Path.Combine(src, "android");
                Check(Run(Gradlew(android), new[] { "assembleRelease", "--console=plain" }, android), "gradle assemble");
                return Done(Newest(Path.Combine(android, "app", "build", "outputs", "apk"), "*.apk"));
            }
            case MobileKind.AndroidNative:
            {
                string module = MobileProject.FindAndroidAppModule(src) ?? throw new CrossWinException("Android app module not found in the work copy.");
                string task = (Path.GetRelativePath(src, module) is var rel && rel != "." ? ":" + rel.Replace(Path.DirectorySeparatorChar, ':') + ":" : "")
                    + (release ? "assembleRelease" : "assembleDebug");
                Check(Run(Gradlew(src), new[] { task, "--console=plain" }, src), "gradle " + task);
                return Done(Newest(Path.Combine(module, "build", "outputs", "apk"), "*.apk"));
            }
            default:
                throw new CrossWinException($"{p.KindLabel} cannot be built for Android.");
        }
    }

    private static void NodeInstall(string src, Func<string, IEnumerable<string>, string, int> run, Action<int, string> check)
    {
        if (Directory.Exists(Path.Combine(src, "node_modules")) &&
            File.GetLastWriteTimeUtc(Path.Combine(src, "node_modules")) >= File.GetLastWriteTimeUtc(Path.Combine(src, "package.json")))
            return;
        (string tool, string[] args) = File.Exists(Path.Combine(src, "yarn.lock")) && HostEnv.FindOnPath("yarn") != null
            ? ("yarn", new[] { "install", "--frozen-lockfile" })
            : File.Exists(Path.Combine(src, "pnpm-lock.yaml")) && HostEnv.FindOnPath("pnpm") != null
                ? ("pnpm", new[] { "install", "--frozen-lockfile" })
                : ("npm", File.Exists(Path.Combine(src, "package-lock.json")) ? new[] { "ci" } : new[] { "install" });
        string exe = HostEnv.FindOnPath(tool) ?? throw new CrossWinException("Node.js/npm not found on PATH (needed for React Native).");
        check(run(exe, args, src), tool + " install");
        Directory.SetLastWriteTimeUtc(Path.Combine(src, "node_modules"), DateTime.UtcNow);
    }

    private static string Gradlew(string dir)
    {
        string wrapper = Path.Combine(dir, AndroidSdk.IsWindows ? "gradlew.bat" : "gradlew");
        if (File.Exists(wrapper))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(wrapper, File.GetUnixFileMode(wrapper) | UnixFileMode.UserExecute);
            return wrapper;
        }
        return HostEnv.FindOnPath("gradle") ?? throw new CrossWinException($"No Gradle wrapper in {dir} and no 'gradle' on PATH.");
    }

    private static string Newest(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) throw new CrossWinException($"Build finished but {dir} does not exist.");
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                   .Where(f => !f.EndsWith("-unsigned.apk") && !f.Contains("androidTest"))
                   .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
               ?? throw new CrossWinException($"No APK ({pattern}) found under {dir}.");
    }
}
