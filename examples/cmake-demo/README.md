# Building C with dotcc as the CMake C compiler

C is rarely compiled one file at a time by hand, so dotcc plugs into CMake as
an actual `CMAKE_C_COMPILER` — you write a plain `add_executable()`, and CMake's
own compile→link graph drives dotcc.

```
geom.h                  shared header: `struct Point` + two prototypes
geom.c                  defines manhattan() / dot()
main.c                  #includes geom.h, calls them, prints the result
CMakeLists.txt          project(... C) + add_executable(geom_app main.c geom.c)
dotcc-toolchain.cmake   wires dotcc as the C compiler
dotcc-link.sh           link helper (merge objects → build → launcher)
```

## Run it

Needs the .NET 10 SDK and a built `dotcc.dll` (`dotnet build DotCC -c Release`).
Verified under WSL (cmake 3.28, .NET 10); dotcc's IL is portable, so a
Windows-built `dotcc.dll` runs under WSL's `dotnet`.

```sh
cmake -S . -B build --toolchain dotcc-toolchain.cmake \
      -DDOTCC_DLL=/abs/path/to/DotCC/bin/Release/net10.0/dotcc.dll
cmake --build build
ctest --test-dir build --output-on-failure
#   1/1 Test #1: geom .....................   Passed
```

## How it works — LTO-style

dotcc transpiles C to .NET; it has no native per-TU codegen. So it behaves like
`-flto`: the per-file **compile** emits an *intermediate* (the TU's emitted C#,
a `.cs`/`.obj` object fragment — see `--emit=obj`), and the **link** does the
real work (merge the objects, dedup types from shared headers, build).

| stage | rule (`dotcc-toolchain.cmake`) | dotcc |
|---|---|---|
| compile `<src>` → `<obj>` | `CMAKE_C_COMPILE_OBJECT` | `dotcc --emit=obj <src> -o <obj>` |
| link `<objs>` → `<exe>` | `CMAKE_C_LINK_EXECUTABLE` | `dotcc <objs> --emit=build` + a `dotnet` launcher at `<exe>` |

Detection is skipped (`CMAKE_C_COMPILER_FORCED` — dotcc has no native ABI to
probe), and the "executable" CMake links is a tiny launcher that runs the built
.NET assembly, so `ctest`/`./build/geom_app` just work.

The object suffix is whatever CMake's platform picks (`.obj` here, like MSVC) —
dotcc keys "is this an object to link?" off "not a `.c` source", not a fixed
extension, exactly because the suffix is the build system's call.

## Limits (honest)

- **`-I` / `-D`** aren't threaded through the compile rule in this minimal
  toolchain (the demo's header is auto-found next to the `.c`). Adding
  `<INCLUDES>`/`<DEFINES>` to `CMAKE_C_COMPILE_OBJECT` is the generalization.
- **`-lfoo` native deps** would lower to `[LibraryImport]` P/Invoke (signature
  from the header prototype dotcc already parses, or inferred from the call
  site) — designed, not yet wired. See the repo's `C-SUPPORT.md`.
- Unix `dotcc-link.sh` launcher (WSL/Linux/macOS); a `.cmd` would cover native
  Windows.
