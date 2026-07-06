# Architecture

> Extracted from CLAUDE.md (2026-07-07) — the full architecture reference. CLAUDE.md keeps
> a one-screen summary and points here.

The compiler is an N-frontend × M-backend frame meeting at one **typed IR**. Two seams hold it: `IFrontend` (`Frontends/IFrontend.cs` — lex/parse a source language and bind it to the IR, returning the `IrBuilder`) and `ITarget` + the per-target backend classes (`Ir/Target.cs`, `Backends/` — project the neutral IR onto an output language). Today that's 2×2 (C, Zig → C#, wat) with no pairwise special-casing.

The C front half is a straight pull-pipe:

```
.c file → BytesLexer
        → PreprocessorTokenStream  (#include / #define / #undef / #if / #ifdef / #ifndef / #else / #endif / #pragma / #error / #warning; object-like macro substitution via Rewrite)
        → MacroExpander            (function-like macro expansion with paren-balanced arg collection + multi-pass rescan)
        → DialectKeywordRewriter   (dialect-aware keyword promotion: e.g. C23 `bool` → `_Bool`, gated on -std=)
        → TypeNameRewriter         (C lexer hack: promote ID → TYPE_NAME after typedef)
        → SizeofFolder             (fold `sizeof(T)` → literal, avoiding an ArrDims/Subscript LALR conflict)
        → SyncLATokenIterator      (one-token lookahead)
        → Parser                   (LALR(1) tables built from c.lalr.yaml; runs the generated C.IdentityVisitor — the parse tree is yielded raw)
        → IrBuilder.AddUnit        (bind the parse tree to the typed IR: CExpr/CStmt with a CType on every expression)
        → CSharpBackend / WatBackend  (precedence-aware printers over the IR, type/literal spelling behind ITarget)
        → BuildShell(...)          (wrap the emitted fn list in a .NET 10 program shell — C# target only)
```

The Zig front half is the same shape behind the same seam (`ZigFrontend`): its own lexer/parser from `zig.lalr.yaml`, then `ZigLowering` binds the Zig parse tree to the **same** `IrBuilder` — a mixed `.c` + `.zig` input set lowers both into one IR module. Everything from the IR down (backends, shell, runtime) is shared and frontend-agnostic.

The five token-rewriting stages are all `RewritingTokenStream` subclasses (an upstream LALR.CC base class owning the iterator plumbing — ready queue, look-ahead buffer, exhaustion flag — and exposing a `ProcessToken` hook plus `Emit` / `CollectUntil` / `TryReadNext`). One subclass per policy, mechanics shared; future contextual-keyword/DSL rewriters plug in the same way.

The stage *mechanics* are owned by SharpAstro.LALR.CC. `DotCC.Lib` contributes:
- `Compiler.EmitCSharp(...)` / `EmitWat` / `EmitObject` / `LinkObjects` / `Preprocess` / `EmitDependencyRule` — public entry points (`Compiler.cs`), which dispatch inputs to the right `IFrontend` and drive a backend over the returned IR.
- `CFrontend` / `ZigFrontend` — the `IFrontend` impls: each owns its language's lex→parse→bind pipeline and flushes source-level diagnostics; neither knows any output language.
- `CPreprocessor` — impl of generated `C.IPreprocessor`: object + function-like macros, `#include` (quoted and angle), `#pragma once`, `#error`/`#warning`, defined-set tracking.
- `MacroExpander` — function-like macro calls (paren-balanced arg collection + multi-pass rescan).
- `DialectKeywordRewriter` — dialect-aware keyword promotion ("rule 2"). A data table maps `(identifier spelling → MinVersion + target terminal)`; an `ID` is promoted only when the active `CDialect.Version ≥ MinVersion`. **`CDialect.Version` is keyed by ISO year (1990/1999/2011/2017/2023) so the gate `Version >= year` is monotonic** — keying by the short `90/99/11/17/23` suffix sorts `c11` below `c99` and silently mis-gates (a real past bug). Why rule 2 and not the binder: keywords spelled like identifiers (`inline`/`bool`/`true`/…) can't be gated post-parse — `int true = 5;` is valid older code. Under an older `-std=` the spelling stays an identifier, so the feature is simply unavailable there (a structural rejection, no `DialectGate` row needed). Sits after `MacroExpander` (a header's `#define bool _Bool` wins) and before `TypeNameRewriter`. Genuinely new *syntax* (`_BitInt`, `_Generic`) is gated in the IR binder instead; `_Capital_` keywords are always accepted.
- `TypeNameRewriter` — the C lexer hack: tracks typedef-bound names, promotes matching `ID` → `TYPE_NAME` so `Color * x;` routes as a declaration.
- `IrBuilder` (`Ir/`, partials: `.Comptime` — the unified compile-time interpreter both front-ends share, `.Aggregates`, `.MallocPromote`) — binds C parse trees to the typed IR (`IrNodes.cs`: `CExpr`/`CStmt` records, every expression carrying a `CType`), runs the IR-level checks (`-Wconversion`, qualifier discard, implicit fallthrough) and passes (malloc→stack promotion). `ZigLowering` (`Frontends/`) is the Zig peer, binding into the same builder.
- `SymbolTable` + `INameLegalizer` — shared name resolution: the table owns the neutral mechanism (scope tracking + collision counting), the target owns the policy (`CSharpNameLegalizer`: reserved-word escaping, **block-scope local renaming** for CS0136 — a colliding decl gets a fresh `name__k`).
- `CSharpBackend` + `CSharpTarget`, `WatBackend` + `WatTarget` (`Backends/`) — per-target printers over the IR; `ITarget` carries the type/literal spelling so the IR namespace never depends on an output language. `GotoScopeNormalizer` fixes C#'s label/decl scoping rules; the wat backend lowers `goto` via a CFG dispatch loop (`WatBackend.Cfg`).
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
| C function | `internal static unsafe` method of a top-level `DotCcProgram` class (NOT a local function — class methods can be `&fn`-addressed and stored in fn-ptr tables, which Lua's `luaL_Reg` code needs). `-shared` uses the parallel `DotCcLib`. |
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
