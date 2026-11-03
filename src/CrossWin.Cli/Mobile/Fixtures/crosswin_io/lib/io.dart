// CrossWin preview: app files in the browser.
//
// The browser has no file system, so dart:io's File and Directory throw there. CrossWin installs IOOverrides, the
// official dart:io hook, so every File(...) and Directory(...) — in the app and in packages such as path_provider —
// is one of the classes below. They implement dart:io's interfaces on a per-device "phone storage" kept in memory
// (synchronous calls work) and written through to the CrossWin preview server, which stores it on the computer
// (~/.crosswin/work/…/preview-fs/<device>/). Each frame loads its storage on start, so drafts, thumbnails and caches
// survive reloads, like an app's sandbox on a phone. Generated into the preview work copy only.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io' as io;
import 'dart:js_interop';
import 'dart:js_interop_unsafe';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';

// ------------------------------------------------------------------ storage

class _Node {
  _Node(this.bytes, this.modified);
  Uint8List bytes;
  DateTime modified;
}

@JS('fetch')
external JSPromise<_Response> _fetch(JSString url, JSObject init);

extension type _Response(JSObject _) implements JSObject {
  external bool get ok;
  external JSPromise<JSString> text();
}

io.OSError _noEntry() => const io.OSError('No such file or directory', 2);

/// The device's storage. [install] runs before the app starts.
class CrossWinFs {
  static final Map<String, _Node> _files = {};
  static final Set<String> _dirs = {'/'};
  static String _device = 'default';
  static Future<void> _queue = Future.value();

  static const String root = '/crosswin';

  /// Loads the frame's storage and routes all File/Directory creation here.
  static Future<void> install(String device) async {
    io.IOOverrides.global = CrossWinIOOverrides();
    _device = device;
    _addDir(root);
    try {
      final init = JSObject()..['cache'] = 'no-store'.toJS;
      final resp = await _fetch('/crosswin/fs/$device'.toJS, init).toDart;
      if (!resp.ok) return;
      final map = jsonDecode((await resp.text().toDart).toDart) as Map<String, dynamic>;
      for (final d in (map['dirs'] as List).cast<String>()) {
        _addDir(d);
      }
      (map['files'] as Map<String, dynamic>).forEach((path, v) {
        final l = v as List;
        _files[path] = _Node(base64Decode(l[1] as String), DateTime.fromMillisecondsSinceEpoch(l[0] as int));
        _addDir(_parentOf(path));
      });
      debugPrint('crosswin: storage for $device: ${_files.length} files');
    } catch (e) {
      debugPrint('crosswin: storage not loaded ($e)');
    }
  }

  static void _send(String method, String path, {Uint8List? body, bool dir = false}) {
    final url = '/crosswin/fs/$_device?path=${Uri.encodeQueryComponent(path)}${dir ? '&dir=1' : ''}';
    _queue = _queue.then((_) async {
      try {
        final init = JSObject()..['method'] = method.toJS;
        if (body != null) init['body'] = body.toJS;
        await _fetch(url.toJS, init).toDart;
      } catch (_) {}
    });
  }

  static String normalize(String path) {
    final parts = <String>[];
    for (final p in path.replaceAll('\\', '/').split('/')) {
      if (p.isEmpty || p == '.') continue;
      if (p == '..') {
        if (parts.isNotEmpty) parts.removeLast();
      } else {
        parts.add(p);
      }
    }
    return '/${parts.join('/')}';
  }

  static String _parentOf(String path) {
    final n = normalize(path);
    final i = n.lastIndexOf('/');
    return i <= 0 ? '/' : n.substring(0, i);
  }

  static void _addDir(String dir) {
    var d = normalize(dir);
    while (_dirs.add(d) && d != '/') {
      d = _parentOf(d);
    }
  }

  static bool isFile(String p) => _files.containsKey(normalize(p));
  static bool isDir(String p) => _dirs.contains(normalize(p));

  static Uint8List read(String p) {
    final n = _files[normalize(p)];
    if (n == null) throw io.PathNotFoundException(p, _noEntry(), 'Cannot open file');
    return n.bytes;
  }

  static DateTime modified(String p) {
    final n = _files[normalize(p)];
    if (n == null) throw io.PathNotFoundException(p, _noEntry(), 'Cannot retrieve modification time');
    return n.modified;
  }

  static void write(String p, List<int> bytes, {bool append = false}) {
    final path = normalize(p);
    if (!isDir(_parentOf(path))) throw io.PathNotFoundException(p, _noEntry(), 'Cannot open file');
    final old = _files[path];
    final data = append && old != null ? Uint8List.fromList([...old.bytes, ...bytes]) : Uint8List.fromList(bytes);
    _files[path] = _Node(data, DateTime.now());
    _send('PUT', path, body: data);
  }

