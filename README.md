# NovMai

Godot 4.7.2 C# front end with the [cimai](https://github.com/re-poem/cimai)
C parser as a git submodule. Chart parsing and the runtime preview no longer
contain the legacy Rust parser/FFI.

## Checkout and run

1. Initialize the submodule and build cimai:

   ```sh
   git submodule update --init --recursive
   just cimai
   ```

2. Build and run the Godot project with Godot 4.7.2 .NET.

3. The FFmpeg video format needs FFmpeg 9 shared libraries. On Linux install
   your distribution's FFmpeg 9 package, or set `NOVMAI_FFMPEG_ROOT` to a
   directory containing `libavcodec.so.63`, `libavformat.so.63`,
   `libavutil.so.61`, `libswscale.so.10` and `libswresample.so.7`.
   `just package` stages the host libraries next to the executable.

The demo loads the built-in chart at `res://assets/charts/sample/maidata.txt`,
draws the ring and notes with `Sprite2D` nodes, and drives the playhead from a
generated 120 BPM metronome through `AudioStreamPlayer` unless a chart audio
file is present.

Controls:

- `Open chart…`: choose a `maidata.txt` file.
- `Open folder…`: choose a song folder containing `maidata.txt`.
- Difficulty dropdown: switch among `inote_1` … `inote_7`.
- `O`: open the chart file dialog.
- `Space`: play / pause
- `R`: reset time
- `Left` / `Right`: seek by one second

Selecting a chart automatically picks the highest available difficulty. If
`track.mp3`, `track.wav`, or `track.ogg` is next to the chart, it is used as
the playback clock instead of the generated metronome. For scripted testing,
`NOVMAI_CHART=/absolute/path/to/maidata.txt` overrides the startup chart;
`NOVMAI_DIFFICULTY=5` forces a difficulty for scripted tests.

## Assets

Built-in chart text under `project/assets/charts/` is tracked and shipped in
the PCK. User Skin/SFX content is not tracked and is not shipped in the PCK.

External layout, next to the executable:

```text
<executable dir>/
  assets/
    Skin/
    SFX/
```

The resolver also accepts `Skin/` and `SFX/` directly at the executable root or
at the repository root during editor runs. Set `NOVMAI_EXTERNAL_ASSETS` to
a directory containing them to override discovery. The game resolves them
through absolute paths, not `res://`; `res://` in an exported build is the
read-only PCK.

Skin images are loaded with `Image.LoadFromFile` and `ImageTexture`; SFX are
loaded with `AudioStreamWav`, `AudioStreamOggVorbis` or `AudioStreamMP3`
`LoadFromFile`. The preview uses external `TapSkins`, `HoldSkins`, `SlideSkins`,
`TouchSkins` and `TouchHoldSkins` sprites when present, with generated
fallbacks. Hit-sound scheduling is still follow-up work.

## FFmpeg video

`src/Rendering/` adds an FFmpeg-backed `VideoStream` to the engine, so
`VideoStreamPlayer` can play any container/codec FFmpeg supports instead of
only Ogg Theora. The bindings are `FFmpeg.AutoGen` 9.0 (`NovMai.csproj`).

- `FFmpegVideoStream`: `VideoStream` subclass; `File` may be a `res://`,
  `user://` or absolute OS path.
- `FFmpegVideoPlayback`: demux/decode, RGBA texture upload, and decoded PCM
  pushed to `VideoStreamPlayer` through Godot 4.7's
  `VideoStreamPlayback.mix_audio`; seek/pause/loop aware.
- `FFmpegResourceFormatLoader`: registers `mp4`, `m4v`, `mov`, `mkv`,
  `webm`, `avi`, `wmv`, `flv`, `mpg`, `mpeg`, `m2v`, `ts`,
  `m2ts`, `mts`, `3gp` and `3g2` with `ResourceLoader` (Ogg Theora
  keeps using the built-in loader).
