# Deferred ledger — deliberate cuts still on the books

One place to look for "we chose not to do this *yet*, and here's why." Keeps deferrals
from scattering across commit messages, PR bodies, and memories.

**Scope:** things we intend to finish eventually but have *staged* — parse-only bricks
whose lowering is a loud cut, grammar/lowering gaps cut for a stated reason (a
conflict, rarity, or a missing engine), and **runtime-fidelity divergences** (libc/Zig
runtime functions whose behavior measurably differs from the real thing, surfaced by
audit). **Not** permanent exclusions — those live in the
SUPPORT docs and don't belong here:

- **C** permanent out-of-scope → [`../C-SUPPORT.md`](../C-SUPPORT.md) (VLA, trigraphs, Annex-K, …).
- **Zig** permanent exclusions → [`../ZIG-SUPPORT.md`](../ZIG-SUPPORT.md) (`async`/`await`, inline `asm`, SIMD `@Vector` — bias std to scalar instead).

**Discipline:** when you defer something with a reason, add a row here (and delete it when
it lands). A deferral that isn't written down is the thing that's "hard to keep track of."

---

## Runtime fidelity

Source: the 2026-07-17 runtime audit (all 40 `DotCC.Libc/*.cs` files, every public function,
diffed against the SUPPORT-doc rows). Verdict: the "no silent lies" invariant held *almost*
everywhere — the POSIX tier table, threads fidelity notes, and locale/setjmp rows were all
accurate. These are the divergences the audit surfaced that were **not** yet on the books
(each SUPPORT row now carries its caveat and points here); all are finishable, none blocks
current programs.

**Landed 2026-07-17** (the low-hanging C fruits — moved off the list):
- `printf`/`fprintf` (+ `w*`) now **return the byte count** (was always 0) — `PrintfBuilder`
  accumulates UTF-8 bytes through a counting `Emit` and returns the total from `Done()`.
- `scanf` now **supports `%x`/`%X`/`%o`/`%u`**, **honors max field width**, and **throws
  loudly** on a spec the routed overload can't satisfy (`%n`, `%[…]`, or a format/arg-type
  mismatch) instead of a silent no-op — closing the fail-loudly-invariant hole.
- `socket(AF_INET6/AF_UNIX)` **fails at create with `EAFNOSUPPORT`** (loud, not a dead-end fd).
- `setsockopt(SO_REUSEPORT)`→`ReuseAddress` is now a **documented, symmetric** substitution
  (`getsockopt` reads the same bit back).

**Landed 2026-07-17** (the low-hanging Zig allocator fruits — `DotCC.Libc/ZigAlloc.cs`):
- `FixedBufferAllocator` now **honors the requested `Alignment`** — `FbaAlloc` aligns the bump
  pointer up exactly like real zig's `alignPointerOffset` (the pad is charged to the cursor), and the
  devirtualized `AllocFba`/`CreateFba`/`ReallocFba` sites feed the real `AlignOf<T>` instead of
  `default(0)`. `AlignOf<T>` is now a single shared source of truth capped at 16 — which is why the
  C heap (≥16-aligned) and the arena (16-aligned data start, 16-rounded bumps) satisfy every request
  dotcc can generate *by construction*, so neither needed a code change (documented in place).
- `FixedBufferAllocator.free`/`FreeFba`/`DestroyFba` now **reclaim the last allocation** — real zig's
  `isLastAllocation` trick (the freed region ends exactly at the bump cursor ⇒ rewind by its length);
  freeing an earlier region stays a correct no-op. Pins in `ZigAllocRuntimeTests`.

**Still open:**

| Gap | Divergence | Fix sketch |
|---|---|---|
| `realpath` | lexical `Path.GetFullPath` only — no symlink dereference | walk components via `FileSystemInfo.LinkTarget`/`ResolveLinkTarget` (net6+, AOT-clean) |
| Wide-format transcode cache | keyed by pointer **address** — a mutated format buffer at the same address serves stale text | key by content hash, or skip the cache for non-RVA pointers |

