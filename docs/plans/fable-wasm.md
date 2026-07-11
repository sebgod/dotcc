# The Wasm front-end — road to Embedded Swift (2026-07-07, Fable)

> Campaign plan, git-tracked sibling of `road-to-zig-std.md` (independent arcs — no
> shared blocker in either direction; both meet at the typed IR). This graduates
> **FRONTEND-IDEAS.md #3b** (Swift via compiled output) and **#5** (compiled-input
> frontends, the meta-strategy) from idea to plan. Snapshot of main at `6b504e3`.

**Goal.** A third `IFrontend`: consume compiled **WebAssembly** (`.wasm` binaries)
and lower it to the shared typed IR → C# (.NET 10, AOT-clean). The **end-goal is
consuming Embedded Swift**: drawboard-core's freestanding `Geometry`/`CoreToolbox`
targets, compiled by the *real* `swiftc` to wasm, transpiled by dotcc into
first-class .NET methods callable from C#. The frontend itself is language-agnostic
— anything that emits wasm (Rust `no_std`, Zig, TinyGo, clang `--target=wasm32`,
and **dotcc's own wat backend**) comes along for free. One frontend, many languages.

## Why wasm is the Swift seam (not a `swift.lalr.yaml`)

The full argument lives in FRONTEND-IDEAS.md #3; the load-bearing points:

1. **Swift source is Tier 3** on the LALR rubric (contextual disambiguation,
   trailing closures, custom operators — wants backtracking LALR.CC doesn't give),
   and even the Embedded subset keeps generics, closures, payload enums, and a
   stdlib to shim. Route A reimplements Swift; Route B just executes it.
2. **ARC dissolves before the wasm exists.** swiftc runs ARC insertion + elision in
   SIL, then IRGen emits plain `swift_retain`/`swift_release` calls and wires
   `deinit` onto the release-to-zero path. By the time dotcc sees the module there
   is *no ownership semantic left to model* — a refcount is a memory word, retain
   is a function, deterministic `deinit` timing is preserved for free. The entire
   `using`-vs-refcount-vs-finalizer decision tree of a source frontend never arises.
3. **Linear memory sidesteps ARC-vs-GC.** Swift objects live in the module's linear
   memory — a `NativeMemory` arena *outside* the .NET GC. They never become GC
   objects; they stay refcounted blobs exactly as native.
4. **Embedded Swift specifically** monomorphizes generics, bans existentials and
   reflection metadata, and links a tiny runtime — precisely the subset that leaves
   a small, self-contained wasm module. And drawboard-core's `Geometry` is already
   embedded-clean *and* value-typed (structs, no classes), so the first real target
   doesn't even exercise the refcounting half.

Why not just embed a wasm interpreter/JIT (wasmtime-dotnet, Mono-wasm)? Because the
point is the dotcc model: a single AOT-clean emitted program, no runtime dependency,
wasm functions as ordinary `static` methods that NativeAOT compiles to machine code
— and IR-level composability with the C/Zig frontends (a C `main` can call a
Swift-compiled export once both are in one IR module).

## Decisions (settled up front)

- **D1 — Binary `.wasm` is the canonical input; no wat-text grammar.** Real
  toolchains emit binary; the binary format is *simpler* than the text format (no
  folded-expression sugar, no abbreviations, no identifiers — just LEB128 +
  sections). A hand-written reader is ~1–2k lines, zero deps, fully unit-testable
  from byte arrays. This is the blessed path: *"LALR(1) is an implementation detail
  of how the C frontend happens to be built, not an architectural commitment"*
  (FRONTEND-IDEAS) — the `IFrontend` contract is "produce typed IR", and a binary
  reader satisfies it without a parser generator. wabt (`wat2wasm`) appears **only
  in opt-in oracles** (it's already a dependency of the existing wat-oracle leg),
  never on the always-on path. Committed fixture `.wasm` blobs (small, stable) are
  the always-on corpus — the "committed sidecar IS the cached reference" pattern.
- **D2 — Emit C# source via the existing backend; no IL backend this campaign.**
  FRONTEND-IDEAS #5 already argued IL-emit is a fit/optimization decision, not a
  dependency. The C# backend + `goto` handles everything wasm needs (D3). Revisit
  only if output quality/perf demands it — as its own campaign.
- **D3 — No relooper needed, ever.** Wasm control flow is *already structured*
  (`block`/`loop`/`if` nest; `br` is a multi-level break/continue, `br_table` a
  switched one). Lift `block`/`loop`/`if` to IR structured statements and lower
  every `br` to `goto` (forward: label after the block; to a `loop`: label at the
  loop head). The C# backend + `GotoScopeNormalizer` already handle labels/goto.
  Pleasing symmetry: the wat *backend* lowers C's `goto` via a `br_table` dispatch
  loop (`project_wat_goto_dispatch_loop`); the wasm *frontend* lowers `br_table`
  via `goto` — the two backends are exact inverses across the seam.
- **D4 — Destackifier: conservative first, pretty later.** Wasm is a stack machine;
  the IR wants expression trees. V1 lifts in three-address style: every instruction
  result lands in a fresh local, evaluation order trivially preserved (wasm effects
  are strictly ordered). A folding pass that re-inlines single-use, effect-safe
  temps into expression trees comes after correctness (the Zig ANF-hoist logic is
  the in-house precedent for reasoning about hoist/purity). Readable output is
  explicitly NOT a goal for compiled input (FRONTEND-IDEAS #5) — correct output is.
- **D5 — Linear memory = one `NativeMemory` arena outside the GC, bounds-checked.**
  Loads/stores lower to pointer derefs at `mem + addr + offset`. Out-of-bounds must
  **trap catchably** (an AV would kill the process — .NET can't catch it), so v1
  emits an explicit bounds check per access via runtime helpers; a "trusted module"
  flag to elide them is a later optimization, never the default. Unaligned access
  must work (wasm semantics): use unaligned read/write helpers, don't trust the
  `align` hint. `memory.grow` reallocs the arena (or reserves max up front when the
  memory declares a max — cheaper and simpler; decide in WF3).
- **D6 — Traps → a `WasmTrap` exception family in a `WasmRt` runtime block**
  (`DotCC.Libc/WasmRt.cs`, the embedded-resource single-source pattern, spliced
  only when a wasm input is present). `unreachable`, OOB access, bad
  `call_indirect`, trapping float→int conversions all throw it. Where C# already
  traps compatibly (int div-by-zero → `DivideByZeroException`, `INT_MIN/-1` →
  `OverflowException`) map/wrap as needed — but note the gotcha: wasm
  `i32.rem_s(INT_MIN, -1)` is **0, not a trap**, while C# `%` throws → `rem_s`
  needs a helper.
- **D7 — Process model: one static class per module, single instance.** Exports →
  `public static` methods (names via the export name, else the `name` custom
  section through `INameLegalizer`, else `f<idx>`); internal functions private;
  globals → static fields; memory → a static arena initialized (data segments
  copied in) by the module initializer; `start` function runs there too. Multiple
  instantiations of one module = a named loud cut.
- **D8 — Scope = MVP + the LLVM-default post-MVP features**, everything else
  rejects loudly (fail loudly, grow on purpose): **sign-extension ops**,
  **nontrapping float→int** (`trunc_sat`), **bulk memory** (`memory.copy/fill`,
  passive data segments — LLVM emits these for memcpy/memset, non-negotiable),
  **mutable globals**. `multivalue` and `reference-types`-encoded tables: decode,
  but reject beyond what real producers force (WF0 *measures* whether swiftc emits
  them — the S0 lesson: inventory before building). Out of scope v1: GC proposal,
  v128 SIMD, threads/atomics, exceptions proposal, tail calls, multi-memory,
  memory64, component model.

## The instruction set maps almost entirely onto the BCL

The finite, enumerable surface (~180 core opcodes) is the whole point — contrast a
language grammar. The mapping table (representative rows; the WF2/WF3 pins encode
it exhaustively):

| wasm | C# lowering | note |
|---|---|---|
| `i32`/`i64`/`f32`/`f64` | `int`/`long`/`float`/`double` | locals, params, results |
| `add`/`sub`/`mul` | `+ - *` unchecked | C# default wraps — matches |
| `div_s`/`div_u`/`rem_u` | `/` on int/uint | native exceptions ≈ traps |
| `rem_s` | helper | `INT_MIN % -1` = 0 in wasm, throws in C# |
| `shl`/`shr_s`/`shr_u` | `<< >> >>>` | C# masks shift counts — matches wasm |
| `rotl`/`rotr`, `clz`/`ctz`/`popcnt` | `BitOperations.*` | AOT-clean intrinsics; `Lzcnt(0)`=32 matches |
| `eqz`/`eq`/`lt_s`/… | `(a OP b ? 1 : 0)` | wasm comparisons produce i32 |
| `f64.sqrt`/`abs`/`ceil`/`floor`/`trunc`/`nearest` | `Math.Sqrt/Abs/Ceiling/Floor/Truncate/Round(ToEven)` | |
| `min`/`max`/`copysign` | `Math.Min/Max/CopySign` | modern .NET is IEEE-correct (NaN, ±0) — audit pin it |
| `neg` | unary `-` | IEEE sign-flip incl. NaN — matches |
| `wrap`/`extend`/`convert` | casts | `extend8_s` → `(sbyte)` etc. |
| `trunc_f*` (trapping) | `WasmRt.TruncChecked*` | C# float→int overflow is unspecified — helper required |
| `trunc_sat_f*` | `WasmRt.TruncSat*` | saturating helpers |
| `reinterpret` | `BitConverter.*BitsTo*` / `Unsafe.BitCast` | |
| `load`/`store` (+offset) | bounds-check + unaligned deref helper | D5 |
| `memory.size`/`grow`/`copy`/`fill` | arena helpers | D5/D8 |
| `block`/`loop`/`if` | structured stmts + labels | D3 |
| `br`/`br_if`/`br_table` | `goto` / `if→goto` / `switch`-of-`goto` | D3 |
| `call` | direct static call | |
| `call_indirect` | fn-ptr table: `delegate*<…>` array + type-id check | trap on mismatch; C fn-ptr machinery exists |
| `select` | ternary | non-short-circuit: both already evaluated (stack) |
| `unreachable` | `WasmRt.Trap()` | |
| data segments | RVA blob (`L(...)`-style) copied into the arena at init | zero-copy RVA machinery exists |

## The host seam — import tiers

Imports are the only place wasm touches the outside world; each tier is a milestone
boundary, not a monolith:

- **T0 — no imports.** Pure compute. Embedded Swift `Geometry` functions and half
  the wat-oracle corpus live here. Works from WF2.
- **T1 — dotcc's own output.** Exactly one import: `wasi_snapshot_preview1.fd_write`
  (that's all `WatBackend` ever emits) → route to the Libc write path. Unlocks the
  *full* self-round-trip corpus (WF4).
- **T2 — WASI-lite.** The preview1 subset real freestanding modules touch:
  `fd_write`, `proc_exit`, `random_get`, `clock_time_get`, args/env stubs. WASI is
  libc-shaped and `DotCC.Libc` already is a libc — each function routes to the
  obvious existing primitive. Grown demand-driven, loud on the rest.
- **T3 — the Embedded Swift runtime floor.** ✅ **WF0 measured it: bundled.** The
  Geometry-shaped module carries 62 in-module functions and *zero* non-WASI imports —
  the allocator + refcount + math runtime is ordinary in-module code (tier T0/WF3
  memory ops), not a host ABI. T3 is effectively empty for value-typed Swift; the
  only floor is the T2 WASI reactor set (`args_*`, `fd_*`, `proc_exit`,
  `random_get`). Re-measure if a class-ful / `-no-static-stdlib` build ever imports a
  runtime symbol; shim that short list then.

Unknown imports: reject loudly, named, at lift time — never a linker-style silent
stub.

## The oracle story (the crown jewel)

The wasm frontend gets a **built-in differential oracle no other frontend had on
day one**: dotcc already *emits* wat, and the wat oracle already assembles it.

1. **Self-round-trip (WF2+, opt-in like all oracles).** For each program in the
   existing `WatOracleTests` corpus: `C → EmitWat → wat2wasm → WasmFrontend → C# →
   run` and compare against the direct `C → C# → run` result (and the pinned
   expected value). Every instruction the wat backend can emit gets its lift
   validated mechanically, with zero new fixtures. Runs where the wat oracle runs
   (CI ubuntu x64 has wabt+node; win-arm64 skips — same posture as today).
2. **Committed fixture blobs (always-on).** Tiny `.wasm` binaries checked in
   (hand-assembled or toolchain-built once) + expected outputs → the reader and
   lifter get unit/functional coverage with **no toolchain on the host**. The
   reader's unit pins assemble modules from raw bytes via a small test-side
   `WasmBytes` builder (LEB128 etc.) — wabt-free.
3. **Foreign-module differential (WF6+, opt-in).** Run the same `.wasm` under
   **wasmtime** (ships win-arm64 builds) and under dotcc's transpile; compare
   stdout/results — the zig-oracle pattern, `DOTCC_RUN_WASM_ORACLE=1`. This is the
   oracle for Swift/Rust/clang-produced modules where no C source exists.
4. **The victory lap (stretch, WF8):** dotcc itself → .NET wasm tooling →
   `dotcc.wasm` → dotcc's wasm frontend → run. Round-trip self-consistency, per
   FRONTEND-IDEAS #5 — a showcase, not a gate.

## Milestones

Sizes: S < M < L. Every milestone lands with always-on unit pins + its opt-in
oracle leg, per the house discipline. Loud cuts throughout.

### WF0 — the surface probe (S/M) — *measure before building* — ✅ DONE (2026-07-09)

The S0 wall-finder lesson, applied: **inventory real modules before writing the
lifter.** A `WasmModuleProbe` (`DotCC.Lib/Wasm/`, reader skeleton: section decode +
opcode/import histogram + structural post-MVP feature detection — this *is* WF1's
first increment, fail-soft like `ZigParseProbe`) run over:
  (a) dotcc's own emitted wat corpus — all 146 inline programs from `WatOracleTests`,
      harvested off the `[InlineData]` metadata (zero duplication) and assembled with
      wat2wasm;
  (b) one Embedded Swift module (`@_expose(wasm)`, `-target wasm32-unknown-wasip1
      -enable-experimental-feature Embedded -wmo -static-stdlib`) — **built on
      win-arm64 with the host swiftc 6.3.1 + the swift.org wasm SDK bundle** (the
      "biggest logistics unknown" — resolved; exact runbook in
      `DotCC.FunctionalTests/WasmProbeModules/README.md`);
  (c) one clang `--target=wasm32` C file and one zig `wasm32-freestanding` module.
Deliverables (all landed): `WasmSurfaceProbeTests` (opt-in, `DOTCC_RUN_WASM_PROBE=1`),
committed producer blobs + sources under `WasmProbeModules/`, and the committed
timestamped report `docs/plans/wasm-surface-probe.report.txt`.

**Findings (the measurement that corrects the feature lists below):**
- **The Embedded Swift runtime is BUNDLED, not imported.** The Geometry-shaped
  module has **62 functions in-module and zero non-WASI imports** — allocator +
  refcount + math are ordinary in-module code (tier T0/WF3 memory ops), *not* a host
  ABI to shim. **This collapses tier T3 to near-empty for value-typed Swift** — the
  campaign's biggest de-risking. The WASI imports it does carry (`args_get`,
  `args_sizes_get`, `fd_close`, `fd_fdstat_get`, `fd_seek`, `fd_write`, `proc_exit`,
  `random_get`) are the `_start` reactor floor = tier T2, and libc-shaped.
- **`multivalue` is declared but NOT emitted.** swiftc's `target_features` custom
  section advertises `+multivalue`, but the *actual encoding* uses no multi-result
  type or block-type-index — the structural detector never fires. Lesson banked:
  `target_features` is a capability manifest, not a usage record; trust the
  histogram. **WF2 can safely defer multivalue** (D8's gate holds).
- **`bulk-memory` and `reference-types` are non-negotiable for Swift.** The module
  emits `memory.copy`/`memory.fill` (bulk) and a funcref table + `call_indirect` (9
  sites) + an elem segment even for this trivial value math. So **WF5's
  `call_indirect` is needed to run *any* real Swift**, not just fancy dispatch —
  reorder expectations accordingly. clang's module is pure-MVP; zig's `-dynamic`
  build is a PIC/shared shape (imported memory + `__indirect_function_table`).
- **Export names are usable verbatim.** `@_expose(wasm, "square")` surfaces exactly
  `square` — no demangling needed for the public API surface (WF6's name-section
  work is a nicety for *internal* Swift-mangled names, not a blocker).
- **The self-round-trip surface is now exact.** All 146 wat-corpus modules probe
  clean; the histogram *is* the WF2/WF3 opcode worklist (top: `local.get`,
  `i32.const`, `i32.add`, `call`, `if`/`block`/`loop`/`br`/`br_if`, the load/store
  family, `i32.trunc_sat_f64_s`, `i32.extend8_s`, `select`, `br_table`,
  `memory.size`/`grow`). Only import across the whole corpus:
  `wasi_snapshot_preview1.fd_write` (confirms the T1 tier is exactly one function).
- **Loud cut:** no Rust module (no `rustc` on this machine) — the probe auto-includes
  any `*.wasm` dropped in `WasmProbeModules/`, so a Rust `no_std` fixture slots in
  when a toolchain is around.

**WF0 now has a public face + a live consumer (2026-07-11).** `Compiler.ProbeWasm(byte[])
→ string` exposes the probe (read-only, fail-soft) beyond the opt-in test, and the web
sandbox's `wasm` tab (`fable-web.md` WEB6) calls it on the binary it assembles in-browser
— dotcc reading back the wasm dotcc itself produced. This is the "Tier 1" wasm-in-sandbox
increment: it needs no external toolchain and does **not** lift (that is WF1/WF2 below).
Tier 2 (probe a pre-built Embedded Swift `.wasm` asset in the sandbox) and Tier 3 (lift
dotcc's own wasm — the WF2 heart / WF8 self-eating round-trip) remain.

### WF1 — the binary reader (M)

Full MVP+D8 decode into an in-memory `WasmModule` model: type/import/function/
table/memory/global/export/start/element/code/data sections + the `name` custom
section; skip-with-note on unknown custom sections. LEB128 (+ the signed variants),
validation errors loud and positioned (byte offset). No lifting, no execution.
Lives in `DotCC.Lib/Wasm/` (reader + model), AOT-clean, zero deps, no
`Process.Start`. Unit pins via the `WasmBytes` test builder; a malformed-module
rejection suite (truncated section, bad LEB, type index OOB).

### WF2 — pure-compute lifting: the `WasmFrontend` (L — the heart)

`Frontends/WasmFrontend.cs` implementing `IFrontend` (dispatch on `.wasm` in
`Compiler`): numeric locals/params, the full arithmetic/comparison/conversion
table, structured control flow + `br*` → goto (D3), direct `call`, the
three-address destackifier (D4), `main`/exports → static methods (D7). No memory
yet — T0 modules only. Validation: the arithmetic/control-flow half of the
wat-oracle corpus round-trips (oracle #1); emit pins for each opcode family;
fixture blobs for always-on. Cuts: everything memory-, table-, or import-shaped
rejects loudly by name.

### WF3 — linear memory + data segments (M)

The arena (D5): load/store family with offset + sign/zero-extending narrow
variants, `memory.size/grow`, bounds-check helpers, active data segments → RVA
blobs copied at init, bulk-memory `memory.copy/fill` + passive segments +
`data.drop`. Unlocks the string/pointer/heap half of the wat-oracle corpus (dotcc's
own shadow-stack + bump-allocator output — a great stress test since it does real
pointer arithmetic over the arena).

### WF4 — the host seam v1: T1 imports + exports as API (M)

Import binding machinery (loud on unknown); `fd_write` → Libc write path (T1) —
the **full** wat-oracle corpus now round-trips, including every printf test.
Exports become the module's public C# API surface: numeric params/results direct;
pointer-shaped params documented as arena offsets with a public
`Memory`/read-write helper surface (a hand-written friendly wrapper per use is the
v1 answer; auto-marshaling is a cut). Traps → `WasmTrap` (D6) with a
functional-test pin that a trap is catchable from C#.

### WF5 — indirection: tables, `call_indirect`, globals (S/M)

Funcref tables + element segments → `delegate*<…>` arrays (static methods, AOT-
clean) with per-slot type-id check → trap on mismatch; mutable/immutable globals →
static fields; `global.get/set`. Validation: the function-pointer subset of the
wat corpus + a hand-built indirect-dispatch fixture.

### WF6 — the Swift on-ramp (M, empirical)

Turn WF0(b) into a paved road: a runbook (`examples/swift-wasm/README.md`) for
compiling Embedded Swift → wasm on this machine (or WSL fallback), 3–5 committed
fixture modules covering: pure value math (Geometry-shaped), a struct-in-linear-
memory function, a generic instantiated (monomorphized) function, stack-heavy
recursion. Each with a wasmtime-differential sidecar (oracle #3,
`DOTCC_RUN_WASM_ORACLE=1`, optional CI leg with the swift-wasm SDK on ubuntu).
Name-section → legalized method names (Swift mangling survives `INameLegalizer`;
demangling is a nicety, not a blocker).

### WF7 — the Embedded Swift runtime floor (M/L, shaped by WF0/WF6 findings)

Whatever the probes showed: bundled runtime (likely) means allocations/refcounts
are just WF3 memory ops — then this milestone is only about *validating* class-ful
Swift (a class with `deinit`, shared references) behaves identically under
wasmtime and dotcc. Imported runtime means shimming the short list (T3). Add
`multivalue` here if (and only if) swiftc's output forced it in WF0. Panics/
`fatalError` → whatever swiftc emits (`unreachable` or an import) → `WasmTrap`.

### WF8 — victory laps (stretch, unordered)

- **The drawboard demo:** a real `Geometry` routine (transform/bezier math) from
  drawboard-core, swiftc → wasm → dotcc → NativeAOT .NET, called from a C# test,
  results bit-compared against the Swift-native run.
- **Mixed-IR composition:** a `.c` + `.wasm` input set in one invocation — C
  declares `extern` what the wasm module exports, both lower into one IR module,
  one emitted program. (The IR-composition multiplier, FRONTEND-IDEAS.)
- **Free riders:** Rust/TinyGo/Zig-built fixture modules as oracle entries.
- **The self-eating round-trip** (dotcc.wasm through dotcc) — showcase only.

## Risks & empirical unknowns

- **Swift-wasm toolchain on win-arm64** — the biggest logistics unknown, isolated
  into WF0/WF6 deliberately. Mitigation: fixtures are committed blobs; the
  toolchain is only needed to *regenerate* them (the sidecar-is-the-reference
  pattern), and WSL/CI can be the build host.
- **What swiftc actually emits** (multivalue? reference-types tables? bundled vs
  imported runtime?) — unknowable from the armchair; WF0 exists to replace this
  paragraph with a table. Plan feature lists are provisional until then.
- **Destackifier subtleties** — block result values at `br`/`end` edges, unwinding
  the value stack at branches. Mitigated by three-address-first (D4) and by the
  self-round-trip corpus, which exercises exactly the shapes our own backend
  produces before foreign modules raise the bar.
- **Float fidelity** — `nearest`/min/max/NaN-canonicalization corner cases. One
  audit pin per float op against spec vectors; the wasmtime differential catches
  drift on real modules.
- **Bounds-check cost** — accepted for v1 (correctness + catchable traps first);
  an elision pass (dominating checks, constant addresses) is a named later.
- **Output size** — three-address style makes big C# files; Roslyn copes (chibi
  proved multi-MB emits fine). The folding pass is quality-of-life, not a gate.

## What this campaign is NOT

Not a wat *text* parser (D1). Not an IL backend (D2). Not a general Swift
frontend — Swift *syntax* in dotcc remains FRONTEND-IDEAS #3a, unplanned. Not a
wasm *runtime/sandbox product* — no fuel metering, no epoch interruption, no
capability sandboxing beyond the arena's natural isolation; dotcc transpiles
trusted modules, it doesn't host adversarial ones.

## Progress-bar convention

Like the zig-std campaign: WF0's committed probe report is the baseline; each
milestone regenerates it (opcode-coverage % + wat-corpus round-trip % + per-module
pass list) and re-commits — `git diff` on the report is the progress log. The
instruction set being *finite* makes this progress bar exact in a way a grammar
never was.
