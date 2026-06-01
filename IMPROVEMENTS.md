# Improvements & open ideas

Forward-looking tracker for dotcc — things that work today but could go
further, and deliberate next steps with their reasoning. Distinct from
[`C-SUPPORT.md`](C-SUPPORT.md) (which tracks *language/libc* coverage): this
file is for tooling, integration, and architectural follow-ons. Same legend
(✅ done · 🟡 partial · ❌ not yet · 🚫 out of scope).

## Build-system integration

dotcc plugs into CMake as a real `CMAKE_C_COMPILER` — you write a plain
`project(... C)` + `add_executable()` and CMake's own compile→link graph
drives dotcc. Demo + how-it-works: [`examples/cmake-demo/`](examples/cmake-demo/).

| Item | Status | Notes |
|---|---|---|
| **Separate compilation** (`--emit=obj`) | ✅ | LTO-style: each `.c` → a `.cs` object fragment (functions + its type decls + globals, no shell/runtime); link merges fragments, dedups shared-header types, wraps in the shell. `Compiler.EmitObject` / `LinkObjects`; objects self-identify via the `//!dotcc object` magic marker. |
| **CMake as `CMAKE_C_COMPILER`** | ✅ | `dotcc-toolchain.cmake`: `CMAKE_SYSTEM_NAME Generic`, `CMAKE_C_COMPILER_ID "dotcc"`, `CMAKE_C_COMPILER_FORCED` (no native ABI to probe), custom `CMAKE_C_COMPILE_OBJECT` / `CMAKE_C_LINK_EXECUTABLE`. ctest passes. |
| **`-I` / `-D` threading** | ✅ | Compile rule forwards CMake's `<DEFINES>`/`<INCLUDES>`. dotcc is clang-shaped, so CMake's default *glued* spellings (`-I/abs/dir`, `-DNAME=VAL`) and bare `-DNAME` parse as-is — no `CMAKE_INCLUDE_FLAG_SEP_C` tweak. Proven in the demo: `include/buildcfg.h` is reachable *only* via `-I`, and `-DSCALE=10` is used numerically (`tag=cfg-ok scaled=70`). |
| **cwd-independent launcher** | ✅ | `dotcc-link.sh` embeds an absolute dll path (`realpath`) so the "executable" runs from any directory, not just the build dir. |
| **`-MD`/`-MMD` dependency files** | ✅ | `-MD` / `-MMD` / `-MF <file>` / `-MT <target>` write a Make-format rule (`Compiler.EmitDependencyRule`) listing the TU + every `#include`d header, so CMake/Ninja/Make track header→TU deps and recompile a unit when a header it pulls in changes. `-MMD` drops angle (`<...>`) headers; synthetic embedded system headers (no disk path) are always omitted — nothing to stat. The toolchain wires it via `CMAKE_C_DEPFILE_FORMAT gcc` + inline `-MD -MT <DEP_TARGET> -MF <DEP_FILE>`. **Verified end-to-end** with the Ninja generator: touch `geom.h` → both `geom.c` and `main.c` recompile (both `#include` it); a no-op rebuild is `ninja: no work to do`. The scan respects `#if`/`#ifdef` (a header behind a false branch isn't a dependency). |
| **`-lfoo` native deps → `[LibraryImport]`** | ❌ | Link against a prebuilt native lib. Design (reasoned out, not built): a prototype that resolves to the runtime → `using static Libc`; a prototype with no body, *not* in the runtime → external → emit a `[LibraryImport("foo")]` P/Invoke. Linking is name-based (`-l` = which lib, `-L` = where); dotcc keeps the parsed prototype's signature purely for managed↔native marshalling. Falls back to call-site signature inference when no prototype is in scope. |
| **`<FLAGS>` / `-isystem`** | 🟡 | Compile rule threads only `<DEFINES>`/`<INCLUDES>`; `<FLAGS>` (CMAKE_C_FLAGS, optimization) is intentionally omitted to keep demo output clean. dotcc already ignores-with-a-warning unknown flags, so adding `<FLAGS>` is low-risk when needed. `-isystem` would be a trivial alias of `-I`. |
| **Native-Windows launcher** | 🟡 | `dotcc-link.sh` writes a *bash* launcher (WSL/Linux/macOS). The dll is portable; only the shim is Unix — a `.cmd` sibling would cover native Windows. |
| **Parsing real OS / third-party headers** | ❌ | The real wall, and *not* a build-system flag. `-I` *finding* `<windows.h>`/glibc internals is necessary-not-sufficient: dotcc's C99-subset grammar chokes on vendor extensions (`__declspec`, SAL annotations, `#pragma intrinsic`, builtins). Clean, portable C headers work today; OS headers are the long road in [`C-SUPPORT.md`](C-SUPPORT.md). |

### Design note — why dotcc does *not* impersonate clang to CMake

There are two levels of "pretend to be a clang compiler", and dotcc
deliberately takes one and not the other:

- **CLI-convention level — adopt it (and we do).** Accept `-I`/`-D`/`-o`/`-c`/
  `-std=`, the glued spellings, and ignore-with-a-warning unmodeled flags. The
  more clang-shaped the CLI, the less any build system has to know about us —
  this is exactly why `-I`/`-D` threading needed *zero* CLI work.
- **Compiler-*identity* level (`CMAKE_C_COMPILER_ID "Clang"`) — don't.** Claiming
  to be Clang loads CMake's built-in Clang/platform modules, which assume a
  *native* toolchain: a real linker line (`clang -o exe a.o b.o`), injected
  `-O3 -DNDEBUG -fPIC`, and the `-MD -MF` depfile protocol the Ninja/Make
  generators rely on. We can honor none of those as-is — our objects are `.cs`
  fragments and our "link" is merge→build→launcher. So we declare our own
  compiler ID + `Generic` system + explicit rules instead: clang-shaped
  ergonomics, no native baggage.

Precedent: emscripten's `emcc` *is* clang underneath and transpiles C→wasm (as
we do C→.NET), yet integrates with CMake as its **own** compiler ID via
`Emscripten.cmake` — not by impersonating Clang — for exactly these reasons.
dotcc is in the canonical spot for a transpiler. "Be more clang-like" pays off
as concrete, bounded features (the `-MD` depfiles above), not as identity.