Doc-rot fixed by the audit (no action left): the `signal.h` row's stale "deferred to
standalone-REPL" note (functions landed), `Float128.cs`'s stale "later stages" header comment
(everything landed), and `realpath` misfiled under the faithful tier.

## Zig — parse-only (parses today; lowering is a loud `IrUnsupportedException`)

The road-to-zig-std S9 bricks advance *parse* coverage ahead of lowering on purpose (the
probe is parse-only; a construct that reaches the binder fails loudly, never silently). Each
of these parses and has a `ZigParseProbe` pin, but lowering is not wired yet:

| Construct | Landed | Lowering gap |
|---|---|---|
| Error-set merge `A \|\| B` | #86 | erased set registered; no member-set constraint |
| `++` concat / `**` repeat | #88 | **literals + comptime STRING/INT/ARRAY consts + `@typeName` + a type-BORROWING anon `.{…}` operand now fold** (S9/S5 — string/typed-array literals; a `const` bound to a comptime string/int (`_comptimeValues`) or array (`_comptimeArrayConsts`); `@typeName(T)` for a primitive/slice/pointer/optional via source-spelling; an anon `.{…}` operand borrows a typed operand's element type). Only cut now: two UNTYPED anon `.{…}` operands (`.{1} ++ .{2}` — common-type/tuple inference) and `@typeName` of a USER type (zig's file-qualified `file.Name`) |
| `@typeName(T)` of a user type / alias | S5 | zig's fully-qualified `file.Name` (or an alias's resolved name) — dotcc lacks the file-qualification scheme; primitives + composed-of-primitives fold |
| Nested `const Inner = enum/union {…};` as a container member | #89 | V1 binds nested STRUCTS (fields-only, plain-name in parent methods); nested enum/union + external `Parent.Inner` qualified access deferred |