  /// A file kept for this session only (not sent to the computer), e.g. a cache the app can rebuild.
  static void writeVolatile(String p, List<int> bytes) {
    _addDir(_parentOf(p));
    _files[normalize(p)] = _Node(Uint8List.fromList(bytes), DateTime.now());
  }

  static void touch(String p, DateTime when) {
    final n = _files[normalize(p)];
    if (n == null) throw io.PathNotFoundException(p, _noEntry(), 'Cannot set modification time');
    n.modified = when;
  }

  static void mkdir(String p, {bool recursive = false}) {
    final path = normalize(p);
    if (isDir(path)) return;
    if (!recursive && !isDir(_parentOf(path))) throw io.PathNotFoundException(p, _noEntry(), 'Creation failed');
    _addDir(path);
    _send('PUT', path, dir: true);
  }

  static void remove(String p, {bool recursive = false, bool expectDir = false}) {
    final path = normalize(p);
    if (!expectDir && _files.remove(path) != null) {
      _send('DELETE', path);
      return;
    }
    if (!_dirs.contains(path)) throw io.PathNotFoundException(p, _noEntry(), 'Deletion failed');
    final prefix = path == '/' ? '/' : '$path/';
    final inside = _files.keys.any((k) => k.startsWith(prefix)) || _dirs.any((d) => d.startsWith(prefix));
    if (inside && !recursive) throw io.FileSystemException('Deletion failed', p, const io.OSError('Directory not empty', 39));
    _files.removeWhere((k, _) => k.startsWith(prefix));
    _dirs.removeWhere((d) => d.startsWith(prefix) || d == path);
    _dirs.add('/');
    _send('DELETE', path);
  }

  static void move(String from, String to) {
    final a = normalize(from), b = normalize(to);
    final n = _files.remove(a);
    if (n != null) {
      if (!isDir(_parentOf(b))) {
        _files[a] = n;
        throw io.PathNotFoundException(to, _noEntry(), 'Cannot rename file');
      }
      _files[b] = n;
      _send('DELETE', a);
      _send('PUT', b, body: n.bytes);
      return;
    }
    if (!_dirs.contains(a)) throw io.PathNotFoundException(from, _noEntry(), 'Rename failed');
    final moved = {for (final e in _files.entries) if (e.key.startsWith('$a/')) e.key: e.value};
    for (final e in moved.entries) {
      _files.remove(e.key);
      final dest = b + e.key.substring(a.length);
      _files[dest] = e.value;
      _send('PUT', dest, body: e.value.bytes);
    }
    final dirs = _dirs.where((d) => d == a || d.startsWith('$a/')).toList();
    _dirs.removeAll(dirs);
    for (final d in dirs) {
      _addDir(b + d.substring(a.length));
    }
    _send('DELETE', a);
  }

  static List<io.FileSystemEntity> children(String dir, {bool recursive = false}) {
    final path = normalize(dir);
    if (!_dirs.contains(path)) throw io.PathNotFoundException(dir, _noEntry(), 'Directory listing failed');
    final prefix = path == '/' ? '/' : '$path/';
    bool direct(String p) => p.startsWith(prefix) && p != path && (recursive || !p.substring(prefix.length).contains('/'));
    return [
      for (final d in _dirs) if (direct(d)) CrossWinDirectory(d),
      for (final f in _files.keys) if (direct(f)) CrossWinFile(f),
    ];
  }

  static io.FileSystemEntityType typeOf(String p) => isFile(p)
      ? io.FileSystemEntityType.file
      : isDir(p)
          ? io.FileSystemEntityType.directory
          : io.FileSystemEntityType.notFound;
}

/// Makes dart:io's File(...) / Directory(...) constructors return the storage-backed classes.
final class CrossWinIOOverrides extends io.IOOverrides {
  @override
  io.File createFile(String path) => CrossWinFile(path);
  @override
  io.Directory createDirectory(String path) => CrossWinDirectory(path);
  @override
  io.Directory getCurrentDirectory() => CrossWinDirectory(CrossWinFs.root);
  @override
  void setCurrentDirectory(String path) {}
  @override
  io.Directory getSystemTempDirectory() => CrossWinDirectory('${CrossWinFs.root}/tmp')..createSync(recursive: true);
  @override
  Future<io.FileSystemEntityType> fseGetType(String path, bool followLinks) async => CrossWinFs.typeOf(path);
  @override
  io.FileSystemEntityType fseGetTypeSync(String path, bool followLinks) => CrossWinFs.typeOf(path);
  @override
  Future<bool> fseIdentical(String path1, String path2) async => fseIdenticalSync(path1, path2);
  @override
  bool fseIdenticalSync(String path1, String path2) => CrossWinFs.normalize(path1) == CrossWinFs.normalize(path2);
}

Never _unsupported(String what) =>
    throw UnsupportedError('$what is not available in the CrossWin browser preview (run on the emulator: crosswin mobile run).');

