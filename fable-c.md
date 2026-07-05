# C front-end — next-batch plan (2026-07-03, Fable)

> Untracked scratch notes, sibling to `fable-zig.md`. Snapshot of main at
> `3dbc80a` (constexpr merged — the C11/C23 completion milestone is DONE and the
> ❌ roadmap column is empty; see `~/.claude/plans/c11-c23-completion.md`).
> This file plans the follow-up batch picked from the 2026-07-03 coverage sweep:
> the four cheap/moderate items that close real gaps. Each is its own focused
> increment per [[lalr-feature-loop]] (separate PRs, three-legged validation).

## 1. `unreachable()` (C23, S) — ✅ DONE 2026-07-03 (on `close-the-c-gap`)

Shipped as designed: `<stddef.h>` macro (C23-gated) → `[DoesNotReturn]` runtime
helper `__dotcc_unreachable` (throws `System.Diagnostics.UnreachableException`);
backend lowers a **statement-position** call to a real C# `throw` (CS0161 only
respects a literal throw, not `[DoesNotReturn]` on a callee), and `Terminates`
recognizes it (no dead `break;`, CS0162). Fixture `c23-unreachable/` gcc
`-std=c2x`-verified (`total=22 first=6`); MSVC opts out — cl.exe has NO
`unreachable()` even under `/std:clatest` (LNK2019 unresolved `unreachable` +
C4715; verified via the harness oracle, not assumed). unit `UnreachableTests`
(3 pins). GOTCHA confirmed: a `[DoesNotReturn]` callee does NOT satisfy CS0161 —
must emit a literal `throw`. Original design notes below.



The `<stddef.h>` undefined-behavior marker macro (C23 §7.21.1). dotcc-defined
behavior for *reaching* it: a **loud throw**, pairing naturally with the
existing `_Noreturn` → `[DoesNotReturn]` lowering.

- **Surface**: `#define unreachable() __dotcc_unreachable()` in the synthetic
  `stddef.h`, version-guarded `>= 202311L` (same hygiene as `<threads.h>`'s
  `thread_local` macro). No grammar change — it's an ordinary call after
  expansion.
- **Runtime**: `__dotcc_unreachable()` in `DotCC.Libc`, marked
  `[DoesNotReturn]`, throws (e.g. `InvalidOperationException("unreachable code
  reached")`). The attribute means C#'s definite-return analysis is satisfied
  when it ends a switch arm / function tail — the same reason `[DoesNotReturn]`
  was restored for `_Noreturn`.
- **Validation**: fixture where the unreachable arm is *not* taken (gcc-oracle
  parity holds — executed `unreachable()` is UB in gcc, so only the not-reached
  path is oracle-able); unit pin that reaching it throws (in-process).

## 2. `int_leastN_t` / `int_fastN_t` families (C99, S) — ✅ DONE 2026-07-03 (on `close-the-c-gap`)

Shipped header-only in `<stdint.h>`: least types = exact-width; fast types
follow glibc x86-64 LP64 EXACTLY (`int_fast8_t`=signed char, but
`int_fast16/32/64_t`=`long`), so `sizeof(int_fast16_t)`==8 matches gcc — the
load-bearing parity. + all MIN/MAX limit macros. Fixture `c99-stdint-fast/`
gcc `-std=c17`-verified (`sizes 1 8 8 8` / values); MSVC opts out (LLP64 → the
fast types are 32-bit int, data-model divergence). unit `StdintFamiliesTests`
(3 pins incl. `INT_FAST16_MAX` as an array-bound ICE). C23 `*_WIDTH` macros
NOT added (C23-optional, rarely used). Original design notes below.



The C99-optional `<stdint.h>` families, absent today (the exact-width forms
are in). dotcc is LP64/glibc-shaped, so follow glibc's x86-64 mapping exactly
(keeps the gcc oracle byte-identical on `sizeof` prints):

- `int_least8/16/32/64_t` + unsigned → the exact-width equivalents.
- `int_fast8_t` → `signed char`; `int_fast16/32/64_t` → `long` (glibc LP64
  convention); unsigned mirrors.
- The `INT_LEASTn_MIN/MAX`, `INT_FASTn_MIN/MAX`, `UINT_*_MAX` macros; C23
  `*_WIDTH` macros if cheap.
- Synthetic-header-only change (`stdint.h`); no lowering work. Validation:
  fixture printing sizes/limits vs gcc.

## 3. `[[nodiscard]]` discarded-result warning (M) — ✅ DONE 2026-07-03 (on `close-the-c-gap`)