**Lowered since** (parses *and* lowers now — moved off the gap list):
- Switch-prong bodies `=> return [e]` / `=> |x| body` (parsed #89) — return + capture-value/ref prong bodies, non-union and tagged-union, reuse the statement return-lowering; oracle-verified.
- Inline named-field struct **type** (`fn f() struct { a: u8 }`, `field: struct {…}`, parsed #90) — `LowerType` reifies a synthesized nominal struct type per source site (`__AnonStruct<n>`), built via `.{ … }` and read with `p.field`; oracle-verified. Fields-only (a method / `const` / nested-container member still needs a named container decl).
- Nested `const Inner = struct {…};` as a struct-body member (parsed #89) — bound under a parent-mangled name (`Outer__Inner`), resolved by plain name inside the parent's methods, built via `.{…}` and read with `i.field`; oracle-verified. Fields-only (a method / `const` / further-nested container is a precise loud cut); nested enum/union + external `Parent.Inner` qualified access still deferred.

## Zig — real-std navigation vs the curated std (mutually exclusive today)

**Setting `DOTCC_ZIG_LIB_DIR` breaks the curated allocator path.** With the std root
configured, `@import("std")` navigates REAL upstream source (S1/G1), and 6 zig-oracle
programs that pass without it fail with a loud
`zig type 'FixedBufferAllocator' not supported yet (slice)` (also `ArenaAllocator`, and a
user `Node` through the same path): `arena`, `alloc_fba`, `alloc_oom`, `opaque_resize_remap`,
`resize_remap_fba`, `alloc_param`. Measured 2026-08-08 and **reproduced identically on
`main`** — pre-existing, not caused by the G4 methods work; the full oracle is green in its
default (no-lib-dir) configuration, which is what CI runs.

So the S1 design note "curated `std.mem`/`debug`/`heap`/`testing` fast-paths still win
(checked first)" does **not** hold for `std.heap.*` *types* — only, apparently, for the
value/method paths. `std.heap.FixedBufferAllocator.init(&buf)` in a type / static-call position reaches
real-source navigation and dies there instead of falling back to the curated type.

**Why this matters for G4:** the end state — real `std.ArrayList` from source, allocating
through an allocator — needs real-std navigation and allocators *at the same time*, which is
exactly the combination that is broken today. Ranks alongside the plan's blocker (4) (S4d
type-position module-graph fallback); the two are the same seam viewed from opposite sides
(one can't reach source, the other can't fall back from it).

**Fix sketch:** make the curated-std peephole authoritative in TYPE position too — check
`StdTypes`/`StdGenericTypes` before `ResolveModulePath` in `LowerType`'s `Zig.Field` case —
and add a regression leg that runs the oracle suite WITH the lib dir set, so the two
configurations can't drift apart again unnoticed.

## Zig — bad emit (transpiles "successfully" but the emitted C# does NOT compile)

The worst category — it breaks the fail-loudly invariant, since dotcc exits 0 and the error only
surfaces when the C# is compiled. All three were found by a lowering sweep around the G4
reified-methods brick (each reproduces on a plain/ordinary construct, so none is generic-specific).
Each needs its own focused fix.

| Gap | Repro | Emitted / error | Fix sketch |
|---|---|---|---|
| A `[N]T` field initialized from a struct LITERAL | `const B = struct { items: [4]u8, len: usize }; … return .{ .items = undefined, .len = 0 };` | `new B { items = default(byte*), len = 0 }` → **CS1666** "cannot use fixed size buffers contained in unfixed expressions" | a `fixed` buffer field can't be assigned in an object initializer at all: build the value into a local, `fixed`-pin it and fill (or skip the member for an `undefined` array — the field is already default-initialized). Work-around today: `var s: T = undefined; s.len = 0;` |
| A comptime VALUE param typed by an earlier comptime TYPE param | `fn C(comptime T: type, comptime start: T) type` | loud (not bad emit): `zig type 'T' not supported yet` | `EvalTypeReturningCall` resolves all args in one loop but installs the type seeds into `_typeAliases` only AFTER it, so a later param's type can't see `T`. Install each seed inside the loop (params bind left-to-right, as in zig). Not load-bearing for `Aligned` (its `alignment` is `?mem.Alignment`, not `T`-typed) — but IS what `SentinelSlice(comptime s: T)` needs |

**Fixed 2026-08-08** (was in this section's spirit, landed with the G4 methods brick): a narrow
UNSIGNED (`u8`/`u16`) comptime VALUE seed substituted as `40u` — a `uint` literal that will not
implicitly assign to a `byte`/`ushort` sink (**CS0266**). Reproduced on a plain W3a generic
(`fn mk(comptime start: u8) u8 { return start; }` → `return 40u;`). Normalized in the single
`ComptimeVarLit` substitution point (narrow-unsigned → `int`, value-preserving), which is the same
rule `BindFoldedCapture` already applied on the captured-`if` path — so every comptime-var path
(W3a value seed, `comptime var`, `inline for` capture, reified-method seeds) now shares it.

## Zig — deferred grammar (does NOT parse yet; cut for a reason)

| Construct | Why deferred | Unblock |
|---|---|---|
| Inline named struct as a **value** (`const X = if (c) struct {…} else struct {…}`) | S/R conflict with `structDecl`: the value cascade (`CurlySuffix → Type`) would make `const X = struct {…}` ambiguous between a container decl and a typed value | route `const X = struct{…}` through the value path and drop `structDecl` (big lowering refactor), or a GLR/precedence escape |
| Inline named struct under a recursive type prefix (`?struct{…}`, `[]struct{…}`) | `AType` adds the inline form only at the *top* of an annotation slot, not inside `Type`'s recursive prefixes | thread the inline form through the `?`/`*`/`[]` element positions |
| Anonymous struct type with mixed named+positional / arity > 7 | tuple lowering bound at 7 | — |

## Zig — the big open parse buckets (not cuts; just next)

These are ranked live in [`std-parse-probe.report.txt`](std-parse-probe.report.txt) — the
report *is* the worklist. Current head (2026-08-08, 32.0% parse-clean): top-level
file-is-a-struct fields (`$`/bare-IDENT in state 0/128, 25 files each), `)`-in-440 (23),
`(`-in-276 (20), statement-position `if`/`switch` in a value slot (15 each), `align(N)`.
S4a retired the former `'|'`-in-518 bucket (value-position captures). See the S9 table in
[`road-to-zig-std.md`](road-to-zig-std.md).
