// CrossWin preview: Image.file and FileImage for files in the preview's phone storage (see io.dart).
library;

import 'dart:io' as io;
import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

export 'io.dart' show CrossWinIsolate, CrossWinPlatform;

/// Like FileImage, reading from CrossWin's storage. Equal for the same path, so `evict()` works the same way.
@immutable
class CrossWinFileImage extends ImageProvider<CrossWinFileImage> {
  const CrossWinFileImage(this.file, {this.scale = 1.0});

  final io.File file;
  final double scale;

  @override
  Future<CrossWinFileImage> obtainKey(ImageConfiguration configuration) => SynchronousFuture<CrossWinFileImage>(this);

  @override
  ImageStreamCompleter loadImage(CrossWinFileImage key, ImageDecoderCallback decode) => MultiFrameImageStreamCompleter(
        codec: _load(key, decode),
        scale: key.scale,
        debugLabel: key.file.path,
      );

  Future<ui.Codec> _load(CrossWinFileImage key, ImageDecoderCallback decode) async {
    final bytes = await key.file.readAsBytes();
    if (bytes.isEmpty) {
      PaintingBinding.instance.imageCache.evict(key);
      throw StateError('${key.file.path} is empty and cannot be loaded as an image.');
    }
    return decode(await ui.ImmutableBuffer.fromUint8List(bytes));
  }

  @override
  bool operator ==(Object other) => other is CrossWinFileImage && other.file.path == file.path && other.scale == scale;

  @override
  int get hashCode => Object.hash(file.path, scale);

  @override
  String toString() => 'CrossWinFileImage("${file.path}", scale: $scale)';
}

/// Image.file's signature, backed by [CrossWinFileImage].
class CrossWinImage {
  static Image file(
    io.File file, {
    Key? key,
    double scale = 1.0,
    ImageFrameBuilder? frameBuilder,
    ImageErrorWidgetBuilder? errorBuilder,
    String? semanticLabel,
    bool excludeFromSemantics = false,
    double? width,
    double? height,
    Color? color,
    Animation<double>? opacity,
    BlendMode? colorBlendMode,
    BoxFit? fit,
    AlignmentGeometry alignment = Alignment.center,
    ImageRepeat repeat = ImageRepeat.noRepeat,
    Rect? centerSlice,
    bool matchTextDirection = false,
    bool gaplessPlayback = false,
    bool isAntiAlias = false,
    FilterQuality filterQuality = FilterQuality.medium,
    int? cacheWidth,
    int? cacheHeight,
  }) =>
      Image(
        key: key,
        image: ResizeImage.resizeIfNeeded(cacheWidth, cacheHeight, CrossWinFileImage(file, scale: scale)),
        frameBuilder: frameBuilder,
        errorBuilder: errorBuilder,
        semanticLabel: semanticLabel,
        excludeFromSemantics: excludeFromSemantics,
        width: width,
        height: height,
        color: color,
        opacity: opacity,
        colorBlendMode: colorBlendMode,
        fit: fit,
        alignment: alignment,
        repeat: repeat,
        centerSlice: centerSlice,
        matchTextDirection: matchTextDirection,
        gaplessPlayback: gaplessPlayback,
        isAntiAlias: isAntiAlias,
        filterQuality: filterQuality,
      );
}
