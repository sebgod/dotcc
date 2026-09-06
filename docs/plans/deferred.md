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
| A `[N]T` field set to real contents in a struct literal (`.{ .items = [_]u8{1,2}, … }`) | — | an array field is inline storage (a C# `fixed` buffer), which can't be assigned in an object initializer; only `undefined` lowers (the member is dropped). Real contents need the literal built into a local + element-wise stores through the ANF hoist — doable, just not built. Work-around: `var v: T = undefined; v.items[0] = …;` |
| `@typeInfo(T).<kind>.bits` through an `anytype` param or `@TypeOf(expr)` | S5b | the DECLARED width now rides a type BINDING (a spelling, an alias, a `comptime T: type` param) and joins the instance key. What is left has no spelling ANYWHERE: an `anytype` param's type is inferred from a value, and `@TypeOf(x)` on a `u21` variable yields the widened `uint`. Answering the lowered width would disagree with zig, so it stays a loud cut. **Fix:** the declared width would have to ride the VALUE's type — i.e. live on `CType` — which was rejected for the whole front end (`Prim` has value equality, so a width-carrying `u21` stops comparing equal to `u32` and perturbs coercion, peer typing and every memoization key). Likely permanent unless `_BitInt(N)` forces the issue |
| `@typeInfo(T).<aggregate>.decl_names` | S5c | the member LISTS now fold (`field_names` / `field_types` / `field_values` / `tag_type`, with `.len`, a comptime index and a `const` binding). `decl_names` does not: dotcc's container-const and method registries are NAME-keyed, so a DECLARATION-ORDER list is not available. `@hasDecl` works, since membership needs no order. **Fix:** record declaration order alongside those registries — mechanical, just not yet needed by anything measured |
| `@typeInfo(E).@"enum".tag_type` of an INFERRED enum tag | S5c | zig infers an untyped enum's tag as the smallest unsigned int holding its largest member (`u2` for four members); dotcc defaults an untyped enum to `int`, so answering would disagree on the width and on `@sizeOf`. A SPELLED tag (`enum(u8) {…}`) is answered — `_enumsWithSpelledTag` records which is which. **Fix:** apply zig's inference rule when registering an untyped enum, which also needs the declared-width machinery from S5b to describe the result |
| A CI differential for `@typeInfo`'s member lists | S5c | not a cut in dotcc — a TOOLCHAIN split. dotcc targets zig 0.17-dev (whose std it compiles); the CI oracle pins 0.16.0, the newest durable tag, where `@typeInfo` still exposes `fields: []const StructField`. No single program is valid in both, so the surface has emit pins + a by-hand 0.17-dev run instead. **Resolves itself** when 0.17.0 is tagged: add the program and bump `ZIG_VERSION` + `ZIG_SHA256` in `dotnet.yml` |
| ...and the same for `inline for` over one | S6 | PARTIALLY answered: the unroll is one code path whatever the operand, so the oracle program `inline_for_comptime_lists` exercises the type-list, parallel and indexed shapes over `[_]type{…}` literals — version-stable, so it runs under 0.16.0 too. Only the member-list OPERAND is still uncovered. **Reusable move:** when a version split blocks an oracle, look for a version-stable operand that reaches the same code |
| Runtime parallel `for (a, b) \|x, y\|` over two slices | S6 | the grammar accepts the form (it had to, for the comptime one), but only `inline for` over comptime LISTS lowers. A runtime lockstep walk is a straightforward two-index `for` — nothing measured needs it yet, and a wrong guess about which operand drives the length is worse than the named error |
| `inline for` over an anonymous `.{…}` / `&.{…}` type list | S6 | the typed `[_]type{…}` form is recognized structurally on its element spelling; the anonymous forms carry no element type, so they need sink inference to know they are a list of TYPES. 6 uses in the pinned std, against 21 for the typed form |
| `inline for` over a comptime list of AGGREGATES | S6 | the pinned zig exposes PARALLEL ARRAYS of scalars (`field_names` / `field_types` / `field_values`), so a list element is a string, a type or an integer — never a struct. If a later zig reverts to `fields: []const StructField`, this is the shape to build, and it needs the general aggregate value domain S5 originally assumed |
| `@Struct` / `@Union` / `@Enum` / `@Pointer` / `@Fn` / `@Tuple` reification | S7 | each takes comptime AGGREGATE arguments — a `[]const []const u8` of field names, a `*const [N]type`, an attributes struct — which needs the comptime-value engine S5 deliberately did not build (it folds types, not aggregate VALUES). Reifying from a half-understood description would emit a wrong LAYOUT, which is worse than a named gap. Measured: ≤14 uses each, against 207 for `@Int` |
| `@Vector(len, T)` and the SIMD family | S7 | 475 uses, and not a reflection gap: a whole execution model (vector registers, `@splat`/`@reduce`/`@shuffle`, per-lane semantics) that dotcc's scalar C# backend does not have. Named separately from the reification cuts so its count does not read as "S7 is unfinished" |
| `@bitSizeOf` of an AGGREGATE | S7 | zig's answer is the byte size in bits; dotcc byte-packs its own layout, so the two can disagree. The cut points at `@sizeOf(T) * 8` — the same approximation, opted into deliberately. The scalar and enum kinds, which is what the measured operands are almost entirely made of, ARE exact |
| A type-returning fn whose body returns a non-container TYPE (`return @Int(…);`, `return Aligned(T, null);`) | W4 / S7 | W4 requires `return struct {…}`. S7 makes the `@Int` form newly worth having, but it is only 4 uses in the pin — the real prize is the **44** bodies that return a delegating CALL and the **20** that return a `switch`/`if`, which is a W4 brick, not an S7 one |
| The `std.Target` METHODS on `builtin` (`cpu.has(…)`, `target.isGnuLibC()`, `ptrBitWidth()`, `stackAlignment()`) | S3 | ~30 call sites in the pin. dotcc's `builtin` is duck-typed — bare enum literals and anonymous structs — precisely to avoid `std.Target`'s ~37,000 lines of CPU feature tables; a method needs a real type to hang on, and each one is its own decision about what dotcc's target is. Answer them individually when a G-goal actually pulls one in |
| A same-file struct constant's field read does not fold | S3a | only a MODULE-QUALIFIED read does. Every name-keyed comptime map is guarded on the name not resolving to a runtime symbol (that guard is what stops a function-flat map answering for an unrelated local), and a constant that emits its declaration always HAS one. Reaching through an import has no such ambiguity. The measured need is entirely module-qualified |
| `@hasDecl(root, "std_options")` | S3 | `root` resolves and is empty by design, but `@hasDecl` is modeled over a container TYPE, not over a module. Needed for std's override-probing pattern; until then std takes its defaults, which is the correct behaviour for a program that declares no overrides |
| `builtin.mode` disagrees with `zig build-exe` | S3 | dotcc reports `.ReleaseFast` deliberately (it does not trap integer overflow, so a safe mode would have std emit checks dotcc does not honour) while zig defaults to `.Debug`. A DIVERGENCE by design, not a gap — recorded because it is the one `builtin` member a differential oracle program cannot read |
| `@typeInfo(T).pointer.size` | S5a | dotcc lowers `*T`, `[*]T` and `[*c]T` to one C pointer, so the pointer SIZE class (`.one`/`.many`/`.slice`/`.c`) is genuinely not recoverable from the lowered type. Reporting `.one` unconditionally would be wrong for `[*]T`. Recoverable from the source spelling if it earns demand, like `bits` |
| `@typeInfo([]const T).pointer.child` carries dotcc's element `const` | S5a | dotcc models `[]const T` as `Slice(T const)`, so the reflected `child` is `const T` where zig's is plain `T`. Reading through it agrees; *writing* through a `const`-bound alias would be rejected by dotcc and accepted by zig. Not exercised by the oracle; the honest fix is to strip the qualifier when the slice's constness is what carries it |

**Lowered since** (parses *and* lowers now — moved off the gap list):
- Switch-prong bodies `=> return [e]` / `=> |x| body` (parsed #89) — return + capture-value/ref prong bodies, non-union and tagged-union, reuse the statement return-lowering; oracle-verified.
- Inline named-field struct **type** (`fn f() struct { a: u8 }`, `field: struct {…}`, parsed #90) — `LowerType` reifies a synthesized nominal struct type per source site (`__AnonStruct<n>`), built via `.{ … }` and read with `p.field`; oracle-verified. Fields-only (a method / `const` / nested-container member still needs a named container decl).
- Nested `const Inner = struct {…};` as a struct-body member (parsed #89) — bound under a parent-mangled name (`Outer__Inner`), resolved by plain name inside the parent's methods, built via `.{…}` and read with `i.field`; oracle-verified. Fields-only (a method / `const` / further-nested container is a precise loud cut); nested enum/union + external `Parent.Inner` qualified access still deferred.

## Zig — bad emit (transpiles "successfully" but the emitted C# does NOT compile)

The worst category — it breaks the fail-loudly invariant, since dotcc exits 0 and the error only
surfaces when the C# is compiled. **Currently EMPTY.** Keep it that way: a construct dotcc can't
lower correctly must throw, not emit C# that won't build.

Four gaps were found by the lowering sweep around the G4 reified-methods brick (2026-08-08) — each
reproduced on a plain/ordinary construct, so none was generic-specific — and all four are now fixed:

- **A narrow UNSIGNED (`u8`/`u16`) comptime VALUE seed** substituted as `40u`, a `uint` literal that
  will not implicitly assign to a `byte`/`ushort` sink (**CS0266**). Repro: `fn mk(comptime start: u8)
  u8 { return start; }` → `return 40u;`. Normalized in the single `ComptimeVarLit` substitution point
  (narrow-unsigned → `int`, value-preserving) — the same rule `BindFoldedCapture` already applied on
  the captured-`if` path, so every comptime-var path (W3a value seed, `comptime var`, `inline for`
  capture, reified-method seeds) now shares it. Landed with the G4 methods brick.
- **A `[N]T` field initialized from a struct LITERAL** — `return .{ .items = undefined, .len = 0 };`
  emitted `new B { items = default(byte*), len = 0 }` → **CS1666** ("cannot use fixed size buffers
  contained in unfixed expressions"), because an array field is inline storage (a `fixed` buffer /
  `[InlineArray]` wrapper) and cannot be assigned in an object initializer at all. `BuildStructInit`
  now DROPS an `undefined` array member (zig's `undefined` asks for no particular contents, so C#'s
  zero-init stands) and loudly rejects any other value, which would need element-wise stores into a
  pinned buffer. Fixed 2026-08-09.
- **A comptime VALUE param typed by an earlier comptime TYPE param** (`fn C(comptime T: type,
  comptime start: T) type`) — loud, not a bad emit, but the same sweep. `EvalTypeReturningCall`
  resolved all arguments in ONE loop and installed the type seeds only afterwards, so a later
  parameter's declared type couldn't see `T`. Now two-phase like `InstantiateGeneric` (type args in
  the caller's env first, then seed and read the rest), i.e. parameters bind left-to-right as in zig.
  This is what `Aligned`'s nested `SentinelSlice(comptime s: T)` needs. Fixed 2026-08-09.
- **`DOTCC_ZIG_LIB_DIR` and the curated allocators were mutually exclusive.** With a std root
  configured, `@import("std")` navigates REAL upstream source (S1/G1) — and upstream re-exports its
  allocators as whole FILES, so `std.heap.FixedBufferAllocator` was both a curated type and a
  navigable module. Navigation sat above the curated `.init` fast-paths in `LowerMethodCall`, won, and
  died lowering upstream's own `init`: 6 oracle programs (`arena`, `alloc_fba`, `alloc_oom`,
  `opaque_resize_remap`, `resize_remap_fba`, `alloc_param`) failed with the std root set, invisibly,
  since CI runs the default configuration. Navigation is now guarded by a registry-driven
  `IsCuratedStdPath`, restoring S1's "the curated set is checked first" rule in the one position where
  it didn't hold. The full oracle is green in BOTH configurations, and an opt-in leg
  (`Dotcc_matches_zig_for_curated_allocators_with_real_std_configured`) keeps them from drifting
  apart. Fixed 2026-08-09. **The complementary direction is still open** — a NON-curated std TYPE
  cannot fall back to navigation; that is the plan's G4 blocker (4), S4d.

## Zig — the module seam (navigation works; these edges don't)

Type-position navigation landed 2026-09-03 (road-to-zig-std S4d): a non-curated dotted type, and a
module-qualified call in a type slot, resolve through the module graph, and a navigated type carries
its methods + enum members. These edges of the same seam are deliberate V1 cuts — each is LOUD.

| Gap | Divergence | Fix sketch |
|---|---|---|
| Container types are registered under their PLAIN source name | Two modules declaring `struct Options {…}` collide; `IrBuilder.RegisterStructType` now THROWS rather than silently keeping the first (which was a wrong-layout miscompile waiting for the first cross-module type reference). Real std has many same-named containers, so this will fire as the G-goals grow | module-qualified container naming (S1's stated `std__mem__…` rule, never implemented for containers): register a lazy module's containers as `<module>__<Name>`, keep its own `_containerTypes` keyed by the plain name → the mangled `CType.Named`, and thread the mangled name through pass 0a/0b's ~18 registration sites (methods/consts/nested containers key off it already) |
| A cross-module container `const` (`k.Cfg.MAX`) | Not resolved — the value path reports `unresolved identifier 'k'` (a 3-segment value chain through a module). Container consts ARE registered, in the owning module's tables | give the value path the same owner lookup the type path got, or share the container-const table through `ZigImportScope` like the method + enum-member tables |
| A comptime VALUE argument to an imported type-returning generic | Evaluated in the OWNING module's environment, so a literal works but a caller-scoped named constant fails with "must be a compile-time-known value" | pre-resolve non-type comptime args in the caller (the two-phase split already threads the caller's scope for TYPE args) |
| A type-returning body that returns another type-returning CALL | `pub fn ArrayList(comptime T: type) type { return Aligned(T, null); }` — exactly what `std.ArrayList` is — hits W4's "the body's final statement must be `return struct { … };`". Now REACHABLE (the module-qualified call resolves), which is the S4d dividend | G4 blocker 2: admit a `return <type-expr>;` body by evaluating it as a type and returning it, instead of requiring a struct literal |

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
