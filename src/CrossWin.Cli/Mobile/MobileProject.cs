using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrossWin.Mobile;

public enum MobileKind { Flutter, ReactNative, Expo, AndroidNative, IosNative }

/// <summary>What kind of mobile app a directory holds, and which platforms it can be built for.</summary>
public sealed record MobileProject(string Root, string Name, MobileKind Kind, bool HasAndroid, bool HasIos, List<string> Notes)
{
    public string KindLabel => Kind switch
    {
        MobileKind.Flutter => "Flutter",
        MobileKind.ReactNative => "React Native",
        MobileKind.Expo => "React Native (Expo)",
        MobileKind.AndroidNative => "Android (Kotlin/Java, Gradle)",
        MobileKind.IosNative => "iOS (Swift/Objective-C, Xcode project)",
        _ => Kind.ToString(),
    };

    /// <summary>Directories that are build outputs or caches: not mirrored, never deleted in the work copy.</summary>
    public static readonly IReadOnlySet<string> MirrorSkip = new HashSet<string>
    {
        ".git", "build", ".dart_tool", ".gradle", ".cxx", "node_modules", "Pods", "DerivedData", ".idea", ".expo",
        ".kotlin", "xcuserdata", ".crosswin",
    };

    public static MobileProject Detect(string path)
    {
        string root = Commands.SourceRoot(path);
        if (!Directory.Exists(root)) throw new CrossWinException($"No such directory: {path}");
        var notes = new List<string>();
        string name = Path.GetFileName(root.TrimEnd('/', '\\'));

        string pubspec = Path.Combine(root, "pubspec.yaml");
        if (File.Exists(pubspec) && Regex.IsMatch(File.ReadAllText(pubspec), @"^\s*sdk:\s*flutter\s*$", RegexOptions.Multiline))
        {
            var m = Regex.Match(File.ReadAllText(pubspec), @"^name:\s*(\S+)", RegexOptions.Multiline);
            if (m.Success) name = m.Groups[1].Value;
            bool android = Directory.Exists(Path.Combine(root, "android"));
            bool ios = Directory.Exists(Path.Combine(root, "ios"));
            if (!android) notes.Add("no android/ folder: run 'flutter create --platforms=android .' to add one");
            return new(root, name, MobileKind.Flutter, android, ios, notes);
        }

        string pkg = Path.Combine(root, "package.json");
        if (File.Exists(pkg))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            bool Dep(string n) => new[] { "dependencies", "devDependencies" }
                .Any(k => doc.RootElement.TryGetProperty(k, out var deps) && deps.TryGetProperty(n, out _));
            if (doc.RootElement.TryGetProperty("name", out var nm) && nm.GetString() is { } s) name = s;
            if (Dep("expo") || Dep("react-native"))
            {
                bool android = Directory.Exists(Path.Combine(root, "android"));
                bool ios = Directory.Exists(Path.Combine(root, "ios"));
                if (Dep("expo") && (!android || !ios))
                {
                    notes.Add("Expo app: native folders are generated in the work copy with 'expo prebuild' (your tree is untouched)");
                    return new(root, name, MobileKind.Expo, true, true, notes);
                }
                return new(root, name, MobileKind.ReactNative, android, ios, notes);
            }
        }

        if (FindAndroidAppModule(root) is { } module)
        {
            if (!File.Exists(Path.Combine(root, "gradlew"))) notes.Add("no gradlew wrapper: a 'gradle' on PATH is used");
            bool ios = Directory.EnumerateDirectories(root, "*.xcodeproj", SearchOption.AllDirectories).Any();
            if (ios) notes.Add("also contains an Xcode project (Kotlin Multiplatform?)");
            notes.Add($"app module: {Path.GetRelativePath(root, module)}");
            return new(root, name, MobileKind.AndroidNative, true, ios, notes);
        }

        if (FindXcodeContainer(root) is { } xc)
        {
            notes.Add($"Xcode container: {Path.GetRelativePath(root, xc)}");
            notes.Add("Swift/iOS apps can only be compiled by Apple's toolchain on macOS: use a Mac via --ios-host or the cloud backend");
            return new(root, Path.GetFileNameWithoutExtension(xc), MobileKind.IosNative, false, true, notes);
        }

        throw new CrossWinException($"{root}: no Flutter (pubspec.yaml), React Native/Expo (package.json), Android Gradle or Xcode project found.");
    }

    /// <summary>The Gradle module that applies the Android application plugin.</summary>
    public static string? FindAndroidAppModule(string root)
    {
        foreach (string f in Directory.EnumerateFiles(root, "build.gradle*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}") || f.Contains("node_modules")) continue;
            string text = File.ReadAllText(f);
            if (text.Contains("com.android.application") || text.Contains("android.application") && text.Contains("alias(libs.plugins"))
                return Path.GetDirectoryName(f);
        }
        return null;
    }

    /// <summary>Prefers a .xcworkspace (CocoaPods) over the .xcodeproj next to it.</summary>
    public static string? FindXcodeContainer(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 };
        var ws = Directory.EnumerateDirectories(dir, "*.xcworkspace", opts)
            .Where(p => !p.Contains(".xcodeproj") && !p.Contains("Pods")).OrderBy(p => p.Length).FirstOrDefault();
        return ws ?? Directory.EnumerateDirectories(dir, "*.xcodeproj", opts)
            .Where(p => !p.Contains("Pods")).OrderBy(p => p.Length).FirstOrDefault();
    }
}
