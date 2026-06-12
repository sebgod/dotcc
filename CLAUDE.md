# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**dotcc** is a clang-like C compiler frontend that transpiles C to .NET 10 / C# 14 (AOT-clean, `unsafe` code). It is driven entirely by [SharpAstro.LALR.CC](https://github.com/sharpastro/LALR.CC): the grammar lives in `DotCC.Lib/c.lalr.yaml`, the source generator emits a typed AST + `IVisitor` surface at compile time, and the rest is a thin shell wiring the LALR.CC pipeline (lexer → preprocessor → parser → emitter visitor) into a clang-shaped CLI. The grammar was seeded from LALR.CC's `examples/CMinus/cminus.lalr.yaml` and grows toward real C as we go.

**For the feature-by-feature view of what's supported today** — lexical, types, operators, statements, declarations, preprocessor, every libc function, beyond-C99 / out-of-scope — see [`C-SUPPORT.md`](C-SUPPORT.md). Update that file when a feature lands; it is the source of truth for coverage and dialect-gate detail, so this file defers to it rather than duplicating lists.

## Solution layout

| Project | Type | Role |
|---|---|---|
| `DotCC.Lib/` | Library (net10.0, `IsAotCompatible=true`) | **The actual compiler.** Owns the grammar (`c.lalr.yaml`), the lowering visitor (`CSharpEmitter`), the preprocessor (`CPreprocessor`), and the public `Compiler.EmitCSharp` / `Compiler.Preprocess` entry points. |
| `DotCC.Libc/` | Library (net10.0, `IsAotCompatible=true`, `AllowUnsafeBlocks=true`) | The libc-shaped runtime emitted programs link against — I/O, memory, C-string, full `<math.h>`/`<tgmath.h>` (`double`→`Math`, `float`→`MathF`), string-literal lowering (`L(ReadOnlySpan<byte>)`), `Float128`, `CBool`. Each function routes to the obvious BCL primitive. **Same source, two consumers:** the `.cs` files compile into `DotCC.Libc.dll` for the tests AND are `<EmbeddedResource>` in `DotCC.Lib.csproj` so `Compiler.LoadRuntimeBlock` splices them into every emitted program (single source of truth). Drops out once published to NuGet. |
| `DotCC/` | Exe (`PublishAot=true`, `<AssemblyName>dotcc</AssemblyName>`) | Frontend. Clang-shaped CLI via `System.CommandLine` — parses args, dispatches to `Compiler`. ~150 lines, no compiler logic. |
| `DotCC.Tests/` | xUnit v3 + Shouldly | Library-level unit tests. Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, plus exercise every `DotCC.Libc.Libc` function directly. Fast, no I/O beyond a temp file. |
| `DotCC.FunctionalTests/` | xUnit v3 + Shouldly + Microsoft.CodeAnalysis.CSharp | End-to-end fixture tests. **Always-on:** dotcc → Roslyn compile in-process → invoke with `Console.Out` redirected → assert stdout matches the committed `expected-stdout.txt`. Two **opt-in differential oracles** (MSVC, gcc-in-WSL) re-validate the snapshot against real compilers, plus a **native shared-library round-trip oracle** (`-shared` → NativeAOT publish → real C consumer) — see **Testing**. |
| `examples/` | (just `.c` files) | Hand-written C programs demonstrating the language surface. Run with `dotnet run --project DotCC -- examples/hello/*.c`. Independent from test fixtures. |

The frontend exe is intentionally trivial. All testable logic lives in `DotCC.Lib`, reachable in-process from both test projects — no test spawns `dotcc.exe` or `dotnet run` against emitted code (Roslyn in-process drives that round-trip).

## Build & test commands

