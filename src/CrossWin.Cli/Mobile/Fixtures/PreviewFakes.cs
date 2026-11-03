using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrossWin.Mobile.Fixtures;

/// <summary>
/// Device features for the browser preview, where Flutter plugins without a web implementation would fail.
///
/// Two parts, both only in the preview's own work copy:
/// 1. Plugins whose Dart code asks <c>dart:io</c> for the platform (which throws in a browser) are copied next to the
///    app and patched to ask Flutter instead, so they follow the frame's iOS/Android behaviour; a
///    <c>dependency_overrides</c> entry makes the app use the copy.
/// 2. <c>.crosswin/crosswin_fakes.dart</c> answers those plugins' method channels with CrossWin's test data — the same
///    photos (with dates and places) and contacts with birthdays that <c>crosswin mobile seed</c> puts on a device.
///    The preview server serves the data under /assets/crosswin/.
/// </summary>
public static class PreviewFakes
{
    /// <summary>Plugins answered with test data in the preview.</summary>
    public static readonly string[] Shimmed = { "photo_manager", "flutter_contacts" };

    /// <summary>Federated plugins whose platform instance the preview replaces (their web version lacks the feature).</summary>
    public static readonly string[] Replaced = { "permission_handler_platform_interface" };

    private const string Marker = "# crosswin-preview-overrides";

    // ------------------------------------------------------------------ shims

    /// <summary>Vendors and patches the shimmed plugins the app uses; returns the ones handled.</summary>
    public static List<string> Prepare(string src, string flutter)
    {
        var cfg = Path.Combine(src, ".dart_tool", "package_config.json");
        if (!File.Exists(cfg) || File.GetLastWriteTimeUtc(cfg) < File.GetLastWriteTimeUtc(Path.Combine(src, "pubspec.lock")))
        {
            var r = Proc.Run(flutter, new[] { "pub", "get" }, null, src);
            if (r.ExitCode != 0) throw new CrossWinException("flutter pub get failed:\n" + r.Output.Trim());
        }
        var used = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
        foreach (var pkg in doc.RootElement.GetProperty("packages").EnumerateArray())
        {
            string name = pkg.GetProperty("name").GetString()!;
            if (Replaced.Contains(name)) { used.Add(name); continue; }
            if (!Shimmed.Contains(name)) continue;
            string root = pkg.GetProperty("rootUri").GetString()!;
            if (root.StartsWith("file://")) root = new Uri(root).LocalPath;
            else root = Path.GetFullPath(Path.Combine(src, ".dart_tool", root));
            string dest = Path.Combine(src, ".crosswin", "packages", name);
            if (Path.GetFullPath(root) != Path.GetFullPath(dest)) Vendor(root, dest);
            used.Add(name);
        }
        PatchPubspec(src, used.Where(Shimmed.Contains).ToList());
        return used;
    }

