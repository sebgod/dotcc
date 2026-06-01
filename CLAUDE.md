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
dotnet run --project DotCC -c Release -- --emit=file examples/hello/main.c examples/hello/math.c > out.cs
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
- `DialectKeywordRewriter` — `RewritingTokenStream` subclass doing dialect-aware keyword promotion. A data table maps `(identifier spelling → MinVersion + target terminal)`; an `ID` is promoted only when the active `CDialect.Version` ≥ `MinVersion`. `CDialect.Version` is keyed by **ISO year** (1990/1999/2011/2017/2023) precisely so it's monotonic — the gate is a plain `Version >= year` with no footgun (an earlier design keyed it by the short `90/99/11/17/23` suffix, which sorts `c11` below `c99` and silently mis-gated C99 keywords under the default `c17`). Rows today: `inline → inline` (1999), and `bool → _Bool` / `true`/`false`/`nullptr` / `static_assert` / `noreturn → _Noreturn` / `typeof` / `typeof_unqual → typeof` (2023). This is "rule 2" of the dialect-gating model: keywords spelled like identifiers (`inline`/`bool`/`true`/`nullptr`/…) can't be gated in the visitor — `int true = 5;` / `int inline = 5;` are valid older code, so always-treating them as keywords would parse-error before semantics ran. The flip side: under an older `-std=` the spelling stays an identifier, so the *feature* isn't available there even permissively (e.g. `inline int f()` is a parse error under `c90`) — the rule-2 rejection is structural, no `DialectGate` row needed. Sits after `MacroExpander` (so an included header's `#define bool _Bool` wins and the table doesn't fire) and before `TypeNameRewriter`. Genuinely new *syntax* (`_BitInt`, `typeof`, `_Generic`) is instead gated in the visitor; `_Capital_` keywords are always-accepted (reserved in every dialect)
- `TypeNameRewriter` — `RewritingTokenStream` subclass implementing the C lexer hack: tracks typedef-bound names and promotes matching `ID` tokens to the `TYPE_NAME` terminal so the parser unambiguously routes `Color * x;` as a declaration when `Color` is a typedef-name
- `CSharpEmitter` — impl of generated `C.IVisitor<EmitContent>` (one `Visit` method per AST record). Returns an `EmitContent` discriminated union; most variants are `Text` (C# code), but `SpecList` / `Args` / `EnumItems` / `InitMembers` / `FnHeader` carry structured intermediate data. Also owns **block-scope local renaming** (CS0136 avoidance): a scope stack maps each raw C name to the unique C# identifier it was emitted as, pushed at block entry (the epsilon `ScopeEnter` marker reduced after `{` / a `for (` decl header — the only bottom-up way to get a *block-entry* hook, since the `Block` / `stmtForDecl` action is the matching *exit*) and popped at exit. A colliding declaration gets a fresh `name__k`; references resolve through the stack. This makes C's legal nested-vs-enclosing same-name locals (which C# rejects) compile — params keep their spelling, the inner local is renamed. All side tables stay keyed by the raw C name, so the rename rides purely the emitted text
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
| `dotcc <a.c> <b.c>` | Compile translation units (whole-program). Default: write `Program.cs + dotcc-out.csproj` to `./a.out-cs/`. **`.cs` inputs are treated as object fragments → link them** (see `--emit=obj`). |
| `-o <path>` | Output. A directory for csproj/build; a file for `file`/`obj`. **Inferred when omitted** (`obj` → `<src>.cs`, csproj/build → `./a.out-cs/`, file → stdout). |
| `--emit=file` | Write a single .NET 10 file-based program (`#:property AllowUnsafeBlocks=true`). To `-o <file>` if given, else stdout (pipe to a `.cs`, then `dotnet run <file>`). (Renamed from `--emit=csharp` — every mode emits C#, so the name now describes the *artifact*: a single runnable file.) |
| `--emit=csproj` | Default — write `Program.cs` + paired csproj to `-o` dir. |
| `--emit=build` | As `csproj`, then run `dotnet build -c Release` in the output dir. |
| `--emit=obj` | **Separate compilation.** Compile ONE `.c` to a `.cs` object fragment (the TU's emitted C# — functions + its type decls + globals — no shell/runtime; the LTO-style intermediate). Link by passing the `.cs` objects back: `dotcc a.cs b.cs -o app` merges them (deduping shared types) and wraps in the shell. This is what a CMake/make toolchain drives per file (`examples/cmake-demo/`). |
| **`-o` ⇄ `--emit` inference** | When one is omitted it's inferred from the other: `-o foo.cs` (no `--emit`) ⇒ `file`; `-o <dir>` ⇒ `csproj`; `--emit=obj` with no `-o` ⇒ `<src>.cs`. An explicit `--emit` always wins; `obj` is never inferred (ask for it). |
| `-E` | Preprocess only — dump the post-`#include`/`#define` token stream to stdout. No parsing. |
| `-I <dir>` | Add header search directory. Repeatable. Auto-includes each `<input>.c`'s directory. |
| `-D NAME[=VALUE]` | Predefine a macro. Repeatable. With `=VALUE`, the right-hand side is lexed through the same byte lexer the parser uses, so use-site substitution behaves like an in-source `#define`. Without `=`, the macro is a defined-as-marker (empty body). |
| `-MD` / `-MMD` | **Header-dependency file.** Alongside compilation, write a Make-format rule (`Compiler.EmitDependencyRule`) listing the TU + every `#include`d header (transitively), so CMake/Ninja/Make recompile a unit when a header it pulls in changes. `-MMD` drops angle (`<...>`) headers; synthetic embedded system headers (no disk path) are always omitted — nothing for make to stat. The scan honors `#if`/`#ifdef` (a header behind a false branch isn't listed) via a focused preprocess-only pass. In `--emit=obj` mode the object is the default rule target; otherwise the source-basename `.cs`. Paths are normalized to `/` and make-special chars (space/`#`/`$`) escaped. |
| `-MF <file>` | Dependency-file output path (with `-MD`/`-MMD`). Defaults to the object/source name with a `.d` extension. |
| `-MT <target>` | Set the dependency rule's target name(s). Repeatable (multiple targets joined by space). Defaults to the output object. CMake passes `-MT <OBJECT>` so the rule key matches its build graph. |
| `-c` | Compile to .NET assembly (no native publish). Clang-shaped alias for `--emit=build`. |
| `-shared` | Produce a shared library: csproj configured for `<NativeLib>Shared</NativeLib>` + `<PublishAot>true</PublishAot>`, non-static C functions exported via `[UnmanagedCallersOnly(EntryPoint = "name", CallConvs = …CallConvCdecl…)]`. `main` not required. Run `dotnet publish -c Release -r <RID>` in the output dir to produce the actual native `.dll`/`.so`/`.dylib`. |
| `-std=<dialect>` | C dialect: `c90`/`c99`/`c11`/`c17`/`c18`/`c23`. Default: `c17`. Sets predefined macros — `__STDC_VERSION__` gets the right per-dialect value (omitted for `c90` per spec) so headers can `#if __STDC_VERSION__ >= 199901L`-gate — and drives the dialect *acceptance* gate (rule-2 keyword promotion via `DialectKeywordRewriter`). Alone it stays **permissive**: the parser is dialect-agnostic, so `//` comments, `_Bool`, designated init etc. are accepted regardless. Add `-pedantic` to *reject* newer features (below). `c89` is omitted in favor of the canonical `c90`; no `gnu*` variants — dotcc implements no GNU extensions. |
| `-pedantic` | Opt into **dialect rejection**: features newer than the selected `-std=` are reported as warnings (to stderr) but still compiled. Off by default. Maps `CDialect.Version` (keyed by ISO year — 1990/1999/2011/2017/2023 — so it's monotonic) against a per-feature introduced-year table. Gated features today: `_Bool` / `long long` / `ll`-suffix / designated initializers / `for`-init declaration / `__func__` / variadic macros / mixed declarations-and-statements / compound literals / hex float literals / array designators / `_Complex` / flexible array member (C99), `_Static_assert` / `_Noreturn` / anonymous struct/union members (C11), `enum : T` / `_Float128` / `#warning` / empty initializer `{}` (C23). (`inline` is dialect-sensitive too but its rejection is structural — the rule-2 rewriter just doesn't promote it pre-C99 — so it needs no `DialectGate` row.) Gates fire at the natural layer: `CSharpEmitter` (syntactic + the mixed-decl per-block accumulator via the `DeclStmtMarker` tag), `CPreprocessor` (variadic macros, `#warning`). `//` comments are intentionally NOT gated (universally accepted; clang/gcc only flag them under `-pedantic` too, lowest value). Implemented as `DialectGate` collecting on the emit pass only. |
| `-pedantic-errors` | Like `-pedantic` but the violations are **errors** — collect-all, then exit non-zero with every violation listed. |
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
2. **Prefer idiomatic C# types where the source's usage allows.** E.g. when an `int*` from `malloc(N*4)` is only ever accessed via `*(arr + i)`, lower it to `int[]` and rewrite the access to `arr[i]`; when a `char*` is only used as a `printf` argument or compared to another `char*`, lower it to `string`. The low-level form stays as the fallback when usage analysis can't prove safety. (`enum` → real C# `enum` is the first instance of this — see item 5.)
3. **Grow the grammar past CMinus.** Mostly in place — see `C-SUPPORT.md` for the per-feature view. Next milestones: `struct` (always lowered to C# struct — value type, matches C semantics exactly; supports stack-allocated locals AND heap-via-malloc through unsafe pointers), then `typedef` (the classic lexer hack — distinguish typedef-name from identifier at parse time). Logical `!`, `do { } while`, and ternary `?:` are small follow-ons.

4. **Peephole: same-function `malloc`/`free` → stack-allocated struct value.** ✅ **Done.** When `S* p = (S*)malloc(sizeof(S))` and a matching `free(p)` live in the same function, with `p` used only through `->` (no escape — not returned, passed, address-taken, indexed, compared, or reassigned), dotcc rewrites the variable to a stack value `S p = new S();`, the `->` accesses to `.`, and drops the `free`. `new S()` on a C# struct is value-initialization, not a heap allocation. **Implementation note:** the verdict needs the *whole* function body, but the pipeline is single-pass bottom-up SDT (the decl reduces before its later uses), so `Compiler.EmitCSharp` runs **two passes** — an analysis pass (`CSharpEmitter` records per-`(function,var)` usage via `EmitContent.SizeofType` / `MallocSizeof` markers + `->`/free/total ref counts, finalising a promotable set at each `FuncDef`) then an emit pass seeded with that set, which decides locally at each node. Recognition is structural (AST markers, ref-count balance `TotalRefs == ArrowRefs + FreeRefs`), not text-matching. Restricted to single-declarator decls of known struct types. Fixture `malloc-stack-promote/`; unit tests cover the promote / escape-via-return / no-free cases.

5. **`enum` → real C# `enum` (all dialects).** ✅ **Done.** C `enum Color { … }` lowers to `enum Color : int { … }` (was `static class { const int }`), preserving the type name, `switch`/`case`, type-safe params, and `ToString`. The catch: C treats enums as plain ints in every expression, but C# enums have asymmetric operator rules (`enum±int` → enum, but `enum&int`/`enum*int`/`enum==int` are all errors). dotcc resolves this with a **lightweight enum-typing synthesis** rather than a full type system: `EmitContent.Text` carries an optional `EnumType`, set by leaf nodes (enum var read via `_localTypes`/`_globalTypes`/`_enumTags`; enumerator ref; enum-returning call via `_fnReturnTypes`) and propagated through transparent wrappers (paren, cast, `++`/`--`, assign). Then any arithmetic/bitwise/relational/shift operator **decays its enum operands to `(int)`** (so it's pure-int C semantics, result int — never an enum), and the int↔enum casts are inserted only at typed **sinks**: decl init / global field (`ReconcileEnumInit`), assignment, `return` (`_currentFunctionReturnType`), `printf` varargs (`Args.ArgEnums`), array index, `Cond.B` (`CondOf`), and compound assignment (`CompoundAssign` expands `m |= x` to `m = (Mode)((int)m | x)` since C# enum `op=` is unreliable). No separate analysis pass — it all rides the existing pass-2 visitor (decls reduce before uses). C23 fixed underlying type `enum Name : Type` parses via a new grammar production (`enumDefTyped`) and maps the C type to the C# base. Fixtures `enum-day/`, `enum-shadow/`, `enum-flags/`, `enum-from-int/`, `enum-underlying-c23/`; unit tests for the emit shape. **Known gaps** (would need a callee-param-type pass): regular calls passing an enum to an `int` param (or vice versa) aren't cast, and struct/union enum-typed fields aren't enum-typed on read.

6. **`sizeof expr` via a type-synthesis layer.** ✅ **Done.** Generalises item 5's enum-typing into a real (if partial) expression-type layer: `EmitContent.Text` carries an optional `CType` (`DotCC.Lib/CType.cs` — `Sized(csType)` or `Arr(element, count)`), set by expression visitors (Var/Num/Flt/Chr/Str/Subscript/Deref/Cast/Paren/Call) and read by `Visit(C.SizeofExpr)`. The motivating case is the array-length idiom `sizeof(a)/sizeof(a[0])`: because dotcc lowers `T arr[N]` to a C# **pointer** (`stackalloc`), C# `sizeof(arr)` would give 8, so `sizeof(arr)` is emitted as `count * sizeof(element)` — dotcc tracks element type + count in `_localArrayInfo` (populated at `DeclArr`/`DeclArrInit`). Everything else (scalars, pointers, structs-as-values, enums) defers to C# `sizeof(type)`. Subtleties handled: `sizeof('a')` is `sizeof(int)` (C char-literal promotion), `sizeof((char)'a')` is 1, `sizeof("hi")` is `len+1`. Grammar: `Unary → 'sizeof' Unary`, conflict-free because the TYPE_NAME lexer hack already splits `sizeof(ID)` (expr) from `sizeof(int)`/`sizeof(typedef)` (type). Unsynthesizable operands raise a clear `CompileException` rather than emit a wrong size. Fixture `sizeof-expr/` (gcc-oracle-validated). **Next consumers of this layer**: struct/union field types (unlocks `sizeof(s.field)` and the enum-field gap from item 5), callee param types (the enum↔param-cast gap), and `char*`→`string` / `int*`→`int[]` idiomatic lowerings (item 2).

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
- **Alternation in lexer regexes is supported** (LALR.CC ≥ 4.0.0): `|` works at the top level and, crucially, *inside* a repetition — which is what the `STRING` rule needs (`"(\\[…]|[^"\\])*"`, re-deciding escape-pair vs plain-char at every position of the `*`). `.` matches any char except `\n`. Still, for *token-level* alternatives prefer multiple `LexRule`s over a top-level `|` — longest match wins, first-rule-wins on ties, and the per-rule form reads clearer (e.g. the keyword/identifier tie-break). Reach for `|` when the choice must live within a single token's pattern.
- **Preprocessor directives are declared in `preprocessor:`** at the bottom of the YAML. Each one becomes a method on the generated `IPreprocessor`. Conditionals (`#if`/`#ifdef`/`#ifndef`/`#else`/`#endif`) are handled by LALR.CC's built-in `PreprocessorTokenStream` engine — declare them in `conditionals:` to wire it on; the `IsDefined` hook on `IPreprocessor` is what drives the boolean evaluation.

If a grammar change introduces an unresolved conflict, `Parser`'s constructor throws `GrammarConflictException` with the offending state + lookahead — don't catch it; fix the grammar by adding the colliding productions to a precedence group.

## Conventions to respect

- **Conditional contexts use `Cond.B(...)`**. C says non-zero ints and non-null pointers are truthy; C# wants `bool`. The visitor wraps every `if` / `while` / `for`-cond with `Cond.B(E)` and `BuildShell` emits an overloaded static class with `B(bool)` / `B(int)` / `B(double)` / `B(void*)` so overload resolution at C# compile time picks the right form. Any new grammar production that takes an expression in a conditional position (ternary, `do { ... } while (E)`, etc.) **must** wrap E the same way.
- **C-semantics coercions live on the lowered *type*, not on emitter rewrites.** `_Bool` lowers to the `CBool` value type (`DotCC.Libc/CBool.cs`), and CBool carries C's store-normalization via **implicit conversion operators** (`int`/`long`/`double`/`bool`/`void*` → CBool, and CBool → `int`). Because the coercion is on the type, *every* store position — decl init, assignment, argument to a `_Bool` param, struct/array element, `return` in a `_Bool` function — coerces uniformly with **no emitter pass and no syntactic-position special-casing**. Notably this includes pointer stores: `_Bool b = p;` (meaning `p != NULL`) works because a typed `T*` reaches the `void*` operator through the standard `T* → void*` conversion (one standard + one user-defined step, which C# permits). **C# does NOT forbid user-defined conversions to/from pointer types** — an earlier code comment claimed it did and that mistaken belief nearly drove an emitter-side `Cond.B`-injection workaround; prefer the type-level operator. When a new C type needs C-flavored coercion at stores, add the conversion to the lowered C# type before reaching for an emitter rewrite.
- **AOT-clean.** `DotCC.Lib` is `<IsAotCompatible>true</IsAotCompatible>` and the frontend exe is `<PublishAot>true</PublishAot>`. No reflection beyond BCL collections, no `dynamic`, no runtime-codegen serializers. The LALR.CC source generator runs at *build* time — YamlDotNet never makes it into the runtime closure (it's `PrivateAssets="all"`). Run `dotnet publish -c Release` periodically to catch trim/AOT regressions.
- **No `Process.Start` from the library, and none on the dotcc round-trip path.** The frontend exe uses it for `--emit=build` (invoking `dotnet build` on the generated csproj). In tests, `Process.Start` is confined to the **opt-in differential oracles** (`MsvcOracleTests` → cl.exe + vcvars; `GccWslOracleTests` → `wsl.exe`/gcc) — never on the always-on path, where Roslyn in-process drives the dotcc-emitted code end-to-end. Adding `Process.Start` to the default test path (or to `DotCC.Lib`) would be a regression on the structural intent.
- **Keep `dotcc` and `clang -std=c99` round-trippable.** When extending the grammar, prefer real-C syntax over inventions; when extending the emitter, prefer lowerings that preserve C's observable semantics (overflow, signedness, side-effect order, evaluation order at sequence points).
- **C# types are `unsafe` in emitted code** (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is set on the generated csproj and on `DotCC.FunctionalTests` where Roslyn runs the emitted code). `DotCC.Lib` itself is **not** unsafe — it produces unsafe C# as a string, but doesn't run any.
- **Don't pull more deps without checking AOT compat.** The lib's closure is intentionally `LALR.CC` + `YamlDotNet` (build-time only). The frontend adds `System.CommandLine`. Tests add `xUnit`/`Shouldly`/`Microsoft.CodeAnalysis.CSharp`.
- **Watch the XML.** Comments in `Directory.Build.props` / `Directory.Packages.props` can't contain `--` (XML forbids it). Easy to miss when you type `dotnet run --file`.
