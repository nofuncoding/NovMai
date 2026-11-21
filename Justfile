# NovMai packaging.
# Set GODOT to the Godot 4.7.2 .NET executable if it is not on PATH.
#
# Host shortcuts (picked by platform attributes):
#   just cimai            build the cimai native library for this host
#   just export           export the game for this host
#   just package          build the host distribution
#   just ffmpeg-natives   stage FFmpeg 9 libraries for the host distribution
#   just verify           verify the Linux distribution (Linux only)
#
# The explicit `*-linux` / `*-windows` recipes stay available for
# cross-target builds (for example `just package-windows` from Linux with
# mingw-w64); platform attributes describe the host, not the target.

godot := env_var_or_default("GODOT", "godot")
build_root := "build"

default:
    @just --list

# Build the cimai C parser used by the C# chart wrapper.
_build-cimai compiler shared_flags output:
    @mkdir -p .build/cimai
    {{compiler}} -std=c23 -O2 {{shared_flags}} -I native/vendor/cimai/include \
        native/vendor/cimai/src/misc/thirdparty_impl.c \
        native/vendor/cimai/src/memory.c \
        native/vendor/cimai/src/meta_parser.c \
        native/vendor/cimai/src/chart_parser.c \
        native/vendor/cimai/src/parser.c \
        -o {{output}}

cimai-linux: (_build-cimai "cc" "-fPIC -shared" ".build/cimai/libcimai.so")

# Windows cimai (cross-build: needs x86_64-w64-mingw32-gcc).
cimai-windows: (_build-cimai "x86_64-w64-mingw32-gcc" "-shared" ".build/cimai/libcimai.dll")

[linux]
cimai: cimai-linux

[windows]
cimai: cimai-windows

# Stage the FFmpeg 9 shared libraries used by the video format.
ffmpeg-natives-linux:
    @tools/stage_ffmpeg_natives.sh {{build_root}}/linux

ffmpeg-natives-windows:
    @tools/stage_ffmpeg_natives.sh --windows {{build_root}}/windows

[linux]
ffmpeg-natives: ffmpeg-natives-linux

[windows]
ffmpeg-natives: ffmpeg-natives-windows

# just does not expand {{variables}} in dependency arguments, so pass plain
# target/preset/executable names and let the body build the output path.
_export target preset executable:
    @mkdir -p {{build_root}}/{{target}} .build
    @touch {{build_root}}/.gdignore .build/.gdignore
    "{{godot}}" --headless --path . --export-release "{{preset}}" "{{build_root}}/{{target}}/{{executable}}"

export-linux: (_export "linux" "Linux" "NovMai.x86_64")

export-windows: (_export "windows" "Windows Desktop" "NovMai.exe")

[linux]
export: export-linux

[windows]
export: export-windows

# Linux distribution: executable, PCK, .NET runtime, cimai, FFmpeg 9 natives.
package-linux: cimai-linux export-linux ffmpeg-natives-linux
    cp .build/cimai/libcimai.so {{build_root}}/linux/
    @mkdir -p {{build_root}}/linux/assets/Skin {{build_root}}/linux/assets/SFX
    cp tools/assets-readme.txt {{build_root}}/linux/assets/README.txt
    @echo "Linux package: {{build_root}}/linux"

# Windows distribution (cross-buildable; needs mingw-w64 + FFMPEG_WIN_DIR).
package-windows: cimai-windows export-windows ffmpeg-natives-windows
    cp .build/cimai/libcimai.dll {{build_root}}/windows/
    @mkdir -p {{build_root}}/windows/assets/Skin {{build_root}}/windows/assets/SFX
    cp tools/assets-readme.txt {{build_root}}/windows/assets/README.txt
    @echo "Windows package: {{build_root}}/windows"

[linux]
package: package-linux

[windows]
package: package-windows

# Verify the Linux package in a clean directory (Linux only).
[linux]
verify-linux: package-linux
    @rm -rf .build/verify-linux
    @cp -a {{build_root}}/linux .build/verify-linux
    @python3 tools/make_test_assets.py .build/verify-linux/assets
    @set -e; cd .build/verify-linux; \
        timeout 60 ./NovMai.x86_64 --headless --quit-after 5 2>&1 | tee ../verify-linux.log; \
        grep -q "loaded res://assets/charts/sample/maidata.txt" ../verify-linux.log; \
        grep -q "skin loader probe ok" ../verify-linux.log; \
        grep -q "SFX loader probe ok" ../verify-linux.log; \
        echo "OK: exported Linux package loads built-in chart and external assets."

[linux]
verify: verify-linux

# Headless FFmpeg video format probe (needs GODOT + system ffmpeg).
[linux]
verify-video:
    @tools/test_ffmpeg_video.sh

clean:
    rm -rf {{build_root}} .build/cimai .build/verify-linux .build/verify-linux.log
