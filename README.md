# dotcc

[![ci](https://github.com/sebgod/dotcc/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sebgod/dotcc/actions/workflows/dotnet.yml)
[![lua](https://github.com/sebgod/dotcc/actions/workflows/lua.yml/badge.svg)](https://github.com/sebgod/dotcc/actions/workflows/lua.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**dotcc** is a clang-like C compiler frontend that compiles C to **.NET 10 / C# 14** (AOT-clean, `unsafe` C#) — and, as a second backend, to **WebAssembly text** (`--target=wat`).

It is real enough to compile the **full Lua 5.5 interpreter** — core, stdlib, and the standalone `lua` binary — into a .NET program that passes Lua's own upstream conformance suite (`testes/all.lua`, including bytecode dump/undump round-trips). That run is CI: every push transpiles Lua, Roslyn-compiles it, and greps for `final OK !!!`.

```c
#include <stdio.h>

int main(void) {
    printf("Hello from C, running on .NET!\n");
    return 0;
}
```

```bash
dotnet run --project DotCC -c Release -- examples/hello/main.c examples/hello/math.c -o build/
cd build && dotnet run
```

## Why

- **One source of truth, two worlds.** The same `.c` file compiles under both `dotcc` and `clang -std=c99` with equivalent observable behavior. The grammar is a strict subset of real C — no invented keywords, no dialect of its own.
- **Modern output, any input dialect.** C enums become real C# enums, strings become pinned UTF-8 `u8` literals, `malloc`/`free` pairs that never escape get promoted to stack values. `-std=c90` through `-std=c23` all emit the same modern C#.
- **AOT-clean by construction.** The compiler library is `IsAotCompatible`, the frontend publishes with NativeAOT, and emitted programs need nothing but the embedded libc-shaped runtime.
- **Grammar-driven.** The whole frontend is generated from one YAML grammar by [SharpAstro.LALR.CC](https://github.com/sharpastro/LALR.CC) — add a production and the typed AST + visitor surface update in lockstep at build time.

## What's supported

Essentially **all of C89/C99, and most of C11/C23** that maps cleanly onto .NET: the full preprocessor (function-like macros, `##`/`#`, `#if` expression evaluation), structs/unions/enums/bit-fields, function pointers, `goto`, `setjmp`/`longjmp` (common shapes), `volatile` and `_Atomic` lowered faithfully, variadic functions with `<stdarg.h>`, and 22 libc headers — including real `FILE*` I/O, the complete `<math.h>`/`<tgmath.h>`, and a clean-room software **IEEE-754 binary128** for `_Float128` validated bit-for-bit against gcc.

The feature-by-feature tracker — every lexical form, type, operator, statement, libc function, and what's partial or out of scope — lives in **[C-SUPPORT.md](C-SUPPORT.md)**. There are no known silent miscompiles: every gap fails loudly at compile time.

Out of scope by design: VLAs, wide string literals, trigraphs/digraphs, `_BitInt(N)`, GNU extensions.

## Usage

dotcc speaks clang's dialect of command line:

```bash
dotcc a.c b.c                      # whole-program: Program.cs + csproj → ./a.out-cs/
dotcc a.c b.c --emit=file > out.cs # single .NET 10 file-based program (dotnet run out.cs)
dotcc a.c b.c -c -o build/         # compile to a .NET assembly (--emit=build)
dotcc a.c --emit=obj -o a.cs       # separate compilation: one .c → one .cs object fragment
dotcc a.cs b.cs -o app             # ...link object fragments back into a program
dotcc -E a.c                       # preprocess only
dotcc a.c --target=wat -o a.wat    # WebAssembly text backend
dotcc lib.c -shared                # native shared library via [UnmanagedCallersOnly] exports
```

| Flag | Meaning |
|---|---|
| `-o <path>` | Output file or directory (inferred from `--emit` when omitted, and vice versa) |
| `--emit=` | `csproj` (default) / `file` / `build` / `obj` |
| `--target=` | `cs` (default) / `wat` — WebAssembly text module |
| `-std=` | `c90` `c99` `c11` `c17` (default) `c18` `c23` — sets `__STDC_VERSION__`, drives keyword promotion |
| `-pedantic` / `-pedantic-errors` | Diagnose features newer than the selected `-std=` (gcc model) |
| `-I` / `-D` | Header search dirs / predefined macros, repeatable |
| `-MD` `-MMD` `-MF` `-MT` | Make-style header dependency files (CMake/Ninja-ready) |
| `-Wconversion` | Warn on implicit narrowing integer conversions |
| `-shared` | Shared library: NativeAOT csproj exporting non-static functions C-callably |
| `-E` | Preprocess only |

Separate compilation means dotcc drops into existing build systems — `examples/cmake-demo/` drives it from CMake one file at a time.

## How it works

A straight pull-pipeline, generated and driven by LALR.CC:

```
.c → lexer → preprocessor → macro expander → dialect keyword rewriter
   → typedef rewriter ("the lexer hack") → LALR(1) parser
   → typed-IR emitter → C# program shell   (or → WAT module)
```

The grammar lives in [`DotCC.Lib/c.lalr.yaml`](DotCC.Lib/c.lalr.yaml); a source generator turns it into a typed AST and an `IVisitor` interface at build time, so grammar and emitter can never drift apart. Emitted programs link against `DotCC.Libc`, a libc-shaped runtime where each function routes to the obvious BCL primitive (`malloc` → `NativeMemory`, `sin` → `Math.Sin`, `printf` → a boxing-free fluent builder).

| Project | Role |
|---|---|
| `DotCC.Lib/` | The compiler: grammar, preprocessor, emitters |
| `DotCC.Libc/` | The libc runtime emitted programs link against |
| `DotCC/` | The clang-shaped CLI (~150 lines, NativeAOT) |
| `DotCC.Tests/` | Unit tests against inline C strings |
| `DotCC.FunctionalTests/` | Golden fixtures: C in, Roslyn-compiled, stdout vs snapshot |
| `examples/` | hello, calc, factorial, cmake-demo, smoke-lib — and Lua 5.5 |

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
dotnet test
dotnet publish DotCC -c Release    # NativeAOT `dotcc` executable
```

Every fixture in `DotCC.FunctionalTests/Fixtures/` is a folder of `.c` files plus an `expected-stdout.txt`; adding a test is dropping a folder. The committed snapshots are additionally re-validated against **real compilers** by opt-in differential oracles — MSVC (`DOTCC_RUN_MSVC_ORACLE=1`) and gcc-in-WSL (`DOTCC_RUN_GCC_ORACLE=1`) — so dotcc, MSVC, and gcc must all agree on each fixture's output. The `wat` backend has its own execution oracle in CI (wat2wasm + Node).

## License

[MIT](LICENSE)
