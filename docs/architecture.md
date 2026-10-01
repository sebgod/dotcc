# Architecture

> Extracted from CLAUDE.md (2026-07-07) — the full architecture reference. CLAUDE.md keeps
> a one-screen summary and points here.

The compiler is an N-frontend × M-backend frame meeting at one **typed IR**. Two seams hold it: `IFrontend` (`Frontends/IFrontend.cs` — lex/parse a source language and bind it to the IR, returning the `IrModule`) and `ITarget` + the per-target backend classes (`Ir/Target.cs`, `Backends/` — project the neutral IR onto an output language). Today that's two front-ends and two backends — but **not a full 2×2**, and the seams are less symmetric than the frame suggests (measured by the 2026-09 architecture review):

- **Zig × wat is mostly unbuilt.** The IR carries ~25 node types that only the Zig lowering produces
  (`ZigTry`, `ZigCatch`, `AllocCall`/`FreeCall`/…, `TupleLiteral`/`TupleIndex`, `SwitchExpr`,
  `OptionalOrElse`, `ErrUnionOk`/`ErrUnionErr`, …; each doc-commented "Zig-lowering / C#-target only"),
  and `WatBackend` renders none of them — they fall to its loud default. So `--target=wat` compiles C
  (the web sandbox's C side) and, for Zig, only programs whose lowering stays inside the C-shaped node
  set — in practice `std.debug.print` (→ `fprintf(stderr, …)`) over scalars. Tracked in
  [`plans/deferred.md`](plans/deferred.md). C × C#, C × wat and Zig × C# are real.
- **The neutral IR is `IrModule`; the C binder is `IrBuilder`.** `Ir/IrModule.cs` (+ `IrModule.Comptime.cs`)
  is what every front-end builds into and every backend reads: the output lists (functions, globals,
  emitted aggregates/enums, diagnostics, the Zig test manifest and error table), the aggregate/enum
  registries with their compile-time layout model, and the comptime interpreter (`ConstEval`). It knows
  no source language. `Ir/IrBuilder.cs` is the C front-end's parse-tree binder (every `case C.*`), and
  builds into its `Module`; `Frontends/ZigLowering*.cs` is the Zig binder, a peer holding an `IrModule`
  directly. Until the 2026-09 architecture review these were one class — the C binder WAS the IR, and
  the Zig front-end constructed a whole C binder just to host its output. A third front-end follows the
  Zig shape: bind into an `IrModule`.

What IS uniform: every expression carries a `CType`, both front-ends lower to the same statement /
expression node set for everything C-shaped (control flow, calls, arithmetic, aggregates), and the C#
backend has no front-end-specific rendering for those.

The C front half is a straight pull-pipe:

```
.c file → BytesLexer
        → PreprocessorTokenStream  (#include / #define / #undef / #if / #ifdef / #ifndef / #else / #endif / #pragma / #error / #warning; text passes through, #if conditions go to CPreprocessor.EvaluateCondition)
        → MacroExpander            (all macro replacement, object-like and function-like, via MacroEngine's per-token hide sets)
        → DialectKeywordRewriter   (dialect-aware keyword promotion: e.g. C23 `bool` → `_Bool`, gated on -std=)
        → TypeNameRewriter         (C lexer hack: promote ID → TYPE_NAME after typedef)
        → SizeofFolder             (fold `sizeof(T)` → literal, avoiding an ArrDims/Subscript LALR conflict)
        → SyncLATokenIterator      (one-token lookahead)
        → Parser                   (LALR(1) tables built from c.lalr.yaml; runs the generated C.IdentityVisitor — the parse tree is yielded raw)
        → IrBuilder.AddUnit        (bind the parse tree to the typed IR: CExpr/CStmt with a CType on every expression)
        → CSharpBackend / WatBackend  (precedence-aware printers over the IR, type/literal spelling behind ITarget)
        → BuildShell(...)          (wrap the emitted fn list in a .NET 10 program shell — C# target only)
```

The Zig front half is the same shape behind the same seam (`ZigFrontend`): its own lexer/parser from `zig.lalr.yaml`, then `ZigLowering` binds the Zig parse tree to the **same** `IrModule` — a mixed `.c` + `.zig` input set lowers both into one IR module. Everything from the IR down (backends, shell, runtime) is shared and frontend-agnostic.

The five token-rewriting stages are all `RewritingTokenStream` subclasses (an upstream LALR.CC base class owning the iterator plumbing — ready queue, look-ahead buffer, exhaustion flag — and exposing a `ProcessToken` hook plus `Emit` / `CollectUntil` / `TryReadNext`). One subclass per policy, mechanics shared; future contextual-keyword/DSL rewriters plug in the same way.

The stage *mechanics* are owned by SharpAstro.LALR.CC. `DotCC.Lib` contributes:
- `Compiler.EmitCSharp(...)` / `EmitWat` / `EmitObject` / `LinkObjects` / `Preprocess` / `EmitDependencyRule` — public entry points (`Compiler.cs`), which dispatch inputs to the right `IFrontend` and drive a backend over the returned IR.
- `CFrontend` / `ZigFrontend` — the `IFrontend` impls: each owns its language's lex→parse→bind pipeline and flushes source-level diagnostics; neither knows any output language.
- `CPreprocessor` — impl of generated `C.IPreprocessor`: the macro table, `#include` (quoted and angle), `#pragma once`, `#error`/`#warning`, `#if` evaluation (`EvaluateCondition`), defined-set tracking; `WrapPreprocessor(lexer)` builds the directive stage.
- `MacroEngine` — C macro replacement for every context (text, argument prescan, `#if`, `#line`): Prosser's hide-set algorithm, one `HideSet` per token, replacements pushed back in front of the remaining input.
- `MacroExpander` — the streaming stage that feeds each token of ordinary text to `MacroEngine`.
- `DialectKeywordRewriter` — dialect-aware keyword promotion ("rule 2"). A data table maps `(identifier spelling → MinVersion + target terminal)`; an `ID` is promoted only when the active `CDialect.Version ≥ MinVersion`. **`CDialect.Version` is keyed by ISO year (1990/1999/2011/2017/2023) so the gate `Version >= year` is monotonic** — keying by the short `90/99/11/17/23` suffix sorts `c11` below `c99` and silently mis-gates (a real past bug). Why rule 2 and not the binder: keywords spelled like identifiers (`inline`/`bool`/`true`/…) can't be gated post-parse — `int true = 5;` is valid older code. Under an older `-std=` the spelling stays an identifier, so the feature is simply unavailable there (a structural rejection, no `DialectGate` row needed). Sits after `MacroExpander` (a header's `#define bool _Bool` wins) and before `TypeNameRewriter`. Genuinely new *syntax* (`_BitInt`, `_Generic`) is gated in the IR binder instead; `_Capital_` keywords are always accepted.
- `TypeNameRewriter` — the C lexer hack: tracks typedef-bound names, promotes matching `ID` → `TYPE_NAME` so `Color * x;` routes as a declaration.
- `IrModule` (`Ir/IrModule.cs`, partial `.Comptime` — the unified compile-time interpreter both front-ends share) — the neutral IR: output lists, aggregate/enum registries + layout model, `ConstEval`.
- `IrBuilder` (`Ir/`, partials: `.Declarators` (init-declarator lists at every scope), `.Aggregates` (brace initializers), `.MallocPromote`) — the C binder: binds C parse trees to the typed IR (`IrNodes.cs`: `CExpr`/`CStmt` records, every expression carrying a `CType`), runs the IR-level checks (`-Wconversion`, qualifier discard, implicit fallthrough) and passes (malloc→stack promotion). `ZigLowering` (`Frontends/`) is the Zig peer, binding into the same `IrModule`.
- `SymbolTable` + `INameLegalizer` — shared name resolution: the table owns the neutral mechanism (scope tracking + collision counting), the target owns the policy (`CSharpNameLegalizer`: reserved-word escaping, **block-scope local renaming** for CS0136 — a colliding decl gets a fresh `name__k`).
- `CSharpBackend` + `CSharpTarget`, `WatBackend` + `WatTarget` (`Backends/`) — per-target printers over the IR; `ITarget` carries the type/literal spelling so the IR namespace never depends on an output language. `GotoScopeNormalizer` fixes C#'s label/decl scoping rules; the wat backend lowers `goto` via a CFG dispatch loop (`WatBackend.Cfg`).
- The wat target's libc (`WatLibc/`, `Compiler.WatLibc`) is C compiled with the program, as emscripten compiles musl into every module: a `SourceLibrary` handed to the C front-end, whose worklist binds the unit of each function a program calls, and each extern object it uses, that no unit defines (one name per file, so a program carries only what it uses, and its own definition of a name wins). `<math.h>` is vendored musl libm (`WatLibc/musl/`, see its README), and so is the core of `<stdio.h>`: musl's `FILE` with its read, write and seek functions over WASI's `fd_*` calls, its buffers, `ungetc`, `fgets`, `fseek`/`ftell` and the rest, taking no locks yet; `stdout` is unbuffered, like `stderr`, because a printf with a literal format writes straight to fd 1, and `tmpfile` is an in-memory stream (WASI preview1 has no anonymous files). The scanf family and the `strtol` family are musl's too (`vfscanf`, `__intscan`). String, ctype and stdlib are dotcc's own. The printf family is the backend's when its format is a literal it can lay out (expanded inline at the call, its arguments evaluated first, its count returned), and otherwise the libc's: a `vsnprintf` in C that reads the format at run time and lays each conversion out through intrinsics (`WatLibc/include/printf_impl.h`, `WatBackend.FormatIntrinsics`) that are the expansion's own runtime functions, so the two print alike; `vfprintf` formats into a buffer and writes it in one go. The library's units are weak: a call the backend lowers its own way never reaches one, and the module keeps only what `main` reaches, functions through the calls and function addresses their emitted code holds and the library's data objects through the addresses it holds (`WatBackend.Reachable`), so a program that never uses a library `FILE` carries none, nor the stream functions it points at. A library unit includes the library's private headers (`WatLibc/include/`: musl's `libm.h`, a `math.h` with its classification macros) and then dotcc's, never the program's `-I` directories (`IncludeResolver.LibraryDir`). What is one wasm instruction (`sqrt`, `fabs`, the directed roundings, `copysign`, `memcpy`/`memset` as bulk memory) never comes from the library. The libc reaches the host as emscripten's does: a function named `__wasi_<name>` that nothing defines is the import `wasi_snapshot_preview1.<name>`, typed by its C prototype (`WatLibc/include/wasi.h`). A variadic call follows emscripten's ABI too: the caller stores each promoted argument in an 8-byte slot of a buffer in its frame and passes the buffer's address as a hidden last parameter (`$__va`); a `va_list` is an 8-byte object holding the cursor. `setjmp`/`longjmp` ride wasm exception handling: the IR's `SetjmpGuard` is a `try`/`catch $__longjmp`, a `SetjmpCapture` that `try` inside a `loop` it re-enters with the value; each setjmp arms its `jmp_buf` with a fresh token, `longjmp` throws the token and the value, and a handler rethrows any other setjmp's token and restores `$__sp`. A program that calls `__wasi_thread_spawn`, `memory.atomic.wait32` or `notify` (the libc's C11 `<threads.h>`, `WatLibc/include/threads_impl.h`) is threaded, on wasi-threads: its memory is a shared one the host passes in (`env.memory`), every thread an instance of the module over it entered at the exported `wasi_thread_start`; data segments are passive and the first instance's start function lays memory out behind an atomic flag; the heap's next free byte is a memory cell malloc bumps with a compare-exchange; each thread has a TLS block (`$__tls`: the formatter's scratch, then the `_Thread_local` objects and `errno`, copied from a template); atomics are atomic instructions and blocking is a futex. A program without threads keeps its plain shape.
- `BuildShell` — C# scaffolding around emitted functions (top-level statements + entry-point wiring, struct/typedef section, `using static Libc`, embedded-runtime splice point); argv UTF-8 marshalling for `int main(int, char**)`.
- `Compiler.LoadRuntimeBlock` (`_runtimeBlock`) — reads every `DotCC.Libc/*.cs` source from the assembly manifest (embedded at build time), strips file-scope artifacts (`#nullable`, `using`s, `namespace`), concatenates into the type-decls section. Single source of truth: edit `MathLib.cs`, both the unit-tested DLL and every emitted program pick it up next build.
- `SystemHeaders` — synthetic `.h` files under `DotCC.Lib/include/`, embedded as resources so the parser sees signatures with no disk I/O. User `-I` headers win on name collisions (clang's quoted-include rule).
- `CompileException` — wraps LALR.CC's `ParseErrorException` into a stable public surface. `Parser.ParseInput` defaults to `ParserErrorMode.Throw`; `Compiler.EmitCSharp` catches and re-raises so callers know one exception type.

The generated `DotCC.C` / `DotCC.Zig` partial classes (from the `.lalr.yaml` grammars at build time by `LALR.CC.SourceGenerators`) expose: `BuildLexer()` / `BuildParser(visitor)` / `WrapPreprocessor(lexer, impl)`; `IPreprocessor` (one method per directive + `Rewrite(Item)` + `IsDefined(string)`); and the `<RecordName>` AST records — one per `action:` rule. Both front-ends parse with the generated `IdentityVisitor` (the parse tree comes back raw) and pattern-match the typed records in the binder (`IrBuilder` / `ZigLowering`). **The grammar and the record surface change in lockstep**, but note the enforcement honestly: since the identity-visitor cutover it is *not* a compile error to leave a new `action:` unbound — an unhandled record falls to the binder's `default:` case, which throws a loud `IrUnsupportedException` naming the node type at the first program that reaches it (the "fail loudly, grow on purpose" rule). Emit pins for every new production are what keep that gap closed.

## Code generation strategy

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
| C function | `internal static unsafe` method of a top-level `DotCcProgram` class (NOT a local function — class methods can be `&fn`-addressed and stored in fn-ptr tables, which Lua's `luaL_Reg` code needs). Past `Compiler.FunctionsPerClass` (8192) functions continue in `DotCcProgram2`, `DotCcProgram3`, ..., each surfaced by its own `using static`: the CLR caps a type at 65535 methods (CPython's ~70k functions, mostly each unit's copies of header `static inline`s, failed to load as one class), and smaller classes also build far faster (CPython's C# from 6.5 min to under a minute). `-shared` uses the parallel `DotCcLib`, one class. |
| Prototype / forward decl | empty emit (C# methods hoist) |

**Self-contained emit, no inlining duplication.** Every emitted file pulls in the `DotCC.Libc` runtime via the single block `BuildShell` splices from embedded resources (see `LoadRuntimeBlock` above) — `using static Libc;` then surfaces every method by bare name.

The single-source-of-truth design intent: **the same `.c` file should compile under both `dotcc` and `clang -std=c99` and produce equivalent observable behavior** — keep the grammar a strict subset of real C, and keep the emitter's output semantics aligned with the C abstract machine.

**Types are structural, not incidental.** Every IR expression node carries a `CType` (`DotCC.Lib/Ir/CType.cs` — primitives, `Pointer`/`Array`/`Named`/`Enum`/`Func`, plus the Zig-side `Slice`/`Optional`/`ErrorUnion`/`Allocator`/`ZigList`/`Tuple`), synthesized during binding. That type spine powers: real C# `enum` lowering (decay enum operands to `(int)` for C's plain-int arithmetic, re-cast only at typed sinks); `sizeof expr` (notably the `sizeof(a)/sizeof(a[0])` array-length idiom — arrays lower to pointers, so `sizeof(arr)` folds via the layout model); `_Generic` selection; the comptime interpreter's typing; and the same-function `malloc`/`free` → stack-value peephole (`IrBuilder.MallocPromote` — a two-pass IR analysis: pass 1 records per-`(function,var)` usage, pass 2 promotes when ref-counts balance and the var never escapes). Recognition is **structural** (IR nodes + ref-counts), never text-matching on emitted output. Done features and their fixtures/gaps are tracked in `C-SUPPORT.md`.

**Once `DotCC.Libc` ships to NuGet** the embedding goes away: the shell emits `#:package`/`<PackageReference>` and drops the `{{runtimeBlock}}` splice — no runtime change, the embedding is purely a pre-NuGet deployment workaround.

## Sibling-or-NuGet wiring (`UseLocalLalrCc`)

`DotCC.Lib`'s only library dependency is **SharpAstro.LALR.CC** (runtime + bundled source generator). The build switches consumption modes on whether a sibling working copy is present:

| Mode | When | Wired in |
|---|---|---|
| **Sibling** (`UseLocalLalrCc=true`) | `../../sharpastro/LALR.CC/` exists | `ProjectReference` to `LALR.CC.csproj` + generator project as `Analyzer` + `PackageReference YamlDotNet` (`PrivateAssets=all`) feeding the analyzer host |
| **NuGet** (`UseLocalLalrCc!=true`) | sibling absent, or `-p:UseLocalLalrCc=false` | Single `PackageReference Include="SharpAstro.LALR.CC"` (the package bundles the analyzer DLL + YamlDotNet under `analyzers/dotnet/cs/`) |

Auto-detection lives in `Directory.Build.props` (MSBuild `Exists(...)`); the conditional `ItemGroup`s in `DotCC.Lib/DotCC.Lib.csproj`. CI always builds the NuGet path — override locally with `-p:UseLocalLalrCc=false` to validate the same. Same pattern as `sharpastro/tianwen` and `sharpastro/Console.Lib`; intentionally **different** from `sebgod/chess` (NuGet-only). Sibling mode picks up a LALR.CC generator/runtime tweak on the next dotcc build with no version bump.

## Grammar conventions

`c.lalr.yaml` follows LALR.CC's YAML schema:

- **`symbols:` ordering is meaningful** — index = symbol ID, LHS=0 is the start symbol. Don't reorder existing entries; append.
- **Precedence groups order matters** — listed lowest → highest, used to resolve S/R and R/R conflicts via `derivation: leftmost|rightmost|none`. Dangling-else lives in a `rightmost` group (so `else` shifts onto the nearest open `if`).
- **Lexer-regex alternation is supported** (LALR.CC ≥ 4.0.0): `|` works at top level and *inside* a repetition — what the `STRING` rule needs. `.` matches any char except `\n`. For *token-level* alternatives prefer multiple `LexRule`s (longest match wins, first-rule-wins on ties, reads clearer); reach for `|` only when the choice lives within one token.
- **Preprocessor directives go in `preprocessor:`** (each becomes an `IPreprocessor` method). Conditionals are handled by LALR.CC's built-in engine — declare them in `conditionals:`; the `IsDefined` hook drives boolean evaluation.

An unresolved conflict throws `GrammarConflictException` with the offending state + lookahead — don't catch it; fix the grammar by adding the colliding productions to a precedence group.
