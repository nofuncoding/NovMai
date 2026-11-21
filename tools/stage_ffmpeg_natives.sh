#!/usr/bin/env bash
# Copy the FFmpeg 9 shared libraries next to a packaged game executable.
#
# Usage:
#   tools/stage_ffmpeg_natives.sh <target-dir>              # host platform (Linux)
#   tools/stage_ffmpeg_natives.sh --windows <target-dir>    # Windows DLLs
#
# Sources:
#   - NOVMAI_FFMPEG_ROOT (directory containing the FFmpeg 9 libraries)
#   - system locations (Linux)
#   - FFMPEG_WIN_DIR for --windows
#
# FFmpeg.AutoGen 9 loads fixed library names (libavcodec.so.63, avcodec-63.dll,
# ...), so the files must come from an FFmpeg 9 shared build.
set -euo pipefail

mode="linux"
target=""
for arg in "$@"; do
  case "$arg" in
    --windows) mode="windows" ;;
    *) target="$arg" ;;
  esac
done

if [[ -z "$target" ]]; then
  echo "usage: $0 [--windows] <target-dir>" >&2
  exit 2
fi
mkdir -p "$target"

linux_libraries=(libavutil.so.61 libswresample.so.7 libswscale.so.10 libavcodec.so.63 libavformat.so.63)
windows_libraries=(avutil-61.dll swresample-7.dll swscale-10.dll avcodec-63.dll avformat-63.dll)

candidate_dirs=()
if [[ -n "${NOVMAI_FFMPEG_ROOT:-}" ]]; then
  candidate_dirs+=("${NOVMAI_FFMPEG_ROOT}")
fi
if [[ "$mode" == "windows" ]]; then
  if [[ -n "${FFMPEG_WIN_DIR:-}" ]]; then
    candidate_dirs+=("${FFMPEG_WIN_DIR}")
  fi
else
  candidate_dirs+=(/usr/lib /usr/lib64 /usr/local/lib /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu /lib/x86_64-linux-gnu /lib/aarch64-linux-gnu)
fi

find_library() {
  local name="$1"
  for dir in "${candidate_dirs[@]}"; do
    if [[ -f "$dir/$name" ]]; then
      printf '%s\n' "$dir/$name"
      return 0
    fi
  done
  return 1
}

if [[ "$mode" == "windows" ]]; then
  libraries=("${windows_libraries[@]}")
else
  libraries=("${linux_libraries[@]}")
fi

missing=()
for library in "${libraries[@]}"; do
  if ! source_path="$(find_library "$library")"; then
    missing+=("$library")
    continue
  fi
  cp -f "$source_path" "$target/$library"
  echo "staged $(basename "$source_path") -> $target/"
done

if (( ${#missing[@]} > 0 )); then
  echo "FAILED: missing FFmpeg 9 libraries: ${missing[*]}" >&2
  if [[ "$mode" == "windows" ]]; then
    echo "Set FFMPEG_WIN_DIR (or NOVMAI_FFMPEG_ROOT) to a directory with FFmpeg 9 shared Windows builds." >&2
  else
    echo "Install FFmpeg 9 or set NOVMAI_FFMPEG_ROOT to the directory containing those files." >&2
  fi
  exit 1
fi

if [[ "$mode" == "windows" ]]; then
  echo "OK: FFmpeg 9 Windows DLLs staged in $target."
else
  echo "OK: FFmpeg 9 shared libraries staged in $target."
  echo "Note: these are host libraries; ship a matching FFmpeg 9 shared build for other machines."
fi
