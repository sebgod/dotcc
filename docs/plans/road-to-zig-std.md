# Road to the Zig std lib (2026-07-07, Fable)

> Sibling to `fable-wall.md` (whose entire arc W0–W6 is ✅ complete and merged).
> Snapshot of main at `9dbb2f2`. This plan covers what fable-wall.md deliberately
> fenced off as "non-arc work": making **real upstream zig std source compilable
> by dotcc**, replacing curation with compilation wherever the three-tier model
> says that's possible.
>
> **The three honest tiers** (ZIG-SUPPORT.md § "Why these are out"):
> (a) **leaf std** — ordinary Zig, leanable from source once the front-end fills in;
> (b) **std's comptime-reflection codegen** — the one unbuilt-but-buildable brick;
> (c) **std's platform floor** — syscalls + inline asm, permanently BCL-redirected.
> This plan is the build-out of (a) via (b), with (c) formalized as a redirect
> table instead of hand-waved.

## Ground truth — measured against the pinned std source (not guessed)

The local zig `0.17.0-dev.667+0569f1f6a` install ships the full std source at
`<zig>/lib/std` (`zig env` → `std_dir`). Measured 2026-07-07:

| Fact | Number | Consequence |
|---|---|---|
| std size | **553 files, 433k LOC** | eager lowering of the import closure is a non-starter → lazy decl-driven lowering is a hard precondition (B0) |
| `usingnamespace` | **0 uses** | **removed upstream — NOT a blocker.** (Corrects the stale claim in earlier discussion; do not build it.) |
| `@cImport` | 0 uses | confirmed dead (matches the Zig-frontend plan: C interop = `extern fn` + `-lc`) |
| `@typeInfo` | 758 uses | the reflection engine (B2) is unavoidable for tier (a)'s generic core |
| `inline for` / `inline while` | 494 / 121 | unrolling over comptime **aggregates** (fields, slices) is the companion brick — dotcc today unrolls counted ranges only |
| `@field(…)` / `@hasDecl` | 220 / 73 | comptime-name field access + decl probing needed |
| `@compileError` | 596 uses | must fire **only in taken comptime branches** (post-folding), else all of std "fails" |
| `@setEvalBranchQuota` | 82 uses | maps 1:1 onto the ComptimeInterpreter's existing step budget — cheap |
| `@Type(info)` reification | **0 uses — the builtin is GONE in this pin** | replaced by **kind-specific builtins**: `@Int(signedness, bits)` ×207, `@Pointer` ×14, `@Struct` ×5, `@Enum` ×4, `@Union` ×3. Each maps 1:1 onto a `CType` constructor — dramatically easier than modeling the old monolithic `@Type(std.builtin.Type)`. |
| `std.builtin.Type` | moved to `std/lang.zig` (`pub const builtin = lang;` re-export); tags are lowercase **quoted identifiers** (`.@"struct"`, `.@"enum"`, `.@"union"`, `.@"fn"`, `.@"opaque"`) | `@"…"` quoted-identifier syntax is REQUIRED grammar surface (dotcc lacks it) |
| `@import("builtin")` | load-bearing inside `std.zig` itself (`mode`, `strip_debug_info`, `zig_backend`, `object_format`, …) | a synthetic per-target `builtin` module is non-optional (S3) |
| `@import("root")` | used by `std.zig` (the `std_options` override pattern, via `@hasDecl`) | the root module must be addressable |
| `test "…" {}` blocks | **1857** | must parse-and-DROP or most std files won't even parse — tiny brick, giant coverage lever |
| `packed struct` | 556 uses | sub-byte bit-packing (dotcc V1 byte-packs) becomes load-bearing |
| `@Vector` | 447 uses | do NOT build SIMD — bias std away via target config + scalarize the remainder (see S9) |
| inline `asm` | 463 uses | all inside the platform floor / cpu feature probes → tier (c), redirected not lowered |
| atomics (`@atomic*` 280, `@cmpxchg*` 54) | | `Interlocked`/`Volatile` mapping needed for `std.Thread`/`std.atomic` (tier (c) edges) |
| `threadlocal` | 80 uses | C `_Thread_local` → `[ThreadStatic]` precedent exists; port to the Zig side |
| `@fieldParentPtr` / `@addWithOverflow`-family | 156 / 55+ | layout-offset math + overflow-tuple builtins (tuples already exist) |
| `u21` (Unicode codepoints) | 58 uses | arbitrary-width ints can't stay out; `std.unicode` is unusable without them |
| `extern fn` decls | ~818 | libc-shaped: with `link_libc = true`, std itself routes to libc calls **dotcc's runtime already implements** — the single biggest redirect lever (S8) |

