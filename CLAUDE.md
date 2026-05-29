# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**dotcc** is a clang-like C compiler frontend that transpiles C to .NET 10 / C# 14 (AOT-clean, `unsafe` code). It is driven entirely by [SharpAstro.LALR.CC](https://github.com/sharpastro/LALR.CC): the grammar lives in `DotCC.Lib/c.lalr.yaml`, the source generator emits a typed AST + `IVisitor` surface at compile time, and the rest is a thin shell that wires the LALR.CC pipeline (lexer → preprocessor → parser → emitter visitor) into a clang-shaped CLI.

The grammar was initially seeded from LALR.CC's `examples/CMinus/cminus.lalr.yaml` (tight C99 subset). It is intended to grow toward more of real C as we go.

**For the feature-by-feature view of what's supported today** — lexical, types, operators, statements, declarations, preprocessor, every libc function, and what's beyond-C99 / explicitly out of scope — see [`C-SUPPORT.md`](C-SUPPORT.md). Update that file when a feature lands.

## Solution layout

| Project | Type | Role |
|---|---|---|
| `DotCC.Lib/` | Library (net10.0, `IsAotCompatible=true`) | **The actual compiler.** Owns the C grammar (`c.lalr.yaml`), the lowering visitor (`CSharpEmitter`), the preprocessor impl (`CPreprocessor`), and the public `Compiler.EmitCSharp` / `Compiler.Preprocess` entry points. |
| `DotCC.Libc/` | Library (net10.0, `IsAotCompatible=true`, `AllowUnsafeBlocks=true`) | The libc-shaped runtime surface emitted programs link against. **I/O:** `fprintf` (primitive) / `printf` / `sprintf` / `snprintf` / `fscanf` / `scanf` / `sscanf` / `fputs` / `puts` plus `stdin` / `stdout` / `stderr`. **Memory:** `malloc` / `free` / `memset` / `memcpy`. **C-string:** `strlen` / `strcmp` / `strcpy`. **Math (`<math.h>` / `<tgmath.h>`):** full C99 surface in `MathLib.cs` — every function has both `double` (→ `Math.X`) and `float` (→ `MathF.X`) overloads. **String-literal lowering:** `L(ReadOnlySpan<byte>)` (pins the UTF-8 RVA). Each function routes to the obvious BCL primitive. Independently growable and unit-tested. **Same source, two consumers:** the `.cs` files compile into `DotCC.Libc.dll` for the tests, AND are marked `<EmbeddedResource>` in `DotCC.Lib.csproj` so `Compiler.LoadRuntimeBlock` can splice them into every emitted program (single source of truth, no duplicated copies). Once published to NuGet, emitted programs will reference the package directly and the embedding drops out. |
| `DotCC/` | Exe (`PublishAot=true`, `<AssemblyName>dotcc</AssemblyName>`) | Frontend. Clang-shaped CLI via `System.CommandLine` — parses args, dispatches to `Compiler`. ~150 lines, no compiler logic. |
| `DotCC.Tests/` | xUnit v3 + Shouldly | Library-level unit tests. Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, AND exercise every `DotCC.Libc.Libc` function (strlen/strcmp/memset/memcpy/strcpy/printf/puts/malloc-free). Fast, no I/O beyond a temp file. |
| `DotCC.FunctionalTests/` | xUnit v3 + Shouldly + Microsoft.CodeAnalysis.CSharp | End-to-end fixture tests. **Always-on**: dotcc → Roslyn compile in-process → invoke with `Console.Out` redirected → assert stdout matches the committed `expected-stdout.txt`. **Opt-in MSVC oracle** (`MsvcOracleTests.cs`): set `DOTCC_RUN_MSVC_ORACLE=1` to also build + run each fixture with cl.exe and assert MSVC matches the snapshot; `DOTCC_REGEN_BASELINE=1` to refresh the snapshot from MSVC (writes back to the source-tree file). **Opt-in gcc oracle** (`GccWslOracleTests.cs`): set `DOTCC_RUN_GCC_ORACLE=1` to build + run each fixture with gcc inside WSL — a second reference compiler that covers what MSVC can't (e.g. C23 bare `bool`); a `no-gcc-oracle.txt` sidecar opts a fixture out (ABI-divergent cases like `long double` width). Both oracles are skipped by default for speed — the snapshot IS the cached compiler output. **Process.Start is only used in oracle modes** (cl.exe / `wsl.exe` + the produced program + a one-shot vcvars-env capture at init); the dotcc-emitted code always runs in-process via Roslyn. |
| `examples/` | (just `.c` files, not a csproj) | Hand-written C programs to demonstrate the language surface. Run with `dotnet run --project DotCC -- examples/hello/*.c`. Independent from test fixtures. |