// ------------------------------------------------------------------ File

class CrossWinFile implements io.File {
  CrossWinFile(this.path);

  @override
  final String path;

  @override
  io.File get absolute => this;
  @override
  bool get isAbsolute => path.startsWith('/');
  @override
  Uri get uri => Uri.file(path);
  @override
  io.Directory get parent => CrossWinDirectory(CrossWinFs._parentOf(path));

  @override
  Future<bool> exists() async => existsSync();
  @override
  bool existsSync() => CrossWinFs.isFile(path);

  @override
  Future<io.File> create({bool recursive = false, bool exclusive = false}) async {
    createSync(recursive: recursive, exclusive: exclusive);
    return this;
  }

  @override
  void createSync({bool recursive = false, bool exclusive = false}) {
    if (recursive) CrossWinFs.mkdir(CrossWinFs._parentOf(path), recursive: true);
    if (existsSync()) {
      if (exclusive) throw io.FileSystemException('Cannot create file', path, const io.OSError('File exists', 17));
      return;
    }
    CrossWinFs.write(path, const []);
  }

  @override
  Future<Uint8List> readAsBytes() async => readAsBytesSync();
  @override
  Uint8List readAsBytesSync() => Uint8List.fromList(CrossWinFs.read(path));
  @override
  Future<String> readAsString({Encoding encoding = utf8}) async => readAsStringSync(encoding: encoding);
  @override
  String readAsStringSync({Encoding encoding = utf8}) => encoding.decode(CrossWinFs.read(path));
  @override
  Future<List<String>> readAsLines({Encoding encoding = utf8}) async => readAsLinesSync(encoding: encoding);
  @override
  List<String> readAsLinesSync({Encoding encoding = utf8}) =>
      const LineSplitter().convert(readAsStringSync(encoding: encoding));

  bool _append(io.FileMode mode) => mode == io.FileMode.append || mode == io.FileMode.writeOnlyAppend;

  @override
  Future<io.File> writeAsBytes(List<int> bytes, {io.FileMode mode = io.FileMode.write, bool flush = false}) async {
    writeAsBytesSync(bytes, mode: mode, flush: flush);
    return this;
  }

  @override
  void writeAsBytesSync(List<int> bytes, {io.FileMode mode = io.FileMode.write, bool flush = false}) =>
      CrossWinFs.write(path, bytes, append: _append(mode));

  @override
  Future<io.File> writeAsString(String contents,
      {io.FileMode mode = io.FileMode.write, Encoding encoding = utf8, bool flush = false}) async {
    writeAsStringSync(contents, mode: mode, encoding: encoding, flush: flush);
    return this;
  }

  @override
  void writeAsStringSync(String contents,
          {io.FileMode mode = io.FileMode.write, Encoding encoding = utf8, bool flush = false}) =>
      writeAsBytesSync(encoding.encode(contents), mode: mode);

  @override
  Future<int> length() async => lengthSync();
  @override
  int lengthSync() => CrossWinFs.read(path).length;
  @override
  Future<DateTime> lastModified() async => lastModifiedSync();
  @override
  DateTime lastModifiedSync() => CrossWinFs.modified(path);
  @override
  Future<DateTime> lastAccessed() async => lastModifiedSync();
  @override
  DateTime lastAccessedSync() => lastModifiedSync();
  @override
  Future setLastModified(DateTime time) async => setLastModifiedSync(time);
  @override
  void setLastModifiedSync(DateTime time) => CrossWinFs.touch(path, time);
  @override
  Future setLastAccessed(DateTime time) async {}
  @override
  void setLastAccessedSync(DateTime time) {}

  @override
  Future<io.File> copy(String newPath) async => copySync(newPath);
  @override
  io.File copySync(String newPath) {
    CrossWinFs.write(newPath, CrossWinFs.read(path));
    return CrossWinFile(newPath);
  }

  @override
  Future<io.File> rename(String newPath) async => renameSync(newPath);
  @override
  io.File renameSync(String newPath) {
    CrossWinFs.move(path, newPath);
    return CrossWinFile(newPath);
  }

  @override
  Future<io.FileSystemEntity> delete({bool recursive = false}) async {
    deleteSync(recursive: recursive);
    return this;
  }

  @override
  void deleteSync({bool recursive = false}) => CrossWinFs.remove(path);

  @override
  Future<String> resolveSymbolicLinks() async => CrossWinFs.normalize(path);
  @override
  String resolveSymbolicLinksSync() => CrossWinFs.normalize(path);

  @override
  Stream<List<int>> openRead([int? start, int? end]) async* {
    final b = CrossWinFs.read(path);
    yield b.sublist(start ?? 0, end ?? b.length);
  }