### What dotcc's Zig front-end already has (the wall dividend)

- The full monomorphization spine (W3): retained-AST templates, demand
  instantiation, memoization keyed by resolved value/type, mangling, a deferred
  worklist with re-entrancy discipline, shadow-saved comptime seeds. **This is
  the same machinery B0 generalizes.**
- Lowering-time type env (W1 `_typeAliases`), on-the-fly container registration
  (W2), type-returning fns (W4), `anytype` inference (W5).
- ComptimeInterpreter (Milestone T, shared with C): call frames, loops, step
  budget — **value domain is scalar-only today**; no `TypeVal`, no aggregate values.
- `inline for` over a counted range (unroll); `comptime var/const`; comptime-if
  folding inside generic instances.
- Builtins present: `@TypeOf`, `@This`, `@as`, `@sizeOf`, `@alignOf`, `@intCast`,
  `@truncate`, `@ptrCast`, `@bitCast`, `@alignCast`, `@enumFromInt`,
  `@intFromEnum`, `@errorName`, `@memcpy`, `@memset`, `@import` (std-only stub).
- Types: power-of-2 ints i8…u128, usize/isize, f32/f64, c_* ABI types, slices,
  optionals, error unions, allocators, tuples, `[*c]T`, `[N:s]T` global sentinel
  arrays, fn-ptr types.
- `@import("<sibling>.zig")` file resolution exists at the oracle/input level,
  but `@import` **lowering** hard-rejects any module string except `"std"`
  (`ZigLowering.Types.cs:37`), and `"std"` is only a namespace tag consumed by
  the curated-path resolver (`TryResolveStdPath`) — there is no module system.

## The load-bearing blockers, ranked

**B0 — lazy, decl-driven lowering (architectural precondition; bigger than any
single feature).** dotcc lowers eagerly: pass 0 comptime consts → pass 1
signatures → pass 2 bodies, over ALL decls. 433k LOC of std makes that a
non-starter — and unnecessary, since real zig also analyzes only *referenced*
decls. The W3 worklist is the proven template: every top-level decl of an
imported module becomes a retained AST, instantiated on first reference,
memoized by `(module, name)`. Laziness also *defines away* most of tier (c):
an un-referenced `std.fs` decl that dotcc could never lower simply never lowers
— exactly upstream semantics.

**B1 — a real module graph.** `@import("std")` must resolve to the real std
root; `@import` must return a **namespace value** whose field accesses resolve
decls in that module's scope; per-module symbol scoping (today all maps are
compilation-flat — the deferred flat-maps→frames refactor becomes load-bearing
here, for real this time); the special modules `builtin` (compiler-generated)
and `root` (the root file) must exist.