The frontend exe is intentionally trivial. All testable logic lives in `DotCC.Lib` and is reachable in-process from both test projects. No test needs to spawn `dotcc.exe`, and no test needs to spawn `dotnet run` against the emitted code — the functional tests use Roslyn in-process for that round-trip.

## Build & test commands

```bash
dotnet build -c Release                                # build everything (sibling LALR.CC if present, NuGet otherwise)
dotnet build -c Release -p:UseLocalLalrCc=false        # force NuGet mode even when sibling working copy exists

dotnet test                                            # run all tests (unit + functional)
dotnet test DotCC.Tests/DotCC.Tests.csproj             # unit tests only
dotnet test DotCC.FunctionalTests/DotCC.FunctionalTests.csproj   # functional fixtures only

dotnet run --project DotCC -c Release -- examples/hello/main.c examples/hello/math.c -o build/
dotnet run --project DotCC -c Release -- --emit=csharp examples/hello/main.c examples/hello/math.c > out.cs
                                                       # single .NET 10 file-based program with #:property AllowUnsafeBlocks
dotnet run --project DotCC -c Release -- --emit=build examples/hello/main.c examples/hello/math.c -o build/
                                                       # write Program.cs + csproj, then `dotnet build -c Release` in -o dir
dotnet run --project DotCC -c Release -- -E examples/hello/main.c
                                                       # preprocess-only: dump post-#include/#define token stream

dotnet publish DotCC -c Release                        # AOT publish — exe named `dotcc`
```

`dotcc` with no input files exits non-zero with `dotcc: error: no input files` (matches clang). There is no silent "compile something bundled" fallback.

## Sibling-or-NuGet wiring (`UseLocalLalrCc`)

`DotCC.Lib`'s only library dependency is **SharpAstro.LALR.CC** (runtime + bundled source generator). The build switches between two consumption modes based on whether a sibling working copy is present:

| Mode | When | Wired in |
|---|---|---|
| **Sibling** (`UseLocalLalrCc=true`) | `../../sharpastro/LALR.CC/` exists | `ProjectReference` to `LALR.CC.csproj` + generator project as `Analyzer` + `PackageReference YamlDotNet` (`PrivateAssets=all`) feeding the analyzer host via `<Analyzer Include="$(PkgYamlDotNet)...">` |
| **NuGet** (`UseLocalLalrCc!=true`) | sibling absent, or `-p:UseLocalLalrCc=false` | Single `PackageReference Include="SharpAstro.LALR.CC"` (the published package bundles the analyzer DLL + YamlDotNet under `analyzers/dotnet/cs/`) |

The auto-detection lives in `Directory.Build.props` (uses MSBuild `Exists(...)` against the sibling repo paths). The conditional `ItemGroup`s live in `DotCC.Lib/DotCC.Lib.csproj`. CI always builds with the NuGet path — override locally with `-p:UseLocalLalrCc=false` to validate the same.

This is the same pattern as `sharpastro/tianwen` and `sharpastro/Console.Lib`, and intentionally **different** from `sebgod/chess` (which is NuGet-only). Sibling mode means a tweak to LALR.CC's generator/runtime is picked up on the next dotcc build with no version bump.

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

`PreprocessorTokenStream`, `MacroExpander`, `DialectKeywordRewriter`, and `TypeNameRewriter` are all `RewritingTokenStream` subclasses (a small upstream base class in LALR.CC that owns the iterator plumbing — ready queue, look-ahead buffer, exhaustion flag — and exposes a `ProcessToken` hook plus `Emit` / `CollectUntil` / `TryReadNext` helpers). The preprocessor handles directives + object-like macro substitution; `MacroExpander` handles function-like macros where `TryReadNext`-based lookahead is needed (peek for the `(`, collect paren-balanced args, substitute formals → actuals, rescan); `DialectKeywordRewriter` promotes identifier-spelled keywords onto their grammar terminal only when `-std=` makes them first-class (rule 2 of the dialect-gating model — see below); the typedef rewriter tracks typename state and rewrites `ID` tokens after a `typedef`. Future contextual-keyword or DSL-mode rewriters will plug in the same way — one subclass per policy, mechanics shared.