    private static void Vendor(string from, string to)
    {
        string stamp = Path.Combine(to, ".crosswin-from");
        if (File.Exists(stamp) && File.ReadAllText(stamp) == from) return;
        if (Directory.Exists(to)) Directory.Delete(to, true);
        Commands.Mirror(from, to, new HashSet<string> { ".dart_tool", "example", "test", "build" });
        string lib = Path.Combine(to, "lib");
        File.WriteAllText(Path.Combine(lib, "crosswin_platform.dart"), """
            // Added by CrossWin for the browser preview: dart:io's Platform throws on the web, so the platform comes
            // from Flutter, which the preview sets per device frame (iOS or Android behaviour).
            import 'package:flutter/foundation.dart';

            class CrossWinPlatform {
              static bool get isAndroid => defaultTargetPlatform == TargetPlatform.android;
              static bool get isIOS => defaultTargetPlatform == TargetPlatform.iOS;
              static bool get isMacOS => false;
              static bool get isWindows => false;
              static bool get isLinux => false;
              static bool get isFuchsia => false;
              static String get operatingSystem => isIOS ? 'ios' : 'android';
              static String get operatingSystemVersion => isIOS ? '18.0' : '16';
            }
            """);
        string pkgName = Path.GetFileName(to);
        foreach (string f in Directory.EnumerateFiles(lib, "*.dart", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            string patched = Regex.Replace(text, @"(?<![\w.])Platform\.(isAndroid|isIOS|isMacOS|isWindows|isLinux|isFuchsia|operatingSystemVersion|operatingSystem)\b", "CrossWinPlatform.$1");
            if (patched == text) continue;
            // After the first import, so library/part directives stay first.
            var m = Regex.Match(patched, @"^import .*;\s*$", RegexOptions.Multiline);
            string imp = $"import 'package:{pkgName}/crosswin_platform.dart';\n";
            patched = m.Success ? patched.Insert(m.Index + m.Length + 1, imp) : imp + patched;
            File.WriteAllText(f, patched);
        }
        File.WriteAllText(stamp, from);
    }

    /// <summary>Points the shimmed packages at the patched copies and makes flutter_web_plugins importable.</summary>
    public static void PatchPubspec(string src, List<string> used)
    {
        string path = Path.Combine(src, "pubspec.yaml");
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        string text = File.ReadAllText(path);
        int cut = text.IndexOf(Marker, StringComparison.Ordinal);
        if (cut >= 0) text = text[..cut].TrimEnd() + "\n";
        if (!Regex.IsMatch(text, @"^\s+flutter_web_plugins:", RegexOptions.Multiline))
            text = Regex.Replace(text, @"^dependencies:[ \t]*\r?\n", "dependencies:\n  flutter_web_plugins:\n    sdk: flutter\n", RegexOptions.Multiline);
        var block = new System.Text.StringBuilder($"\n{Marker} (generated, preview work copy only)\n");
        bool hasOverrides = Regex.IsMatch(text, @"^dependency_overrides:", RegexOptions.Multiline);
        string entries = string.Concat(used.Select(u => $"  {u}:\n    path: .crosswin/packages/{u}\n"));
        if (hasOverrides) text = Regex.Replace(text, @"^dependency_overrides:[ \t]*\r?\n", "dependency_overrides:\n" + entries, RegexOptions.Multiline);
        else block.Append("dependency_overrides:\n").Append(entries);
        File.WriteAllText(path, text + block);
        // Keep the user's timestamp, so Flutter doesn't think dependencies changed on every reload.
        File.SetLastWriteTimeUtc(path, mtime);
    }

    // ------------------------------------------------------------------ test data served to the preview

    /// <summary>Generates (once per day) the photos, thumbnails and JSON the fakes read.</summary>
    public static string EnsureData(HostEnv env, DateTime today)
    {
        string dir = Path.Combine(env.Home, "fixtures", $"preview-{today:yyyy-MM-dd}");
        if (File.Exists(Path.Combine(dir, "photos.json"))) return dir;
        Directory.CreateDirectory(Path.Combine(dir, "photo"));
        Directory.CreateDirectory(Path.Combine(dir, "thumb"));
        var photos = TestData.Photos(today);
        Console.WriteLine($"Generating {photos.Count} test photos for the preview ...");
        Parallel.ForEach(photos, p =>
        {
            string id = Path.GetFileNameWithoutExtension(p.FileName);
            File.WriteAllBytes(Path.Combine(dir, "photo", id + ".jpg"), TestData.Render(p));
            double s = 320.0 / Math.Max(p.Width, p.Height);
            File.WriteAllBytes(Path.Combine(dir, "thumb", id + ".jpg"),
                TestData.Render(p with { Width = (int)(p.Width * s), Height = (int)(p.Height * s) }));
        });
        var manifest = photos.Select(p => new
        {
            id = Path.GetFileNameWithoutExtension(p.FileName),
            title = p.FileName,
            w = p.Width, h = p.Height,
            t = new DateTimeOffset(p.Taken).ToUnixTimeSeconds(),
            lat = p.Place.Lat, lng = p.Place.Lon,
        });
        File.WriteAllText(Path.Combine(dir, "photos.json"), JsonSerializer.Serialize(manifest));
        // flutter_contacts' JSON shape.
        var contacts = TestData.Contacts(today).Select((c, i) => new Dictionary<string, object?>
        {
            ["id"] = $"cw{i + 1}",
            ["displayName"] = $"{c.First} {c.Last}",
            ["name"] = new { first = c.First, last = c.Last },
            ["phones"] = new[] { new { number = c.Phone, normalizedNumber = c.Phone.Replace(" ", ""), label = new { label = "mobile" } } },
            ["emails"] = c.Email == null ? Array.Empty<object>() : new object[] { new { address = c.Email, label = new { label = "home" } } },
            ["events"] = new[] { new Dictionary<string, object?> { ["year"] = c.Year, ["month"] = c.Month, ["day"] = c.Day, ["label"] = new { label = "birthday" } } },
        });
        File.WriteAllText(Path.Combine(dir, "contacts.json"), JsonSerializer.Serialize(contacts));
        return dir;
    }

    /// <summary>Serves /assets/crosswin/… from the data folder; false when the path is not CrossWin's.</summary>
    public static bool TryServe(System.Net.HttpListenerContext ctx, string dataDir)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        const string prefix = "/assets/crosswin/";
        if (!path.StartsWith(prefix)) return false;
        string rel = Uri.UnescapeDataString(path[prefix.Length..]);
        string file = Path.GetFullPath(Path.Combine(dataDir, rel));
        if (!file.StartsWith(Path.GetFullPath(dataDir)) || !File.Exists(file)) { ctx.Response.StatusCode = 404; return true; }
        ctx.Response.ContentType = file.EndsWith(".json") ? "application/json" : "image/jpeg";
        ctx.Response.Headers["Cache-Control"] = "max-age=3600";
        using var fs = File.OpenRead(file);
        fs.CopyTo(ctx.Response.OutputStream);
        return true;
    }