```bash
dotnet build -c Release                                # build everything (sibling LALR.CC if present, NuGet otherwise)
dotnet build -c Release -p:UseLocalLalrCc=false        # force NuGet mode even when sibling working copy exists

dotnet test                                            # run all tests (unit + functional)
dotnet test DotCC.Tests/DotCC.Tests.csproj             # unit tests only
dotnet test DotCC.FunctionalTests/DotCC.FunctionalTests.csproj   # functional fixtures only

dotnet run --project DotCC -c Release -- examples/hello/main.c examples/hello/math.c -o build/
dotnet run --project DotCC -c Release -- --emit=file examples/hello/main.c examples/hello/math.c > out.cs
                                                       # single .NET 10 file-based program with #:property AllowUnsafeBlocks
dotnet run --project DotCC -c Release -- --emit=build examples/hello/main.c examples/hello/math.c -o build/
                                                       # write Program.cs + csproj, then `dotnet build -c Release` in -o dir
dotnet run --project DotCC -c Release -- -E examples/hello/main.c
                                                       # preprocess-only: dump post-#include/#define token stream

dotnet publish DotCC -c Release                        # AOT publish — exe named `dotcc`
```

`dotcc` with no input files exits non-zero with `dotcc: error: no input files` (matches clang). No silent "compile something bundled" fallback.

## Sibling-or-NuGet wiring (`UseLocalLalrCc`)

`DotCC.Lib`'s only library dependency is **SharpAstro.LALR.CC** (runtime + bundled source generator). The build switches consumption modes on whether a sibling working copy is present:

| Mode | When | Wired in |
|---|---|---|
| **Sibling** (`UseLocalLalrCc=true`) | `../../sharpastro/LALR.CC/` exists | `ProjectReference` to `LALR.CC.csproj` + generator project as `Analyzer` + `PackageReference YamlDotNet` (`PrivateAssets=all`) feeding the analyzer host |
| **NuGet** (`UseLocalLalrCc!=true`) | sibling absent, or `-p:UseLocalLalrCc=false` | Single `PackageReference Include="SharpAstro.LALR.CC"` (the package bundles the analyzer DLL + YamlDotNet under `analyzers/dotnet/cs/`) |

Auto-detection lives in `Directory.Build.props` (MSBuild `Exists(...)`); the conditional `ItemGroup`s in `DotCC.Lib/DotCC.Lib.csproj`. CI always builds the NuGet path — override locally with `-p:UseLocalLalrCc=false` to validate the same. Same pattern as `sharpastro/tianwen` and `sharpastro/Console.Lib`; intentionally **different** from `sebgod/chess` (NuGet-only). Sibling mode picks up a LALR.CC generator/runtime tweak on the next dotcc build with no version bump.

## Architecture

The compilation pipeline is a straight pull-pipe inside `DotCC.Lib`:

```
.c file → BytesLexer
        → PreprocessorTokenStream  (#include / #define / #undef / #if / #ifdef / #ifndef / #else / #endif / #pragma / #error / #warning; object-like macro substitution via Rewrite)
        → MacroExpander            (function-like macro expansion with paren-balanced arg collection + multi-pass rescan)
        → DialectKeywordRewriter   (dialect-aware keyword promotion: e.g. C23 `bool` → `_Bool`, gated on -std=)
        → TypeNameRewriter         (C lexer hack: promote ID → TYPE_NAME after typedef)
        → SyncLATokenIterator      (one-token lookahead)
        → Parser                   (LALR(1) tables built from c.lalr.yaml)
        → CSharpEmitter            (IVisitor<string> → emitted C# snippets concatenated)
        → BuildShell(...)          (wrap the emitted fn list in a .NET 10 program shell)
```

The four rewriting stages are all `RewritingTokenStream` subclasses (an upstream LALR.CC base class owning the iterator plumbing — ready queue, look-ahead buffer, exhaustion flag — and exposing a `ProcessToken` hook plus `Emit` / `CollectUntil` / `TryReadNext`). One subclass per policy, mechanics shared; future contextual-keyword/DSL rewriters plug in the same way.