- `FFmpegMediaSource`, `FFmpegIoContext`, `IFFmpegInput`: the decoder core.
  It reads through Godot `FileAccess` via a custom FFmpeg `AVIOContext`, so
  packed `res://` files work without a temporary file.
- `FFmpegNative`, `FFmpegVideoRuntime`: locate/load the FFmpeg 9 shared
  libraries and install the format loader.
- `FFmpegVideoSupport`: optional autoload `Node` that calls
  `FFmpegVideoRuntime.Install()` for scenes that do not install it themselves.

Usage in C#:

```csharp
FFmpegVideoRuntime.Install(); // once, e.g. in an autoload or _Ready

var stream = ResourceLoader.Load<VideoStream>("res://bga.mp4");
// or, for an absolute path outside the PCK:
// var stream = FFmpegVideoStream.Create("/songs/x/bg.webm");

var player = new VideoStreamPlayer { Stream = stream, Expand = true, Loop = true };
AddChild(player);
player.Play();
```

`Main` calls `FFmpegVideoRuntime.Install()` during startup. Audio is mixed by
`VideoStreamPlayer` (volume, bus and audio track work as for Theora);
`GetStreamLength`, `StreamPosition` seeking, pause and loop are supported.
Decoding is software-only on the main thread: it is sized for background
videos, not 4K feature playback.

Development check:

```sh
GODOT_BIN=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 just verify-video
```

This generates H.264/AAC MP4 and VP9/Opus WebM fixtures with the system
`ffmpeg`, then runs the game headless through `NOVMAI_VIDEO_SELFTEST` and
checks decoding through both `FFmpegVideoStream` and the registered loader,
including texture pixels, seeking, pausing, and audio reaching an
`AudioEffectCapture` bus. Optional environment variables:
`NOVMAI_VIDEO_SELFTEST_EXPECT_SECONDS` and `NOVMAI_VIDEO_LOADER_TEST`
(`res://` path).

## Package

