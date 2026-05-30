# Handover — C23 (and later) keyword handling

`-std=` v1 has shipped and is committed (predefined-macro path only —
`__STDC__` / `__STDC_HOSTED__` / `__dotcc__` always, `__STDC_VERSION__`
per dialect, omitted for c90). Accepted: `c90` / `c99` / `c11` / `c17` /
`c18` (alias) / `c23`. Default `c17`. No `gnu*` dialects — dotcc
implements zero GNU extensions and will not advertise itself as one. No
`c89` spelling — c90 is the single canonical name.

These are throwaway notes for whoever picks up parser-level dialect
awareness.

**Progress:**
- **Step 1 — DONE.** The rewriter scaffold with C23 `bool → _Bool` as the
  first row — `DotCC.Lib/DialectKeywordRewriter.cs`, wired into
  `Compiler.EmitCSharp` between `MacroExpander` and `TypeNameRewriter`.
- **Step 4 (`true`/`false`/`nullptr`) — DONE** (commit "C23 keyword
  constants"). Dedicated `TRUE`/`FALSE`/`NULLPTR` grammar terminals (no
  lexer rule), rewriter promotes under c23, lowers to C# `true`/`false`/
  `null`. Fixture `c23-constants/`. Also added the per-fixture `std.txt`
  sidecar so fixtures can pick a dialect, threaded through FixtureRunner +
  gcc oracle (`GccStdFor` maps c23→c2x).
- **`static_assert` (part of steps 2+3) — DONE** (this commit). Grammar:
  `_Static_assert` keyword at file + block scope, C11 two-arg + C23
  message-less arities; rewriter promotes lowercase `static_assert` under
  c23. Lowered to an inert comment (compile-time only; NOT evaluated — no
  const-eval pass yet). Fixture `c23-static-assert/`.

**Still not started:** the rest of step 2/3 (`thread_local`/`_Thread_local`,
`alignas`/`alignof` + the `_Capital_` forms) and all of step 5 (real-grammar
items `typeof`, `constexpr`, `_BitInt`, decimal FP, `auto` flip). Note:
`_Thread_local` / `_Alignas` / `_Alignof` have no clean C# lowering in the
current emit shape (no per-local alignment; `[ThreadStatic]` is field-only) —
weigh value before building grammar for keywords that can only stub.

## The architectural split

The deferred "step 3" needs a real architectural call before we touch
it. Three layers can carry the gating; the right one differs per feature.

1. **Rewriter layer** (existing pattern — `TypeNameRewriter` already lives
   here). Add a sibling `DialectKeywordRewriter` taking a table of
   `(dialect_min_version, source_id, target_token)`. Sits in the
   pipeline AFTER `MacroExpander` and BEFORE `TypeNameRewriter` (or
   alongside; the rewriters compose), and promotes `ID → KEYWORD` only
   when the active dialect demands it.
   - **Cheap.** No LALR.CC change. Same lexer hack we already use.
   - **Best for:** lowercased aliases of existing `_Capital_` keywords,
     and brand-new keyword-shaped constants (`true` / `false` / `nullptr`).

2. **Grammar layer** (real productions in `c.lalr.yaml`). For each new
   keyword: add the token to the `symbols:` list, slot it into the
   relevant production (TypeSpec / DeclSpec / Constant / new construct),
   regenerate.
   - **Necessary for** genuinely new syntax that doesn't fit the
     promotion shortcut (`typeof`, `constexpr`, `_BitInt(N)`, `_Decimal*`).
   - Combines with the rewriter: rewriter promotes the *spelling*, the
     grammar holds the *shape*.

3. **Visitor layer** (`CSharpEmitter`). Throw `CompileException` when a
   parsed construct is too new (or too old) for the active dialect.
   Reads `Compiler`'s threaded `CDialect`. Lossy (you parsed it before
   rejecting) but cheap.
   - **Best for:** *restrictions* — features that exist in the grammar
     today but shouldn't be available under older dialects (e.g.
     `_Generic` under c90, designated initializers under c90).
   - Also: *semantic flips* like C23's `auto` (storage class → type
     inference) — visitor reads dialect and branches the lowering.

