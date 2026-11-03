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
    public static readonly string[] Replaced = { "permission_handler_platform_interface", "path_provider_platform_interface" };

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
        WriteIoPackage(src);
        PatchPubspec(src, used.Where(Shimmed.Contains).ToList());
        RewriteFileAccess(src);
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

    /// <summary>The browser stand-in for the app's dart:io file use (see crosswin_io/io.dart).</summary>
    private static void WriteIoPackage(string src)
    {
        string dir = Path.Combine(src, ".crosswin", "packages", "crosswin_io");
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "pubspec.yaml"), """
            name: crosswin_io
            description: CrossWin preview stand-in for dart:io files (generated).
            publish_to: none
            environment:
              sdk: '>=3.4.0 <4.0.0'
            dependencies:
              flutter:
                sdk: flutter
            """);
        foreach (string res in new[] { "io.dart", "flutter.dart" })
        {
            using var s = typeof(PreviewFakes).Assembly.GetManifestResourceStream("crosswin_io/" + res)!;
            using var r = new StreamReader(s);
            string target = Path.Combine(dir, "lib", res);
            string text = r.ReadToEnd();
            if (!File.Exists(target) || File.ReadAllText(target) != text) File.WriteAllText(target, text);
        }
    }

    /// <summary>
    /// In the preview copy of the app's lib/: Image.file / FileImage use the storage-backed versions (Image.file
    /// refuses to run on the web) and dart:io's Platform, which throws in a browser, follows the frame. File and
    /// Directory need no change: IOOverrides routes them. Each file keeps the source's timestamp, so only real
    /// edits recompile.
    /// </summary>
    public static void RewriteFileAccess(string src)
    {
        string lib = Path.Combine(src, "lib");
        if (!Directory.Exists(lib)) return;
        foreach (string f in Directory.EnumerateFiles(lib, "*.dart", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            if (!text.Contains("Platform.") && !text.Contains("Image.file(") && !text.Contains("FileImage(") && !text.Contains("Isolate.run(")) continue;
            DateTime mtime = File.GetLastWriteTimeUtc(f);
            string t = text;
            bool images = false;
            t = Regex.Replace(t, @"(?<![\w.])Platform\.(isAndroid|isIOS|isMacOS|isWindows|isLinux|isFuchsia|operatingSystemVersion|operatingSystem|pathSeparator|localeName|numberOfProcessors|environment)\b",
                m => { images = true; return "CrossWinPlatform." + m.Groups[1].Value; });
            t = Regex.Replace(t, @"(?<![\w.])Isolate\.run(?=[<(])", _ => { images = true; return "CrossWinIsolate.run"; });
            t = Regex.Replace(t, @"(?<![\w.])Image\.file\(", _ => { images = true; return "CrossWinImage.file("; });
            t = Regex.Replace(t, @"(?<![\w.])FileImage\(", _ => { images = true; return "CrossWinFileImage("; });
            if (images)
            {
                var m = Regex.Match(t, @"^import .*;\s*$", RegexOptions.Multiline);
                const string imp = "import 'package:crosswin_io/flutter.dart';\n";
                t = m.Success ? t.Insert(m.Index + m.Length + 1, imp) : imp + t;
            }
            if (t == text) continue;
            File.WriteAllText(f, t);
            File.SetLastWriteTimeUtc(f, mtime);
        }
    }

    // ------------------------------------------------------------------ phone storage on the server

    /// <summary>GET a device's storage, PUT a file or folder, DELETE a path: /crosswin/fs/&lt;device&gt;[?path=…].</summary>
    public static bool TryServeStorage(System.Net.HttpListenerContext ctx, string storageRoot)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        const string prefix = "/crosswin/fs/";
        if (!path.StartsWith(prefix)) return false;
        string device = Regex.Replace(path[prefix.Length..], "[^A-Za-z0-9_-]", "");
        if (device.Length == 0) { ctx.Response.StatusCode = 400; return true; }
        string root = Path.Combine(storageRoot, device);
        string? Local(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            string full = Path.GetFullPath(Path.Combine(root, p.TrimStart('/')));
            return full.StartsWith(Path.GetFullPath(root)) ? full : null;
        }
        string? target = Local(ctx.Request.QueryString["path"]);
        switch (ctx.Request.HttpMethod)
        {
            case "GET":
            {
                var files = new Dictionary<string, object[]>();
                var dirs = new List<string>();
                if (Directory.Exists(root))
                {
                    foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                        dirs.Add("/" + Path.GetRelativePath(root, d).Replace('\\', '/'));
                    foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        files["/" + Path.GetRelativePath(root, f).Replace('\\', '/')] = new object[]
                        {
                            new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds(),
                            Convert.ToBase64String(File.ReadAllBytes(f)),
                        };
                }
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { dirs, files });
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.OutputStream.Write(json);
                return true;
            }
            case "PUT" when target != null:
                if (ctx.Request.QueryString["dir"] == "1") Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var fs = File.Create(target);
                    ctx.Request.InputStream.CopyTo(fs);
                }
                return true;
            case "DELETE" when target != null:
                if (File.Exists(target)) File.Delete(target);
                else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                return true;
            default:
                ctx.Response.StatusCode = 400;
                return true;
        }
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
        if (!Regex.IsMatch(text, @"^\s+crosswin_io:", RegexOptions.Multiline))
            text = Regex.Replace(text, @"^dependencies:[ \t]*\r?\n", "dependencies:\n  crosswin_io:\n    path: .crosswin/packages/crosswin_io\n", RegexOptions.Multiline);
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
        import 'dart:ui' as ui;

        import 'package:flutter/foundation.dart';
        import 'package:flutter/services.dart';
        import 'package:flutter_web_plugins/flutter_web_plugins.dart';

        import 'package:crosswin_io/io.dart' as cw;
        """ + (used.Contains("permission_handler_platform_interface")
            ? "import 'package:permission_handler_platform_interface/permission_handler_platform_interface.dart';\n" : "")
            + (used.Contains("path_provider_platform_interface")
            ? "import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';\n" : "") + """
        void installCrossWinFakes() {
        """ + (used.Contains("photo_manager") ? "  MethodChannel('com.fluttercandies/photo_manager', const StandardMethodCodec(), webPluginRegistrar).setMethodCallHandler(_photos);\n" : "")
            + (used.Contains("flutter_contacts") ? "  MethodChannel('flutter_contacts', const StandardMethodCodec(), webPluginRegistrar).setMethodCallHandler(_contacts);\n" : "")
            + (used.Contains("permission_handler_platform_interface") ? "  PermissionHandlerPlatform.instance = _CrossWinPermissions();\n" : "")
            + (used.Contains("path_provider_platform_interface") ? "  PathProviderPlatform.instance = _CrossWinPaths();\n" : "")
            + """
        }

        Map<String, dynamic> _args(MethodCall c) =>
            c.arguments is Map ? (c.arguments as Map).cast<String, dynamic>() : <String, dynamic>{};

        Future<Uint8List> _bytes(String key) async => (await rootBundle.load(key)).buffer.asUint8List();

        // ---------------------------------------------------------------- photo_manager

        // The test photos plus what the app saved to the gallery (kept in the frame's phone storage).
        const _savedDir = '/crosswin/gallery';
        const _savedIndex = '$_savedDir/saved.json';
        List<Map<String, dynamic>>? _library;

        List<Map<String, dynamic>> _saved() => cw.CrossWinFs.isFile(_savedIndex)
            ? (jsonDecode(utf8.decode(cw.CrossWinFs.read(_savedIndex))) as List).cast<Map<String, dynamic>>()
            : <Map<String, dynamic>>[];

        Future<List<Map<String, dynamic>>> _all() async {
          if (_library != null) return _library!;
          final list = (jsonDecode(await rootBundle.loadString('crosswin/photos.json')) as List).cast<Map<String, dynamic>>();
          list.addAll(_saved());
          list.sort((a, b) => (b['t'] as int).compareTo(a['t'] as int)); // newest first
          return _library = list;
        }

        Future<Uint8List?> _photoBytes(String id, {bool thumb = false}) async {
          final saved = _library?.where((p) => p['id'] == id && p['file'] != null).firstOrNull;
          if (saved != null) return saved['type'] == 2 ? null : cw.CrossWinFs.read(saved['file'] as String);
          return _bytes('crosswin/${thumb ? 'thumb' : 'photo'}/$id.jpg');
        }

        /// Puts [bytes] into the gallery like the phone would, and returns the new asset.
        Future<Map<String, dynamic>> _store(Uint8List bytes, String title, {bool video = false, int? createdMs}) async {
          final all = await _all();
          var w = 0, h = 0;
          if (!video) {
            try {
              final frame = await (await ui.instantiateImageCodec(bytes)).getNextFrame();
              w = frame.image.width;
              h = frame.image.height;
            } catch (_) {}
          }
          final id = 'saved_${DateTime.now().microsecondsSinceEpoch}';
          final ext = title.contains('.') ? title.substring(title.lastIndexOf('.')) : (video ? '.mp4' : '.png');
          final file = '$_savedDir/$id$ext';
          cw.CrossWinFs.mkdir(_savedDir, recursive: true);
          cw.CrossWinFs.write(file, bytes);
          final entry = <String, dynamic>{
            'id': id, 'title': title, 'w': w, 'h': h, 't': (createdMs ?? DateTime.now().millisecondsSinceEpoch) ~/ 1000,
            'lat': null, 'lng': null, 'file': file, 'type': video ? 2 : 1,
            'mime': video ? 'video/mp4' : (ext == '.jpg' || ext == '.jpeg' ? 'image/jpeg' : 'image/png'),
          };
          cw.CrossWinFs.write(_savedIndex, utf8.encode(jsonEncode([..._saved(), entry])));
          all.insert(0, entry);
          return _asset(entry);
        }

        Map<String, dynamic> _asset(Map<String, dynamic> p) => {
              'id': p['id'], 'type': p['type'] ?? 1, 'width': p['w'], 'height': p['h'], 'duration': 0, 'orientation': 0,
              'title': p['title'], 'subtype': 0, 'createDt': p['t'], 'modifiedDt': p['t'],
              'relativePath': p['file'] != null ? 'Pictures/' : 'DCIM/CrossWin/', 'mimeType': p['mime'] ?? 'image/jpeg',
              'lat': p['lat'], 'lng': p['lng'],
            };

        Map<String, dynamic> _album(int count, int modified) => {
              'id': 'isAll', 'name': 'Recents', 'assetCount': count, 'albumType': 1, 'isAll': true, 'modified': modified,
            };

        bool _wantsImages(Object? type) => type == null || ((type as int) & 1) != 0;

        Future<Object?> _photos(MethodCall c) async {
          final a = _args(c);
          final all = await _all();
          final type = a['type'] as int?;
          final images = all.where((p) => type == null || (((p['type'] as int? ?? 1) == 2 ? 2 : 1) & type) != 0).toList();
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
              return _photoBytes(a['id'] as String, thumb: !big);
            case 'getOriginBytes':
              return _photoBytes(a['id'] as String);
            case 'saveImage':
              return _store(a['image'] as Uint8List, (a['filename'] ?? a['title'] ?? 'image.png') as String,
                  createdMs: a['creationDate'] as int?);
            case 'saveImageWithPath':
              return _store(cw.CrossWinFs.read(a['path'] as String), (a['title'] ?? 'image.png') as String);
            case 'saveVideo':
              return _store(cw.CrossWinFs.read(a['path'] as String), (a['title'] ?? 'video.mp4') as String, video: true);
            case 'getFullFile':
              // iOS-style reads go through a file: hand out a cached copy in the frame's storage.
              final id = a['id'] as String;
              final stored = byId(id)?['file'] as String?;
              if (stored != null) return stored;
              final path = '/crosswin/cache/photo_manager/$id.jpg';
              if (!cw.CrossWinFs.isFile(path)) {
                final b = await _photoBytes(id);
                if (b == null) return null;
                cw.CrossWinFs.writeVolatile(path, b);
              }
              return path;
            case 'getLatLngAndroidQ':
              final p = byId(a['id']);
              return {'lat': p?['lat'], 'lng': p?['lng']};
            case 'systemVersion':
              return defaultTargetPlatform == TargetPlatform.iOS ? '18.0' : '36';
            case 'getTitleAsync':
              return byId(a['id'])?['title'] ?? '';
            case 'getMimeTypeAsync':
              return byId(a['id'])?['mime'] ?? 'image/jpeg';
            case 'getFileSize':
              return (await _photoBytes(a['id'] as String))?.length ?? 0;
            case 'isLocallyAvailable':
              return true;
            default:
              return null; // getFullFile, presentLimited, openSetting, releaseMemoryCache … (no files in a browser)
          }
        }

        """ + (used.Contains("permission_handler_platform_interface") ? PermissionsFake : "")
            + (used.Contains("path_provider_platform_interface") ? PathsFake : "") + """

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

    // App folders inside the preview's phone storage (crosswin_io), like an app sandbox on a phone.
    private const string PathsFake = """

        // ---------------------------------------------------------------- path_provider

        class _CrossWinPaths extends PathProviderPlatform {
          String _dir(String name) {
            final path = '${cw.CrossWinFs.root}/$name';
            cw.CrossWinDirectory(path).createSync(recursive: true);
            return path;
          }

          @override
          Future<String?> getTemporaryPath() async => _dir('tmp');
          @override
          Future<String?> getApplicationSupportPath() async => _dir('support');
          @override
          Future<String?> getLibraryPath() async => _dir('library');
          @override
          Future<String?> getApplicationDocumentsPath() async => _dir('documents');
          @override
          Future<String?> getApplicationCachePath() async => _dir('cache');
          @override
          Future<String?> getExternalStoragePath() async => _dir('external');
          @override
          Future<List<String>?> getExternalCachePaths() async => [_dir('external-cache')];
          @override
          Future<List<String>?> getExternalStoragePaths({StorageDirectory? type}) async => [_dir('external')];
          @override
          Future<String?> getDownloadsPath() async => _dir('downloads');
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
