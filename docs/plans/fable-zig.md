# Zig front-end — session findings (2026-07-02, Fable)

> Untracked scratch notes from the "what's next for C/Zig support" survey.
> Snapshot of main at `4a43106` (Milestones Y/Z merged 2026-06-29, PRs #33/#34).
> **Re-verified current 2026-07-03 at main `3dbc80a`** — every claim below
> cross-checked against ZIG-SUPPORT.md and `ZigLowering.cs`; only the
> cross-frontend-dividends section needed updating (both C milestones since
> completed). One Zig feature landed in between: `threadlocal var` (the C
> `_Thread_local` twofer, PR #42) — it was never on this list.
> The formal roadmap `~/.claude/plans/zig-remaining-features.md` is **fully
> exhausted** — every planned milestone (I → T, plus ß and the memory-tracked
> U/V/W/X/Y/Z) is done and merged. What follows is the inventory of what
> REMAINS, grouped by leverage, for whenever a new Zig roadmap is drawn.

## The one big structural unlock: sub-expression positions (ANF hoist)

The single largest cluster of deferred cuts is **"works at a full
`const`/`var`/`return`/assignment RHS, rejected in a sub-expression"**:

- `a catch b` with a side-effecting / capturing fallback; `catch |e|` capture
- `a catch return` / `a orelse return` control-flow fallbacks
- value-position `if`/`switch` with block-bodied branches (Y1 lowers them as a
  statement filling `__vcf` — only at a full RHS)
- labeled-block-as-value `blk: { …; break :blk v; }`
- loop-as-value `while/for … else` (`__lv` temp, same restriction)

All of these share one missing pass: a **statement-hoist / ANF transform** with
eval-order analysis (hoist the value-producing construct to a temp *before* the
containing expression, preserving C-like sequencing). Y3 documented it as the
deliberate cut. One well-designed pass clears the whole cluster at once —
that's the natural next L-size Zig milestone.

> **STATUS 2026-07-04** (groups 1–4 done; PRs #47 + #48). The cluster splits into
> two halves:
> - ✅ **PHASE A — catch/orelse in a sub-expression: DONE** (branch
>   `feat/zig-anf-hoist`). `a catch b()` (side-effecting), `a catch |e| b`, `a catch
>   return`, `a orelse return` now hoist out of a SUB-expression (`x + (a catch
>   b())`, decl-init / assignment / return). Implemented as a statement-level
>   `_hoist` buffer installed by `Hoisted()` at eval-safe points (return / expr-stmt
>   / assignment / decl-init — NOT loop conditions, which stay rejected). A
>   value-producing construct appends its pre-statements + a `__anfN` temp and
>   evaluates to a bare `VarRef`; the enclosing statement runs the buffer first (a
>   brace-less `Seq`). **Eval-order correctness** rides on `_hoistImpureSeen`: a call
>   sets the watermark (in `LowerCall`, AFTER its args), and a construct checks it
>   BEFORE lowering its own internals — hoisting past a prior side effect is a clear
>   error, not a silent reorder (`f() + (a catch b())` rejects). A hoisted
>   construct's own internals restore the flag (they're sequenced in the buffer), so
>   two hoists in one expression compose.
> - ✅ **PHASE B — if / switch / labeled-block / loop as values in a sub-expr: DONE**
>   (branch `feat/zig-anf-hoist`). The `Primary -> ( Expr )` grouping production
>   became `Primary -> ( RhsExpr )` — conflict-free (the parens delimit the
>   control-flow value, so it never leaks into the cascade as a bare binary
>   sub-operand). A parenthesized value-if/switch/labeled-block/loop then hoists via
>   the Phase A machinery (LowerValueControlFlowStmt / LowerLabeledValueBlock fill a
>   temp; the `grouped` lowering appends that to the buffer and returns the temp). A
>   SIMPLE bare-expr value-if/switch stays an inline C# ternary/switch (no hoist).
>   Same eval-order guard. Requires explicit parens in a sub-expr (zig allows a bare
>   `if`/`switch` there; a small accept-less that keeps the grammar conflict-free).
>
> **The ANF milestone is COMPLETE. fable-zig.md's implementable items are all done**
> (the trade-offs section is explicitly "design notes, not increments"; "the wall"
> is out of scope by design).

## Quick wins (S-size each, mostly grammar-light)

> **STATUS 2026-07-04**: groups 1–3 DONE on branch `feat/zig-quick-wins` → **PR #47**
> (all 12 CI jobs green: zig-oracle Linux+Windows, msvc/gcc oracles = no C regression,
> wat + shared-lib oracles). Awaiting merge go-ahead. Group 4 (smaller grammar items)
> triaged below — all 6 probed items are real gaps, NOT yet done.

- ~~**Lexer stragglers**~~ — investigated 2026-07-04; only ONE of the three was
  a real gap:
  - **exponent-only floats** (`1e10`/`4E2`) — ✅ **DONE** (`c-support` FLOAT
    lexer rule `[0-9][0-9_]*[eE][+\-]?[0-9_]+`; `1e3` used to mis-lex as
    `INTEGER 1` + `IDENT e3`). Passes through as a C# double verbatim — no
    lowering change.
  - **uppercase radix `0X`/`0O`/`0B`** — ❌ **not valid Zig** (real zig: `error:
    base prefix must be lowercase`). Deliberately NOT lexed — accepting it would
    fail the differential oracle. The straggler note was wrong.
  - **non-BMP `\u{…}` (> 0xFFFF)** — ✅ **already worked**; the "needs
    surrogate-pair emission" note was a misdiagnosis. A STRING expands via
    `char.ConvertFromUtf32` → correct UTF-8 bytes (U+1F600 → `F0 9F 98 80`); a
    CHAR is the codepoint as an int (`comptime_int`), so no surrogate at all.
    Now pinned by unit tests so it can't silently regress.
- **Curated `std.mem` helpers** — IN PROGRESS (2026-07-04):
  - ✅ **DONE (commit 2a)**: `std.mem.eql`, `std.mem.copyForwards`, and the
    `@memcpy`/`@memset` builtins → new `ZigMem` runtime (auto-spliced like
    `ZigAlloc`) via a single `ZigMemCall` IR node (`ZigMem.{Method}<T>(…)`, the
    `CopyArrayResult` pattern). Bonus fix: the `*[N]T` → `[]T` coercion (`&array`
    to a slice param) was a real pre-existing gap — `CoerceToSlice` now strips
    the address-of. GOTCHA hit: a void `ZigMemCall` used as a statement needs
    adding to the backend's `IsStmtExpr` allow-list (else `_ = <void>` = CS8209).
  - ✅ **DONE (commit 2b)**: `std.mem.span` (NUL-sentinel `[*:0]T` → `[]const T`
    via `ZigMem.SpanZ`, byte-wise scan; yields a const slice, sentinel-0 only —
    dotcc erases the sentinel) and `std.mem.zeroes` (scalar/struct → C#
    `default(T)`; array-value form a documented cut — arrays lower to a pointer
    so `default` would be null).
- ~~**While-story completion**~~ — ✅ **DONE (2026-07-04)**: the capture-while
  family now mirrors the `if`-capture family (moved into the dangling-else
  `rightmost` group; conflict-free). `while (opt) |x| … else …` (else runs on
  natural exit), `: (cont)` continue-expr capture-while (→ C `For` post, so
  `continue` runs the cont), and error-union `while (eu) |x| … else |e| …`
  (reuses the `if`-capture ErrUnion arm: IsErr/Value/Code). `for (s, 5..)`
  non-zero index start was ALREADY working (LowerForSlice binds `__i + N`) — the
  grammar comment + doc were stale; now pinned. **Deferred:** cont + else
  together (rare).
- **Smaller (group 4 — NOT done; all triaged 2026-07-04 as real gaps vs zig 0.17)**:
  - ✅ **global `[N:s]T` sentinel arrays** — DONE (feat/zig-grammar-items). The
    pinned global store now reserves N+1 (literal appends the sentinel; zero-
    sentinel undefined zero-fills N+1; non-zero undefined lays down `[0×N, s]`).
    Symbol keeps the logical `[N]T` type.
  - ✅ **`export const`/`export var`** (exported data) — DONE on branch
    `feat/zig-grammar-items`. Added `Decl -> export VarDecl` / `pub export
    VarDecl` + peeled PubVar/ExportVar/PubExportVar in `Unwrap` (also fixed the
    pre-existing `pub const`/`var` top-level bug — grammar accepted it but Unwrap
    didn't peel it). Console = no-op; `-shared` data export dropped (documented cut).
  - ✅ **unnamed-param fn-pointer types** `fn (i32) i32` + **`!T`-returning**
    `fn (i32) E!i32` — DONE (feat/zig-grammar-items). Replaced the Params-based
    `tyFn` with a unified `FnTypeParam -> Type | IDENT : Type` list (conflict-free:
    `:` shifts named, `,`/`)` reduces the bare type); added `tyFnErr`/`tyFnNoArgsErr`
    (Return = `CType.ErrorUnion`). Needed a `Flatten` case for `FnTypeParamsCons/One`
    (Flatten is a hardcoded per-list-type dispatch — new list nonterminals must be
    added). `callconv` on a fn-ptr type still a cut.
  - ✅ **empty + arity>7 tuples** — DONE (feat/zig-grammar-items). Empty `.{}` (no
    struct sink) → the non-generic `System.ValueTuple`; arity > 7 nests via C#'s
    8th `TRest` field (recursive `RenderValueTuple` + `BuildValueTupleCtor`; a
    tuple index ≥ 7 chains `.Rest`). Removed the 1..7 arity rejections.
  - ✅ **`pub`-wrapped containers** — DONE (feat/zig-grammar-items). Grouped the 9
    container forms under a `ContainerDecl` nonterminal (re-parented from `Decl`),
    added `Decl -> ContainerDecl` (transparent) + `Decl -> pub ContainerDecl`, and
    peeled `PubContainer` in `Unwrap`. ⏳ **in-function containers** still a cut
    (`const X = struct {…}` inside a fn — needs on-the-fly type registration during
    body lowering, not the top-level pre-registration pass).
  - ✅ **fn-pointer globals** (+ forward-ref to a later FN) — DONE
    (feat/zig-grammar-items). Typed already worked; the INFERRED form needed `&fn`
    to be a bare `CType.Func` value (not `Pointer(Func)`) so `const alias = &fn;`
    is callable. Forward-ref to a later FUNCTION works (functions register before
    globals). ⏳ **CUT**: forward-ref to a later const VALUE (`const a = b+1;
    const b = 41;`) — needs order-independent comptime-const resolution + a topo-
    sorted emit (C# static-field init runs in declaration order); niche, declare in
    dependency order.

  DEFERRED cuts (documented): in-function containers, const→later-const-value
  forward-ref. Everything else in group 4 is DONE.

## Known representation trade-offs (design notes, not increments)

- **Z1 — by-ref `|*x|` on optional/error-union `if`/`while`**: blocked because
  a value optional `?T` lowers to C# `Nullable<T>`, which has no addressable
  payload (`.Value` is a property — no `&opt.Value`). Fixing it means a custom
  `ZigOpt<T>` struct with a real field (then `??`/`.Value` sugar is lost and
  every existing optional lowering re-touches). A real trade-off decision, not
  an increment. (The for/switch by-ref forms work because `&s.Ptr[i]` and
  `&u.__payload.v` ARE addressable.)
- **Distinct per-set error code spaces** (X3 deferral): membership is checked
  but all sets share one flat `ushort` space — cross-call `try` set-coercion
  and statement-form set-switch exhaustiveness ride on un-flattening it.
- **128-bit**: `i128`/`u128` have no wat lowering (throws); saturating `+|`
  at 128 bits is a documented cut (the exact-clamp accumulator would itself
  overflow).

## Cross-frontend dividends — fully cashed (updated 2026-07-03)

The unified `ComptimeInterpreter` (Milestone T) + layout model (`AlignOfConst`,
T4) paid off in C completely — BOTH follow-on C milestones are done and merged:

- **C comptime dividend** (PRs #35–#38): `_Static_assert` evaluation,
  `_Alignof`/`_Alignas`, `#elifdef`/`#elifndef`, `[[attributes]]`.
- **C11/C23 completion** (PRs #40/#42/#43): `_Generic` (lowering-time selection
  on the typed IR's CType synthesis — dividend #3), `_Thread_local` (whose Zig
  twofer shipped `threadlocal var` — a rare C→Zig direction dividend), and
  `constexpr` (ConstEval binding — dividend #4). The C11/C23 ❌ roadmap column
  is now EMPTY; the C follow-up batch lives in `fable-c.md`.

For Zig, the relevant residue: `_Generic`'s lowering-time type-matcher is a
primitive the STILL-CUT generic methods could build on, and `constexpr`'s
ConstEval-bound symbols use the same substitution shape a future comptime-var
widening would.

## The wall (unchanged — do not relitigate)

comptime TYPES / generics / `anytype`, a real `std`, `async`/`suspend`,
inline `asm`, comptime tuple flavor, safe-mode overflow traps on plain `+`.
Reasoning in `ZIG-SUPPORT.md` § "Why these are out of scope".