**B2 — the comptime-reflection engine.** Interpreter `TypeVal` + aggregate
values, `@typeInfo` synthesized from `CType` (never by lowering `std/lang.zig`'s
`Type` union from source — it's compiler-known, as in real zig), `inline for`
over comptime aggregates, `@field` with a comptime name, the kind-specific
reification builtins (`@Int` first — 207 uses), `@compileError` that fires only
when reached. This is what `std.fmt` / `std.meta` / `std.hash_map` are built on.

**B3 — the language-surface long tail.** Quoted identifiers, test-block
dropping, arbitrary-width ints, sub-byte packed structs, overflow/saturating
operators, atomics, `@fieldParentPtr`… Individually small; collectively the
bulk of the wall-clock. **Don't enumerate by guesswork — build the wall-finder
(S0) and let the pinned std source rank the list.**

**B4 — the platform floor as a redirect table (tier (c) formalized).** Never
lower `std/posix.zig`, `std/os/*`, `std/Thread.zig`, `std/fs.zig`, … — the
module loader substitutes them. The cheapest substitution is usually a
dotcc-authored Zig file whose fns are `extern` onto the **libc-shaped runtime
dotcc already ships for C** — `std.posix` is libc-shaped by construction, and
`builtin.link_libc = true` biases std itself onto libc-backed code paths.

## Milestones

Each S-milestone is one lalr-feature-loop increment (own branch/PR, emit pins +
zig-oracle differential + runnable example). G-goals are integration proofs
that retire curated shortcuts.

> **Status update (2026-07-13) — S1 + S2 + G1 DONE (branch `feat/zig-module-graph`,
> depends on LALR.CC v4.6.0 / [SharpAstro/LALR.CC#6](https://github.com/SharpAstro/LALR.CC/pull/6)).**
> A **resilient (error-recovering) parse mode** was added to LALR.CC first
> (`ParseInputResilient` — list-boundary panic-mode recovery), so a file's decls parse
> independently: only the decls a program references need to parse. On top of it:
> **S1** the module graph (`ZigModuleGraph`/`ZigModule`; `@import` a namespace value —
> relative siblings + the real `std` root, imports recorded as deferred specs resolved on
> first navigation); **S2** lazy decl-driven lowering (a referenced function's signature
> lowers on demand + body enqueued for a top-level drain; an unreferenced unlowerable decl
> is invisible, a referenced one fails loudly); **G1** `@import("std")` navigates the real
> `std.zig` → `ascii.zig` and compiles `std.ascii`'s classifiers (isDigit/isUpper/isLower/
> toUpper/toLower/isWhitespace + `@intFromBool`) **from real upstream source, lazily**,
> oracle-verified == real zig (exit 63). Curated std.mem/debug/heap/testing fast-paths still
> win (checked first). The root unit stays eager. Std root via `DOTCC_ZIG_LIB_DIR`
> (a `--zig-lib-dir` flag is the remaining front-door polish). **Not yet done:** S3 synthetic
> `builtin`/`root` (recorded as deferred, never navigated by ascii); S4–S7 comptime engine;
> the heavier G-goals (G2 mem, G3 fmt, G4 ArrayList, G5 hashmap).

### S0 — the wall-finder + std pin (S; do FIRST, it steers everything)

An opt-in test/tool (`DOTCC_RUN_STD_PROBE=1`, env `DOTCC_ZIG_LIB_DIR` or
auto-discovered via `zig env`) that walks the pinned `lib/std`, attempts to
**parse** each file (later: lower each referenced decl), and emits a ranked
report: which construct fails first, in how many files. Deliverables:
- A re-runnable coverage metric ("N% of std files parse / lower") — the
  campaign's progress bar, chibi-style.
- A data-driven S9 worklist (replaces guesswork).
- Vendoring decision: do NOT embed std (9MB) as resources; point at the
  installed lib dir (clang's `-resource-dir` shape: a `--zig-lib-dir` flag,
  matching real zig's flag) + pin the version in CI the way the oracle already
  pins `0.17.0-dev.667`.

Hints: parse-only probing needs no process spawn (BytesLexer + parser
in-process); keep it off the default path like the oracles. Expect the first
report to be dominated by trivia (`test` blocks, quoted identifiers, missing
operators) — that's the point.

**Status: ✅ DONE (2026-07-07).** `DotCC.Lib/Frontends/ZigParseProbe.cs` is the
parse-only engine (lex → parse, no lowering; every failure classified, never
throws); `DotCC.FunctionalTests/StdParseProbeTests.cs` is the opt-in walker +
ranker (`DotCC.Tests/ZigParseProbeTests.cs` pins the classifier always-on, no
zig install needed). Re-run the progress bar with:

```bash
export PATH="$PATH:$HOME/AppData/Local/Programs/Zig"        # zig on PATH (win-arm64 dev box)
DOTCC_RUN_STD_PROBE=1 \
  DOTCC_ZIG_LIB_DIR="…/Zig/lib" \                           # or omit → auto via `zig env`
  DOTCC_STD_PROBE_OUT=/tmp/std-probe.txt \                  # optional; else OS temp
  dotnet test DotCC.FunctionalTests -c Release --filter FullyQualifiedName~StdParseProbeTests
```

**Baseline: 32 / 553 files parse-clean = 5.8%** (grammar `zig.lalr.yaml` at
`fa60b53`). The measured S9 ranking is folded into S9 below — it replaced the
guessed order. `--zig-lib-dir` CLI flag is deferred to S1 (nothing lowers std
yet, so there's no consumer); parse-only discovery via env / `zig env` suffices.
Not wired as a CI gate — a ratcheting coverage floor lands once coverage is
meaningful (post-S9-first-bricks).

**Progress log:** `docs/plans/std-parse-probe.report.txt` is a committed,
self-timestamped snapshot of the full ranked report (every bucket + its
`expected one of: …` context). **Regenerate and re-commit it after each brick
lands** — `git diff` on that file is the campaign's progress: the coverage line
climbs and buckets drop off. It's the only durable copy (std isn't vendored); the
`generated:` UTC line and `parse-clean:` line date each version.

### S1 — module graph + namespace values (L; co-design with S2)

- `ZigModule` = (canonical path, parse tree, **decl table**). The decl table is
  built WITHOUT lowering bodies (name → retained AST + kind), so import cycles
  (legal in zig, common in std) are fine.
- `@import("std")` → the std root module; `@import("./rel.zig")` → module
  relative to the importing file; `_imports[name]` generalizes from a string tag
  to a `ModuleRef`. Field access on a ModuleRef resolves in that module's decl
  table (transitively: `std.mem.eql` = module → decl `mem` (itself a
  `@import("mem.zig")` re-export) → decl `eql`).
- Per-module scoping: the function-flat maps (`_typeAliases`, `_imports`,
  `_errorSets`, `_containerTypes`, …) become per-module environments. This is
  the flat-maps→frames refactor fable-wall.md deferred twice; it is now a
  correctness requirement, not a cleanup (two std files both declaring
  `const testing = @import("testing.zig")` must not collide).
- IR naming: module-qualified mangling for emitted artifacts
  (`std__mem__eql__u8` shape; deterministic, collision-free by construction).
- The curated-path resolver stays: `TryResolveStdPath` becomes a **peephole in
  front of** real resolution — if the path matches a curated fast-path
  (`std.debug.print`, `ZigList<T>`), use it; else fall through to the module
  graph. Curation becomes an optimization, no longer a wall.

### S2 — lazy decl-driven lowering (L; the risk center, like W3 was)

- Generalize the W3 worklist to ALL imported-module decls: first reference to
  `std.mem.eql` enqueues (signature immediately, body on the worklist),
  memoized by `(module, decl, instantiation-key)`. A generic decl composes with
  the existing machinery unchanged — `std.ArrayList(i32)` is just a
  type-returning fn (W4) that happens to live in another module.
- The ROOT file stays eager (today's behavior: all root fns emit — no
  observable change for existing programs; also what `-shared` export semantics
  require).
- Diagnostics flip for imported modules only: an error in an unreferenced std
  decl is invisible — deliberately, matching upstream ("fail loudly" continues
  to apply to everything the program actually reaches).
- Re-entrancy: draining stays sequential at top level (the W3a conclusion);
  per-module environments push/pop around each drained decl the way
  `_typeAliasShadows` already does per instance.
- Compile-time budget: memoize aggressively; expect `std.fmt` closures of a few
  hundred decls, not thousands.

### S3 — synthetic `builtin` + `root` modules (S/M)

- Generate `builtin.zig` **as Zig source text** at compile start (exactly what
  real zig does per-target) and feed it through the normal S1 module path — no
  special lowering. Contents: `zig_version`, `mode`, `os`, `cpu`, `abi`,
  `object_format`, `single_threaded = false`, `strip_debug_info`,
  `link_libc = true`.
- **Target-config choices (load-bearing):**
  - `mode = .ReleaseFast` — dotcc does not trap overflow; ReleaseFast makes std
    skip safety-check codegen paths, honestly matching dotcc semantics. (The
    safe-mode flip stays a separate decision, per fable-wall.md.)
  - `os.tag` = the HOST os — so `std.fs.path.sep` etc. are right; the platform
    floor that `os.tag` would otherwise pull in is intercepted by S8 before
    those files load.
  - `link_libc = true` — the big lever: std biases toward libc-backed
    implementations, which land on `extern fn`s dotcc's C runtime already
    implements.
  - `cpu` features minimal — so `std.simd.suggestVectorLength(…)` returns
    null-ish/small and std takes its scalar fallback paths (the cheap answer to
    447 `@Vector` uses).
- `root` = the root compilation module (S1 gives this for free); `@hasDecl`
  probing of `root` (the `std_options` pattern) works once S5 lands `@hasDecl`.

### S4 — interpreter `TypeVal` + comptime type computation (M)

The brick W1 and W4 both explicitly deferred:
- Value domain += `TypeVal(CType, containerAst?)`.
- Type equality/comparison (`T == i32`), comptime `if`/`switch` over types in
  **multi-statement type-returning bodies** — W4's V1 cut ("single
  `return struct`") lifts: the body runs in the interpreter; `return struct
  {…}` reifies via the W2 primitive as before.
- `comptime` function calls that compute values used in types (`[log2(n)]u8`).
- Hint: W3b's "keyed by RESOLVED type" rule already defines TypeVal equality —
  reuse `MangleType` as the canonical key.

### S5 — `@typeInfo` + comptime aggregate values (L; the heart of B2)

- Interpreter aggregates: comptime struct values, comptime slices/arrays of
  values, comptime strings (`[]const u8`). (Scalar-only today — this sub-brick
  is most of the milestone.)
- `@typeInfo(T)` → a comptime VALUE of `std.lang.Type` shape, **synthesized
  directly from `CType`** — never by compiling `std/lang.zig` for the layout
  (compiler-known, kept in sync by hand; the union's own doc comment says
  exactly this about the real compiler). CType already carries field
  names/types via the layout registry.
- Union-with-payload values in the interpreter + `switch` over them with
  capture — the `switch (@typeInfo(T)) { .int => |info| … }` shape (758 uses).
- Grammar: **quoted identifiers `@"…"`** (needed for `.@"struct"` arms — a
  lexer rule + identifier-position acceptance, no parser surgery expected).
- `@hasDecl` / `@hasField` / `@typeName` — comptime bool/string from the same
  CType-derived info (typeName needs the pre-mangling Zig spelling; keep a
  reverse map).

**Pre-engine down-payment landed (2026-07-13, PR #95):** the LITERAL `++`/`**` case
already folds without the interpreter — string literals via the shared string
encode path, typed array literals (`[_]T{…}`) via the shared `BuildArrayInit`
(`LowerConcat`/`LowerRepeat` in `ZigLowering.Exprs.cs`). What remains a loud cut is
precisely a **non-literal** comptime operand — a comptime-const bound to a literal
(`const p = "x"; p ++ y`), an `@typeName(T)` result, a sink-needing anon `.{…}`.
That residue is *exactly* this S5 sub-brick: once a comptime `const` binds a comptime
VALUE (string/aggregate) and `@typeName` yields a comptime string, `LowerConcat`/
`LowerRepeat` fold them with no new operator logic. **Do NOT special-case const-string
propagation for `++`/`**` alone — it is the engine's core (`_comptimeValues`), built
once here, consumed by `++`/`**`, `@typeName`, comptime-`if`, and array-extent consts.**

### S6 — `inline for` over aggregates + `@field` (M)

- Extend the existing range-unroller: `inline for (info.@"struct".fields) |f|`
  unrolls over a comptime slice value, binding the capture to a comptime
  aggregate per iteration (each iteration lowers with the capture seeded, the
  W3a `_comptimeVars` shape).
- `inline while` (121 uses) rides the same machinery (condition folds per
  iteration; the comptime-var mutation loop already exists from Milestone T).
- `@field(x, "name")` with a comptime name → rewritten to an ordinary field
  access at lowering time (the AOT rule: no runtime reflection, ever).
- This + S5 is precisely what `std.fmt.format`'s per-field loops need.

### S7 — reification builtins + `@compileError` (M)

- `@Int(signedness, bits)` (207 uses) → `CType` integer constructor — after
  S9's arbitrary-width brick, arbitrary `bits` values work. `@Pointer`,
  `@Struct`, `@Enum`, `@Union` (≤14 uses each) follow the same 1:1 pattern,
  demand-driven. **The old monolithic `@Type(info)` does not exist in the pin —
  don't build it.**
- `@compileError(msg)`: an instantiation-trace-carrying diagnostic that fires
  ONLY when the branch survives comptime folding (the subtle bit — 596 uses sit
  in *not-taken* branches as guards; firing eagerly would "break" all of std).
  Hint: thread it as a poison value through the interpreter/folder, raised at
  materialization.
- `@setEvalBranchQuota(n)` → sets the interpreter step budget for the enclosing
  comptime evaluation (the mechanism exists; this is a setter).
- `@compileLog` → stderr warning channel.

### S8 — the platform-floor redirect table (M/L; incremental, parallel after S1)

- A module-path override table consulted at S1 resolution: `std/posix.zig`,
  `std/os/*`, `std/Thread.zig`, `std/fs.zig`, `std/heap/PageAllocator.zig`,
  `std/debug.zig` (the stack-trace half), `std/Io.zig` lower layers → dotcc-owned
  replacements. Two substitution mechanisms, chosen per module:
  1. **Zig-source substitution** (preferred): a dotcc-authored `posix.zig`
     whose fns are `extern fn` onto the libc-shaped runtime — `read`, `write`,
     `open`, `close`, `mmap`→`malloc`-backed, … dotcc's C runtime already
     implements these; the `-lc` extern seam is proven (Milestone V / the
     import-mode work).
  2. **Curated lowering** (the existing resolver) where no source-level
     expression exists.
- Bring-up order = demand order: whatever G-goals actually pull in. Expect
  `std.posix`/`std.Io` slices first (via `std.fmt`'s writer plumbing), then
  `std.heap`, then `std.Thread` (atomics from S9).
- The 818 `extern fn`s in std are the measure of how far `link_libc = true`
  alone carries: many floor paths bottom out in symbols the runtime has.

### S9 — surface-debt bricks (many S/M; parallel any time; wall-finder-ranked)

**Measured ranking (first S0 run, 2026-07-07 — 5.8% baseline).** The parse
wall-finder ranks the gaps by *files that fail on this construct first* (not
total uses; fixing the top of the list unblocks whole files). The head:

| Files | Construct (first-fail) | Brick |
|---|---|---|
| ~150 | **top-level container fields** — the file-is-a-struct idiom (`bytes: T,` / `graph: *Graph,` at file scope) | grammar: allow container-body decls at the top level |
| 51 | **`@"quoted"` identifiers** (`.@"io-uring"`) — lexer can't tokenize `@"` | lexer rule (also unblocks `std.builtin.Type` tags) |
| 35 | **trailing comma in fn params** (multi-line signatures) | grammar: optional trailing `,` in Params |
| 29 | **struct field default values** (`field: T = null,`) | grammar + lowering: field initializer |
| 28 | **`++` / `**` operators** (`lowercase ++ uppercase`) | lexer + lowering (comptime array/string concat/repeat) |
| 27 | **`test` blocks** with a bare-ident name (`test encode {`) | parse-and-DROP (see below) |
| 23 | **`callconv(...)`** on fn types (`fn (…) callconv(.c) void`) | parse-and-honor → native-callconv marker |
| 13 | **compound assign in while-continue** (`: (idx += 12)`) | grammar: allow `op=` in the continue clause |
| 12 | **anonymous named-field struct types** (`struct { a: u32, b: Fe }` return) | grammar: named fields in an inline struct type (today tuple-only) |
| 10 | **top-level `comptime {}` blocks** | parse-and-DROP (analysis-only in std) |
|  8 | **`packed struct(u16)`** explicit backing int | grammar + the packed-struct brick below |

Lower buckets (each 1–7 files): `align(N)`, `enum`/`union`/`packed`/`struct` in
value position, `..` ranges, `extern var`, `inline fn`, labeled-block `{…}` as
an expression, `=>` prong shapes. The full per-file report (with the
`expected one of: …` context) is what the probe writes to `DOTCC_STD_PROBE_OUT`
— regenerate it after each brick to watch the number climb.

Seed list (each its own loop increment; ranked by the table above, not guesswork):
- **`test "…" {}` + container-level `comptime {}`: parse and DROP** (1857
  blocks) — the single biggest parse-coverage lever, near-zero risk.
  **DONE + extended (2026-07-12):** test blocks parse, and a `test` **run** mode landed —
  `dotcc zig test <file>` lowers each `test "…" {}` to an `anyerror!void` function and a generated
  entry point runs them (OK/FAIL + summary, non-zero exit on failure), with curated
  `std.testing.expect`/`expectEqual`. This is the **harness the G-goals need** — running a real `std`
  slice's own tests from source and diffing against the `zig test` oracle. (Container-level
  `comptime {}` stays dropped until the comptime engine, S4–S7.)
- **Nested container decls as members + switch-prong value/return bodies**
  **(DONE 2026-07-12** — two conflict-free, parse-only grammar bricks. (1) A container
  body (`struct`/`enum`/`union`) now admits a nested `const Inner = struct/enum/union {…};`
  member (reusing the file-level `Decl → ContainerDecl` split), so a container can hold its
  own nested named-field types — the **top `:` parse bucket, 41→20 files**. (2) A switch-prong
  body is now symmetric — `Block | return [e] | RhsExpr`, with and without a `|x|`/`|*x|`
  capture — clearing the `return`-in-prong bucket (26 files) and its capture sibling (32
  files at the next barrier). Probe **28.9%→30.7%** (160→170 files).**)**
- **Inline named-field struct TYPES in annotation slots** **(DONE 2026-07-12** — closes the
  rest of the `:`-in-307 bucket. A new `AType` non-terminal (= `Type` + `struct { FieldDecls
  }`) is used in the field / param / typed-var-const / union-payload / fn-return slots — but
  NOT the value cascade (`CurlySuffix` keeps bare `Type`), so `fn f() struct { a: u8 }` and
  `field: struct { a: u8 }` parse while `const X = struct {…}` stays `structDecl` with no S/R
  conflict. `AType → Type` is transparent, so every existing annotation lowers unchanged; the
  inline form is parse-only (`LowerType` default = loud cut). Probe **30.7%→31.8%** (170→176);
  the `:`-in-307 bucket is **gone**. **Still deferred** (see [`deferred.md`](deferred.md)):
  inline named struct as a VALUE (`const X = if (c) struct {…} else …`, the CurlySuffix
  conflict) and under a recursive type prefix (`?struct{…}`, `[]struct{…}`).**)**
- **Quoted identifiers `@"…"`** (if not already landed with S5).
- **Arbitrary-width ints** (`u1`…`u128`, `u21` for Unicode): round up to the
  smallest C# container (byte/ushort/uint/ulong/UInt128) + mask at stores and
  observable boundaries (`@truncate`/`@intCast` semantics); `u0` = zero-size
  unit. Wrapping divergence is masked-by-construction; safe-mode traps are out
  of scope (ReleaseFast).
- **Sub-byte `packed struct`**: zig DEFINES a packed struct as a view over a
  backing integer — lower to the backing uN + generated shift/mask accessor
  properties (semantically exact, easier than C bitfields; 556 uses).
- **Wrapping/saturating operators** `+%` `-%` `*%` `+|` `-|` `*|` and the
  overflow-tuple builtins `@addWithOverflow` family (tuples exist; C#
  `unchecked` + compare). `@clz`/`@ctz`/`@popCount`/`@byteSwap`/`@bitReverse` →
  `BitOperations`/`BinaryPrimitives`.
- **Atomics**: `@atomicLoad/Store/Rmw`, `@cmpxchgStrong/Weak`, `@fence` →
  `Interlocked`/`Volatile`; `std.atomic.Value(T)` then compiles from source.
- **`threadlocal`** → `[ThreadStatic]` (the C `_Thread_local` precedent).
- **`@fieldParentPtr` / `@offsetOf` / `@bitOffsetOf`** — the layout model has
  offsets; parent-ptr is pointer arithmetic over them (156 uses; intrusive
  containers).
- **Error-surface completion**: error-set merge `||` **(DONE 2026-07-12** — Mul-level `||`
  lexer/grammar + erased-set lowering; `const E = A || B;` registers an unconstrained set. Top parse
  bucket 42→6 files, probe 25.0%→25.3%.**)**, `anyerror`,
  `@errorCast`, switch-on-error completeness, `errdefer` (audit vs Milestone H).
- **Casts audit**: `@constCast`, `@volatileCast`, `@intFromPtr`/`@ptrFromInt`,
  `@floatFromInt`/`@intFromFloat`/`@floatCast` — fill per wall-finder hits.
- **`@Vector`**: do NOT build SIMD. S3's cpu-config biases std to scalar paths;
  a residual comptime-known small vector scalarizes to an element loop; anything
  else stays a loud cut.
- **Parse-and-honor vs parse-and-ignore** (each a deliberate, documented
  decision): `align(N)` on decls (honor in layout), `callconv(.c)` (honor —
  maps to the native-callconv marker), `inline fn`/`noinline` (ignore — the JIT
  decides), `allowzero`/`addrspace` (ignore, no-op on a managed target),
  `opaque {}` (honor as an incomplete type), `noreturn` (honor — maps to the
  `[[noreturn]]` precedent), multiline strings `\\…` (honor; lexer rule).

### G-goals — integration proofs that retire curation

Each goal = compile a real upstream std slice from source, validate against the
zig oracle differentially, and demote the corresponding curated path to a
peephole (or delete it):

- **G1 `std.ascii`** (pure leaf, minimal comptime) — the first whole-module
  compile. Needs S1–S3 + a few S9 bricks. Success = a fixture calling
  `std.ascii.toUpper`/`isDigit` from SOURCE, oracle-identical.
- **G2 `std.mem.eql`/`indexOfScalar` from source** — diff against the curated
  `ZigMem` versions, then make curation the peephole.
- **G3 `std.fmt` scalar formatting → real `std.debug.print`** — the reflection
  engine's proof (needs S4–S7). Success = W6's curated format-parse becomes a
  fast path; arbitrary format strings (width, alignment, `{any}`) work via
  source.
- **G4 `std.ArrayList` from source** — retires `ZigList<T>` for source-mode
  (keep the curated type as the recognized fast path if compile time warrants).
- **G5 `std.AutoHashMap`** — the graduation exam: hash-fn selection via
  `@typeInfo`, heavy comptime, aggregate reflection end-to-end.

## Sequencing

```
S0 wall-finder ──────────────── (data; re-run every milestone — the progress bar)
S1 module graph ══ S2 lazy lowering (co-designed) ──→ S3 builtin/root ──→ G1
S4 TypeVal ──→ S5 @typeInfo+aggregates ──→ S6 inline-for/@field ──→ S7 reify+@compileError ──→ G3
S8 redirect table (after S1; incremental, demand-driven)
S9 bricks (any time; wall-finder-ranked; several are G1 prerequisites)
G1 → G2 → G3 → G4 → G5
```

Sizing: S0 S · S1 L · S2 L · S3 S/M · S4 M · S5 L · S6 M · S7 M · S8 M/L spread
· S9 many S/M. The risk center is **S1+S2** (the biggest ZigLowering refactor
since the wall — flat maps → per-module environments + universal laziness);
like W3, expect to split it (S1a decl tables + namespace values eager; S1b
laziness) if the audit says so.

## What stays permanently behind (do not relitigate)

- **`async`/`suspend`/`resume`** — managed-target root; not in pinned 0.17's
  usable surface (the oracle cannot validate it).
- **Inline `asm` as code** — both backends target a VM. Its 463 std uses live in
  the platform floor / cpu probes → redirected (S8) or dead under our target
  config, never lowered.
- **Real syscalls** — no syscall surface on .NET; the redirect table IS the
  answer, same as C's libc.
- **True SIMD `@Vector` codegen** — scalarize or cut; revisit only if a G-goal
  is actually blocked on performance semantics (none is — zig's own scalar
  fallbacks exist for every std use).
- **Safe-mode overflow traps** — a semantics flip, separate decision
  (fable-wall.md's standing note); `mode = .ReleaseFast` states it honestly.
- **NOT behind the wall anymore (measured, not assumed):** `usingnamespace`
  (0 uses — removed upstream), `@cImport` (0 uses), monolithic `@Type(info)`
  (0 uses — superseded by kind-specific builtins).

## Doc debt to pay as milestones land

- ZIG-SUPPORT.md: the three-tier § gains a pointer here; each S/G lands its
  rows (coverage table + "Why these are out" shrinks as tier (b) becomes built).
- fable-wall.md § "What stays behind the wall": the tier-(2)/(3) bullets defer
  to this file once S0 ships.
- The wall-finder's coverage percentages get recorded per milestone in THIS
  file (the chibi-campaign runbook pattern).
