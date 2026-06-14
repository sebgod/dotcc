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

Dropped for now (see memory `project_zig_frontend_plan.md`). Key recorded decision:
`@cImport` should be **IR-level composition** with the existing C frontend, *not* a
translate-c text pass — the C frontend lowers the imported headers to IR and the Zig
frontend composes against that. This is the canonical example of the IR-composition
multiplier and informs every other "language that needs to talk to C" frontend
(Python extensions above, Swift C-interop below).

**Status:** parked; plan preserved.

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

## Cross-cutting notes

- **IR composition is the recurring multiplier.** Python C-extensions, Zig
  `@cImport`, and Swift C-interop all reduce to "reuse the C frontend's IR." Invest
  there and several frontends get cheaper at once.
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
