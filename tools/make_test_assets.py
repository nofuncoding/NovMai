#!/usr/bin/env python3
"""Create a tiny valid Skin/SFX fixture for package probes."""

import math
import struct
import sys
import wave
import zlib
from pathlib import Path

root = Path(sys.argv[1])
(root / "Skin" / "TapSkins").mkdir(parents=True, exist_ok=True)
(root / "SFX").mkdir(parents=True, exist_ok=True)


def chunk(tag: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + tag
        + payload
        + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
    )


png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(b"\x00\xff\x00\x00\xff"))
png += chunk(b"IEND", b"")
(root / "Skin" / "TapSkins" / "tap.png").write_bytes(png)

with wave.open(str(root / "SFX" / "tap.wav"), "wb") as out:
    out.setnchannels(1)
    out.setsampwidth(2)
    out.setframerate(44100)
    frames = bytearray()
    for i in range(4410):
        sample = int(0.2 * 32767 * math.sin(2 * math.pi * 440 * i / 44100))
        frames += struct.pack("<h", sample)
    out.writeframes(frames)

print(f"created {root}/Skin/TapSkins/tap.png and {root}/SFX/tap.wav")
