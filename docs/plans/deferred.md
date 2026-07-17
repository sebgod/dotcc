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

## Zig — deferred grammar (does NOT parse yet; cut for a reason)

| Construct | Why deferred | Unblock |
|---|---|---|
| Inline named struct as a **value** (`const X = if (c) struct {…} else struct {…}`) | S/R conflict with `structDecl`: the value cascade (`CurlySuffix → Type`) would make `const X = struct {…}` ambiguous between a container decl and a typed value | route `const X = struct{…}` through the value path and drop `structDecl` (big lowering refactor), or a GLR/precedence escape |
| Inline named struct under a recursive type prefix (`?struct{…}`, `[]struct{…}`) | `AType` adds the inline form only at the *top* of an annotation slot, not inside `Type`'s recursive prefixes | thread the inline form through the `?`/`*`/`[]` element positions |
| Anonymous struct type with mixed named+positional / arity > 7 | tuple lowering bound at 7 | — |

## Zig — the big open parse buckets (not cuts; just next)

These are ranked live in [`std-parse-probe.report.txt`](std-parse-probe.report.txt) — the
report *is* the worklist. Current head (2026-07-12, 31.8% parse-clean): top-level
file-is-a-struct fields (`$`/bare-IDENT in state 0/128), `(`-in-276, value-position
`if`/`switch`, `align(N)`. See the S9 table in [`road-to-zig-std.md`](road-to-zig-std.md).
