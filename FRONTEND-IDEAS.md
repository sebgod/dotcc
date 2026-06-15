# Frontend ideas for dotcc

A scratchpad for **new language frontends** dotcc could grow, and the strategies for
getting there. Nothing here is committed work — it's a place to think before we
plan. Each entry tries to be honest about effort and the biggest unknowns, not just
the happy path.

## Framing: what a "frontend" costs

dotcc is a pipeline:

```
source → LALR.CC grammar (.lalr.yaml) → typed IR → backend (C#, wat)
```

Everything **downstream of the IR is reusable** — the typed IR, the C# backend, the
wat backend, and the runtime-shim mechanism. So a new frontend ≈

1. **a grammar** (LALR(1) tables from a YAML file), plus any `RewritingTokenStream`
   stages for lexer hacks (the C frontend uses these for the typedef lexer-hack,
   dialect-keyword promotion, etc. — the same slot absorbs Python indentation or
   Swift contextual keywords);
2. **a lowering** from that AST to the shared IR; and
3. **a runtime / standard-library shim** in the `DotCC.Libc` style (one source,
   compiled both as a DLL for tests and spliced into emits as an embedded resource).

**Reusable machinery any frontend inherits for free:**

- LALR(1) table generation from YAML; `RewritingTokenStream` for tokenizer tricks.
- The typed IR + both backends (C# now, wat already, more later).
- **Synthetic system headers** (embedded resources) — "the language's standard
  headers, no disk I/O." `<Python.h>`, a Swift module map, etc. drop in here.
- The **embedded runtime shim** — single-source-of-truth runtime that's both a
  unit-tested DLL and the block spliced into every emit.
- **IR-level composition** — because every frontend lowers to the *same* IR, one
  language can call another's lowered code in one .NET world. This is the key
  multiplier (it's the basis of the Zig `@cImport` plan): a Python/Swift frontend
  reuses the **C frontend** to absorb C headers/extensions rather than reimplement.
- **Native interop**: `-shared` cdecl export (`[UnmanagedCallersOnly]`) + import
  mode (GOT-style `dlopen` binding) + `<dlfcn.h>` over `NativeLibrary`. Validated
  end-to-end by the shared-lib round-trip oracle.

**Rough evaluation rubric** (per idea): grammar tractability · semantic distance
from C#/.NET · size of the stdlib/runtime to shim · whether IR-composition with the
C frontend does heavy lifting · is the *value* unique vs. just using the language's
existing toolchain.

### Two routes for any non-C language: source vs. compiled

Every language below can be approached two ways, and most entries list both:

- **Route A — source frontend.** A `<lang>.lalr.yaml` grammar + lowering to the IR.
  Faithful to the *syntax*, gives readable C#, first-classes the language in dotcc —
  but **reimplements the language's semantics + standard library** (the long pole).
- **Route B — compiled-input frontend.** Consume the language's **compiled output**
  (WebAssembly or LLVM IR) and lower *that* to the IR. The language's own compiler
  does the semantic + stdlib work; one such frontend unlocks *many* source languages
  at once. Cost: you lose source-level structure (names/types/structured control
  flow) and may want a lower-level backend (see #5). This is the generalization of
  the Wasm/LLVM-IR idea in #5.

A is the path when we want the language's *syntax* in dotcc; B is the path when we
just want its code to *run* on .NET. They're not exclusive, and they sequence
differently. The backend axis (emit C# source vs. emit IL directly) interacts with
this — see #5.

### Grammar tractability — an LALR(1) parseability rubric

For any **source** frontend (Route A), the first question is whether LALR.CC can
chew the grammar. The honest framing: *almost any* language can be forced through an
LALR(1) grammar **if you let the lexer cheat**. So the real axis is **how much
violence the lexer/precedence layer does** before the context-free core is
LALR(1)-clean — and dotcc's dividing line is concrete: **does the nastiness fit in a
`RewritingTokenStream`?** That slot already absorbs the C typedef lexer-hack and
dialect-keyword promotion; it equally absorbs INDENT/DEDENT injection, `>>`
token-splitting, and contextual-keyword promotion. If the language's hard part fits
there → cheap. If it needs the **symbol table mid-parse**, **backtracking**, or is
genuinely **context-sensitive** → expensive (hand-RD) or impossible via LALR.

| Tier | Cost | Languages |
|---|---|---|
| **1 — clean LALR(1)** | yacc/Bison-able as-is | Pascal · **C** (modulo the typedef lexer-hack + dangling-else precedence) · SQL (PostgreSQL/MySQL ship real Bison grammars) · PHP (`zend_language_parser.y` is Bison) · Lua · Go (`gc` was yacc through 1.4) · Scheme/Lisp (trivial) |
| **2 — LALR(1) + bounded lexer hacks** | one `RewritingTokenStream` | Java (`>>`/`>>>` token-splitting) · Haskell (**GHC uses Happy/LALR**, with the off-side `parse-error(t)` feedback) · Ruby (Bison `parse.y`, but a lexer-state swamp) · Python (INDENT/DEDENT injection; LL/pgen-parsed for ~30 yrs, PEG only since 3.9) · **Zig — lands here but leans Tier 1, see below** |
| **3 — fights LALR; wants backtracking / hand-RD** | speculative parse | C# (the `F(G<A,B>(7))` cast-vs-compare) · JavaScript (ASI + `/` regex-vs-divide + arrow lookahead) · Rust (turbofish, macro token-trees) · Swift (custom operators, trailing closures, heavy contextual disambiguation) · **Mercury/Prolog** (operator-precedence term parser over a *runtime-mutable* `op/3` table — not a fixed grammar) · fixed-form Fortran (insignificant whitespace + no reserved words → not even tokenizable) |
| **4 — not LALR(1) at all** | GLR / hand-RD only | **C++** (context-sensitive: `a<b>c`, most-vexing-parse, `typename` needs the symbol table mid-parse — GCC & Clang are both hand-RD) · Perl (provably undecidable — BEGIN blocks rewrite the grammar) |

**Where the ideas land:** Python source (#1, Route A) is **Tier 2** — the
INDENT/DEDENT rewriter drops it squarely in LALR.CC's wheelhouse; *genuinely cheap*.
Swift source (#3a) is **Tier 3** — contextual disambiguation that wants backtracking
LALR.CC doesn't give, which is the concrete reason #3b (compiled Wasm input) is the
better Swift bet. Anything C++-shaped is **Tier 4** — reachable only through a
compiled-input route (#5: IR, not grammar). And **Zig is the surprise** — see #2.

#### PEG / operator-precedence sources — and why LALR is an *implementation detail*

Two ideas have grammars not specified as CFGs at all: Zig ships an official **PEG**,
Mercury/Prolog uses an **operator-precedence** reader. Natural question: can we ingest
PEG directly, or auto-translate PEG → LALR? The layered answer:

- **A general, automatic PEG→LALR translator is impossible** — the classes are
  *incomparable*, not nested. PEG recognizes some non-context-free languages (`aⁿbⁿcⁿ`
  via predicates), so no CFG exists for those; and PEG is always unambiguous, so an
  *inherently ambiguous* CFL has no PEG at all. There isn't even an algorithm to decide
  whether a given PEG *is* context-free, let alone emit the CFG.
- **The tame subset real language-PEGs live in transcribes semi-mechanically**, and two
  formalism inversions make it clarifying: PEG forbids **left recursion** while LALR
  prefers it (so you rewrite `e*`/right-recursion into the left-recursive rules LALR
  wants), and PEG is never ambiguous while a CFG can be — so translation can *introduce*
  conflicts, and **the PEG's ordered-choice priority is exactly the resolution hint**. A
  "translator" is really *"make PEG's implicit ordering explicit as LALR.CC precedence
  groups"* — which is just LALR.CC's existing conflict-driven workflow
  (`GrammarConflictException` → add a precedence group). For one grammar (Zig), hand
  transcription guided by those conflict errors is *less* work than any translator, and
  faithful. Syntactic predicates `&`/`!` are the part that must move to the lexer /
  `RewritingTokenStream` or be flagged.
- **If PEG inputs ever became recurring, add a PEG *engine* (packrat) behind the
  `IFrontend` seam, not a translator** — the worst option is the impossible-in-general,
  lossy-in-practice middle path.

That last point generalizes: **LALR(1) is an implementation detail of *how the C
frontend happens to be built*, not an architectural commitment of dotcc.** The real
contract is "a frontend produces typed IR" (`IFrontend`); the parser *technology* is a
per-frontend choice downstream of that seam. LALR.CC is simply the cheapest, cleanest
tool for C (and for the Tier 1–2 languages), so we use it — but a PEG engine, a
hand-written recursive-descent parser, or an operator-precedence reader could equally
feed the same IR for a language where LALR is the wrong fit. So this whole rubric is a
*cost-if-you-build-on-LALR.CC* table, **not** a gate on which languages dotcc can host:
Tier 3/4 languages aren't excluded, they just don't get to use the LALR.CC shortcut.

---

## 1. Python — own frontend + "pretend to be CPython" for native extensions

**Motivation.** IronPython is a nice Python-on-.NET but structurally **can't build
wheels that need native (C) code**. dotcc could do both halves in one toolchain:
a Python frontend *and* compile a wheel's C extension to .NET via the existing C
frontend — so a native-code wheel works (built from source, not loaded as a
prebuilt `.so`).

**Two decoupled halves** (prototypable independently): the **runtime/frontend** (how
we get a Python that runs) and the **C-extension shim** (how native extensions plug
in). The shim is orthogonal — it sits atop whatever runtime Route A or B produces.

- **Python frontend — Route A (source)** (`python.lalr.yaml`). Conventional-but-big.
  The one Python-specific wrinkle for LALR.CC is significant indentation — handled by
  a pre-lexer that emits `INDENT`/`DEDENT`/`NEWLINE` tokens (the off-side rule), which
  is exactly the `RewritingTokenStream` slot. Python was LL/pgen-parsed for ~30 years
  before the PEG switch, so most of it is LALR-able once those tokens exist. The long
  pole is *semantics + stdlib*, not the grammar.

- **Python — Route B (compiled): weaker than for Swift, list it but lean A.** Python
  has no clean standalone compiled artifact to consume. The options are (i) ingest
  CPython **bytecode** (`.pyc`) and supply a VM — but the ops assume CPython's object
  model, so that's ≈ reimplementing the runtime (IronPython-shaped), skipping only
  the parser; or (ii) **Python-on-Wasm** (Pyodide-style) under the #5 Wasm frontend —
  which degenerates into "run *all* of CPython as Wasm on .NET," i.e. the rejected
  compile-CPython idea in Wasm clothing. So for Python, Route A is the natural primary
  and Route B is mostly a curiosity. (Contrast Swift, where `swiftc → Wasm` makes B
  genuinely attractive.)

- **C-extension shim** (the dotcc-flattering half). A synthetic `Python.h` family
  (just another set of embedded headers) + a libpython shim (the `DotCC.Libc`
  pattern) implementing the C-API over our own .NET Python object model. "Pretend
  to be CPython" = at **build time** declare the API + version/feature macros so a
  wheel's `#include <Python.h>` resolves and it *believes* it's CPython; at **run
  time** the shim bridges those calls to our objects.

**The insight that makes "pretend" tractable: target the Limited API / `abi3`.**
The normal nightmare is that `Py_INCREF` & friends are *macros that compile inline*
and poke `((PyObject*)o)->ob_refcnt` — so the extension depends on our exact
`PyObject` layout (struct-ABI hazard). The **Limited API** (`Py_LIMITED_API` →
`abi3`) was built to kill that: `PyObject*` is **opaque** and `Py_INCREF` becomes a
**function call**. So with an abi3 synthetic header:
- `PyObject*` is an **opaque handle** — an index into a managed table, a GCHandle
  token, whatever our runtime wants; the extension never dereferences it.
- Everything is **function calls** → dotcc's cdecl-shim/export sweet spot.
- No struct-layout matching, no GIL internals, no `obmalloc`.

Honest scope: abi3 excludes full-API wheels (numpy-core internals), but a real and
growing set ships abi3 (`cryptography`, `pydantic-core`, …). That's the bounded
target. Wheel flow is **build-from-source with dotcc-as-`CC`**, not load-prebuilt —
the only sane path in a managed world.

**Why not "just compile CPython's C with dotcc"?** Considered first; rejected for
the wheel goal. CPython is ~600k LOC + autotools + threads/GIL + `obmalloc`/`mmap` +
computed-goto `ceval` (gateable off) — "get the runtime for free" is illusory. The
pretend-CPython route trades that for "build our own runtime, implement only the API
surface extensions call," which is strictly more achievable for the stated goal.
(`configure`/`make` themselves are *not* a blocker — dotcc is a `cc` drop-in;
hand-write `pyconfig.h` and skip configure's run-probes.)

**Cheapest first probe.** The canonical docs **`spam` extension** (`PyModule_Create`
+ one function using `PyArg_ParseTuple`/`Py_BuildValue`): minimal limited-API
`Python.h` + a stub shim, compile with dotcc, call `PyInit_spam` from a ~30-line
.NET host. Round-trips ⇒ the architecture is proven and we have the abi3-shim
skeleton, *before* committing to the Python frontend.

**Status:** idea. Highest near-term signal = the `spam` probe.

---

## 2. Zig — IR-level `@cImport`, not translate-c

Parked (full plan: `~/.claude/plans/dotcc-zig-frontend.md`, memory
`project_zig_frontend_plan.md`). The recorded core decision still holds: `@cImport`
is **IR-level composition** with the existing C frontend, *not* a translate-c text
pass — the C frontend lowers the imported headers to IR and the Zig frontend composes
against that. This is the canonical IR-composition multiplier and informs every other
"language that needs to talk to C" frontend (Python extensions above, Swift C-interop
below).

**Parked on *semantics*, not *parsing* — and the grammar is cheaper than C's.** This
is the correction worth recording. Zig sits in the rubric's Tier 2 but leans Tier 1,
*below C*, because the one thing that forces C off the clean path — the typedef
lexer-hack, where `Color * x;` is a decl-or-multiply depending on the **symbol
table** — simply doesn't exist in Zig:

- **No typedef / no type-vs-identifier ambiguity.** Types are ordinary `comptime`
  values (`const T = struct {…};`), so there's never a "is this token a type?"
  question mid-parse. No semantic feedback to the lexer.
- **No preprocessor**, **no whitespace significance** (no INDENT/DEDENT), **no
  contextual-keyword soup.** So Zig needs **zero `RewritingTokenStream` stages** —
  not even the one C requires. A strictly lighter front than the C frontend we ship.
- The only friction is mechanical PEG→LALR translation: a handful of bounded
  shift/reduce conflicts that resolve with precedence + one-token lookahead — **none
  need the symbol table.**

**M0 grammar spike — ✅ DONE (validated empirically, not just argued).** A slice-1
hand translation of Zig's C-shaped value/type core generates **clean LALR(1) tables —
zero shift/reduce or reduce/reduce conflicts**, parses real Zig (functions, pointer/
optional types, error-union returns, calls, field chains, `.*`/`.?`, if/else, while)
through the real `BytesLexer → Parser.ParseInput` pipeline, and rejects faithfully
(the non-associative `a < b < c`) — with **zero `RewritingTokenStream` stages**. The
feared part — Zig's *unified* value/type grammar, where `*` `&` `[` `!` `.` each pull
double duty (operand-position type/prefix vs after-operand binary/postfix) — separates
purely by **parser state**, no precedence hacks beyond the cascade + a `rightmost`
dangling-else group. So the v1 subset is *more* tractable than the C grammar in
production — now confirmed, not conjectured. Lives as a conflict regression in
**LALR.CC `examples/Zig/`** (PR #4 → SharpAstro/LALR.CC; grammar frozen + pinned to
`ziglang/zig` `3391ad7a`, oracle `zig 0.17.0-dev.667`).

**The real open items, and their status:**

1. **The remaining grammar risk — `!ExprSuffix` / `!BlockExpr`.** Slice 1 deliberately
   deferred control-flow *as expressions* (`if`/`for`/`while`/`switch` exprs), which
   PEG gates with **negative-lookahead predicates** — the one genuine PEG-ism, where
   LALR can't say "this alternative only if *not* followed by an operator." Whether
   that translates (precedence) or forces an over-accept + semantic check is the next
   real test; everything else in the grammar is now known-clean.
2. **The frontend seam (M0's other half).** `ITarget` exists (the M-axis: `cs`, `wat`);
   there is **no `IFrontend` yet** (verified — only `ITarget` in `Ir/Target.cs`).
   Extracting it (C pipeline behind it, byte-identical) is pure refactoring, and with
   the grammar spike done it's now the **gating prerequisite** to stand up the
   production Zig frontend *in dotcc*.
3. **comptime.** The plan scopes v1 comptime to **const-folding only**, and *that*
   has a concrete, independently-valuable build-up path — see below.

### Doing C23 `constexpr` first — the const-eval bridge to comptime ⭐

**Is "const evaluation for C23" a thing?** Yes. C23 adds **`constexpr`** (N3096
§6.7.1) — but for **objects only** (`constexpr int N = 8;`, `constexpr double k =
1.5;`), *not* functions (unlike C++). A `constexpr` object is a named compile-time
constant usable anywhere a constant expression is required (array bounds, `case`
labels, enum init, further `constexpr`); its initializer must itself be constant and
must convert without value change. It's spelled like an identifier, so it'd be gated
the rule-2 way — `DialectKeywordRewriter` promotes it to a keyword only under
`-std=c23` (exactly like `bool`/`true`/`nullptr`), per the no-fake-keywords rule.
**dotcc has it as ❌ today** (`C-SUPPORT.md`).

**Why doing it first helps Zig.** Zig comptime-v1 and C23 `constexpr` need the *same
primitive*: evaluate an initializer to a value, bind it to a **named const symbol**,
and resolve later references to that symbol in further constant contexts. dotcc's
`ConstEval` (`IrBuilder.cs`) already folds expressions and resolves `EnumConstRef` —
but it's **integer-only (`long?`)** and has **no general const-object binding**.
`constexpr` forces exactly that generalization:

- a `ConstObjRef`-style node (the analog of the existing `EnumConstRef`) so a folded
  const flows back into `ConstEval` — *the same node Zig comptime const-bindings
  resolve through*;
- pushing `ConstEval` past `long?` toward **float** (C23 `constexpr` allows floating
  constants; Zig has `comptime_float`) — a shared generalization either way.

So: build the const-eval foundation **under a real, shipping C23 feature**, and the
Zig comptime-v1 prerequisite falls out as a byproduct — a standalone C win that also
retires open item #2. That's the "do it first" payoff, concretely.

**Honest limit of the overlap.** C23 `constexpr` ≈ Zig comptime *v1* — declarative
constant *objects* and folding. It is **not** full Zig comptime (Turing-complete
compile-time *execution*: loops, function calls, type construction), which both the
plan's v1 and `constexpr` deliberately leave out. So `constexpr`-first de-risks the
**v1** comptime blocker, not full comptime — but v1 is exactly what the plan commits
to, so that's the right-sized win.

### One allocator abstraction, shared by C *and* Zig

The sharper goal (not "collapse Zig's allocators down to C's `malloc`", the inverse):
make Zig's explicit-allocator model the **shared allocation substrate for *both*
frontends**. Zig has **no global `malloc`** — allocation goes through an explicit
`std.mem.Allocator` value (a `{ ptr: *anyopaque, vtable: *const VTable }` of
`alloc`/`resize`/`free` fn-ptrs) threaded as a parameter. C keeps its single *implicit*
allocator, but **under the hood that's just the one global/default instance of the same
abstraction**: one `Allocator` surface in the `DotCC.Libc` runtime; C binds the default
instance, Zig threads instances explicitly.

**dotcc already 80% has this — it just isn't abstracted.** The `-fsanitize=address`
debug heap (`DOTCC_DEBUG_HEAP=1`) *already* swaps C's `malloc`/`free` wholesale for a
redzone/size-header implementation at startup. That swap **is** "pick a different
allocator instance" — `c_allocator` vs the debug/GP allocator is the same operation in
Zig. Formalize it into one abstraction and the debug heap, a future arena/region, and
Zig's allocators are all just instances of one vtable.

- **IR**: keep an allocator-aware allocate/free node — C lowers with the operand
  *absent* (→ default instance), Zig with an explicit operand. The malloc→`stackalloc`
  peephole still fires on the node *before* allocator binding, so it's unaffected.
- **Runtime**: one `Allocator` — `NativeMemory` default · the ASan debug heap (already
  built) · `ArenaAllocator` (bulk-free on `deinit`) · `FixedBufferAllocator` (bump
  pointer over a `byte[]`/`stackalloc`). C's `-fsanitize=address` becomes "swap the
  global instance," said properly.

**Two honest wrinkles:**
- **`free` signatures differ.** C's `free(void*)` is size-less; Zig's vtable `free`
  takes the original length (Zig allocators don't store sizes). So the C default
  instance must recover the size — which the debug heap *already* does (its `[magic|
  size]` header) and size-less `NativeMemory.Free` doesn't need. The shared vtable
  carries Zig's `(ptr, len, align)`; C binds through a size-tracking adapter.
  Runtime-shim detail, no IR change.
- **Keep C's `malloc` zero-overhead.** Routing every C `malloc` through a vtable adds
  an indirect call. Mitigation: the default global instance is *statically known*, so
  the C# backend devirtualizes to direct calls — the vtable hop appears only when a
  non-default allocator is actually in play (Zig, or a C program that opts in).
  AOT-clean.

The realistic C-facing surface is **whole-program allocator selection** (as ASan is
today), since C has no syntax to thread allocators per-call — but real C libraries take
allocator callbacks (`lua_Alloc`, sqlite's `mem_methods`), and this abstraction is what
those bind to. And it's *cleaner than C* on the Zig side: because the allocator is
explicit in the source, a fixed-buffer/arena allocation is syntactically visible, so the
malloc→`stackalloc` promotion (which in C must *recover* escape/ownership by analysis)
gets the ownership hint for free. Memory stays in a `NativeMemory` arena **outside the
GC** (as in 3b) — native blobs, never GC objects; Zig has no ARC, so no ARC-vs-GC
tension. Net: unify C + Zig allocation, make the ASan swap principled and
arena-extensible, and the Zig allocator story falls out for free.

**Status: un-parked — moving to implementation.** The grammar question is answered
(M0 spike done, LALR.CC PR #4: slice-1 LALR(1)-clean, parses real Zig, zero rewriting
stages); the IR was already designed for zero v1 generalization; the comptime blocker
has a real on-ramp via C23 `constexpr`; and recent struct/union/bit-field MSVC-faithful
packing quietly helps (extern-struct fidelity + the slice `{ptr,len}` fat-struct). The
remaining gates, in order: **(1)** extract the `IFrontend` seam (byte-identical C
pipeline behind it); **(2)** port the production grammar into dotcc and push it through
slice 2 (`!ExprSuffix` control-flow-as-expr); **(3)** lower to IR + `@cImport`
composition; comptime as const-fold. The LALR.CC `examples/Zig` spike stays as the
isolated conflict regression; the real grammar + frontend live here in dotcc.

---

## 3. Swift — feasible only as an *Embedded Swift* subset (or via compiled IR)

**Motivation.** `../../drawboard/drawboard-core` has Embedded-Swift-discipline
targets (`Geometry`, `CoreToolbox`) that are deliberately **freestanding**: no
Foundation, no existentials (`any`), `EmbeddedRestrictions` warnings-as-errors.
Retargeting that pure-computation geometry to .NET via dotcc is a concrete, bounded
motivation (and there's no good Swift-on-.NET today).

**Two routes, very different cost:**

### 3a. Swift *source* frontend (`swift.lalr.yaml`) — large

Swift is a far bigger language than C; even the **Embedded** subset keeps ARC,
specialized generics, classes/structs/enums-with-payload, closures, optionals, and
static-dispatch protocols (it drops existentials, reflection/metadata, Foundation,
and the heavy runtime). The grammar is LALR-able as a subset; the cost is
**semantics + stdlib**, like Python. Mapping to .NET:

| Swift (Embedded) | .NET lowering | note |
|---|---|---|
| `struct` / `enum` (value types) | C# `struct` / tagged-union struct | clean — the natural fit |
| `enum` with payloads + pattern match | tag + union payload (the bit-field/union work helps) | matching lowers to `switch` |
| specialized generics | C# generics | Embedded monomorphizes, so no metadata needed |
| protocols (no existentials) | C# interfaces / generic constraints (static dispatch) | existential ban *helps* — maps to constraints |
| `class` (reference type) | `using`/`IDisposable` (unique, non-escaping) · emitted retain/release (shared) · GC+finalizer (lossy) | **ARC vs GC** — the central decision; see below |
| optionals | `T?` / nullable | clean |
| `throws` / `try` | exceptions or a result type | choose one |
| closures | C# delegates/lambdas | clean |

**The central semantic snag is ARC.** Swift classes are ARC-refcounted with
*deterministic* `deinit`; .NET GC is non-deterministic. For **value-semantic** code
(drawboard-core's `Geometry`: points, transforms, math — structs, no classes, no
`deinit` reliance) this barely matters, which is exactly why that module is the ideal
first target. For classes there's a three-tier lowering of `deinit`:

1. **`using`/`IDisposable` — the unique-ownership fast path.** A class instance that
   is a local, uniquely owned, and never escapes (not stored in a field, returned, or
   captured) lowers to `using var x = …;` — disposed deterministically at scope exit,
   which *is* ARC releasing the only reference there. This is just the statically-
   elided refcount. **dotcc already has the detector**: the `malloc`→stack-value
   peephole is exactly an escape + unique-ownership analysis, so the same machinery
   picks "this class can be a `using` local."
2. **Emitted `retain`/`release` — the faithful general case.** Shared ownership,
   escaping references, reassignment (`x = other` releases the old value mid-scope —
   and C# won't even let you reassign a `using` var), and early last-use release all
   break the `using` model. These need a refcount header on the instance (like
   dotcc's `malloc` machinery) with `deinit` run at release-to-zero — deterministic,
   matches Swift exactly, but woven through codegen (every bind/copy/capture/container
   op).
3. **GC + finalizer — lossy fallback.** Map the class to a C# `class` and `deinit` to
   `~Class()`. Simplest, but non-deterministic + finalizer hazards, and Swift code
   generally *assumes* deterministic `deinit` — so this changes observable behavior.
   Acceptable only where `deinit` is timing-insensitive cleanup.

The Embedded **stdlib** subset (Array/String/Optional/the numeric protocols) is the
other long pole — a `DotCC.Libc`-style Swift-runtime shim.

### 3b. Consume Swift's *compiled output* — likely more realistic ⭐

Instead of reimplementing Swift's semantics, let the **real Swift compiler** do the
hard part and lower its **output** to .NET. Embedded Swift emits freestanding
LLVM IR / object code, and SwiftWasm emits **WebAssembly**. dotcc already has a
**wat *backend*** (C → wat); the inverse — a **Wasm *frontend*** (consume `.wasm`,
lower to the IR → C#) — would let *any* Wasm-producing language (Embedded Swift,
Rust, Zig, C, …) target .NET through dotcc, with the source compiler handling all
the language semantics. The surface to support is then the small, well-defined
Wasm instruction set + a tiny import/host ABI, not a whole language. See idea 5.

**ARC: the strongest argument for 3b.** This route makes 3a's whole `deinit`
three-tier problem *disappear*, because **swiftc resolves ARC before the Wasm
exists.** ARC insertion + elision happens in SIL/the ARC optimizer, then IRGen emits
explicit `swift_retain`/`swift_release` runtime calls and wires the deinit call onto
the release-to-zero path — all before LLVM/Wasm. So by the time dotcc sees the
module there is **no ARC semantic left to model**, only plain mechanism:
- allocation → a runtime call; `retain`/`release` → calls that bump a refcount word
  at a known offset; `deinit` → already emitted, already called on the zero path.

dotcc therefore does **no escape analysis, no ownership reasoning, no
using-vs-refcount-vs-finalizer choice** — it just executes loads/stores/branches/
calls, and deterministic `deinit` timing is preserved for free. The work doesn't
vanish, it **relocates and shrinks** to the Embedded Swift runtime functions, in one
of two forms: **(a) bundled into the `.wasm`** (runtime linked in) → they're ordinary
in-module functions and a refcount is just a memory word, so **ARC is invisible to
dotcc**; or **(b) left as imports** → dotcc shims a small, *known* runtime ABI (a few
functions over a refcounting allocator) — the libc-shim pattern it already lives in.

The memory model is what makes it clean: in the Wasm, Swift objects live in **linear
memory**, manually refcounted; dotcc lowers linear memory to a `NativeMemory`-style
arena (`byte*`) *outside* the .NET GC — so Swift objects **never become GC objects**,
they stay refcounted blobs exactly as native, and the "ARC vs non-deterministic GC"
tension of 3a never arises. Trade-off: Swift code that *runs* in a sandboxed heap,
not Swift objects that *integrate* first-class with C# objects. Note the symmetry —
**3b's advantage is largest exactly where 3a is hardest (classes/ARC) and smallest
where 3a is already trivial (pure value-type `Geometry`).**

**Status:** idea. If Swift specifically is the goal, **3b (Swift→Wasm→dotcc→.NET)**
is probably the shorter path than a source frontend; a source frontend only pays off
if we want Swift *syntax* first-class in the dotcc world. Cheapest probe for 3b: take
one Embedded `Geometry` function, `swiftc`-compile it to Wasm, and see how small the
instruction/import surface actually is — and whether the Embedded runtime comes
bundled (case a) or as imports (case b). (Embedded-Swift-*to-Wasm* is an emerging
toolchain combo, so that split is empirical; the ARC data-flow above holds either way.)

---

## 4. Scheme — already runs on dotcc (via C)

Not a frontend, but the proof point: **chibi-scheme**, transpiled C→.NET by dotcc,
passes the full R7RS suite (1225/1225), identical to the gcc baseline (memory
`project_chibi_scheme_campaign`). So "a real language runtime runs on dotcc today"
is already true *through the C frontend* — which is the same lever the Python and
Swift-via-C-interop ideas pull.

---

## 5. Compiled-input frontends (Wasm / LLVM IR) — the meta-strategy ⭐

The highest-leverage idea, because it's **one frontend that unlocks many languages**
(this is Route B generalized). Each source compiler does the semantic + stdlib work;
dotcc lowers its *output* to the IR → .NET. Two candidate input formats, with quite
different characters:

- **WebAssembly.** Small, stable, W3C-standardized instruction set; linear memory;
  `i32/i64/f32/f64` (+ newer GC/reference types). Crucially it has **structured**
  control flow (`block`/`loop`/`if`/`br`), which maps to C# loops+breaks far more
  cleanly than arbitrary CFGs. dotcc already lowers C → wat (a wat **backend**), so we
  understand the model from the other side; a wat **frontend** (consume `.wasm`) is
  the natural inverse. Anything that targets Wasm — Embedded Swift (3b), Rust, Zig,
  Go(ish), C — comes along. Support surface = the (small, stable) instruction set + a
  host/import ABI.

- **LLVM IR.** Richer and more faithful — keeps struct types, named SSA values, and
  `getelementptr` addressing, so a lowering could recover more structure. But the
  surface is **large, target-specific** (datalayout, intrinsics, calling conventions)
  and **not stable** (textual `.ll` and bitcode drift across LLVM versions). Control
  flow is an **arbitrary CFG** (branches + basic blocks), so the C# backend would hit
  the relooper / structured-control-flow-reconstruction problem — though dotcc already
  solved the inverse (goto via a CFG **dispatch loop**, see
  `project_wat_goto_dispatch_loop`), so the same dispatch-loop trick carries over.

  **Cheap shortcut worth noting:** `llvm-cbe` (LLVM IR → C) → dotcc's **existing C
  frontend** → .NET. Zero new frontend; a real probe path. Caveats: llvm-cbe emits
  ugly low-level C and is a semi-maintained LLVM side project — fine for an
  experiment, not a foundation.

**The backend question (emit C# source vs. emit IL directly).** Today dotcc emits
**C# source** that Roslyn compiles — readability is part of the value *for the C
frontend*. For compiled-IR input that value is already gone (no source structure to
preserve), and a **direct IL/CIL backend** is the more natural fit: IL is itself a
**stack machine with unstructured `br`/labels**, so it matches Wasm (stack machine)
and LLVM (CFG) directly — no control-flow reconstruction, no "make it valid readable
C#." Cost: a whole new backend (verifiable IL via `System.Reflection.Metadata`/
`Reflection.Emit`, the .NET type system, AOT-compat). **Is it needed?** Not strictly —
the C#-source backend + the dispatch-loop trick works, and `llvm-cbe → C → C frontend`
sidesteps a new frontend *and* a new backend. So it's a fit/optimization decision, not
a hard dependency: worth building only if the compiled-input route becomes a primary
direction (then it removes the relooper burden and is the clean target). Sketch it as
its own backend alongside the C# and wat backends; the typed IR already separates
frontends from backends, so it slots in without disturbing the C path.

**Bonus — dotcc ↔ Wasm symmetry, and the bootstrap showcase.** dotcc already *emits*
wat (C → wat backend); a Wasm *frontend* would let it *consume* wasm too — so dotcc
spans both sides of the Wasm boundary. Two distinct payoffs, one solid and one flashy:

- **Solid: dotcc-in-the-browser / WASI.** dotcc is AOT-clean .NET, so .NET's own wasm
  tooling can compile **dotcc itself to `.wasm`** (NativeAOT-LLVM → wasm is the lean
  target; Mono-wasm works but bundles the whole runtime). That's a real, shippable
  deliverable: a C-to-C#/Wasm compiler running entirely client-side — a playground —
  with no server. This needs *no* Wasm frontend; it's just publishing dotcc to wasm.

- **Flashy: the round-trip "recompile ourselves."** `.NET dotcc → (.NET wasm tooling)
  → dotcc.wasm → (dotcc's own Wasm frontend) → .NET dotcc′`. If `dotcc′` behaves like
  the original, the Wasm frontend just chewed a huge real program with a **built-in
  oracle** (the original's behavior) — a compiler-bootstrap-style fixed-point test,
  and a hell of a demo (dotcc eating its own tail).

  Two honest caveats: **(1)** it is *not* classic self-hosting — a self-hosting
  compiler compiles its own *source* language; dotcc is written in C# but its *input*
  is C/Wasm, so it can never compile its own source (barring a C# frontend). This is a
  *round-trip self-consistency* check, a different (still valuable) thing. **(2)** the
  artifact is the hardest possible input — a runtime-bearing wasm — so it's a
  **stretch/showcase milestone**, not a first test. Validate the Wasm frontend on
  small, clean modules (Swift/Rust/C → wasm, WASI imports) *first*; the self-eating
  round-trip is the victory lap.

**Status:** idea — possibly the most strategic one here; would change the calculus on
every other entry (Swift especially). Cheapest probe: `llvm-cbe → C → dotcc` on one
small module, *or* a hand-written `.wasm` through a stub wat frontend — measure how
much instruction/import surface a real module actually exercises.

---

## 6. Mercury — Route B works, but the existing toolchain already wins

**Motivation / context.** Mercury (`../mercury`, where I contribute — two live local
branches: `dotnet10-csharp` and `agc-revival`) is a purely declarative,
strongly-typed, strongly-**moded** logic/functional language. The Melbourne Mercury
Compiler (`mmc`) is a mature production compiler with **multiple backends** — low-level
C (LLDS), **high-level C** (`hlc`, the MLDS→C path), **Java**, and **C#** (the
`csharp` grade). The two usual routes apply: parse Mercury directly, or consume `mmc`'s
high-level-C output. This is the entry where the honest rubric answer is *"don't"* —
and it's worth recording exactly why, because the reason is instructive.

### Route A — parse Mercury directly (`mercury.lalr.yaml`): a non-starter

Two walls, either of which is disqualifying:

- **Grammar (Tier 3).** Mercury inherits Prolog's reader: an **operator-precedence
  term parser driven by a runtime-mutable operator table** (`:- op(Prec, Type, Name)`
  declarations *change the grammar* as the file is read). The token stream and the
  base term grammar (functors, lists, terms) are LALR-able, but operator handling
  wants an operator-precedence sub-parser beside the LALR core, not a fixed table —
  it's not the clean fit a `RewritingTokenStream` gives.
- **Semantics — the hardest target of any idea here.** Type inference + **mode
  analysis** (instantiation states) + **determinism analysis**
  (`det`/`semidet`/`multi`/`nondet`/`cc_*`/`failure`/`erroneous`) + the **logic
  execution model** (unification, backtracking, nondeterminism via success/failure
  continuations). Reimplementing that *is* reimplementing the core of `mmc` —
  research-grade, far beyond the Python/Swift semantic poles. Toy-only.

### Route B — `mmc --grade hlc → .c → dotcc`: technically the strongest Route B here

`mercury.m → mmc --grade hlc.gc → C → dotcc C frontend → C#/.NET`. `mmc` does **all**
the hard lowering (modes, determinism, nondet→continuations) and emits structured
high-level C; dotcc just compiles the C. Because the semantic gap is the largest of
any candidate, "let the source compiler do it" pays off most here in principle. But:

- **You must shim the Mercury runtime + a GC.** The generated HLC links `runtime/`
  (~5.2 MB, 66 `.c` files) and, in `hlc.gc`, **Boehm GC** (`boehm_gc/`, ~6.9 MB).
  Compiling/shimming that through dotcc is the real cost — and **Boehm on .NET is a
  conservative-GC horror** (stack/heap scanning, signals); the honest fallback is a
  non-collecting `NativeMemory` arena, i.e. it leaks.
- **`hlc.agc` is the cleaner-fit grade, and I recently revived it (`agc-revival`).**
  Mercury's **accurate** GC (Cheney copy, forwarding pointers, type-info-guided) is a
  far better match for a managed target than conservative Boehm, and the `agc-revival`
  branch brings that accurate GC back and fixes the `hlc.agc` compile-to-C path. So
  *if* any HLC route is viable, it's `hlc.agc`, not `hlc.gc`. Still a heavy runtime to
  bring across.

### The decisive point: Mercury already runs on .NET, and I'm modernizing that path

`mmc`'s **`csharp` grade compiles Mercury straight to C#/.NET today** — purpose-built,
semantics-aware, maintained. My `dotnet10-csharp` branch is pushing it onto **.NET 10 /
C# 14**, emitting C# `record` DU types, and making it **NativeAOT-compatible**
(reflection-free RTTI via an `MR_DuTerm` interface, `--csharp-aot`). So
`mercury.m → mmc --grade csharp → .NET` is a *better* path to Mercury-on-.NET than
either dotcc route — it's the existing toolchain, and it's actively getting better.
This is the rubric's **"is the value unique vs. the language's own toolchain?"** axis
answering a resounding **no**.

### The one genuine dotcc niche: C `foreign_proc` unification via IR composition

Where dotcc *could* add something the `csharp` grade structurally **cannot**: Mercury
leans heavily on `pragma foreign_proc("C", …)` (runtime, stdlib, user code). The
`csharp` grade needs **C#** foreign_procs — C foreign code does *not* come along; it
must be rewritten. A dotcc HLC route, by contrast, runs the generated Mercury C **and**
its C foreign_procs through one frontend into **one .NET IR** — the same IR-composition
lever as `@cImport`. That's the unique, narrow value: *Mercury + its C foreign code,
together, on .NET*, which the native csharp grade can't deliver. Also marginal: a
single uniform NativeAOT artifact mixing Mercury + C + other dotcc frontends.

**Status:** evaluated, value largely **pre-empted** — Mercury's best .NET path is the
`csharp` grade I already maintain, not dotcc. The only distinct dotcc angle is
C-foreign_proc-in-one-IR (and exercising the `hlc.agc` output on .NET as research).
Recorded here as the deliberate *"existing toolchain wins"* case.

---

## Cross-cutting notes

- **IR composition is the recurring multiplier.** Python C-extensions, Zig
  `@cImport`, and Swift C-interop all reduce to "reuse the C frontend's IR." Invest
  there and several frontends get cheaper at once.
- **One allocator abstraction is a second multiplier** (see #2). Reframing C's
  `malloc` as the default instance of a shared `Allocator` (the abstraction Zig threads
  explicitly) folds the existing `-fsanitize=address` debug heap, a future arena, and
  Zig's allocators into one swappable vtable — a build-once that readies the Zig
  frontend *and* makes the C debug-heap swap principled + arena-extensible.
- **The native-interop trio** (`-shared` export, import mode, `dlfcn`) is the
  mechanism every "load/compile a native extension" story rides on.
- **Recent struct-layout fidelity work** (MSVC-faithful struct + bit-field packing,
  LP64) is a prerequisite for *any* ABI-compatibility goal — it's why the
  pretend-CPython full-API hazard is even discussable (and abi3 sidesteps it).
- **Decision pending (frontend axis):** is the goal to run *more languages on .NET*
  (favor idea 5's compiled-input route) or to have specific languages' *syntax*
  first-class in dotcc (favor source frontends 1/3a)? Not exclusive, but they sequence
  differently.
- **Decision pending (backend axis):** keep emitting **C# source** (readable,
  Roslyn-compiled — the right call for the C frontend) or add a **direct IL backend**
  (the natural target for compiled-IR input — see #5). The typed IR already separates
  the two, so this can be decided per-route rather than globally.
