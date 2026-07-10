# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**dotcc** is a clang-like compiler frontend that transpiles **C** to .NET 10 / C# 14 (AOT-clean, `unsafe` code), with a **WebAssembly-text backend** (`--target=wat`) alongside the C# one, and a **second front-end for Zig** — C and Zig are parsed by their own LALR(1) grammars (`DotCC.Lib/c.lalr.yaml`, `DotCC.Lib/zig.lalr.yaml`) but lower to one shared typed IR, C#/wat backend, and libc-shaped runtime. It is driven entirely by [SharpAstro.LALR.CC](https://github.com/sharpastro/LALR.CC): the source generator emits a typed AST + `IVisitor` surface from each grammar at compile time; the rest is a thin shell wiring the pipeline (lexer → preprocessor → parser → typed-IR lowering → backend) into a clang-shaped CLI.

## Docs map — details live under `docs/`, this file stays thin

| Doc | Contents |
|---|---|
| [`docs/C-SUPPORT.md`](docs/C-SUPPORT.md) | **C feature coverage — the source of truth**: lexical, types, operators, statements, preprocessor, every libc function, dialect gates, out-of-scope. **Update it when a feature lands**; this file defers to it rather than duplicating lists. |
| [`docs/ZIG-SUPPORT.md`](docs/ZIG-SUPPORT.md) | Zig front-end coverage + curated `std` surface + the three-tier out-of-scope reasoning. Same update rule. |
| [`docs/architecture.md`](docs/architecture.md) | The full architecture reference: pipeline stages, every class `DotCC.Lib` contributes, the generated grammar surface, the code-generation strategy table, sibling-or-NuGet LALR.CC wiring, grammar conventions. |
| [`docs/cli.md`](docs/cli.md) | The full clang-shaped flag reference (`--emit` modes, `-l`/`-L` import mode, `-std=`/`-pedantic`, warning flags, `-fsanitize=address`), predefined macros, `-shared` emit shape. |
| [`docs/testing.md`](docs/testing.md) | Test-suite anatomy + all opt-in differential oracles (MSVC / gcc-in-WSL / zig / native shared-lib) with their env vars and baseline-regen rules. |
| [`docs/FRONTEND-IDEAS.md`](docs/FRONTEND-IDEAS.md) | Design rationale for growing new front-ends on the shared IR. |
| `docs/plans/` | Campaign plans: `fable-c.md` / `fable-zig.md` (exhausted), `fable-wall.md` (the completed W0–W6 generics arc), `road-to-zig-std.md` (current — compiling real zig std from source), `fable-wasm.md` (active — a binary-`.wasm` frontend, end-goal consuming Embedded Swift; WF0 done), `fable-web.md` (planned — GitHub Pages site + in-browser sandbox running dotcc as wasm via Blazor + the wat backend), `import-mode.md`. |

## Solution layout

| Project | Role |
|---|---|
| `DotCC.Lib/` | **The actual compiler** (net10.0, `IsAotCompatible=true`): grammars, front-ends (`Frontends/` — `CFrontend`, `ZigFrontend`+`ZigLowering`), typed IR (`Ir/` — `IrBuilder`, `CExpr`/`CStmt`, `CType`), backends (`Backends/` — `CSharpBackend`, `WatBackend`), `CPreprocessor`, and the public `Compiler.EmitCSharp`/`EmitWat`/`Preprocess` entry points. |
| `DotCC.Libc/` | The libc-shaped runtime emitted programs link against (each function routes to the obvious BCL primitive). **Same source, two consumers:** the `.cs` files compile into `DotCC.Libc.dll` for the tests AND are embedded resources in `DotCC.Lib` that `Compiler.LoadRuntimeBlock` splices into every emitted program (single source of truth; drops out once published to NuGet). |
| `DotCC/` | Clang-shaped CLI exe (`PublishAot=true`, assembly name `dotcc`) via `System.CommandLine` — ~150 lines, no compiler logic. All testable logic lives in `DotCC.Lib`, reachable in-process from both test projects. |
| `DotCC.Tests/` | xUnit v3 + Shouldly unit tests: emit pins over inline sources + direct `Libc` function tests. Fast, no I/O beyond a temp file. |
| `DotCC.FunctionalTests/` | End-to-end fixture tests (dotcc → Roslyn in-process → invoke → compare stdout to the committed sidecar) + the opt-in differential oracles. No test spawns `dotcc.exe`. |
| `DotCC.Web/` | Blazor WebAssembly **in-browser sandbox** (fable-web.md, WEB1): runs `DotCC.Lib` as wasm, compiles C via `EmitWat`, assembles with vendored `libwabt.js`, runs via a `fd_write` shim. Refs `DotCC.Lib` only; **outside `dotcc.sln` + CPM**, so CI never builds it (its own GH Pages workflow does — WEB4). |
| `examples/` | Hand-written programs demonstrating the language surface; independent from test fixtures. |

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

