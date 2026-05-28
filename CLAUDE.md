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
| `DotCC.Libc/` | Library (net10.0, `IsAotCompatible=true`, `AllowUnsafeBlocks=true`) | The libc-shaped runtime surface emitted programs link against. **I/O:** `fprintf` (primitive) / `printf` / `sprintf` / `snprintf` / `fscanf` / `scanf` / `sscanf` / `fputs` / `puts` plus `stdin` / `stdout` / `stderr`. **Memory:** `malloc` / `free` / `memset` / `memcpy`. **C-string:** `strlen` / `strcmp` / `strcpy`. **String-literal lowering:** `L(ReadOnlySpan<byte>)` (pins the UTF-8 RVA). Each function routes to the obvious BCL primitive. Independently growable and unit-tested. Today the emitter still inlines copies of these for self-contained file-based programs; once published to NuGet, emitted programs will reference the package instead. |
| `DotCC/` | Exe (`PublishAot=true`, `<AssemblyName>dotcc</AssemblyName>`) | Frontend. Clang-shaped CLI via `System.CommandLine` — parses args, dispatches to `Compiler`. ~150 lines, no compiler logic. |
| `DotCC.Tests/` | xUnit v3 + Shouldly | Library-level unit tests. Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, AND exercise every `DotCC.Libc.Libc` function (strlen/strcmp/memset/memcpy/strcpy/printf/puts/malloc-free). Fast, no I/O beyond a temp file. |
| `DotCC.FunctionalTests/` | xUnit v3 + Shouldly + Microsoft.CodeAnalysis.CSharp | End-to-end fixture tests. **Two assertions per fixture**: (1) dotcc → Roslyn compile in-process → invoke with `Console.Out` redirected → assert stdout matches `expected-stdout.txt`; (2) when MSVC is on the host (located via `vswhere.exe` → `vcvars64.bat` → `cl.exe`), also compile + run the same `.c` with MSVC and assert dotcc's stdout matches MSVC's byte-for-byte — see `MsvcOracle.cs` / `MsvcOracleTests.cs`. **Process.Start is used only to invoke `cmd.exe` for the oracle**; the dotcc-emitted code runs in-process via Roslyn. |
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
        → TypeNameRewriter         (C lexer hack: promote ID → TYPE_NAME after typedef)
        → SyncLATokenIterator      (one-token lookahead)
        → Parser                   (LALR(1) tables built from c.lalr.yaml)
        → CSharpEmitter            (IVisitor<string> → emitted C# snippets concatenated)
        → BuildShell(...)          (wrap the emitted fn list in a .NET 10 program shell)
```

`PreprocessorTokenStream`, `MacroExpander`, and `TypeNameRewriter` are all `RewritingTokenStream` subclasses (a small upstream base class in LALR.CC that owns the iterator plumbing — ready queue, look-ahead buffer, exhaustion flag — and exposes a `ProcessToken` hook plus `Emit` / `CollectUntil` / `TryReadNext` helpers). The preprocessor handles directives + object-like macro substitution; `MacroExpander` handles function-like macros where `TryReadNext`-based lookahead is needed (peek for the `(`, collect paren-balanced args, substitute formals → actuals, rescan); the typedef rewriter tracks typename state and rewrites `ID` tokens after a `typedef`. Future contextual-keyword or DSL-mode rewriters will plug in the same way — one subclass per policy, mechanics shared.

All four pipeline stages are owned by SharpAstro.LALR.CC. `DotCC.Lib` only contributes:
- `Compiler.EmitCSharp(...)` / `Compiler.Preprocess(...)` — the public entry points (`Compiler.cs`)
- `CPreprocessor` — impl of generated `C.IPreprocessor` (object-like + function-like macros, `#include` quoted and angle, `#pragma once`, `#error`/`#warning`, defined-set tracking)
- `MacroExpander` — `RewritingTokenStream` subclass handling function-like macro calls with paren-balanced arg collection and multi-pass rescan
- `TypeNameRewriter` — `RewritingTokenStream` subclass implementing the C lexer hack: tracks typedef-bound names and promotes matching `ID` tokens to the `TYPE_NAME` terminal so the parser unambiguously routes `Color * x;` as a declaration when `Color` is a typedef-name
- `CSharpEmitter` — impl of generated `C.IVisitor<string>` (one `Visit` method per AST record)
- `BuildShell` — C# scaffolding around emitted functions (PrintfBuilder, malloc/free helpers, argv UTF-8 marshalling)
- `SystemHeaders` — synthetic `stdio.h` / `stdlib.h` so the parser knows the intrinsic signatures
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
| `-D NAME[=VALUE]` | Predefine a macro. Repeatable. v1 stores empty body (defined-as-marker only); rich expansions go inside a header. |
| `-c` | Compile to .NET assembly (no native publish). Clang-shaped alias for `--emit=build`. |
| `-shared` | Produce a shared library: csproj configured for `<NativeLib>Shared</NativeLib>` + `<PublishAot>true</PublishAot>`, non-static C functions exported via `[UnmanagedCallersOnly(EntryPoint = "name", CallConvs = …CallConvCdecl…)]`. `main` not required. Run `dotnet publish -c Release -r <RID>` in the output dir to produce the actual native `.dll`/`.so`/`.dylib`. |
| (no inputs) | Error and exit non-zero — same as clang. |

**Library mode (`-shared`) emit shape**: user functions land in `internal static class DotCcLib { … }` so calls between them resolve as direct C# method invocations (the `[UnmanagedCallersOnly]` attribute prohibits managed-call sites — wrappers can only be invoked through a function pointer). Each non-static C function gets a matching `public static …` wrapper in `public static class DotCcExports` annotated with `[UnmanagedCallersOnly]`; NativeAOT publish inlines the wrapper trampoline. C `static` functions stay internal — no export wrapper. Varargs functions are skipped from exports (C# `params object[]` isn't a valid `UnmanagedCallersOnly` signature).

`stdio.h` and `stdlib.h` are baked in as synthetic headers (`Compiler.SystemHeaders`) so the parser knows the signatures of `printf` / `malloc` / `free`; user `-I` headers win on name collisions (matches clang's quoted-include rule).

### Code generation strategy

**Today (initial low-level target — matches LALR.CC's CMinus example):**

| C | Emitted C# |
|---|---|
| `int` / `float` / `double` / `void` | `int` / `float` / `double` / `void` |
| `char` | `byte` (so `char*` arithmetic walks bytes) |
| `T*` | `T*` (unsafe pointer) |
| `"foo"` | `L("foo\0"u8)` — pinned UTF-8 RVA pointer via `MemoryMarshal.GetReference` |
| `malloc(n)` / `free(p)` | `Malloc((int)n)` / `Free(p)` — backed by `System.Runtime.InteropServices.NativeMemory` |
| `printf("%d %s", x, s)` | `Printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — fluent ref-struct builder (avoids `params object[]` boxing of raw pointers) |
| C function | `static unsafe` local function at top level |
| Prototype / forward decl | empty emit (C# methods hoist) |

The emitted file is self-contained today: a `using` block, the `L`/`Malloc`/`Free`/`Printf` helpers, the user's translated functions, and the `PrintfBuilder` ref-struct (which has to come last because C# requires top-level statements to precede type declarations).

**Roadmap (intentional direction — partly in place, partly not):**

1. **Libc library exists** (`DotCC.Libc/`, shipped). Designed to factor as real C does: `fprintf(stream, fmt, ...)` is the primitive; `printf` is `fprintf(stdout, ...)`; `sprintf` reuses the format machinery on a `StringWriter` and flushes to `byte*` on `Done()`; `scanf` family mirrors with a `ScanfReader` that parses `%d`/`%f`/`%s`/`%c` out of a `TextReader` (or, for `sscanf`, a `StringReader` around the `byte*` source). Streams are `TextWriter` / `TextReader` (the obvious BCL mapping for opaque `FILE*`). Growable — adding `fopen` / `fread` / `getenv` / `exit` / `atoi` is just adding a method + a unit test. **Not yet wired into the emitter** — `BuildShell` still inlines copies of `L`/`Malloc`/`Free`/`Printf` so the file-based-program output stays self-contained for `dotnet run` on a bare .cs. Migration path: publish `DotCC.Libc` to NuGet, then emit `#:package DotCC.Libc@<ver>` (file-based) and `<PackageReference Include="DotCC.Libc" />` (csproj) instead of the inlined helpers.
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
- **No `Process.Start` from the library or the test projects.** The frontend exe uses it for `--emit=build` (invoking `dotnet build` on the generated csproj). Tests do not — Roslyn in-process drives the emitted code end-to-end. Adding `Process.Start` to tests would be a regression on the structural intent.
- **Keep `dotcc` and `clang -std=c99` round-trippable.** When extending the grammar, prefer real-C syntax over inventions; when extending the emitter, prefer lowerings that preserve C's observable semantics (overflow, signedness, side-effect order, evaluation order at sequence points).
- **C# types are `unsafe` in emitted code** (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is set on the generated csproj and on `DotCC.FunctionalTests` where Roslyn runs the emitted code). `DotCC.Lib` itself is **not** unsafe — it produces unsafe C# as a string, but doesn't run any.
- **Don't pull more deps without checking AOT compat.** The lib's closure is intentionally `LALR.CC` + `YamlDotNet` (build-time only). The frontend adds `System.CommandLine`. Tests add `xUnit`/`Shouldly`/`Microsoft.CodeAnalysis.CSharp`.
- **Watch the XML.** Comments in `Directory.Build.props` / `Directory.Packages.props` can't contain `--` (XML forbids it). Easy to miss when you type `dotnet run --file`.