Shipped: `Symbol.Nodiscard` (string?, set in `ApplyFnMarkers` from
`_pendingAttrNodiscard`, shared prototype↔definition like `Deprecated`);
`CheckNodiscardDiscarded` at the `StmtExpr` lowering site warns (gcc-verbatim
`ignoring return value of 'f', declared with attribute 'nodiscard'` + `: "reason"`)
on a discarded non-void `Call`. `(void)f()` suppresses for FREE — `BuildCast`
lowers `(void)expr` to the call with `Type=CType.Void`, so the discard check's
`Type is not VoidType` guard skips it. On by default (stderr, flushed by
CFrontend). unit `NodiscardTests` (6 pins: warn, reason, void-cast suppress,
used-result, plain-fn, proto→def). Row stays 🟡 only for `gnu::aligned` drop +
unparsed param/struct/type positions. GOTCHA: `[[nodiscard]]` result USED in
c23-attributes fixture + AttributeTests → no warning, no breakage; emitted C#
has no "nodiscard" text so `ShouldNotContain("nodiscard")` still holds. Original
design notes below.



The one recognized-but-toothless attribute with an implementable semantic: warn
when the result of a `[[nodiscard]]`-declared function is discarded. Today the
attr is accepted + ignored (`CollectDeclAttrs` — the row's 🟡 reason).

- **Record**: `Symbol.Nodiscard` (string? — C23 allows `[[nodiscard("msg")]]`),
  set by the attr walk like `Deprecated`; a prototype's attr reaches the
  definition via the shared symbol.
- **Check**: at expression-statement lowering in `IrBuilder` — a direct `Call`
  in statement position whose callee is nodiscard and whose result type is
  non-void → **warning** (on by default, like gcc/clang treat the attribute):
  gcc wording `ignoring return value of 'f', declared with attribute 'nodiscard'`
  (ground the exact spelling in WSL before shipping).
- **Suppression**: an explicit `(void)f();` cast — the standard idiom both
  gcc and clang honor. The cast is already a distinct AST shape, so the check
  simply doesn't see a discarded *call*.
- **V1 scope**: direct call statements only (not comma-operands or
  ternary arms). Attr on functions only (C23 also allows it on types —
  struct/enum positions don't parse yet anyway, see the attributes row's cuts).

## 4. `-Wimplicit-fallthrough` + `[[fallthrough]]` earns its keep (M) — ✅ DONE

DONE: `FallthroughMarker` IR node (emits nothing both backends, doesn't terminate →
the synthesized `goto case` still lands after it); `AttrStmt` case returns it when
`AttrListHasFallthrough(s.Arg1)`; `BuildSwitch` calls `CheckImplicitFallthrough`
(gated on `WarningFlags.ImplicitFallthrough`) over non-last, non-empty sections whose
effective last stmt isn't a marker and doesn't `StmtTerminates` (IR mirror of the
backend `Terminates`, incl. `unreachable()`). CLI `-Wimplicit-fallthrough`. gcc-verbatim
"this statement may fall through" (grounded in WSL gcc 13.3). 7 unit tests
(`ImplicitFallthroughTests`), no fixture (stderr-only warning, no stdout effect —
`-Wconversion` precedent). unit 1384/1384, functional 210/210. Flag plumbing rode in
free on the WarningFlags refactor.



The one thing `[[fallthrough]]` does in any compiler is suppress
`-Wimplicit-fallthrough` — a warning that fires when a non-empty case falls
through *without* the marker. dotcc's switch lowering already synthesizes C's
fall-through jump (`goto case`), so the attribute carries zero semantic
information; the only way to give it a real job is the opt-in warning:

- **Warn** at each switch section that is non-empty, falls through to the
  next, and is **not** marked `[[fallthrough]];` (as its last statement); the
  attribute suppresses it.
- **Cost**: moderate, self-contained — walk the sections in `IrBuilder`, track
  which end without a terminator and whether a `[[fallthrough]]`
  empty-statement precedes the next label. The plumbing already half-exists:
  the attribute is recognized, and the terminator analysis exists in the
  backend (`CSharpBackend.Terminates`) — it needs mirroring or exposing in the
  IR. The `[[fallthrough]];` statement must leave a recognizable marker at
  build time (today it lowers to nothing).
- **Opt-in**: a `-Wimplicit-fallthrough` CLI flag (the `-Wconversion` shape) —
  it's a **style/lint** warning, off by default in gcc/clang too (needs
  `-Wextra` or explicit opt-in), not a correctness gate.
- **Caveat**: dotcc strips comments in the preprocessor, so it cannot honor
  gcc's alternative `/* fall through */` comment convention — only the
  attribute suppresses.
- gcc wording to match: `this statement may fall through` (ground in WSL).

## Not in this batch (unchanged tiers from the sweep)

- **Nice-to-have, real work (demand-driven)**: `_BitInt(N)` (≤128 onto
  `Int128` with width-masked stores), `_Decimal32/64/128` (needs a clean-room
  IEEE decimal — .NET's `decimal` cannot carry it), the `U"…"`/`u8"…"`/
  char32_t cluster (→ C# `uint`, NOT `Rune`).
- **Attributes positions** (param / struct-member / type) — parse-level cuts,
  separate from the two warnings above.
- **Correctly out of scope**: VLAs, trigraphs/digraphs, Annex K, K&R defs,
  async signal delivery, `gets`.