All four pipeline stages are owned by SharpAstro.LALR.CC. `DotCC.Lib` only contributes:
- `Compiler.EmitCSharp(...)` / `Compiler.Preprocess(...)` — the public entry points (`Compiler.cs`)
- `CPreprocessor` — impl of generated `C.IPreprocessor` (object-like + function-like macros, `#include` quoted and angle, `#pragma once`, `#error`/`#warning`, defined-set tracking)
- `MacroExpander` — `RewritingTokenStream` subclass handling function-like macro calls with paren-balanced arg collection and multi-pass rescan
- `DialectKeywordRewriter` — `RewritingTokenStream` subclass doing dialect-aware keyword promotion. A data table maps `(identifier spelling → MinVersion + target terminal)`; an `ID` is promoted only when the active `CDialect.Version` ≥ `MinVersion`. Today's single row is C23 `bool → _Bool`. This is "rule 2" of the dialect-gating model: keywords spelled like identifiers (`bool`/`true`/`nullptr`/…) can't be gated in the visitor — `int true = 5;` is valid pre-C23 code, so always-treating them as keywords would parse-error before semantics ran. Sits after `MacroExpander` (so an included header's `#define bool _Bool` wins and the table doesn't fire) and before `TypeNameRewriter`. Genuinely new *syntax* (`_BitInt`, `typeof`, `_Generic`) is instead gated in the visitor; `_Capital_` keywords are always-accepted (reserved in every dialect)
- `TypeNameRewriter` — `RewritingTokenStream` subclass implementing the C lexer hack: tracks typedef-bound names and promotes matching `ID` tokens to the `TYPE_NAME` terminal so the parser unambiguously routes `Color * x;` as a declaration when `Color` is a typedef-name
- `CSharpEmitter` — impl of generated `C.IVisitor<EmitContent>` (one `Visit` method per AST record). Returns an `EmitContent` discriminated union; most variants are `Text` (C# code), but `SpecList` / `Args` / `EnumItems` / `InitMembers` / `FnHeader` carry structured intermediate data
- `BuildShell` — C# scaffolding around emitted functions (top-level statements + entry-point wiring, struct/typedef section, `using static Libc`, embedded-runtime splice point); argv UTF-8 marshalling for `int main(int, char**)`
- `_runtimeBlock` (`Compiler.LoadRuntimeBlock`) — reads every `DotCC.Libc/*.cs` source file from the assembly manifest (they're embedded as resources at build time; see "Synthetic system headers + runtime" below) and concatenates them into the type-decls section of every emitted file. Single source of truth: edit `DotCC.Libc/MathLib.cs` and the change lands both in unit tests AND in every emitted program
- `SystemHeaders` — synthetic `.h` files (`stdio.h`, `stdlib.h`, `stddef.h`, `stdbool.h`, `math.h`, `tgmath.h`) under `DotCC.Lib/include/`, embedded as resources so the parser sees the signatures with no disk I/O at runtime
- `CompileException` — wraps `ParseErrorException` from LALR.CC into a stable public surface

The generated `DotCC.C` partial class — produced from `c.lalr.yaml` at build time by `LALR.CC.SourceGenerators` — exposes:

- `C.BuildLexer()` / `C.BuildParser(visitor)` — wire the runtime pipeline
- `C.WrapPreprocessor(lexer, impl)` — slot a user `IPreprocessor` between lexer and parser
- `C.IVisitor<T>` — one `Visit(C.<RecordName>)` per `action:` named in the YAML
- `C.IPreprocessor` — one method per directive declared in the `preprocessor:` block, plus `Rewrite(Item)` for macro expansion and `IsDefined(string)` for the `#if defined(...)` engine
- `C.<RecordName>` AST records — one per action (Arg0/Arg1/... carry the matched Items)

If you change the grammar (`c.lalr.yaml`), the generated surface changes in lockstep: add a new `action: Foo` rule and you'll get a compile error on the visitor until you add `string Visit(C.Foo n) => ...`.

`LALR.CC`'s `Parser.ParseInput` defaults to `ParserErrorMode.Throw` — `Compiler.EmitCSharp` catches `ParseErrorException` and re-raises as `CompileException` so callers (frontend + tests) only need to know one exception type.

### CLI surface (clang-shaped)

| Flag | Meaning |
|---|---|
| `dotcc <a.c> <b.c>` | Compile translation units. Default: write `Program.cs + dotcc-out.csproj` to `./a.out-cs/`. |
| `-o <dir>` | Output directory for csproj/build modes. |
| `--emit=csharp` | Write a single .NET 10 file-based program (`#:property AllowUnsafeBlocks=true`) to stdout. Pipe to a `.cs`, then `dotnet run <file>`. |
| `--emit=csproj` | Default — write `Program.cs` + paired csproj to `-o` dir. |
| `--emit=build` | As `csproj`, then run `dotnet build -c Release` in the output dir. |
| `-E` | Preprocess only — dump the post-`#include`/`#define` token stream to stdout. No parsing. |
| `-I <dir>` | Add header search directory. Repeatable. Auto-includes each `<input>.c`'s directory. |
| `-D NAME[=VALUE]` | Predefine a macro. Repeatable. With `=VALUE`, the right-hand side is lexed through the same byte lexer the parser uses, so use-site substitution behaves like an in-source `#define`. Without `=`, the macro is a defined-as-marker (empty body). |
| `-c` | Compile to .NET assembly (no native publish). Clang-shaped alias for `--emit=build`. |
| `-shared` | Produce a shared library: csproj configured for `<NativeLib>Shared</NativeLib>` + `<PublishAot>true</PublishAot>`, non-static C functions exported via `[UnmanagedCallersOnly(EntryPoint = "name", CallConvs = …CallConvCdecl…)]`. `main` not required. Run `dotnet publish -c Release -r <RID>` in the output dir to produce the actual native `.dll`/`.so`/`.dylib`. |
| `-std=<dialect>` | C dialect: `c90`/`c99`/`c11`/`c17`/`c18`/`c23`. Default: `c17`. v1 effect is predefined macros only — `__STDC_VERSION__` gets the right per-dialect value (omitted for `c90` per spec) so synthetic / user headers can `#if __STDC_VERSION__ >= 199901L`-gate. The parser is dialect-agnostic; `//` comments, `_Bool`, and other "modern" constructs are accepted regardless. `c89` is omitted in favor of the canonical `c90` (same standard, ISO renumbering only); no `gnu*` variants — dotcc implements no GNU extensions, so the parallel toggle would be dead surface. |
| (no inputs) | Error and exit non-zero — same as clang. |

**Predefined macros (seeded by every compile, in addition to any `-D` predefines):**

| Macro | Value | Notes |
|---|---|---|
| `__STDC__` | `1` | Always defined — hosted-conforming compiler indicator. |
| `__STDC_HOSTED__` | `1` | We are hosted; `<stdio.h>` and friends exist. |
| `__STDC_VERSION__` | `199901L` / `201112L` / `201710L` / `202311L` | Set per `-std=`; **undefined** under `-std=c90`. |
| `__dotcc__` | `1` | Compiler identification — analogous to `__clang__` / `__GNUC__`. |

**Library mode (`-shared`) emit shape**: user functions land in `internal static class DotCcLib { … }` so calls between them resolve as direct C# method invocations (the `[UnmanagedCallersOnly]` attribute prohibits managed-call sites — wrappers can only be invoked through a function pointer). Each non-static C function gets a matching `public static …` wrapper in `public static class DotCcExports` annotated with `[UnmanagedCallersOnly]`; NativeAOT publish inlines the wrapper trampoline. C `static` functions stay internal — no export wrapper. Varargs functions are skipped from exports (C# `params object[]` isn't a valid `UnmanagedCallersOnly` signature).

`stdio.h` and `stdlib.h` are baked in as synthetic headers (`Compiler.SystemHeaders`) so the parser knows the signatures of `printf` / `malloc` / `free`; user `-I` headers win on name collisions (matches clang's quoted-include rule).

### Code generation strategy

**Today (initial low-level target — matches LALR.CC's CMinus example):**

| C | Emitted C# |
|---|---|
| `int` / `float` / `double` / `void` | `int` / `float` / `double` / `void` |
| `_Float128` / `__float128` (C23) | `Float128` — MIT software IEEE-754 binary128 (`DotCC.Libc/Float128.cs`), clean-room. Full arithmetic + the complete `<math.h>` surface (algebraic correctly-rounded; transcendentals via a BigInteger fixed-point core) — every op validated against gcc's binary128 (bit-exact where correctly-rounded, ≤2–4 ULP for transcendentals). `%Lf`/`%Le`/`%Lg` printing + decimal Parse; implements `IBinaryFloatingPointIeee754<Float128>`. |
| `char` | `byte` (so `char*` arithmetic walks bytes) |
| `T*` | `T*` (unsafe pointer) |
| `"foo"` | `L("foo\0"u8)` — pinned UTF-8 RVA pointer via `MemoryMarshal.GetReference` |
| `malloc(n)` / `free(p)` | `malloc((int)n)` / `free(p)` — backed by `System.Runtime.InteropServices.NativeMemory`. Names match C; `using static Libc;` brings them into scope by bare name |
| `printf("%d %s", x, s)` | `printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — fluent ref-struct builder (avoids `params object[]` boxing of raw pointers). `fprintf(stream, fmt, …)` follows the same shape with the `TextWriter` as the first arg |
| `sin(x)` / `sinf(x)` / `sqrt(x)` / … | `sin(x)` / `sinf(x)` / `sqrt(x)` — same name, resolved at C# overload-resolution time. `double` overloads route to `System.Math`, `float` overloads to `System.MathF`. That dispatch is exactly what `<tgmath.h>` does in real C |
| C function | `static unsafe` local function at top level |
| Prototype / forward decl | empty emit (C# methods hoist) |

**Self-contained emit, no inlining duplication.** Every emitted file pulls in the DotCC.Libc runtime via a single block spliced in by `BuildShell` from embedded resources. The `DotCC.Libc/*.cs` source files (`Libc.cs`, `MathLib.cs`, `PrintfBuilder.cs`, `ScanfReader.cs`, `SprintfBuilder.cs`) are double-purposed: they compile into `DotCC.Libc.dll` for the unit tests AND are marked `<EmbeddedResource>` in `DotCC.Lib.csproj` so the compiler can read them at emit time. `Compiler.LoadRuntimeBlock` strips file-scope artifacts (`#nullable enable`, `using` directives, `namespace DotCC.Libc;`) and concatenates the rest into the emitted file's type-decls section. `using static Libc;` at the top of the emitted file then brings every method into scope by bare name. **Single source of truth** — edit `MathLib.cs`, both the unit-tested DLL and every emitted program pick up the change on the next build, with no copy-paste between two locations.

**Roadmap (intentional direction — partly in place, partly not):**

1. **Once `DotCC.Libc` ships to NuGet**, the embedding goes away. The shell would emit `#:package DotCC.Libc@<ver>` (file-based) or `<PackageReference Include="DotCC.Libc" />` (csproj) at the top, drop the `{{runtimeBlock}}` splice, and let `using static DotCC.Libc.Libc;` resolve everything. No runtime change needed in `Libc.cs`/`MathLib.cs`/etc. — they're already the right shape; the embedding is just a deployment workaround for being pre-NuGet.
2. **Prefer idiomatic C# types where the source's usage allows.** E.g. when an `int*` from `malloc(N*4)` is only ever accessed via `*(arr + i)`, lower it to `int[]` and rewrite the access to `arr[i]`; when a `char*` is only used as a `printf` argument or compared to another `char*`, lower it to `string`. The low-level form stays as the fallback when usage analysis can't prove safety.
3. **Grow the grammar past CMinus.** Mostly in place — see `C-SUPPORT.md` for the per-feature view. Next milestones: `struct` (always lowered to C# struct — value type, matches C semantics exactly; supports stack-allocated locals AND heap-via-malloc through unsafe pointers), then `typedef` (the classic lexer hack — distinguish typedef-name from identifier at parse time). Logical `!`, `do { } while`, and ternary `?:` are small follow-ons.

4. **Peephole: same-function `malloc`/`free` → stack-allocated struct value.** When the AST shows `S* p = malloc(sizeof(S))` and a matching `free(p)` within the same function, with `p` only used through `->` (no pointer arithmetic, no escape via function args, no comparison with other pointers), rewrite the variable as `S p = new S();` (stack-allocated struct value), the `->` accesses as `.`, and drop the `free`. Path A is enough for this — `new S()` on a C# struct is a value-initialization expression, not a heap allocation, so no class wrapper is needed. The check is purely syntactic over a single function — no whole-program escape analysis required.

The single-source-of-truth design intent: **the same `.c` file should compile under both `dotcc` and `clang -std=c99` and produce equivalent observable behavior** — keep the grammar a strict subset of real C, and keep the emitter's output semantics aligned with the C abstract machine.

## Testing

Two projects, both xUnit v3 on Microsoft.Testing.Platform.

**Unit tests (`DotCC.Tests/`).** Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, assert on the returned string. Cover things like:
- emitted C# contains `static unsafe int main`
- `--emit=csproj` mode omits the `#:property` file-based-program header
- parse errors raise `CompileException`
- missing `main` raises `CompileException`
- `#define` substitutes through the token stream

These tests need no fixture files — they write a temp `.c`, call the API, assert, delete. Fast (~5 tests in well under a second).

**Functional tests (`DotCC.FunctionalTests/`).** Fixture-driven. For each `Fixtures/<name>/` directory containing one or more `.c` files and an `expected-stdout.txt` sidecar:

1. `Compiler.EmitCSharp(...)` in-process → C# source string
2. `CSharpCompilation.Create(...)` with `OutputKind.ConsoleApplication`, `allowUnsafe: true` → emit to `MemoryStream`
3. Fresh `AssemblyLoadContext.LoadFromStream` → reflect entry point → invoke with `Console.Out` redirected
4. Captured stdout `ShouldBe` `expected-stdout.txt` (with `ReplaceLineEndings("\n").TrimEnd('\n')` so trailing newlines / CRLF differences don't false-fail)

**Adding a new fixture is just dropping a folder.** `FixtureRunner.Discover()` walks `Fixtures/` at runtime, and the `[Theory] [MemberData]` test materialises one case per directory. No code change needed.

This is intentionally **the same shape as compiler test suites generally** — golden C in, captured stdout compared to a sidecar. Use this layer for "does this language feature work end-to-end?" tests. Use `DotCC.Tests` for "does this emitter rule produce the expected snippet?" tests.

**MSVC oracle (`MsvcOracleTests.cs`).** Compares each fixture's dotcc emit against a real cl.exe build of the same source. The `expected-stdout.txt` per fixture is the **committed snapshot** — authored to match what MSVC produces. The regular `FixtureTests` already validates dotcc against that snapshot in-process; the MSVC oracle adds the extra check "MSVC live still agrees with this snapshot". cl.exe + program invocation per fixture is slow (~0.5s each after the vcvars cache), so the oracle is **opt-in** rather than running on every dev cycle. Three modes via env vars:

| Mode | When | Behaviour |
|---|---|---|
| **Default** (no env var) | Most dev runs | Oracle tests skip with a hint. `FixtureTests` handles acceptance against the committed `expected-stdout.txt`. |
| **`DOTCC_RUN_MSVC_ORACLE=1`** | CI on Windows agents; pre-merge gate | cl.exe runs per fixture; asserts MSVC's output equals both dotcc's emit AND the committed `expected-stdout.txt`. Surfaces drift between the snapshot and live MSVC (e.g. compiler-version behavior change). |
| **`DOTCC_REGEN_BASELINE=1`** | After intentional behavior change in dotcc; new fixture bootstrap | Same as run mode, but when MSVC's output differs from `expected-stdout.txt` the file is rewritten in-place at the **source path** (resolved by walking up from `AppContext.BaseDirectory` to the `DotCC.FunctionalTests.csproj` root). Review + commit the diff. |

In all opt-in modes, if MSVC isn't available on the host (non-Windows or no VS install), tests skip with a clear message rather than fail. A fixture can opt out of just the MSVC oracle with a **`no-msvc-oracle.txt`** sidecar (symmetric with the gcc oracle's `no-gcc-oracle.txt`); its contents are the skip reason. Used by `float128-basic`, since cl.exe has no `_Float128`.

**gcc oracle (`GccWslOracleTests.cs`).** A second, independent reference compiler — gcc, run inside WSL (`wsl.exe bash -lc "gcc -std=c17 … -o … -lm && ./…"`, sources bridged to `/mnt/…` via `wslpath`). Same snapshot model as the MSVC oracle: it asserts gcc still agrees with the committed `expected-stdout.txt` AND dotcc's emit. Its value is covering ground MSVC can't — e.g. MSVC's C frontend rejects the C23 bare `bool` keyword even under `/std:clatest` (there's no `/std:c23` at all), whereas gcc accepts it under `-std=c2x`. Opt-in via **`DOTCC_RUN_GCC_ORACLE=1`**; add **`DOTCC_REGEN_BASELINE=1`** alongside it to refresh the baseline from gcc (regen here requires the gcc run flag too, so a plain `DOTCC_REGEN_BASELINE=1` — which drives the MSVC oracle — doesn't silently hand baseline authority to gcc). Skips cleanly when `wsl.exe`/gcc isn't reachable. A fixture can opt out of just this oracle by dropping a **`no-gcc-oracle.txt`** sidecar whose contents are the skip reason — used by `float-limits`, where `LDBL_DIG` depends on the ABI's `long double` width (128-bit on Linux/arm64 → 33; dotcc maps `long double` → C# `double` → 15, matching MSVC, which is the committed snapshot).

### Grammar conventions

`c.lalr.yaml` follows LALR.CC's YAML schema. Things to remember when editing it:

- **`symbols:` ordering is meaningful** — index = symbol ID, LHS=0 is the start symbol. Don't reorder existing entries; append.
- **Precedence groups order matters** — listed lowest → highest precedence in the YAML, used to resolve S/R and R/R conflicts via `derivation: leftmost|rightmost|none`.
- **Dangling-else** is handled by putting the two `if` rules in a `rightmost` group (so `else` shifts onto the nearest open `if`).
- **No alternation in lexer regexes.** Express alternatives as multiple `LexRule`s — longest match wins, first-rule-wins on ties.
- **Preprocessor directives are declared in `preprocessor:`** at the bottom of the YAML. Each one becomes a method on the generated `IPreprocessor`. Conditionals (`#if`/`#ifdef`/`#ifndef`/`#else`/`#endif`) are handled by LALR.CC's built-in `PreprocessorTokenStream` engine — declare them in `conditionals:` to wire it on; the `IsDefined` hook on `IPreprocessor` is what drives the boolean evaluation.

If a grammar change introduces an unresolved conflict, `Parser`'s constructor throws `GrammarConflictException` with the offending state + lookahead — don't catch it; fix the grammar by adding the colliding productions to a precedence group.

## Conventions to respect

- **Conditional contexts use `Cond.B(...)`**. C says non-zero ints and non-null pointers are truthy; C# wants `bool`. The visitor wraps every `if` / `while` / `for`-cond with `Cond.B(E)` and `BuildShell` emits an overloaded static class with `B(bool)` / `B(int)` / `B(double)` / `B(void*)` so overload resolution at C# compile time picks the right form. Any new grammar production that takes an expression in a conditional position (ternary, `do { ... } while (E)`, etc.) **must** wrap E the same way.
- **AOT-clean.** `DotCC.Lib` is `<IsAotCompatible>true</IsAotCompatible>` and the frontend exe is `<PublishAot>true</PublishAot>`. No reflection beyond BCL collections, no `dynamic`, no runtime-codegen serializers. The LALR.CC source generator runs at *build* time — YamlDotNet never makes it into the runtime closure (it's `PrivateAssets="all"`). Run `dotnet publish -c Release` periodically to catch trim/AOT regressions.
- **No `Process.Start` from the library, and none on the dotcc round-trip path.** The frontend exe uses it for `--emit=build` (invoking `dotnet build` on the generated csproj). In tests, `Process.Start` is confined to the **opt-in differential oracles** (`MsvcOracleTests` → cl.exe + vcvars; `GccWslOracleTests` → `wsl.exe`/gcc) — never on the always-on path, where Roslyn in-process drives the dotcc-emitted code end-to-end. Adding `Process.Start` to the default test path (or to `DotCC.Lib`) would be a regression on the structural intent.
- **Keep `dotcc` and `clang -std=c99` round-trippable.** When extending the grammar, prefer real-C syntax over inventions; when extending the emitter, prefer lowerings that preserve C's observable semantics (overflow, signedness, side-effect order, evaluation order at sequence points).
- **C# types are `unsafe` in emitted code** (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is set on the generated csproj and on `DotCC.FunctionalTests` where Roslyn runs the emitted code). `DotCC.Lib` itself is **not** unsafe — it produces unsafe C# as a string, but doesn't run any.
- **Don't pull more deps without checking AOT compat.** The lib's closure is intentionally `LALR.CC` + `YamlDotNet` (build-time only). The frontend adds `System.CommandLine`. Tests add `xUnit`/`Shouldly`/`Microsoft.CodeAnalysis.CSharp`.
- **Watch the XML.** Comments in `Directory.Build.props` / `Directory.Packages.props` can't contain `--` (XML forbids it). Easy to miss when you type `dotnet run --file`.
