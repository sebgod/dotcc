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
- **Zig** permanent exclusions → [`../ZIG-SUPPORT.md`](../ZIG-SUPPORT.md) (`async`/`await`, inline `asm`). SIMD `@Vector` is no longer excluded: it lowers to .NET's vector types (target T5).

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
| `free` of a zig sentinel slice (`[:0]T`, task #107) | dotcc's slice carries no sentinel, so `a.free(z)` of a `dupeSentinel` / `allocSentinel` result hands the allocator `len` elements where zig hands `len + 1`. Harmless on every curated allocator (the C heap ignores the size, the arena's free is a no-op, the FBA just does not reclaim it as its last allocation); a custom vtable allocator that checks the length would see one element less | carry the sentinel on `CType.Slice` (it is already parsed), and free `len + 1` for one |

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
| A `[N]T` field set to real contents in a struct literal (`.{ .items = [_]u8{1,2}, … }`) | — | an array field is inline storage (a C# `fixed` buffer), which can't be assigned in an object initializer; only `undefined` lowers (the member is dropped). Real contents need the literal built into a local + element-wise stores through the ANF hoist — doable, just not built. Work-around: `var v: T = undefined; v.items[0] = …;` |
| `@typeInfo(T).<kind>.bits` through an `anytype` param or `@TypeOf(expr)` | S5b | the DECLARED width now rides a type BINDING (a spelling, an alias, a `comptime T: type` param) and joins the instance key. What is left has no spelling ANYWHERE: an `anytype` param's type is inferred from a value, and `@TypeOf(x)` on a `u21` variable yields the widened `uint`. Answering the lowered width would disagree with zig, so it stays a loud cut. **Fix:** the declared width would have to ride the VALUE's type — i.e. live on `CType` — which was rejected for the whole front end (`Prim` has value equality, so a width-carrying `u21` stops comparing equal to `u32` and perturbs coercion, peer typing and every memoization key). Likely permanent unless `_BitInt(N)` forces the issue |
| `@typeInfo(T).<aggregate>.decl_names` | S5c | the member LISTS now fold (`field_names` / `field_types` / `field_values` / `tag_type`, with `.len`, a comptime index and a `const` binding). `decl_names` does not: dotcc's container-const and method registries are NAME-keyed, so a DECLARATION-ORDER list is not available. `@hasDecl` works, since membership needs no order. **Fix:** record declaration order alongside those registries — mechanical, just not yet needed by anything measured |
| A CI differential for `@typeInfo`'s member lists | S5c | not a cut in dotcc — a TOOLCHAIN split. dotcc targets zig 0.17-dev (whose std it compiles); the CI oracle pins 0.16.0, the newest durable tag, where `@typeInfo` still exposes `fields: []const StructField`. No single program is valid in both, so the surface has emit pins + a by-hand 0.17-dev run instead. **Resolves itself** when 0.17.0 is tagged: add the program and bump `ZIG_VERSION` + `ZIG_SHA256` in `dotnet.yml` |
| ...and the same for `inline for` over one | S6 | PARTIALLY answered: the unroll is one code path whatever the operand, so the oracle program `inline_for_comptime_lists` exercises the type-list, parallel and indexed shapes over `[_]type{…}` literals — version-stable, so it runs under 0.16.0 too. Only the member-list OPERAND is still uncovered. **Reusable move:** when a version split blocks an oracle, look for a version-stable operand that reaches the same code |
| Runtime parallel `for (a, b) \|x, y\|` over two slices | S6 | the grammar accepts the form (it had to, for the comptime one), but only `inline for` over comptime LISTS lowers. A runtime lockstep walk is a straightforward two-index `for` — nothing measured needs it yet, and a wrong guess about which operand drives the length is worse than the named error |
| `inline for` over an anonymous `.{…}` / `&.{…}` type list | S6 | the typed `[_]type{…}` form is recognized structurally on its element spelling; the anonymous forms carry no element type, so they need sink inference to know they are a list of TYPES. 6 uses in the pinned std, against 21 for the typed form |
| `inline for` over a comptime list of AGGREGATES | S6 | the pinned zig exposes PARALLEL ARRAYS of scalars (`field_names` / `field_types` / `field_values`), so a list element is a string, a type or an integer — never a struct. If a later zig reverts to `fields: []const StructField`, this is the shape to build, and it needs the general aggregate value domain S5 originally assumed |
| `@Union` / `@Enum` / `@Pointer` / `@Fn` / `@Tuple` reification; `@Struct` outside a type-returning function, or packed, or with a `comptime` field | S7, #93 | each takes comptime AGGREGATE arguments (a `[]const []const u8` of field names, a `*const [N]type`, an attributes struct). `@Struct` as a type-returning function's result is modeled (task #93): the instance names the struct. A bare `const S = @Struct(…)` has no instance to name it after, a packed one needs its backing integer, and a comptime field has no runtime slot. The others still reify from a description dotcc only partly models, which would emit a wrong LAYOUT, worse than a named gap. Measured: ≤14 uses each, against 207 for `@Int` |
| `@tagName` of a runtime value | #93 | a comptime-known enum value folds to its member name; a runtime one needs an emitted name table |
| An enum literal with no result type inside a tuple (`.{ "if", .kw_if }`, std.StaticStringMap(Kw).initComptime) | #113 | zig types a bare `.member` as the comptime-only `@Type(.enum_literal)` and coerces it at use (`sorted_vals[i] = kv.@"1"`). dotcc has no such type, so a bare enum literal still needs a result type |
| `void` as an element or data type (`[*]const void`, `[N]void`, a tuple of `void`), std.StaticStringMap(void) | #114 | a zero-size type in an element or generic-argument position spells C# `void` (`new void[]`, `GlobalArrayFrom<void>`) and does not build; it should lower to `Unit` |
| Two pointers to one comptime array | #100 | a struct spliced from a comptime block pins each pointer's target as its own copy (equal contents, different addresses); an address comparison between them would differ from zig |
| `@shuffle`, and `/` `%` shifts on vectors | S7 → T5 | `@Vector(len, T)` itself landed in the target-identity segment (T5: .NET `Vector64/128/256/512<T>`, `@splat` / `@reduce` / `@select`, element-wise ops, comparison masks). `@shuffle` needs a lane permutation (`Vector128.Shuffle` takes a runtime index vector, zig's mask is comptime with negative indices naming the second operand), and .NET's vector types have no integer `/` / `%` and only a scalar shift count; none is on a measured path yet |
| A vector width with no .NET vector type (`@Vector(3, u8)`, `@Vector(4, u64)` is fine) | T5 | loud: the lanes must fill 8, 16, 32 or 64 bytes. A padded lowering (round up to the next family, mask the tail) would work for element-wise ops but not for `@reduce` / a load, which would read past the operand |
| `@bitSizeOf` of an AGGREGATE | S7 | zig's answer is the byte size in bits; dotcc byte-packs its own layout, so the two can disagree. The cut points at `@sizeOf(T) * 8` — the same approximation, opted into deliberately. The scalar and enum kinds, which is what the measured operands are almost entirely made of, ARE exact |
| A type-returning body statement with no comptime meaning (`std.debug.assert(…)`, a `var`, a discarded call) | the W4 lift | the body is walked as comptime code, so a statement it cannot evaluate is a loud cut rather than a skip. 11 bodies in the pin open with `assert(…)` (the crypto `Aes*` family) — the honest lowering is to EVALUATE the assert at comptime and raise on false, which needs a comptime call to a (curated) `assert` |
| The `std.Target` METHODS on `builtin` (`cpu.has(…)`, `target.isGnuLibC()`, `ptrBitWidth()`, `stackAlignment()`) | S3 | ~30 call sites in the pin. dotcc's `builtin` is duck-typed — bare enum literals and anonymous structs — precisely to avoid `std.Target`'s ~37,000 lines of CPU feature tables; a method needs a real type to hang on, and each one is its own decision about what dotcc's target is. Answer them individually when a G-goal actually pulls one in |
| A same-file struct constant's field read does not fold | S3a | only a MODULE-QUALIFIED read does. Every name-keyed comptime map is guarded on the name not resolving to a runtime symbol (that guard is what stops a function-flat map answering for an unrelated local), and a constant that emits its declaration always HAS one. Reaching through an import has no such ambiguity. The measured need is entirely module-qualified |
| `@hasDecl(root, "std_options")` | S3 | `root` resolves and is empty by design, but `@hasDecl` is modeled over a container TYPE, not over a module. Needed for std's override-probing pattern; until then std takes its defaults, which is the correct behaviour for a program that declares no overrides |
| `builtin.mode` disagrees with `zig build-exe` | S3 | dotcc reports `.ReleaseFast` deliberately (it does not trap integer overflow, so a safe mode would have std emit checks dotcc does not honour) while zig defaults to `.Debug`. A DIVERGENCE by design, not a gap — recorded because it is the one `builtin` member a differential oracle program cannot read |
| `@typeInfo(T).pointer.size` of a raw pointer | S5a | dotcc lowers `*T`, `[*]T` and `[*c]T` to one C pointer, so their SIZE class (`.one`/`.many`/`.c`) is genuinely not recoverable from the lowered type. Reporting `.one` unconditionally would be wrong for `[*]T`. A SLICE answers `.slice` (the W4 lift — `std.meta.Elem` switches on it). Recoverable from the source spelling if it earns demand, like `bits` |
| `@typeInfo([]const T).pointer.child` carries dotcc's element `const` | S5a | dotcc models `[]const T` as `Slice(T const)`, so the reflected `child` is `const T` where zig's is plain `T`. Reading through it agrees; *writing* through a `const`-bound alias would be rejected by dotcc and accepted by zig. Not exercised by the oracle; the honest fix is to strip the qualifier when the slice's constness is what carries it |

**Lowered since** (parses *and* lowers now — moved off the gap list):
- Switch-prong bodies `=> return [e]` / `=> |x| body` (parsed #89) — return + capture-value/ref prong bodies, non-union and tagged-union, reuse the statement return-lowering; oracle-verified.
- Inline named-field struct **type** (`fn f() struct { a: u8 }`, `field: struct {…}`, parsed #90) — `LowerType` reifies a synthesized nominal struct type per source site (`__AnonStruct<n>`), built via `.{ … }` and read with `p.field`; oracle-verified. Fields-only (a method / `const` / nested-container member still needs a named container decl).
- Nested `const Inner = struct {…};` as a struct-body member (parsed #89) — bound under a parent-mangled name (`Outer__Inner`), resolved by plain name inside the parent's methods, built via `.{…}` and read with `i.field`; oracle-verified. **Since G3 (2026-09-23) a full container:** any kind (enum/union too), with methods, consts and further nesting; resolvable from a sibling FIELD (`std.fmt.Number.mode: Mode`) and qualified (`Parent.Inner`, `A.B.C`, `Number.Mode.decimal`, `A.P.two()`).

## Zig × wat — the backend gap (loud, but wide)

The Zig lowering produces ~25 IR node types the C front-end never does (`ZigTry`, `ZigCatch`,
`ZigMemCall`, `ZigListCall`, `AllocCall`/`FreeCall`/`CreateCall`/`DestroyCall`/`ReallocCall`/
`ResizeCall`/`RemapCall`, `TupleLiteral`/`TupleIndex`, `BitCast`, `SwitchExpr`, `ArrayByValueReturn`,
`OptionalOrElse`, `ErrUnionOk`/`ErrUnionErr`, `ZigErrorThrow`, …). `CSharpBackend` renders all of them;
`WatBackend` renders none — each falls to its default `the wat target does not yet support …` error, so
this is a LOUD gap, not a miscompile. Zig × wat therefore works only for programs whose lowering stays
inside the C-shaped node set (the web sandbox's Zig toggle runs `std.debug.print` over scalars).
Building it out is a backend campaign of its own — error unions and `try` need a wasm exception or
multi-value strategy, allocators a linear-memory heap, slices a fat-pointer layout — and is not
scheduled. (Found by the 2026-09 architecture review; the docs used to call the frame a full "2×2".)

## Zig — bad emit (transpiles "successfully" but the emitted C# does NOT compile)

The worst category — it breaks the fail-loudly invariant, since dotcc exits 0 and the error only
surfaces when the C# is compiled. **Currently EMPTY.** Keep it that way: a construct dotcc can't lower correctly
must throw, not emit C# that won't build.

It was not empty for part of 2026-09-24: once every lowering wall in front of `std.fmt.bufPrint` was down, the
emitted C# failed to build on four shapes std's integer printing writes: a compound assignment through `.?`
(C#'s `Nullable<T>.Value` is read-only), an array stored through a slice deref (`buf[i..][0..2].* = …`), an
array returned from a slice deref (std.fmt.digits2), and an `unreachable` switch-expression arm (a void call).
All four fixed the same day (task #50); bufPrint now runs == zig.

It was NOT empty for a stretch of 2026-09-23 — the architecture review caught the ledger saying so
while a measured case sat in the module-seam table below: `std.fmt`'s top-level `Alignment` enum was
emitted under its plain name and collided with dotcc's own runtime `Alignment` carrier (C# CS0101),
and two modules' same-named containers either threw (structs) or silently kept the first definition
(enums). Fixed by **module-qualified container naming** — an imported module's containers, reified
generic instances and inline anonymous structs emit as `<module>__<Name>` (`fmt__Alignment`,
`list__Box__u8`), the module's own code and its importers still spelling the plain name — plus a
collision guard on the enum registry matching the struct one. Oracle `import_same_named_types`.

The same afternoon the FUNCTION half of that collision turned up (the file-as-struct brick, G3): every
module's functions land in one emitted class, and an imported function kept its plain name, so
`util.f` beside the root's own `f` emitted two `static byte f()` methods (CS0111), and a file-as-struct's
`init(u8) Box` beside a root `init(u8) u8` emitted two `init(byte)` methods. Fixed the same way: a free
function an imported module declares, and each generic instance of one, emits as `<module>__<name>`
(`util__f`, `util__maxOf__u8`); lookup by source name is unchanged, a root unit's names and `extern`
prototypes stay as spelled, and a method needs nothing since its container is already qualified. Unit
`ZigCrossModuleGenericTests`; end-to-end in the oracle program `import_file_struct`.

Two more, found by the real-std probes the same day and fixed with them: an imported function named
like a C# keyword emitted as `inner__@double` (the module prefix went onto the ESCAPED name; now the
source name is qualified and escaped once), and a zig `void` PARAMETER emitted as
`byte f(void ctx, byte x)` (C# has no void parameter; void parameters, locals and values are now erased
by type, see ZIG-SUPPORT's void-value row). Units `ZigCrossModuleGenericTests`, `ZigVoidValueTests`.

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
| A cross-module container `const` (`k.Cfg.MAX`) | Not resolved — the value path reports `unresolved identifier 'k'` (a 3-segment value chain through a module). Container consts ARE registered, in the owning module's tables | give the value path the same owner lookup the type path got, or share the container-const table through `ZigImportScope` like the method + enum-member tables |
| A lazy module's top-level `var` named from a function body | Unresolved: only `const`s are lowered on demand, by inlining their comptime initializer at the use site, which a mutable global cannot be | declare the `GlobalVar` on first reference (a lazy counterpart of pass 1.5), memoized per module |
| A NESTED container named through a module in a type position (`io.File.Reader`, three segments) | Loud: "a dotted type `Reader` that is not a modeled std path". Two segments work (`File.Reader` with `const File = @import("File.zig");`, std's own shape), as does a module path ending at a container | resolve the leading segments as a module path, then hand the rest to the owner's qualified nested-type lookup |

## Zig — target identity (`builtin.cpu`, the target-identity segment of road-to-zig-std)

Decided 2026-09-24 by the maintainer: `builtin.cpu` is a real, typed `std.Target.Cpu` filled from the HOST at
dotcc's compile time through .NET's own facts (`RuntimeInformation.ProcessArchitecture`,
`System.Runtime.Intrinsics.X86.*.IsSupported`, `Arm.*.IsSupported`), as zig's `-mcpu=native` does, and
`@Vector(N, T)` lowers to `Vector128<T>` / `Vector256<T>` (software fallbacks keep a build correct on an older
CPU). Segment status: `docs/plans/road-to-zig-std.md`, "The target-identity segment".

| Gap | Divergence | Fix sketch |
|---|---|---|
| `builtin.cpu` read as a RUNTIME value (passed to a non-comptime call, stored) | Bad emit today: the typed literal lowers to `&new Model{…}` (the address of a temporary), a `"…"` at `?[:0]const u8` rendered as a raw pointer, and a `stackalloc` feature list inside an object initializer. Every std use seen so far is comptime (feature questions, `cacheLineForCpu`), which folds | give an aggregate const read at runtime static storage (the anonymous-static path `&.{…}` at a pointer sink already uses), coerce a string literal at an optional-slice sink, and hoist a nested `stackalloc` out of an initializer |
| `comptime a or b` | dotcc's `comptime` prefix binds like a unary operator, so only `a` is comptime; zig's grammar gives it the whole expression (`PrimaryExpr <- KEYWORD_comptime Expr`). The value is the same; only where it is computed differs, which matters when `b` has no runtime lowering (the typed `builtin.cpu` above) | a `Primary -> 'comptime' Expr` production resolved greedily (rightmost), in its own precedence group |
| **arm64 hosts (backlog, maintainer request 2026-09-24)** | The segment is being built and verified on the win-x64 desktop, so the x86 path (`std.Target.x86` features, an `x86_64_vN` model, `Vector256` for AVX2) is the one exercised. On an arm64 host (the laptop, a win-arm64 or linux-arm64 CI runner) the aarch64 half is unverified: the mapping from `System.Runtime.Intrinsics.Arm` (`AdvSimd`, `AdvSimd.Arm64`, `Aes`, `Crc32`, `Dp`, `Rdm`, `Sha1`, `Sha256`, and SVE where .NET exposes it) to `std.Target.aarch64` feature names, the CPU model (`generic` vs a named core), `suggestVectorLength` giving 128 bits for NEON (256 if SVE is reported), and `@Vector` staying on `Vector128<T>` (no AVX-style 256-bit path on NEON) | run the target-segment oracles on an arm64 host; complete the `Arm` → aarch64 feature table and model choice in `ZigSyntheticModules`; add an arm64 differential leg for `std.mem.indexOfScalar` / `std.simd.suggestVectorLength`. Note the CI oracle job runs on ubuntu-x64 and the laptop is the arm64 machine |

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
