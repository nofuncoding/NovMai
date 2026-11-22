#!/usr/bin/env bash
# Headless end-to-end probe for the FFmpeg video format.
#
# Generates H.264/AAC MP4 and VP9/Opus WebM fixtures, then runs the game
# headless with NOVMAI_VIDEO_SELFTEST and checks that both are decoded through
# FFmpegVideoStream (direct path) and through the registered
# ResourceFormatLoader (res:// path), including texture, seek, pause and audio.
#
# Also checks that Godot can instantiate the FFmpegVideoPlayback script with no
# constructor arguments (the engine does this when it recreates the managed
# instance for a still-alive native playback object).
#
# Requires: GODOT_BIN (or GODOT) pointing at the Godot 4.7.2 .NET executable,
# a system ffmpeg CLI with libx264/libvpx/libopus for fixture generation, and
# FFmpeg 9 shared libraries (system or NOVMAI_FFMPEG_ROOT).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT_BIN:-${GODOT:-}}"
if [[ -z "$godot" ]]; then
  echo "Set GODOT_BIN (or GODOT) to the Godot 4.7.2 .NET executable." >&2
  exit 2
fi
if [[ ! -x "$godot" ]]; then
  echo "Godot binary is not executable: $godot" >&2
  exit 2
fi

ffmpeg_bin="${FFMPEG:-ffmpeg}"
if ! command -v "$ffmpeg_bin" >/dev/null 2>&1; then
  echo "System ffmpeg is required to generate video fixtures (set FFMPEG=...)." >&2
  exit 2
fi

work="$root/.build/video-test"
rm -rf "$work"
mkdir -p "$work"
sample_dir="$root/assets/charts/sample"
mp4_fixture="$sample_dir/__ffmpeg_video_probe.mp4"
webm_fixture="$sample_dir/__ffmpeg_video_probe.webm"
playback_probe="$sample_dir/__ffmpeg_playback_probe.gd"
cleanup() {
  # Godot generates a .uid sidecar next to resources it scans; remove those too.
  rm -f "$mp4_fixture" "$mp4_fixture.uid" "$webm_fixture" "$webm_fixture.uid" \
    "$playback_probe" "$playback_probe.uid"
}
trap cleanup EXIT

echo "Generating video fixtures..."
"$ffmpeg_bin" -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=320x240:rate=30:duration=2" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=2" \
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 96k -shortest \
  "$mp4_fixture"
"$ffmpeg_bin" -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=320x180:rate=25:duration=2" \
  -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=2" \
  -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 300k -c:a libopus -shortest \
  "$webm_fixture"

export XDG_DATA_HOME="$work/xdg-data"
export XDG_CONFIG_HOME="$work/xdg-config"
export XDG_CACHE_HOME="$work/xdg-cache"
export XDG_RUNTIME_DIR="$work/runtime"
export TMPDIR="$work/tmp"
export DOTNET_CLI_HOME="$root/.build/dotnet-home"
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$XDG_RUNTIME_DIR" "$TMPDIR"

run_probe() {
  local label="$1"
  local media_path="$2"
  local loader_path="$3"
  local expected="$4"
  local log="$work/$label.log"

  echo
  echo "=== ffmpeg video probe: $label ($loader_path) ==="
  set +e
  NOVMAI_VIDEO_SELFTEST="$media_path" \
  NOVMAI_VIDEO_LOADER_TEST="$loader_path" \
  NOVMAI_VIDEO_SELFTEST_EXPECT_SECONDS="$expected" \
  timeout 120 "$godot" --headless --path "$root" --quit-after 2400 2>&1 | tee "$log"
  local status=${PIPESTATUS[0]}
  set -e

  if [[ $status -ne 0 ]]; then
    echo "FAILED: godot exited with $status for $label (log: $log)" >&2
    exit 1
  fi
  if ! grep -q "ffmpeg video probe ok" "$log"; then
    echo "FAILED: success marker missing for $label (log: $log)" >&2
    exit 1
  fi
  if grep -q "ffmpeg video probe check failed\|ffmpeg video probe failed" "$log"; then
    echo "FAILED: check failures reported for $label (log: $log)" >&2
    exit 1
  fi
}

# Regression check for the engine attempting a native/script instantiation with
# no arguments (Godot logs "does not define a parameterless constructor" and
# drops the script instance when the class has no such constructor).
cat > "$playback_probe" <<'GDSCRIPT'
extends SceneTree

func _initialize() -> void:
	var script: Script = load("res://src/Rendering/FFmpegVideoPlayback.cs")
	if script == null:
		push_error("playback script instantiation probe: could not load FFmpegVideoPlayback.cs")
		quit(1)
		return
	var instance: Object = script.new()
	if instance == null:
		push_error("playback script instantiation probe: script.new() returned null (missing parameterless constructor?)")
		quit(1)
		return
	print("playback script instantiation ok")
	quit(0)
GDSCRIPT

echo
echo "=== playback script instantiation probe ==="
set +e
timeout 120 "$godot" --headless --path "$root" --script "res://assets/charts/sample/__ffmpeg_playback_probe.gd" 2>&1 | tee "$work/playback_new.log"
probe_status=${PIPESTATUS[0]}
set -e
if [[ $probe_status -ne 0 ]] || ! grep -q "playback script instantiation ok" "$work/playback_new.log"; then
  echo "FAILED: playback script instantiation (missing parameterless constructor?)" >&2
  exit 1
fi

run_probe "h264_aac_mp4" "$mp4_fixture" "res://assets/charts/sample/__ffmpeg_video_probe.mp4" "2.0"
run_probe "vp9_opus_webm" "$webm_fixture" "res://assets/charts/sample/__ffmpeg_video_probe.webm" "2.0"

echo
echo "OK: FFmpeg video format probe passed (MP4/H.264+AAC and WebM/VP9+Opus)."