We do **not** want first-class dialect awareness inside LALR.CC. That
would be the right answer if dotcc were one of several
standards-versioned-language clients of LALR.CC, but it isn't, and the
table-doubling / runtime-gating cost is real. Revisit if we ever have a
second client.

### The two-rule gating model (which layer for which feature)

The clean principle is **"always parse the newest standard (the
superset), gate per-dialect in semantic analysis"** — clang/gcc work
this way (parse a superset, then `-pedantic`-diagnose "X is a C23
extension"). It keeps a single grammar/lexer. It holds for genuinely
*new syntax*, but **breaks for new keywords that are spelled like
ordinary identifiers**, because the parser runs before the visitor:

1. **New syntax that can't be a valid old-dialect identifier**
   (`_BitInt(N)`, `typeof`, `constexpr` declarations, `_Generic`,
   `[[attributes]]`) → **gate in the visitor (layer 3).** Grammar
   accepts it always; visitor reads the threaded `CDialect` and throws
   `CompileException("_Generic requires -std=c11")`. Safe because the
   spelling can never collide with a user identifier.

2. **New keyword spelled like an identifier** (`bool`, `true`, `false`,
   `nullptr`, `constexpr`, `static_assert`, `thread_local`, `alignas`)
   → **gate at the rewriter (layer 1), dialect-aware promotion.** These
   are *legal identifiers* in older dialects (`int true = 5;` is valid
   C99). If the grammar always treated them as keywords, valid old code
   would hit a **parse error** and the visitor would never run — you
   can only semantically-gate what the parser accepted. So the
   `ID → KEYWORD` promotion itself carries the `MinVersion`.

3. **`_Capital_` keywords** (`_Bool`, `_Static_assert`, `_Alignas`,
   `_Alignof`, `_Thread_local`) → **always lex as keywords; optional
   visitor gate.** Leading-underscore-plus-capital is reserved to the
   implementation in *every* dialect, so there's no collision risk —
   safe to accept always, and gate *availability* in the visitor only
   if we want e.g. `-std=c90` to reject `_Static_assert`.

The `MinVersion` on a rule-2 promotion does **two** things at once, and
this is the key insight — `bool` is the worked example:

- **In the C23 standard, `bool` / `true` / `false` are first-class
  keywords** — no `<stdbool.h>` needed. In C23 the header is vestigial
  (only `__bool_true_false_are_defined`; the macros are gone *because*
  the words are keywords).
- **In C99/C11/C17, `bool` is NOT a keyword** — it's the `<stdbool.h>`
  macro `#define bool _Bool`.
- **dotcc today models only the C99 view in every dialect**, so it is
  wrong in *both* directions: under `-std=c23` it still forces an
  `#include <stdbool.h>` for bare `bool` to work, and it offers the C99
  header even under `-std=c90` (where `<stdbool.h>` predates the
  standard).

A dialect-aware rewriter promotion fixes both with one `MinVersion`:
- **`>= c23`:** promote bare `bool → _Bool`, `true`/`false`/`nullptr` →
  keyword constants, **no include required**.
- **`< c23`:** don't promote — `bool` stays an ordinary identifier
  unless `<stdbool.h>` is included (which then supplies the macro).
  This is also what protects valid pre-C23 code using `bool`/`true` as
  identifiers from breaking.

## C23 keyword surface — per-bucket plan

| New keyword(s) | Pre-C23 form | Bucket | Notes |
|---|---|---|---|
| `bool` | C23 keyword; pre-C23 it's the `<stdbool.h>` macro for `_Bool` | Rewriter (rule 2) | Grammar already has `_Bool`. `>= c23`: promote bare `bool → _Bool` with no include needed. `< c23`: don't promote — `bool` stays an identifier unless `<stdbool.h>` is included. `MinVersion` does both at once (see two-rule model). |
| `static_assert` | `_Static_assert` (C11) | Rewriter + grammar | Need to add `_Static_assert` to grammar first, then rewriter promotes lowercase. |
| `thread_local` | `_Thread_local` (C11) | Rewriter + grammar | Same shape as above. |
| `alignas` / `alignof` | `_Alignas` / `_Alignof` (C11) | Rewriter + grammar | Same shape. |
| `true` / `false` | `<stdbool.h>` macros → `1` / `0` | Rewriter + small grammar | New `Constant → TRUE | FALSE` productions; rewriter promotes IDs. |
| `nullptr` | `NULL` macro / `(void*)0` | Rewriter + small grammar | New `Constant → NULLPTR` production. |
| `typeof` / `typeof_unqual` | GNU extension | Real grammar work | New operator-style production; no rewriter shortcut. |
| `constexpr` | — | Real grammar work | New decl-spec; constraints on emit. |
| `_BitInt(N)` | — | Real grammar work | Parameterised type constructor; runtime support too. |
| `_Decimal32` / `_Decimal64` / `_Decimal128` | — | Real grammar + libc | Decimal FP types; needs runtime support (.NET `System.Decimal` adjacency, but not 1:1). |
| `auto` (semantic flip) | storage class | Visitor | Dialect-branched lowering: drop under < C23, infer type under ≥ C23. |

## Suggested order

1. ~~**Land the rewriter scaffold with `bool` as the first row.**~~
   **DONE.** `DialectKeywordRewriter` slots in between `MacroExpander`
   and `TypeNameRewriter`; dialect threads via the `activeDialect` local
   in `Compiler.EmitCSharp`; the promotion table is a
   `Dictionary<string,(MinVersion,TargetSymbol,TargetText)>` keyed by
   spelling. Unit tests assert the c23-on / pre-c23-off / include-path
   behaviours.
2. **Add the four C11 `_Capital_` keywords to grammar** (`_Static_assert`,
   `_Thread_local`, `_Alignas`, `_Alignof`) WITHOUT visitor support
   first. Just gets them parseable. Visitor can emit `TODO` stubs or
   throw "not yet implemented" with a clear message.
3. **Layer the C23 lowercase promotions on top** (`static_assert`,
   `thread_local`, `alignas`, `alignof`) — one rewriter-table entry
   each, once the grammar has the targets. Run under c23 mode only.
4. **Add `true` / `false` / `nullptr`** — same recipe, plus the
   `Constant → ...` productions.
5. **Real-grammar items** (`typeof`, `constexpr`, `_BitInt`, decimal FP,
   `auto` flip) — each its own follow-on, scope independently.

## Where the rewriter would live

- New file `DotCC.Lib/DialectKeywordRewriter.cs`. Same shape as
  `TypeNameRewriter`: `RewritingTokenStream` subclass, constructor takes
  the inner stream + the active `CDialect`, internal table of
  `(int MinVersion, string IdText, int TargetSymbolId)` entries.
- Wire into `Compiler.EmitCSharp` between `MacroExpander` and
  `TypeNameRewriter`. Threading the dialect: already threaded as far as
  `CPreprocessor` and `SeedDialectDefines`; just propagate one more hop
  to the rewriter constructor.
- The table is data, not code. Seed it inline in the rewriter ctor (~9
  entries for the C23 set above). When C2y adds more, append rows.

## Open questions for next-me

1. **Reverse direction — too-new keyword under an older dialect.**
   Mostly resolved by the two-rule model: rule-2 promotions are gated
   by `MinVersion` so a too-new *keyword* simply isn't promoted and
   stays an ordinary identifier (then fails at the C# layer if used as
   one — acceptable, same observable result as a strict rejection just
   one layer later). We do NOT need an explicit "refuse" path in the
   rewriter for these. The only case wanting a real dotcc diagnostic is
   rule-1 *new syntax* under an old dialect — that's the visitor's job.
2. **`<stdbool.h>` body under c23.** Once the rewriter makes `bool` a
   keyword at `>= c23`, the C99 header's `#define bool _Bool` is
   redundant (benign no-op promotion). Per the C23 standard the header
   is vestigial — only `__bool_true_false_are_defined`. Gate the macro
   body on `#if __STDC_VERSION__ < 202311L` when the rewriter lands.
3. **`auto` semantic flip** — visitor-level branch, but the C# lowering
   under C23 is non-trivial (we'd need a real type-inference pass on
   the AST). Probably the LAST item on the C23 list; defer indefinitely.
4. **Header hygiene as a parallel commit** — guard `<stdbool.h>`,
   `<stdatomic.h>`, `<stdint.h>`, `<stdalign.h>`, `<stdnoreturn.h>` on
   `__STDC_VERSION__ >= …L`. Independent of the rewriter; cleanup item.