[Just](https://github.com/casey/just) wraps the export and packaging flow.
Set `GODOT` to the Godot 4.7.2 .NET executable if it is not on `PATH`:

```sh
GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 just package
```

`just package` (as well as `just cimai`, `just export` and
`just ffmpeg-natives`) builds for the host platform via Just's
`[linux]`/`[windows]` recipe attributes. The explicit `*-linux` /
`*-windows` recipes stay available for cross-target builds, for example
`just package-windows` from Linux with mingw-w64, because the attributes
describe the host and not the target.

This produces:

```text
build/linux/
  NovMai.x86_64
  NovMai.pck
  data_NovMai_linuxbsd_x86_64/
  libcimai.so
  libavcodec.so.63       (FFmpeg 9 shared libraries, staged by Just)
  libavformat.so.63
  libavutil.so.61
  libswscale.so.10
  libswresample.so.7
  assets/
    Skin/
    SFX/
    README.txt
```

The built-in chart is inside the PCK. `assets/` starts empty; add user Skin/SFX
beside the executable. Run `just verify` (or `just verify-linux` on Linux) to
copy the package to a clean directory, create test assets, and verify that both
the built-in chart and the external loaders work.

Windows packaging uses the same recipes:

```sh
# install a mingw-w64 toolchain providing x86_64-w64-mingw32-gcc
GODOT=/path/to/Godot_v4.7.2-stable_mono_linux.x86_64 just package-windows
```

The Windows package needs `libcimai.dll`; without mingw-w64 the native build
fails even if the Godot export itself succeeds. It also needs FFmpeg 9 shared
Windows builds: set `FFMPEG_WIN_DIR` to a directory with `avcodec-63.dll`,
`avformat-63.dll`, `avutil-61.dll`, `swscale-10.dll` and
`swresample-7.dll`. Linux packaging copies the host FFmpeg libraries, so build
a matching FFmpeg 9 shared build for each target platform before distributing.
FFmpeg libraries keep their original LGPL/GPL licensing; check the license of
the FFmpeg build you ship.

## Preview rendering

The playfield is composed of `Sprite2D` nodes under `src/Preview/PreviewStage.cs`.
The ring is a generated 2D sprite; taps use a head sprite and a faint guide
line, holds use a stretched body plus an end sprite, Touch uses four fan
sprites, and slides use sampled arrow sprites plus a moving guide star.

Hold geometry follows MajdataViewX's note-distance model: head and tail
radii are derived independently from `note.Time` and
`note.Time + note.Duration`, then the body is placed at their midpoint and
stretched. This means a long note's tail is always at its own later time
instead of being projected onto the judgement ring.

Tap/Hold/Slide timing uses the ViewX distance formula with per-timing `HSpeed`
and the `TapSpeed` setting. The preview still intentionally uses a fixed
`TapSpeed = 7.5` rather than a full settings UI.

A song folder's `bg.jpg` / `bg.png` is shown behind the ring and dimmed so the
notes stay readable. The background is loaded by absolute path and is never
packed into the PCK.

## Layout

- `src/Main.cs`: preview controller, file/folder path picker, difficulty selector, and chart audio.
- `src/Preview/PreviewStage.cs`: sprite playfield, ViewX-style distance geometry, hold body/end, Touch fans, and sampled Slide arrows/star.
- `src/Native/NovMaiChart.cs`: cimai-backed chart mapping to flat `NoteView` arrays.
- `src/Native/NativeLibraryResolver.cs`: resolves cimai beside the executable.
- `src/Assets/ExternalAssets.cs`: external Skin/SFX discovery and loaders.
- `src/Rendering/`: FFmpeg video format (`FFmpegVideoStream`,
  `FFmpegVideoPlayback`, `FFmpegResourceFormatLoader`, decoder core, native
  library loader and runtime installation).
- `tools/test_ffmpeg_video.sh`: headless video format probe (`just verify-video`).
- `tools/stage_ffmpeg_natives.sh`: stages FFmpeg 9 shared libraries for packaging.
- `native/vendor/cimai`: pinned cimai submodule (MIT, v1.1.1).
- `native/`: cimai submodule only; legacy Rust parser/FFI removed.
- `assets/charts/sample/maidata.txt`: built-in parser and rendering fixture.
- `export_presets.cfg`: Linux and Windows export presets.
- `Justfile`: cimai build, Godot export, packaging, and verification.
- `tools/check_no_assets.sh`: rejects tracked Skin/SFX or media files.
- `tools/test_external_assets.sh`: editor-side external asset probe.
- `tools/make_test_assets.py`: generates temporary package-test assets.

## Current limits

- Chart audio (`track.mp3` / `track.wav` / `track.ogg`) is loaded when it sits
  next to the selected `maidata.txt`; otherwise a metronome is generated.
- External Skin is used by the preview; SFX are discovered and probed, but
  hit-sound scheduling is not implemented yet.
- Slide rendering handles straight and circle shapes; curve/bend/fan use a
  straight-line approximation until the geometry module lands.
- Chart audio in Opus (`.opus`) is not decoded yet; cimai still parses the chart.
- External skin variants are selected where the skin provides them (Tap/Hold/Slide
  break/each/mine); Touch/TouchHold still use a reduced subset, and SV curves
  are not simulated (only `HSpeed` is applied).
- `just package` (Linux target) has been verified from an exported directory outside
  the source tree, including external `assets/Skin` and `assets/SFX`.
- Windows packaging is scripted but requires a mingw-w64 linker for cimai and
  an FFmpeg 9 shared Windows build (`FFMPEG_WIN_DIR`) for the video format.
- FFmpeg video is decoded in software on the main thread (no hardware
  acceleration, no subtitle tracks). The format has no editor `.import` step;
  resources are loaded at runtime and `*.mp4`/WebM files are only packed when
  they match the export preset's include filter (already configured).
- The FFmpeg video format requires FFmpeg 9 shared libraries at runtime. In the
  source tree they are found through the system loader path or
  `NOVMAI_FFMPEG_ROOT`; packaged builds use the copies staged next to the
  executable.
