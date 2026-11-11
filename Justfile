# NovMai packaging.
# Set GODOT to the Godot 4.7.2 .NET executable if it is not on PATH.

godot := env_var_or_default("GODOT", "godot")
build_root := "build"

default:
    @just --list

# Build the cimai C parser used by the C# chart wrapper.
cimai:
    @mkdir -p .build/cimai
    cc -std=c23 -O2 -fPIC -shared -I native/vendor/cimai/include \
        native/vendor/cimai/src/misc/thirdparty_impl.c \
        native/vendor/cimai/src/memory.c \
        native/vendor/cimai/src/meta_parser.c \
        native/vendor/cimai/src/chart_parser.c \
        native/vendor/cimai/src/parser.c \
        -o .build/cimai/libcimai.so

cimai-windows:
    @mkdir -p .build/cimai
    x86_64-w64-mingw32-gcc -std=c23 -O2 -shared -I native/vendor/cimai/include \
        native/vendor/cimai/src/misc/thirdparty_impl.c \
        native/vendor/cimai/src/memory.c \
        native/vendor/cimai/src/meta_parser.c \
        native/vendor/cimai/src/chart_parser.c \
        native/vendor/cimai/src/parser.c \
        -o .build/cimai/libcimai.dll

export-linux:
    @mkdir -p {{build_root}}/linux .build
    @touch {{build_root}}/.gdignore .build/.gdignore
    "{{godot}}" --headless --path . --export-release "Linux" "{{build_root}}/linux/NovMai.x86_64"

export-windows:
    @mkdir -p {{build_root}}/windows .build
    @touch {{build_root}}/.gdignore .build/.gdignore
    "{{godot}}" --headless --path . --export-release "Windows Desktop" "{{build_root}}/windows/NovMai.exe"

# Linux distribution: executable, PCK, .NET runtime, cimai.
package-linux: cimai export-linux
    cp .build/cimai/libcimai.so {{build_root}}/linux/
    @mkdir -p {{build_root}}/linux/assets/Skin {{build_root}}/linux/assets/SFX
    cp tools/assets-readme.txt {{build_root}}/linux/assets/README.txt
    @echo "Linux package: {{build_root}}/linux"

# Windows distribution requires the x86_64-pc-windows-gnu mingw toolchain.
package-windows: cimai-windows export-windows
    cp .build/cimai/libcimai.dll {{build_root}}/windows/
    @mkdir -p {{build_root}}/windows/assets/Skin {{build_root}}/windows/assets/SFX
    cp tools/assets-readme.txt {{build_root}}/windows/assets/README.txt
    @echo "Windows package: {{build_root}}/windows"

# Copy a built Linux distribution to a clean directory and run it headless.
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

clean:
    rm -rf {{build_root}} .build/cimai .build/verify-linux .build/verify-linux.log