`dotcc` with no input files exits non-zero with `dotcc: error: no input files` (matches clang). The CLI is deliberately clang-shaped — `-o`/`--emit` (file/csproj/build/obj + inference), `-E`, `-I`, `-D`, `-MD`/`-MF`/`-MT`, `-c`, `-shared`, `-l`/`-L` import mode, `-std=`, `-pedantic(-errors)`, `-W…` warning flags, `-fsanitize=address` — full semantics in [`docs/cli.md`](docs/cli.md). dotcc is an **LP64** compiler (`long` → C# `long`, 8-byte pointers; predefined-macro list ibid).

## Architecture in one screen (full reference: [`docs/architecture.md`](docs/architecture.md))

N frontends × M backends meeting at one **typed IR** — today 2×2 (C, Zig → C#, wat) with no pairwise special-casing. Two seams: `IFrontend` (lex/parse a language, bind it into the `IrBuilder`) and `ITarget` + per-target backend printers.

```
.c file → BytesLexer
        → PreprocessorTokenStream  (#include/#define/#if…; object-like macro substitution)
        → MacroExpander            (function-like macros)
        → DialectKeywordRewriter   (dialect-aware keyword promotion, gated on -std=)
        → TypeNameRewriter         (C lexer hack: ID → TYPE_NAME after typedef)
        → SizeofFolder             (fold sizeof(T) → literal)
        → Parser                   (LALR(1) tables from c.lalr.yaml; parse tree yielded raw via IdentityVisitor)
        → IrBuilder.AddUnit        (bind to typed IR: CExpr/CStmt with a CType on every expression)
        → CSharpBackend / WatBackend → BuildShell(...)
```

The Zig front half is the same shape behind the same seam: `zig.lalr.yaml` → `ZigLowering` binds into the **same** `IrBuilder`; a mixed `.c` + `.zig` input set lowers into one IR module. Everything from the IR down is shared and frontend-agnostic.

Load-bearing rules:
- **Types are structural, not incidental.** Every IR expression carries a `CType`; every recognition (malloc→stack promotion, `_Generic`, `sizeof` folding, enum decay) is structural over IR nodes + the type spine — **never text-matching on emitted output**.
- **Fail loudly, grow on purpose.** An unhandled grammar `action:` record falls to the binder's `default:` → a loud `IrUnsupportedException` naming the node type. Emit pins for every new production keep that gap closed.
- **Grammar edits:** `symbols:` are append-only (index = symbol ID); an unresolved conflict throws `GrammarConflictException` — fix the grammar via precedence groups, never catch it. Full conventions in [`docs/architecture.md`](docs/architecture.md#grammar-conventions).
- **Emit shape:** `char` → `byte`, `"foo"` → `L("foo\0"u8)` (pinned UTF-8 RVA), `printf` → fluent ref-struct builder, C functions → `static unsafe` methods on a top-level class (so `&fn` works). Full codegen table in [`docs/architecture.md`](docs/architecture.md#code-generation-strategy).
- **LALR.CC wiring:** sibling working copy auto-detected (`UseLocalLalrCc`, `Directory.Build.props`); CI always builds the NuGet path. Details ibid.

## Testing essentials (full reference + oracle env vars: [`docs/testing.md`](docs/testing.md))

- **Unit (`DotCC.Tests`)**: emit pins — compile inline sources, assert on the emitted string.
- **Functional (`DotCC.FunctionalTests`)**: a fixture = a folder under `Fixtures/` with sources + `expected-stdout.txt`; dotcc → Roslyn compile in-process → invoke with console redirected → compare. Adding a fixture is just dropping a folder.
- **Differential oracles are opt-in env-gated** (MSVC, gcc-in-WSL, zig, native shared-lib round-trip); the committed sidecar IS the cached reference output. **`Process.Start` is confined to oracle modes** — never on the always-on path.
- After a `.lalr.yaml` edit, FULL-build before testing (a stale generated parse table makes a correct grammar change look broken). Run suites serially, never two test processes in parallel.

## Conventions to respect

- **Conditional contexts use `Cond.B(...)`.** C says non-zero ints and non-null pointers are truthy; C# wants `bool`. The visitor wraps every `if`/`while`/`for`-cond with `Cond.B(E)`, and `BuildShell` emits an overloaded static class (`B(bool)`/`B(int)`/`B(double)`/`B(void*)`) so C# overload resolution picks the right form. Any new production with an expression in conditional position (ternary, `do…while(E)`) **must** wrap E the same way.
- **C-semantics coercions live on the lowered *type*, not on emitter rewrites.** `_Bool` lowers to the `CBool` value type (`DotCC.Libc/CBool.cs`), which carries C's store-normalization via **implicit conversion operators** (`int`/`long`/`double`/`bool`/`void*` → CBool, CBool → `int`). Because it's on the type, *every* store position coerces uniformly with no emitter pass — including pointer stores (`_Bool b = p;` works: `T*` → `void*` → CBool is one standard + one user-defined step, which C# permits). **C# does NOT forbid user-defined conversions to/from pointer types** — a mistaken code comment once nearly drove an emitter-side `Cond.B`-injection workaround; prefer the type-level operator. New C types needing store-coercion: add the conversion to the lowered C# type before reaching for an emitter rewrite.
- **AOT-clean.** `DotCC.Lib` is `IsAotCompatible`; the frontend is `PublishAot`. No reflection beyond BCL collections, no `dynamic`, no runtime-codegen serializers. The LALR.CC generator runs at *build* time (YamlDotNet is `PrivateAssets="all"`, never in the runtime closure). Run `dotnet publish -c Release` periodically to catch trim/AOT regressions.
- **No `Process.Start` from the library, and none on the dotcc round-trip path.** The frontend uses it for `--emit=build`. In tests it's confined to the opt-in oracles. Adding it to the default test path or to `DotCC.Lib` is a regression on the structural intent.
- **Keep `dotcc` and `clang -std=c99` round-trippable.** Extending the grammar: prefer real-C syntax over inventions. Extending the emitter: prefer lowerings that preserve C's observable semantics (overflow, signedness, side-effect/evaluation order at sequence points).
- **Emitted C# is `unsafe`** (`AllowUnsafeBlocks` on the generated csproj and on `DotCC.FunctionalTests`). `DotCC.Lib` itself is **not** unsafe — it produces unsafe C# as a string but runs none.
- **Don't pull more deps without checking AOT compat.** The lib's closure is intentionally `LALR.CC` + `YamlDotNet` (build-time only). The frontend adds `System.CommandLine`; tests add `xUnit`/`Shouldly`/`Microsoft.CodeAnalysis.CSharp`.
- **Watch the XML.** Comments in `Directory.Build.props` / `Directory.Packages.props` can't contain `--` (XML forbids it). Easy to miss when you type `dotnet run --file`.