All four stages are owned by SharpAstro.LALR.CC. `DotCC.Lib` only contributes:
- `Compiler.EmitCSharp(...)` / `Compiler.Preprocess(...)` — public entry points (`Compiler.cs`).
- `CPreprocessor` — impl of generated `C.IPreprocessor`: object + function-like macros, `#include` (quoted and angle), `#pragma once`, `#error`/`#warning`, defined-set tracking.
- `MacroExpander` — function-like macro calls (paren-balanced arg collection + multi-pass rescan).
- `DialectKeywordRewriter` — dialect-aware keyword promotion ("rule 2"). A data table maps `(identifier spelling → MinVersion + target terminal)`; an `ID` is promoted only when the active `CDialect.Version ≥ MinVersion`. **`CDialect.Version` is keyed by ISO year (1990/1999/2011/2017/2023) so the gate `Version >= year` is monotonic** — keying by the short `90/99/11/17/23` suffix sorts `c11` below `c99` and silently mis-gates (a real past bug). Why rule 2 and not the visitor: keywords spelled like identifiers (`inline`/`bool`/`true`/…) can't be gated in the visitor — `int true = 5;` is valid older code. Under an older `-std=` the spelling stays an identifier, so the feature is simply unavailable there (a structural rejection, no `DialectGate` row needed). Sits after `MacroExpander` (a header's `#define bool _Bool` wins) and before `TypeNameRewriter`. Genuinely new *syntax* (`_BitInt`, `_Generic`) is gated in the visitor instead; `_Capital_` keywords are always accepted.
- `TypeNameRewriter` — the C lexer hack: tracks typedef-bound names, promotes matching `ID` → `TYPE_NAME` so `Color * x;` routes as a declaration.
- `CSharpEmitter` — impl of generated `C.IVisitor<EmitContent>` (one `Visit` per AST record). Returns an `EmitContent` DU; most variants are `Text` (C# code), some (`SpecList`/`Args`/`EnumItems`/`InitMembers`/`FnHeader`) carry structured intermediate data. Also owns **block-scope local renaming** (CS0136 avoidance): a scope stack maps each raw C name to the unique C# identifier it was emitted as — pushed at block entry (the epsilon `ScopeEnter` marker, the only bottom-up block-*entry* hook), popped at exit; a colliding decl gets a fresh `name__k`, references resolve through the stack. Side tables stay keyed by the raw C name, so the rename rides purely the emitted text.
- `BuildShell` — C# scaffolding around emitted functions (top-level statements + entry-point wiring, struct/typedef section, `using static Libc`, embedded-runtime splice point); argv UTF-8 marshalling for `int main(int, char**)`.
- `Compiler.LoadRuntimeBlock` (`_runtimeBlock`) — reads every `DotCC.Libc/*.cs` source from the assembly manifest (embedded at build time), strips file-scope artifacts (`#nullable`, `using`s, `namespace`), concatenates into the type-decls section. Single source of truth: edit `MathLib.cs`, both the unit-tested DLL and every emitted program pick it up next build.
- `SystemHeaders` — synthetic `.h` files under `DotCC.Lib/include/`, embedded as resources so the parser sees signatures with no disk I/O. User `-I` headers win on name collisions (clang's quoted-include rule).
- `CompileException` — wraps LALR.CC's `ParseErrorException` into a stable public surface. `Parser.ParseInput` defaults to `ParserErrorMode.Throw`; `Compiler.EmitCSharp` catches and re-raises so callers know one exception type.

The generated `DotCC.C` partial class (from `c.lalr.yaml` at build time by `LALR.CC.SourceGenerators`) exposes: `C.BuildLexer()` / `C.BuildParser(visitor)` / `C.WrapPreprocessor(lexer, impl)`; `C.IVisitor<T>` (one `Visit(C.<RecordName>)` per `action:`); `C.IPreprocessor` (one method per directive + `Rewrite(Item)` + `IsDefined(string)`); and the `C.<RecordName>` AST records. **Change the grammar and the generated surface changes in lockstep** — add an `action: Foo` rule and you get a compile error until you add `Visit(C.Foo n)`.

### CLI surface (clang-shaped)

| Flag | Meaning |
|---|---|
| `dotcc <a.c> <b.c>` | Compile translation units (whole-program). Default: write `Program.cs + dotcc-out.csproj` to `./a.out-cs/`. **`.cs` inputs are object fragments → linked** (see `--emit=obj`). |
| `-o <path>` | Output: a directory for csproj/build, a file for `file`/`obj`. **Inferred when omitted** (`obj` → `<src>.cs`, csproj/build → `./a.out-cs/`, file → stdout). |
| `--emit=file` | Single .NET 10 file-based program (`#:property AllowUnsafeBlocks=true`). To `-o <file>` if given, else stdout. |
| `--emit=csproj` | Default — `Program.cs` + paired csproj to `-o` dir. |
| `--emit=build` | As `csproj`, then run `dotnet build -c Release` in the output dir. |
| `--emit=obj` | **Separate compilation.** Compile ONE `.c` to a `.cs` object fragment (functions + its type decls + globals, no shell/runtime). Link by passing `.cs` objects back: `dotcc a.cs b.cs -o app` merges (deduping shared types) and wraps in the shell. Drives CMake/make per file (`examples/cmake-demo/`). |
| **`-o` ⇄ `--emit` inference** | When one is omitted it's inferred: `-o foo.cs` ⇒ `file`; `-o <dir>` ⇒ `csproj`; `--emit=obj` with no `-o` ⇒ `<src>.cs`. Explicit `--emit` wins; `obj` is never inferred. |
| `-E` | Preprocess only — dump the post-`#include`/`#define` token stream to stdout. No parsing. |
| `-I <dir>` | Add header search dir. Repeatable. Auto-includes each `<input>.c`'s directory. |
| `-D NAME[=VALUE]` | Predefine a macro. Repeatable. `=VALUE` is lexed through the same byte lexer as the parser; bare `NAME` is a defined-as-marker (empty body). |
| `-MD` / `-MMD` | **Header-dependency file** (`Compiler.EmitDependencyRule`): a Make rule listing the TU + every transitively-`#include`d header, so CMake/Ninja/Make recompile on header change. `-MMD` drops angle headers; synthetic system headers (no disk path) are always omitted. The scan honors `#if`/`#ifdef`. Paths normalized to `/`, make-special chars escaped. |
| `-MF <file>` | Dependency-file output path (defaults to the object/source name with `.d`). |
| `-MT <target>` | Dependency rule target name(s). Repeatable. CMake passes `-MT <OBJECT>`. |
| `-c` | Compile to .NET assembly (no native publish). Clang-shaped alias for `--emit=build`. |
| `-shared` | Shared library: csproj with `<NativeLib>Shared</NativeLib>` + `<PublishAot>true</PublishAot>`; non-static C functions exported via `[UnmanagedCallersOnly(...CallConvCdecl...)]`. `main` not required. Run `dotnet publish -c Release -r <RID>` for the native artifact. |
| `-std=<dialect>` | `c90`/`c99`/`c11`/`c17`/`c18`/`c23`. Default `c17`. Sets `__STDC_VERSION__` (omitted for `c90`) and drives rule-2 keyword promotion. **Alone it stays permissive** — the parser is dialect-agnostic. `c89` is omitted for the canonical `c90`; no `gnu*` variants (dotcc implements no GNU extensions). |
| `-pedantic` | Opt into **dialect rejection**: features newer than the selected `-std=` warn to stderr but still compile. Maps `CDialect.Version` against a per-feature introduced-year table (`DialectGate`, emit pass only). Per-feature gate list lives in `C-SUPPORT.md`. `//` comments are intentionally NOT gated. |
| `-pedantic-errors` | Like `-pedantic` but violations are **errors** — collect-all, exit non-zero with every violation listed. |
| `-Wconversion` | Opt-in (off by default; like gcc/clang `-Wconversion` / MSVC C4244): warn on an implicit integer conversion that **narrows** (wider → narrower) at init/assignment/return. dotcc inserts the cast regardless; the flag only controls the warning (`ConversionGate`). A constant that fits is neither cast nor flagged. Same-width sign changes are NOT flagged (that's `-Wsign-conversion`, out of scope). |
| `-fsanitize=address` | Opt-in checked **debug heap** (a heap-only subset of clang's ASan): routes the emitted program's `malloc`/`calloc`/`realloc`/`free` through a `[magic\|size]`-header + trailing-redzone block layout, so `free` flags a bad/double free or a write-past-end with a managed stack trace at the call site. The shell calls `Libc.EnableDebugHeap()` once before `main`. `DOTCC_DEBUG_HEAP=1` is the no-recompile runtime override; `DOTCC_DEBUG_HEAP_SCAN=1` adds a full live-block redzone sweep on every alloc/free (catches an overflow into a never-freed block, e.g. a guest GC arena). Other `-fsanitize=` kinds warn and are ignored. Off by default — one inert branch in malloc/free. |
| (no inputs) | Error and exit non-zero — same as clang. |

**Predefined macros** (seeded every compile, plus any `-D`): `__STDC__`=`1`, `__STDC_HOSTED__`=`1`, `__STDC_VERSION__`=per-`-std=` value (undefined under `c90`), `__dotcc__`=`1` (compiler id, like `__clang__`), and the **LP64 data-model trio** `__LP64__`=`1`, `__SIZEOF_POINTER__`=`8`, `__SIZEOF_LONG__`=`8` — dotcc IS an LP64 compiler (`long` → C# `long`, 8-byte pointers), and portable C (chibi-scheme's `SEXP_64_BIT`) decides pointer-tagging strategy from exactly these macros; without them it would mis-configure for 32-bit and miscompute at runtime.

**Library mode (`-shared`) emit shape:** user functions land in `internal static class DotCcLib` so inter-function calls resolve as direct C# invocations (`[UnmanagedCallersOnly]` prohibits managed call sites). Each non-static C function gets a `public static` wrapper in `public static class DotCcExports` annotated `[UnmanagedCallersOnly]`; NativeAOT inlines the trampoline. C `static` functions stay internal (no wrapper). Varargs functions are skipped from exports (`params object[]` isn't a valid `UnmanagedCallersOnly` signature).

### Code generation strategy

| C | Emitted C# |
|---|---|
| `int` / `float` / `double` / `void` | same |
| `_Float128` / `__float128` (C23) | `Float128` — MIT software IEEE-754 binary128 (`DotCC.Libc/Float128.cs`), clean-room. Full arithmetic + `<math.h>` (algebraic correctly-rounded; transcendentals via BigInteger fixed-point), `%Lf`/`%Le`/`%Lg`, decimal Parse; implements `IBinaryFloatingPointIeee754<Float128>`. |
| `char` | `byte` (so `char*` arithmetic walks bytes) |
| `T*` | `T*` (unsafe pointer) |
| `"foo"` | `L("foo\0"u8)` — pinned UTF-8 RVA pointer via `MemoryMarshal.GetReference` |
| `malloc(n)` / `free(p)` | `malloc((int)n)` / `free(p)` — backed by `NativeMemory`. `using static Libc;` surfaces them by bare name |
| `printf("%d %s", x, s)` | `printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — fluent ref-struct builder (avoids `params object[]` boxing). `fprintf` takes the `TextWriter` first |
| `sin(x)` / `sinf(x)` / … | same name; `double` overloads → `System.Math`, `float` → `System.MathF` (exactly `<tgmath.h>` dispatch) |
| C function | `internal static unsafe` method of a top-level `DotCcProgram` class (NOT a local function — class methods can be `&fn`-addressed and stored in fn-ptr tables, which Lua's `luaL_Reg` code needs). `-shared` uses the parallel `DotCcLib`. |
| Prototype / forward decl | empty emit (C# methods hoist) |

**Self-contained emit, no inlining duplication.** Every emitted file pulls in the `DotCC.Libc` runtime via the single block `BuildShell` splices from embedded resources (see `LoadRuntimeBlock` above) — `using static Libc;` then surfaces every method by bare name.

The single-source-of-truth design intent: **the same `.c` file should compile under both `dotcc` and `clang -std=c99` and produce equivalent observable behavior** — keep the grammar a strict subset of real C, and keep the emitter's output semantics aligned with the C abstract machine.

**Type-synthesis layer (the spine of several lowerings).** `EmitContent.Text` carries an optional `CType` (`DotCC.Lib/CType.cs` — `Sized(csType)` / `Arr(element, count)`) and an optional `EnumType`, set by expression visitors and propagated through transparent wrappers. It powers: real C# `enum` lowering (decay enum operands to `(int)` for C's plain-int arithmetic, re-cast only at typed sinks); `sizeof expr` (notably the `sizeof(a)/sizeof(a[0])` array-length idiom — arrays lower to pointers, so `sizeof(arr)` emits `count * sizeof(element)`); and the same-function `malloc`/`free` → stack-value peephole (a two-pass analysis: pass 1 records per-`(function,var)` usage, pass 2 promotes when ref-counts balance and the var never escapes). Recognition is **structural** (AST markers + ref-counts), never text-matching. Done features and their fixtures/gaps are tracked in `C-SUPPORT.md`. **Next consumers:** struct/union field types (unlocks `sizeof(s.field)` + enum-typed field reads), callee param types (the enum↔param-cast gap), and the idiomatic `char*`→`string` / `int*`→`int[]` lowerings.

**Once `DotCC.Libc` ships to NuGet** the embedding goes away: the shell emits `#:package`/`<PackageReference>` and drops the `{{runtimeBlock}}` splice — no runtime change, the embedding is purely a pre-NuGet deployment workaround.

## Testing

Two projects, both xUnit v3 on Microsoft.Testing.Platform.

**Unit tests (`DotCC.Tests/`).** Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, assert on the returned string (e.g. emitted C# contains `static unsafe int main`; `--emit=csproj` omits the `#:property` header; parse errors / missing `main` raise `CompileException`; `#define` substitutes). No fixture files — write a temp `.c`, call, assert, delete. Fast.

**Functional tests (`DotCC.FunctionalTests/`).** Fixture-driven. For each `Fixtures/<name>/` with `.c` file(s) + an `expected-stdout.txt` sidecar: `EmitCSharp` → `CSharpCompilation.Create(OutputKind.ConsoleApplication, allowUnsafe: true)` → fresh `AssemblyLoadContext.LoadFromStream` → reflect entry point → invoke with `Console.Out` redirected → captured stdout `ShouldBe` the sidecar (normalized for line-ending / trailing-newline noise). **Adding a fixture is just dropping a folder** — `FixtureRunner.Discover()` walks `Fixtures/` at runtime and `[Theory]/[MemberData]` materialises one case per directory. Same shape as compiler test suites generally: golden C in, captured stdout vs a sidecar.

**Differential oracles (opt-in, snapshot model).** The committed `expected-stdout.txt` IS the cached compiler output; `FixtureTests` validates dotcc against it in-process every run. Two oracles re-check that snapshot against a *real* compiler, off by default for speed:
- **MSVC** (`MsvcOracleTests.cs`, `DOTCC_RUN_MSVC_ORACLE=1`): cl.exe per fixture; asserts MSVC == dotcc emit == snapshot. `DOTCC_REGEN_BASELINE=1` rewrites the snapshot in-place from MSVC (review + commit the diff).
- **gcc-in-WSL** (`GccWslOracleTests.cs`, `DOTCC_RUN_GCC_ORACLE=1`): a second reference compiler covering ground MSVC can't (e.g. C23 bare `bool`, which MSVC rejects). `DOTCC_REGEN_BASELINE=1` here refreshes from gcc — but requires the gcc run flag too, so a plain regen doesn't silently hand baseline authority to gcc.

A **third** oracle is a different shape — not a per-fixture snapshot check, but a native round-trip:
- **Native shared-library round-trip** (`SharedLibOracleTests.cs`, `DOTCC_RUN_SHARED_LIB_ORACLE=1`): `-shared`-compiles a C lib, NativeAOT-publishes it to a real `.so`, then links + runs a hand-written C consumer that calls the `[UnmanagedCallersOnly]` cdecl exports through the plain C ABI. Proves the export ABI end-to-end — `LibraryModeTests` only checks the managed export metadata (it skips the publish). Needs `clang`/`zlib` (the NativeAOT linker) + `gcc` (the consumer); its own ubuntu CI job.

All three skip cleanly when the host toolchain isn't present. A fixture opts out of a snapshot oracle with a `no-msvc-oracle.txt` / `no-gcc-oracle.txt` sidecar (contents = skip reason) — used for ABI-divergent cases (`float128-basic` has no MSVC `_Float128`; `float-limits`' `LDBL_DIG` depends on `long double` width). **`Process.Start` is confined to these oracle modes** (cl.exe / `wsl.exe` + the program + a one-shot vcvars capture; the shared-lib oracle adds `dotnet publish` + `gcc`) — never on the always-on path.

### Grammar conventions

`c.lalr.yaml` follows LALR.CC's YAML schema:

- **`symbols:` ordering is meaningful** — index = symbol ID, LHS=0 is the start symbol. Don't reorder existing entries; append.
- **Precedence groups order matters** — listed lowest → highest, used to resolve S/R and R/R conflicts via `derivation: leftmost|rightmost|none`. Dangling-else lives in a `rightmost` group (so `else` shifts onto the nearest open `if`).
- **Lexer-regex alternation is supported** (LALR.CC ≥ 4.0.0): `|` works at top level and *inside* a repetition — what the `STRING` rule needs. `.` matches any char except `\n`. For *token-level* alternatives prefer multiple `LexRule`s (longest match wins, first-rule-wins on ties, reads clearer); reach for `|` only when the choice lives within one token.
- **Preprocessor directives go in `preprocessor:`** (each becomes an `IPreprocessor` method). Conditionals are handled by LALR.CC's built-in engine — declare them in `conditionals:`; the `IsDefined` hook drives boolean evaluation.

An unresolved conflict throws `GrammarConflictException` with the offending state + lookahead — don't catch it; fix the grammar by adding the colliding productions to a precedence group.

## Conventions to respect

- **Conditional contexts use `Cond.B(...)`.** C says non-zero ints and non-null pointers are truthy; C# wants `bool`. The visitor wraps every `if`/`while`/`for`-cond with `Cond.B(E)`, and `BuildShell` emits an overloaded static class (`B(bool)`/`B(int)`/`B(double)`/`B(void*)`) so C# overload resolution picks the right form. Any new production with an expression in conditional position (ternary, `do…while(E)`) **must** wrap E the same way.
- **C-semantics coercions live on the lowered *type*, not on emitter rewrites.** `_Bool` lowers to the `CBool` value type (`DotCC.Libc/CBool.cs`), which carries C's store-normalization via **implicit conversion operators** (`int`/`long`/`double`/`bool`/`void*` → CBool, CBool → `int`). Because it's on the type, *every* store position coerces uniformly with no emitter pass — including pointer stores (`_Bool b = p;` works: `T*` → `void*` → CBool is one standard + one user-defined step, which C# permits). **C# does NOT forbid user-defined conversions to/from pointer types** — a mistaken code comment once nearly drove an emitter-side `Cond.B`-injection workaround; prefer the type-level operator. New C types needing store-coercion: add the conversion to the lowered C# type before reaching for an emitter rewrite.
- **AOT-clean.** `DotCC.Lib` is `IsAotCompatible`; the frontend is `PublishAot`. No reflection beyond BCL collections, no `dynamic`, no runtime-codegen serializers. The LALR.CC generator runs at *build* time (YamlDotNet is `PrivateAssets="all"`, never in the runtime closure). Run `dotnet publish -c Release` periodically to catch trim/AOT regressions.
- **No `Process.Start` from the library, and none on the dotcc round-trip path.** The frontend uses it for `--emit=build`. In tests it's confined to the opt-in oracles (above). Adding it to the default test path or to `DotCC.Lib` is a regression on the structural intent.
- **Keep `dotcc` and `clang -std=c99` round-trippable.** Extending the grammar: prefer real-C syntax over inventions. Extending the emitter: prefer lowerings that preserve C's observable semantics (overflow, signedness, side-effect/evaluation order at sequence points).
- **Emitted C# is `unsafe`** (`AllowUnsafeBlocks` on the generated csproj and on `DotCC.FunctionalTests`). `DotCC.Lib` itself is **not** unsafe — it produces unsafe C# as a string but runs none.
- **Don't pull more deps without checking AOT compat.** The lib's closure is intentionally `LALR.CC` + `YamlDotNet` (build-time only). The frontend adds `System.CommandLine`; tests add `xUnit`/`Shouldly`/`Microsoft.CodeAnalysis.CSharp`.
- **Watch the XML.** Comments in `Directory.Build.props` / `Directory.Packages.props` can't contain `--` (XML forbids it). Easy to miss when you type `dotnet run --file`.