  @override
  io.IOSink openWrite({io.FileMode mode = io.FileMode.write, Encoding encoding = utf8}) =>
      _Sink(this, _append(mode), encoding);

  @override
  dynamic noSuchMethod(Invocation invocation) => _unsupported('File.${invocation.memberName}');

  @override
  String toString() => "File: '$path'";
}

class _Sink implements io.IOSink {
  _Sink(this._file, this._appendMode, this.encoding);
  final CrossWinFile _file;
  final bool _appendMode;
  @override
  Encoding encoding;
  final BytesBuilder _buf = BytesBuilder();
  final Completer<void> _done = Completer<void>();

  @override
  void add(List<int> data) => _buf.add(data);
  @override
  void write(Object? object) => _buf.add(encoding.encode('$object'));
  @override
  void writeln([Object? object = '']) => write('$object\n');
  @override
  void writeAll(Iterable objects, [String separator = '']) => write(objects.join(separator));
  @override
  void writeCharCode(int charCode) => write(String.fromCharCode(charCode));
  @override
  void addError(Object error, [StackTrace? stackTrace]) {}
  @override
  Future addStream(Stream<List<int>> stream) => stream.forEach(_buf.add);
  @override
  Future flush() async {}
  @override
  Future close() async {
    CrossWinFs.write(_file.path, _buf.takeBytes(), append: _appendMode);
    if (!_done.isCompleted) _done.complete();
  }

  @override
  Future get done => _done.future;
}

// ------------------------------------------------------------------ Directory

class CrossWinDirectory implements io.Directory {
  CrossWinDirectory(this.path);

  @override
  final String path;

  @override
  io.Directory get absolute => this;
  @override
  bool get isAbsolute => path.startsWith('/');
  @override
  Uri get uri => Uri.directory(path);
  @override
  io.Directory get parent => CrossWinDirectory(CrossWinFs._parentOf(path));

  @override
  Future<bool> exists() async => existsSync();
  @override
  bool existsSync() => CrossWinFs.isDir(path);

  @override
  Future<io.Directory> create({bool recursive = false}) async {
    createSync(recursive: recursive);
    return this;
  }

  @override
  void createSync({bool recursive = false}) => CrossWinFs.mkdir(path, recursive: recursive);

  @override
  Future<io.Directory> createTemp([String? prefix]) async => createTempSync(prefix);
  @override
  io.Directory createTempSync([String? prefix]) {
    final d = CrossWinDirectory('$path/${prefix ?? ''}${DateTime.now().microsecondsSinceEpoch}');
    d.createSync(recursive: true);
    return d;
  }

  @override
  Stream<io.FileSystemEntity> list({bool recursive = false, bool followLinks = true}) =>
      Stream.fromIterable(listSync(recursive: recursive, followLinks: followLinks));
  @override
  List<io.FileSystemEntity> listSync({bool recursive = false, bool followLinks = true}) =>
      CrossWinFs.children(path, recursive: recursive);

  @override
  Future<io.Directory> rename(String newPath) async => renameSync(newPath);
  @override
  io.Directory renameSync(String newPath) {
    CrossWinFs.move(path, newPath);
    return CrossWinDirectory(newPath);
  }

  @override
  Future<io.FileSystemEntity> delete({bool recursive = false}) async {
    deleteSync(recursive: recursive);
    return this;
  }

  @override
  void deleteSync({bool recursive = false}) => CrossWinFs.remove(path, recursive: recursive, expectDir: true);

  @override
  Future<String> resolveSymbolicLinks() async => CrossWinFs.normalize(path);
  @override
  String resolveSymbolicLinksSync() => CrossWinFs.normalize(path);

  @override
  dynamic noSuchMethod(Invocation invocation) => _unsupported('Directory.${invocation.memberName}');

  @override
  String toString() => "Directory: '$path'";
}

// ------------------------------------------------------------------ Isolate

/// Browsers have no isolates; work the app hands to Isolate.run runs on the main thread instead.
class CrossWinIsolate {
  static Future<R> run<R>(FutureOr<R> Function() computation, {String? debugName}) async => await computation();
}

// ------------------------------------------------------------------ Platform

/// dart:io's Platform throws in a browser; this follows the frame's iOS/Android behaviour.
class CrossWinPlatform {
  static bool get isAndroid => defaultTargetPlatform == TargetPlatform.android;
  static bool get isIOS => defaultTargetPlatform == TargetPlatform.iOS;
  static bool get isMacOS => false;
  static bool get isWindows => false;
  static bool get isLinux => false;
  static bool get isFuchsia => false;
  static String get operatingSystem => isIOS ? 'ios' : 'android';
  static String get operatingSystemVersion => isIOS ? '18.0' : '16';
  static String get pathSeparator => '/';
  static String get localeName => 'en_US';
  static int get numberOfProcessors => 8;
  static Map<String, String> get environment => const {};
  static String get version => '3';
}
