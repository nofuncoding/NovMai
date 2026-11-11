#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

# Built-in chart text is allowed. Skin/SFX and audio/video binaries are not.
bad="$(
  git ls-files |
    grep -Ei '(^|/)(skin|sfx|skins)/|\.(wav|mp3|ogg|flac|mp4)$' || true
)"

if [[ -n "$bad" ]]; then
  echo "Tracked external assets are not allowed:"
  printf '%s\n' "$bad"
  exit 1
fi

echo "OK: no tracked Skin/SFX or audio/video assets."