    // ------------------------------------------------------------------ Dart side

    public static string DartFakes(IReadOnlyCollection<string> used) => """
        // Generated by CrossWin for the device preview. Not part of your project.
        // Answers device plugins' method channels with CrossWin's test data (served under /assets/crosswin/).
        import 'dart:convert';

        import 'package:flutter/foundation.dart';
        import 'package:flutter/services.dart';
        import 'package:flutter_web_plugins/flutter_web_plugins.dart';

        """ + (used.Contains("permission_handler_platform_interface")
            ? "import 'package:permission_handler_platform_interface/permission_handler_platform_interface.dart';\n\n" : "") + """
        void installCrossWinFakes() {
        """ + (used.Contains("photo_manager") ? "  MethodChannel('com.fluttercandies/photo_manager', const StandardMethodCodec(), webPluginRegistrar).setMethodCallHandler(_photos);\n" : "")
            + (used.Contains("flutter_contacts") ? "  MethodChannel('flutter_contacts', const StandardMethodCodec(), webPluginRegistrar).setMethodCallHandler(_contacts);\n" : "")
            + (used.Contains("permission_handler_platform_interface") ? "  PermissionHandlerPlatform.instance = _CrossWinPermissions();\n" : "")
            + """
        }

        Map<String, dynamic> _args(MethodCall c) =>
            c.arguments is Map ? (c.arguments as Map).cast<String, dynamic>() : <String, dynamic>{};

        Future<Uint8List> _bytes(String key) async => (await rootBundle.load(key)).buffer.asUint8List();

        // ---------------------------------------------------------------- photo_manager

        List<Map<String, dynamic>>? _library;
        Future<List<Map<String, dynamic>>> _all() async {
          final list = (jsonDecode(await rootBundle.loadString('crosswin/photos.json')) as List).cast<Map<String, dynamic>>();
          list.sort((a, b) => (b['t'] as int).compareTo(a['t'] as int)); // newest first
          return _library ??= list;
        }

        Map<String, dynamic> _asset(Map<String, dynamic> p) => {
              'id': p['id'], 'type': 1, 'width': p['w'], 'height': p['h'], 'duration': 0, 'orientation': 0,
              'title': p['title'], 'subtype': 0, 'createDt': p['t'], 'modifiedDt': p['t'],
              'relativePath': 'DCIM/CrossWin/', 'mimeType': 'image/jpeg', 'lat': p['lat'], 'lng': p['lng'],
            };

        Map<String, dynamic> _album(int count, int modified) => {
              'id': 'isAll', 'name': 'Recents', 'assetCount': count, 'albumType': 1, 'isAll': true, 'modified': modified,
            };

        bool _wantsImages(Object? type) => type == null || ((type as int) & 1) != 0;

        Future<Object?> _photos(MethodCall c) async {
          final a = _args(c);
          final all = await _all();
          final images = _wantsImages(a['type']) ? all : const <Map<String, dynamic>>[];
          Map<String, dynamic>? byId(Object? id) => all.where((p) => p['id'] == id).firstOrNull;
          switch (c.method) {
            case 'requestPermissionExtend':
            case 'getPermissionState':
              return 3; // PermissionState.authorized
            case 'getAssetPathList':
              return {'data': images.isEmpty ? [] : [_album(images.length, all.first['t'] as int)]};
            case 'fetchPathProperties':
              return {'data': [_album(images.length, all.isEmpty ? 0 : all.first['t'] as int)]};
            case 'getAssetCount':
            case 'getAssetCountFromPath':
              return images.length;
            case 'getAssetListPaged':
              final page = a['page'] as int? ?? 0, size = a['size'] as int? ?? 30;
              return {'data': images.skip(page * size).take(size).map(_asset).toList()};
            case 'getAssetListRange':
            case 'getAssetsByRange':
              final start = a['start'] as int? ?? 0, end = a['end'] as int? ?? images.length;
              return {'data': images.skip(start).take(end - start).map(_asset).toList()};
            case 'fetchEntityProperties':
              final p = byId(a['id']);
              return p == null ? null : _asset(p);
            case 'assetExists':
              return byId(a['id']) != null;
            case 'getThumb':
              final o = (a['option'] as Map?)?.cast<String, dynamic>() ?? const {};
              final big = ((o['width'] as int? ?? 0) > 640) || ((o['height'] as int? ?? 0) > 640);
              return _bytes('crosswin/${big ? 'photo' : 'thumb'}/${a['id']}.jpg');
            case 'getOriginBytes':
              return _bytes('crosswin/photo/${a['id']}.jpg');
            case 'getLatLngAndroidQ':
              final p = byId(a['id']);
              return {'lat': p?['lat'], 'lng': p?['lng']};
            case 'systemVersion':
              return defaultTargetPlatform == TargetPlatform.iOS ? '18.0' : '36';
            case 'getTitleAsync':
              return byId(a['id'])?['title'] ?? '';
            case 'getMimeTypeAsync':
              return 'image/jpeg';
            case 'getFileSize':
              return (await _bytes('crosswin/photo/${a['id']}.jpg')).length;
            case 'isLocallyAvailable':
              return true;
            default:
              return null; // getFullFile, presentLimited, openSetting, releaseMemoryCache … (no files in a browser)
          }
        }

        """ + (used.Contains("permission_handler_platform_interface") ? PermissionsFake : "") + """

        // ---------------------------------------------------------------- flutter_contacts

        Future<Object?> _contacts(MethodCall c) async {
          switch (c.method) {
            case 'permissions.check':
            case 'permissions.request':
              return 'granted';
            case 'crud.getAll':
              return jsonDecode(await rootBundle.loadString('crosswin/contacts.json')) as List;
            case 'crud.get':
              final id = _args(c)['id'];
              final all = (jsonDecode(await rootBundle.loadString('crosswin/contacts.json')) as List).cast<Map>();
              return all.where((x) => x['id'] == id).firstOrNull;
            default:
              return null;
          }
        }
        """;

    // The browser's permission_handler throws for photos, contacts and notifications: every permission is granted.
    private const string PermissionsFake = """

        // ---------------------------------------------------------------- permission_handler

        class _CrossWinPermissions extends PermissionHandlerPlatform {
          @override
          Future<PermissionStatus> checkPermissionStatus(Permission permission) async => PermissionStatus.granted;

          @override
          Future<ServiceStatus> checkServiceStatus(Permission permission) async => ServiceStatus.enabled;

          @override
          Future<bool> openAppSettings() async => true;

          @override
          Future<Map<Permission, PermissionStatus>> requestPermissions(List<Permission> permissions) async =>
              {for (final p in permissions) p: PermissionStatus.granted};

          @override
          Future<bool> shouldShowRequestPermissionRationale(Permission permission) async => false;
        }

        """;
}
