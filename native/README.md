# native

This directory is the home of the pinned [cimai](https://github.com/re-poem/cimai)
git submodule used by the C# chart parser.

The legacy Rust chart parser and C ABI have been removed. Do not add a second
parser here; the game's active parser is cimai.

## Build

From the project root:

```sh
git submodule update --init --recursive
just cimai
```

`just cimai` compiles the C sources under `native/vendor/cimai` into
`.build/cimai/libcimai.so` (or `libcimai.dll` for the Windows recipe).
Godot's C# build consumes the bindings directly from the submodule.
