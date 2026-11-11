#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
godot_bin="${GODOT_BIN:-}"
if [[ -z "$godot_bin" ]]; then
  echo "Set GODOT_BIN to the Godot 4.7.2 .NET executable to run this probe." >&2
  exit 2
fi

work="$repo_root/.build/external-assets-test"
rm -rf "$work"
mkdir -p "$work/Skin/TapSkins" "$work/SFX"

python3 - "$work" <<'PY'
import math, struct, sys, wave, zlib
from pathlib import Path

root = Path(sys.argv[1])

def chunk(tag, payload):
    return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

# Valid 1x1 opaque RGBA PNG.
png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(b"\x00\xff\x00\x00\xff"))
png += chunk(b"IEND", b"")
(root / "Skin" / "TapSkins" / "tap.png").write_bytes(png)

# 0.1 s 440 Hz mono 16-bit WAV.
with wave.open(str(root / "SFX" / "tap.wav"), "wb") as out:
    out.setnchannels(1)
    out.setsampwidth(2)
    out.setframerate(44100)
    frames = bytearray()
    for i in range(4410):
        sample = int(0.2 * 32767 * math.sin(2 * math.pi * 440 * i / 44100))
        frames += struct.pack("<h", sample)
    out.writeframes(frames)
PY

output="$(NOVMAI_EXTERNAL_ASSETS="$work" XDG_DATA_HOME="$repo_root/.build/godot-data"   DOTNET_CLI_HOME="$repo_root/.build/dotnet-home"   DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1   NUGET_PACKAGES="$repo_root/.build/nuget"   timeout 60 "$godot_bin" --headless --path "$repo_root" --quit-after 5 2>&1)"

printf '%s\n' "$output"
grep -q "skin loader probe ok" <<<"$output"
grep -q "SFX loader probe ok" <<<"$output"
grep -q "loaded res://assets/charts/sample/maidata.txt" <<<"$output"
echo "OK: external asset probe passed."
