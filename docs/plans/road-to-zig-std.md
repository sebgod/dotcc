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

> **Status update (2026-07-19) — S4a/S4b/S4c DONE** (PRs #107 `576c534`, #108 `6622fb5`,
> #109 `95a0ef6`+`bf6cdee`; all merged to `main`, per-job CI green). Driven by the G4
> prerequisite hunt, so each is a bounded brick rather than the whole S4:
> **#107** the `@addWithOverflow` family → `ZigMath.<op>WithOverflow<T>` returning zig's
> `struct{T, u1}` as a `(T, byte)` tuple (128-bit operand = loud cut). **S4a/#108**
> value-position captured `if` (`const v = if (opt) |x| a else b`) — one conflict-free
> `IfExpr` grammar alt + `LowerIfCaptureExpr` (ANF-hoist to a result temp). **S4b part 1**
> comptime `?T` value params + folding that captured `if` at instantiation
> (`_comptimeOptionalVars`, mangled `__optnull`/`__opt<v>`). **S4b part 2 + S4c** the same
> fold in TYPE position inside a type-returning generic, plus multi-statement
> type-returning bodies (`ProcessTypeReturningBody` admits leading `const NAME = <type>;`
> aliases before the `return struct {…}`, lifting W4's single-statement V1 cut) — so
> `Store(u8, 3)` reifies a `[3]u8` field and `Store(u8, null)` a `[]u8` slice, which is
> exactly `array_list.Aligned(T, alignment)`'s shape.
>
> **Architectural rule these respected:** the comptime interpreter
> (`IrBuilder.Comptime.cs`) still has **no `TypeVal`** — the value/type firewall stands.
> Type computation happens at the *lowering* tier (the W1/W4 tradition), so S4's
> "interpreter TypeVal" framing below is satisfied by lowering-tier folding, not by
> breaching the firewall. Keep it that way.
>
> **Still not done:** S4d (type-position module-graph fallback — see G4's scope note),
> S3, S5–S7, G2–G5. Parse coverage is unchanged by design (32.0%): these were
> lowering-*depth* bricks, though S4a did retire the `'|' in state 518` probe bucket
> (10 files' first failure — value-position captures in `std/Io/Reader.zig` et al).

> **Status update (2026-09-03) — S4d DONE.** Module navigation now reaches **types**, not just
> functions. Before this, `LowerType`'s dotted (`Zig.Field`) case went to the curated std-type
> registry and threw when it missed, and its call (`Zig.CallArgs`) case only recognized a curated
> generic or a locally-declared template — so no type from another module, real `std` included, could
> be named at all. Now:
> - a dotted type no curated row claims resolves through the module graph to that module's registered
>   container (`ResolveExportedType`), with a loud error naming the FILE and the type when it has none;
> - a module-qualified call in a type slot (`list.Box(u8)`, `std.array_list.Aligned(u8)` — the shape
>   `std.ArrayList(T)` has) reifies the imported template **in its own module's environment**, keyed by
>   the type argument resolved in the **caller's** (the arguments are spelled at the call site);
> - the curated set is still checked FIRST, in type position as everywhere else (`IsCuratedStdPath`),
>   with a synthetic `mem.zig` whose `Allocator` cannot lower pinning that ordering.
>
> Making a navigated type *usable* rather than merely nameable took three companion fixes, each a gap
> the brick newly exposed: a lazy module now **drains the generic-instance and reified-method
> worklists** the way pass 2.5 does for a root (otherwise a reified type's methods were declared and
> never lowered — a bad emit); the **method** and **enum-member** tables are now shared down the
> `@import` chain (`ZigImportScope`), since both are keyed by a name that is unique across the emitted
> program; and a prepared module's **container methods are declared on first call**
> (`EnsureMethodDeclared`) rather than at prepare time, which would have lowered signatures for
> unreferenced decls and defeated S2's whole point.
>
> One silent hazard was closed on the way: `IrBuilder.RegisterStructType` was idempotent-by-name and
> **ignored** a second, differently-shaped aggregate — invisible while imported types were unreachable,
> a wrong-layout miscompile the moment they weren't. It now throws. The real fix is module-qualified
> container naming ([`deferred.md`](deferred.md)).
>
> Validation: 8 emit pins (sibling-module + synthetic-std-tree) + 4 new zig-oracle programs
> (`import_type`, `import_type_method`, `import_enum_member`, `import_generic_type`) + the runnable
> `examples/zig-module-types/`, byte-identical to real zig 0.17.0-dev.667 at exit 42.
>
> **Reached but still cut** (each loud, each a next brick): G4 blocker 2 — a type-returning body that
> returns another type-returning CALL (`pub fn ArrayList(comptime T) type { return Aligned(T, null); }`)
> still hits W4's "must be `return struct {…}`"; a cross-module container `const`
> (`k.Cfg.MAX`); and a comptime VALUE argument that is a caller-scoped named constant.

> **Status update (2026-09-04) — S5a DONE: `@typeInfo` + the comptime `switch` over it.**
> The first slice of S5's reflection engine, sized so it answers only what it can answer
> exactly. `ZigLowering.TypeInfo.cs` synthesizes the `std.builtin.Type` value **directly from
> the resolved `CType`** — never by compiling `std/lang.zig` for its layout, exactly as the real
> compiler treats that union — and every consumer folds at the LOWERING tier, so the S4
> architectural rule holds unchanged: the comptime interpreter still has **no `TypeVal`**.
> - `switch (@typeInfo(T))` (200 uses in real std) selects its prong at lowering time and lowers
>   **only that arm**. That is not an optimization: every other arm is written for a different
>   kind and would not lower for this type — a `.@"struct"` arm asking for `.fields` must never
>   be visited while instantiating at `u8`. The quoted tags dispatch with no special casing
>   (`NormalizeIdent` already folds `@"struct"` → `struct`), and one `SelectComptimeProng` serves
>   all three switch positions (statement, expression, value-temp filler).
> - A `|i|` prong capture binds the payload (shadow-saved, the W2/W3b pattern);
>   `const info = @typeInfo(T);` binds a comptime-only name and emits **no decl**.
> - Fields: `signedness` (and `== .signed` folding to a bool literal), `child` (a TYPE, so it
>   resolves in type positions), `is_const`, `len`, `bits`.
>
> **The fidelity rule this brick is really about.** dotcc widens an arbitrary-width `uN`/`iN` to
> the smallest standard width (`u21` → a 32-bit `uint`), so the LOWERED type no longer knows its
> declared width. `bits` is therefore read off the **source spelling** — the rule `@typeName`
> already follows — giving 21 for `@typeInfo(u21)`; asked through a type alias or a
> `comptime T: type` param, where the spelling is gone, it is a **loud cut** rather than a
> silent 32. A missing feature is recoverable; a wrong comptime constant is not. Carrying the
> declared width on the type (which C23 `_BitInt(N)` would want too) is the brick that lifts it.
>
> **Still cut, each loud:** `fields`/`decls` (comptime SLICES of comptime aggregates — that is
> S5's aggregate value domain + S6's `inline for`, the real heart of B2); `pointer.size` (dotcc
> collapses `*T`/`[*]T`/`[*c]T` to one C pointer, so the size class is genuinely not
> recoverable); `@field`/`@hasDecl`/`@hasField`; a `|*x|` by-ref capture; a block-bodied prong in
> value position. `.child` of a `[]const T` carries dotcc's element `const` where zig's does not
> — a divergence recorded in [`deferred.md`](deferred.md), not exercised by the oracle.
>
> Validation: 13 emit pins (every fold path + every loud cut) + 4 zig-oracle programs
> (`typeinfo_kind`, `typeinfo_signedness`, `typeinfo_bits`, `typeinfo_child`) + the runnable
> `examples/zig-typeinfo/`, byte-identical to real zig 0.17.0-dev.667 at exit 42. Parse coverage
> is unchanged by design (32.0%) — this is a lowering-depth brick.

> **Status update (2026-09-04) — S5b DONE: a zig integer's DECLARED width rides its type binding.**
> The debt S5a deliberately took on, paid the next brick. dotcc widens an arbitrary-width `uN`/`iN`
> to the smallest standard width (`u21` → a 32-bit `uint`), so `@typeInfo(T).int.bits` could only be
> answered from a literal spelling. Now the declared width travels WITH the binding — seeded,
> shadow-saved and restored in lockstep with `_typeAliases` — so it answers through a
> `comptime T: type` param, an alias (`const Cp = u21;`), and a W4 reified generic's type argument.
>
> **The buried half was the instance KEY.** `MangleType` keys an integer by its LOWERED width, so
> `u21` and `u32` mangled *identically* and shared one memoized instance — whichever instantiated
> first would have dictated the other's `bits`. `MangleTypeSeed` now keys by the declared width when
> it differs, making them distinct specializations, which is also what zig means (they ARE different
> types). Every standard spelling declares exactly its lowered width, so **no existing mangled name
> changed** — only a narrow width gets a new one.
>
> **Why the width rides ALONGSIDE the type and not ON it.** `CType.Prim` is a record with value
> equality: a width-carrying `u21` would stop comparing equal to `u32` and perturb coercion, peer
> typing, and every memoization key in the front end — a far larger blast radius than this one
> question warrants, and it would break the documented `uN`-widening leniency that existing programs
> rely on. Widening the type-SEED tuple instead (`TypeSeed(Name, Type, DeclaredBits)`) made the
> compiler enumerate every site that had to change, which is how the W4 reification path and the
> body-alias shadow list were found rather than guessed at.
>
> **Still cut, and probably permanently:** an `anytype` param or `@TypeOf(expr)`, where the type is
> inferred from a VALUE and no spelling survives anywhere — that would need the width on `CType`
> after all. Recorded in [`deferred.md`](deferred.md).
>
> Validation: 6 emit pins (through a param / an alias / the `u21`-vs-`u32` non-collision / an
> unchanged standard-width name / the nested same-named-param shadow restore / the remaining cut) —
> the S5a "loud cut" pin flipped to the new behaviour — 2 zig-oracle programs
> (`typeinfo_bits_generic`, `typeinfo_bits_nested_shadow`), and `examples/zig-typeinfo/` grew a
> `generic` line, still byte-identical to real zig 0.17.0-dev.667 at exit 42.

> **Status update (2026-09-04) — S5c DONE: the member lists + membership builtins.**
> S5's "aggregate half" — and the pinned zig made it FAR smaller than this plan assumed. The plan
> was written against the older `fields: []const StructField` API, a comptime slice of comptime
> STRUCTS, which really would have needed a general aggregate value domain. This zig
> (0.17.0-dev.667) instead exposes **parallel arrays**: `field_names: []const [:0]const u8`,
> `field_types: []const type`, `field_values: []const comptime_int`. Each is homogeneous, so what
> was actually needed is a comptime LIST of one element kind. Measured against the pinned source,
> that is also where the uses are: `.field_names.len` ×167, `.field_names[i]` ×34.
>
> Landed: the lists themselves (built from dotcc's OWN registries — `IrBuilder.StructFieldsOf` for a
> struct/union's declared fields in order, `IrBuilder.Enums` for an enum's members in order — never
> by compiling `std/lang.zig`), their `.len`, a bounds-checked comptime index, a `const` binding
> (decl dropped, like every comptime-only name), `tag_type`, and the membership builtins
> `@hasField` / `@hasDecl` plus comptime-named `@field(x, "n")` (73 + 220 uses). Still all
> lowering-tier: no list reaches the IR.
>
> **Two refusals, both the same shape of judgement `bits` made in S5b.**
> - `decl_names` is cut: dotcc's container-const and method registries are NAME-KEYED, so a
>   declaration-ORDER list is not available. `@hasDecl` works precisely because membership needs no
>   order — the error says so rather than implying the whole area is missing.
> - `tag_type` is answered only for a SPELLED tag (`enum(u8) {…}`). zig INFERS an untyped enum's tag
>   as the smallest unsigned int holding its largest member (`u2` for four members) while dotcc
>   defaults to `int` — reporting that would disagree on the width and on `@sizeOf`. A new
>   `_enumsWithSpelledTag` set records which is which.
>
> **Gotcha worth keeping:** `TryFoldTypeInfoListType` runs SPECULATIVELY from the type-alias probe
> (`const first = names[0];`), so a non-type element must return false, not throw — otherwise a
> perfectly good string element becomes a hard error before the value fold ever sees it. Same rule
> `EvalComptimeValue` has always had.
>
> **What this does NOT include:** `inline for` over a member list (95 uses in 37 files) — that is
> S6, and it is now the single highest-value brick left, since the lists it iterates all exist.
>
> **A version split this brick surfaced (and the docs had wrong).** dotcc's front end targets zig
> **0.17-dev** — the version whose std the campaign compiles from source, and which the grammar
> tracks. The CI oracle pins **0.16.0**, the newest DURABLE tagged release: dev/master tarballs are
> GC'd off ziglang.org's index within days (the former 0.17-dev.667 pin is already gone), so a dev
> pin would rot CI. That was fine while every oracle program used the stable core — but the member
> lists are exactly where the two versions differ, since 0.16 still has `fields: []const
> StructField`. So there is deliberately **no member-list oracle program**: the surface is covered by
> emit pins plus a by-hand run against the local 0.17-dev install, and gains a CI differential the
> moment 0.17.0 is tagged (checked: 0.16.0 is still the newest tag). `docs/testing.md` claimed CI
> pinned 0.17.0-dev.667 — it never did since the pin moved; that is now corrected.
>
> Validation: 9 emit pins + 1 zig-oracle program (`typeinfo_membership` — the membership builtins are
> version-stable), and `examples/zig-typeinfo/` grew `members` + `ask` lines — seven lines now, still
> byte-identical to real zig 0.17.0-dev.667 at exit 42 (0.17-dev only, by the same split).

> **Status update (2026-09-04) — S6 DONE: `inline for` over a comptime list.**
> The consumer S5c's lists were built for. A member list has no runtime representation, so a plain
> `for` cannot walk one; `inline for` can, because it is not a loop — it UNROLLS at lowering time
> into one copy of the body per element. The existing unroller could not be reused as-is: it binds
> each capture to a runtime symbol initialized by an emitted `const cap = …;`, and a list element may
> be a TYPE (no runtime slot exists) or a comptime STRING (which `@field` must read at lowering
> time). So the capture is seeded into the same name-keyed maps an ordinary comptime binding uses —
> `_typeAliases` for a type, `_comptimeValues` (+ a new `_comptimeStrings` for the name half) for a
> string or integer — and shadow-restored after every copy, exactly as W3b seeds a type parameter.
>
> **The shapes were measured, not guessed, and that changed the brick.** The plan above assumed one
> form. In the pinned std the PARALLEL two-list form is the commonest member-list shape by a
> distance: `inline for (info.field_names, info.field_types) |field_name, field_type|` ×17, against
> ×11 for the single-list form, ×12 for `(list, 0..) |x, i|`, and ×21 for a `[_]type{…}` literal
> walked as `|T|`. The parallel form needed the one grammar production this brick adds
> (`Expr ',' Expr ')'`, conflict-free against the indexed form by the same 1-token lookahead the
> block already documents: `..` selects one, `)` the other). All four shapes then fall out of a
> single unroll over N index-parallel lists — `0..` is modelled as a synthesized list of its own
> indices, so there is no second code path.
>
> `@field` / `@hasField` / `@hasDecl` now take their member name from a literal **or** a capture
> (`ComptimeStringArg`), which is what makes `inline for (field_names) |f| … @field(v, f) …` — one
> generic body reaching every field of a struct it has never seen — work at all. Nothing dynamic
> survives: the emitted C# holds the field accesses and nothing else.
>
> **Unlike S5c, this brick DOES get a CI differential.** The unroll is one code path regardless of
> where the list came from, so exercising it over `[_]type{…}` literals — which mention `@typeInfo`
> nowhere — puts the type-list, parallel and indexed shapes under a real compiler on CI's zig 0.16.0
> as well. Only the member-list OPERAND stays oracle-less until 0.17.0 is tagged. Worth remembering
> as a general move: when a version split blocks an oracle, look for a version-stable operand that
> reaches the same code.
>
> **Cuts (all loud):** a RUNTIME parallel `for (a, b) |x, y|` (the grammar now accepts it; only the
> comptime form lowers); parallel lists of unequal length; an index capture not starting at 0; an
> anonymous `.{…}` / `&.{…}` type list (needs sink inference); `break`/`continue` in an unrolled body
> (there is no loop left to target); the 4096-copy unroll cap.
>
> Validation: 13 emit pins + 1 zig-oracle program (`inline_for_comptime_lists`), and a new
> `examples/zig-inline-for-lists/` — eight lines, exit 42, identical to real zig 0.17.0-dev.667.

> **Status update (2026-09-06) — S7 DONE: reification builtins + `@compileError`.**
> The direction opposite to S5: `@typeInfo` reads a type's description out, `@Int` builds a type back
> in from one. `ZigLowering.Reify.cs`, **no grammar change** — a builtin call already parses.
> - **`@Int(signedness, bits)`** (207 uses) is the whole of the family that pays, and it needed
>   **`@bitSizeOf`** (431 uses) to be worth anything, since that is the operand of most of its calls.
>   Both go through the S5b declared-width machinery rather than the lowered type: `@bitSizeOf(u21)`
>   is 21, not the 32 dotcc widens it to, and the width a constructed type was BUILT with rides its
>   binding the same way a spelling does — so the two halves round-trip
>   (`@typeInfo(@Int(.unsigned, @bitSizeOf(u21))).int.bits` is 21).
> - **The aggregate constructors are cut, and the measurement is why.** `@Pointer` ×14, `@Struct`
>   ×5, `@Enum` ×4, `@Union` ×3 — and every one takes comptime AGGREGATE arguments (a field-name
>   array, a `*const [N]type`, an attributes struct) that dotcc has no engine for. Reifying from a
>   half-understood description would emit a wrong layout. `@Vector` (475 uses) is named separately:
>   it is SIMD, a whole execution model, not a hole in the reflection arc.
>
> **The `@compileError` subtlety was smaller than the plan feared, and the measurement is again why.**
> The plan called for threading a poison value through the folder. But zig's own reference specifies
> the diagnostic on ANALYSIS — "this function, when semantically analyzed, causes a compile error…
> there are several ways that code avoids being semantically checked, such as using `if` or `switch`
> with compile time constants" — and lowering IS dotcc's analysis, with those very folds already in
> place since S5a/W3a. Counted in the pin, that is where the uses live: **231 of 595 are an `else =>`
> prong** of a `switch` over `@typeInfo`, the arm S5a never lowers; the rest are comptime `if` guards
> inside `comptime T: type` functions. So raising on sight is correct for them, and no poison is
> needed.
>
> **One shape does need it**, and it is one the campaign walks straight into: a top-level
> `pub const NAME = @compileError("use X instead");` — a deprecation tombstone, 25 in the pin,
> including `std/meta.zig` and `std/os/windows.zig`. Zig analyses a declaration only when something
> references it, so firing at the declaration would make those modules unimportable. Hence a poisoned
> NAME: the binding records the message and emits nothing, and the diagnostic is raised at the
> reference — in a value position and a type position alike. **`@setEvalBranchQuota`** (80 uses) now
> raises the interpreter's step budget and never lowers it, which is zig's own rule for it.
>
> **Unlike S5c, both halves get a CI differential.** The `@Type`-to-`@Int`/`@Struct`/`@Enum` split
> already landed in **0.16.0** — verified against that version's own language reference, not assumed
> — so the pinned oracle compiler has `@Int`, and `@bitSizeOf` is ancient. Two new oracle programs
> rather than the version-split hole S5c had to ship with.
>
> **Cuts (all loud):** the aggregate constructors above; `@Vector`; a width outside 1..128; a
> non-comptime width; a non-signedness tag; `@Int` in a value position; `@bitSizeOf` of an AGGREGATE
> (zig's answer is the byte size in bits and dotcc byte-packs its own layout, so the error points at
> `@sizeOf(T) * 8` as the deliberate opt-in). A `fn X(comptime T: type) type { return @Int(…); }`
> hits W4's pre-existing "must `return struct {…}`" — only 4 uses in the pin, and lifting it is a W4
> brick (the bigger prize there is the 44 bodies that return a delegating CALL).
>
> Validation: 23 emit pins + 2 zig-oracle programs (`reify_int_and_bitsizeof`,
> `compile_error_guards_fold_away`), and a new `examples/zig-reify/` — five lines, exit 42,
> byte-identical to real zig 0.17.0-dev.667.

> **Status update (2026-09-06) — S3 DONE: the synthetic `builtin` + `root` modules, and the
> comptime struct constants they need.** Started as G3 and became S3 by measurement: probing
> `std.fmt.bufPrint` against the real tree walls at `zig type 'Mode' not supported` — the generated
> `builtin` module, which the plan already sequences ahead of the G-goals.
>
> **S3a — comptime STRUCT CONSTANTS** (`ZigLowering.ComptimeAggregate.cs`) had to come first, and
> that was not in the plan. The two things every platform-conditional in std asks are
> `builtin.cpu.arch` (172 uses) and `builtin.os.tag` (92) — both a FIELD READ of a struct constant,
> which S5 left with no comptime domain at all (it folds types exactly, and scalars, but never an
> aggregate VALUE). Without an answer they lower to a runtime comparison and BOTH arms of every
> `if (builtin.cpu.arch == .x86_64)` get lowered — dragging in the inline asm, syscalls and per-arch
> code the branch exists to avoid. A platform query has to fold or the module graph cannot walk std.
> So: a `const` RHS that is a struct literal records a field map (a SIDE EFFECT — the runtime decl
> still emits), a field read folds to a tag or a scalar, and `LowerIfStmt` gained a comptime-condition
> fold covering `==`/`!=` on tags, a module-exported bool, and `and`/`or`/`!` over those.
>
> **S3b — the synthetic modules** (`ZigSyntheticModules.cs`) are generated as ZIG SOURCE TEXT and fed
> through the ordinary module path, exactly as the plan prescribed: no special lowering, no second
> navigation rule, and an unprovided member fails with the same "no such declaration" a file module
> gives. They are **duck-typed** — bare enum literals and anonymous struct literals — because real
> zig's `builtin.zig` is only ~55 lines but every line is typed against `std.Target`, which is
> `Target.zig` (3,824 lines) plus `Target/`'s per-architecture feature tables (33,488 lines),
> all describing hardware dotcc does not target. `root` is EMPTY on purpose: std probes it for
> optional overrides, and a program that declares none is what an empty module describes.
>
> **One regression, caught by the real-std oracles and worth remembering.** The first cut also folded
> a module-qualified constant inside `EvalComptimeValue` — which runs during pass 0 of module
> PREPARATION. Recording a `const` then resolved a module path as a side effect, preparing the target
> module eagerly: precisely the fan-out S2's laziness exists to prevent ("std.zig's 66 re-exports
> don't fan out at prepare time"). Seven real-std oracle programs failed. The fold belongs at LOWERING
> time only — `LowerExpr`'s field case and the comptime-condition fold — where resolving another
> module is exactly what is wanted. **Prepare-time recording must stay pure of resolution.**
>
> **Two target choices are load-bearing**, both the plan's: `link_libc = true` (biases std toward
> libc-backed paths, which land on `extern fn`s dotcc's runtime implements) and `mode = .ReleaseFast`
> (dotcc does not trap integer overflow, so claiming a safe mode would have std emit checks dotcc does
> not honour). The second DIVERGES from `zig build-exe`'s `.Debug` default — found by the differential,
> which is why the oracle program reads every other member and not that one. `abi` was corrected from
> a guessed `.msvc` to `.gnu` on Windows once `zig env` showed real zig's own host triple.
>
> **Cuts (all loud):** the `std.Target` METHODS (`cpu.has(…)` ×19, `target.isGnuLibC()`,
> `ptrBitWidth()` — ~30 call sites), each needing a real type to hang on; a same-file struct
> constant's field read (only MODULE-QUALIFIED folds — see the scope note in ZIG-SUPPORT.md);
> `@hasDecl(root, …)`, which needs `@hasDecl` over a module rather than a container type.
>
> Validation: 11 emit pins + 1 zig-oracle program (`builtin_target_queries`, version-stable and
> differentially verified) + `examples/zig-target-builtin/` — two lines, exit 42, byte-identical to
> real zig 0.17.0-dev.667, with ZERO `if` statements surviving in `main`.
>
> **★ The G3 road, re-measured.** G3's bullet below says "`std.fmt` scalar formatting", written when
> the format loop lived there. In the pinned zig it does NOT: `std/fmt.zig` keeps only the comptime
> `Placeholder`/`Parser` types, and the `{}`-placeholder driver moved to
> **`std/Io/Writer.zig:616` `pub fn print(w: *Writer, comptime fmt, args)`** (2,955 lines, importing
> `File`, `Limit` and `ArrayList`). Reading it, the remaining bricks are:
> 1. **the W4 lift** — with S3 done, probing `std.fmt` now walls at `zig type: CallArgs`, a
>    type-position CALL in std/fmt.zig's own declarations. This is W4's "a type-returning body must
>    `return struct {…}`" cut, the same one S7's measurement pointed at (44 bodies return a
>    delegating call, 20 a `switch`/`if`). **This is the next brick, and it is shared with G4.**
> 2. **comptime slice/string VALUES** — `Writer.print` keeps `comptime var literal: []const u8 = ""`
>    and grows it with `++` across an unrolled loop, and slices the format string `fmt[a..b]`.
> 3. **`inline while (true)` with a comptime `break`** — dotcc's unroller needs a folding condition;
>    this is an unbounded loop whose exit is a comptime-known `break`.
> 4. **a comptime CALL returning a struct** — `std.fmt.Placeholder.parse(&arr)`, read field-wise.
> 5. **the `Writer` itself** — a vtable-shaped struct over a `[]u8` with a `drain` fn pointer. The
>    `.fixed(buf)` form needs no OS, so `bufPrint` is reachable before the S8 platform floor is;
>    `std.debug.print` (stderr) is not.

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

**S3 ✅ DONE (2026-09-06)** — both modules, generated as Zig source and fed through the ordinary
module path exactly as the bullets below prescribe. What the bullets did NOT anticipate is that the
module is useless without **comptime struct constants** (S3a): `builtin.cpu.arch` and
`builtin.os.tag` are field reads, and a platform conditional that does not fold lowers both arms.
Status update above, including the re-measured G3 road.

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

**S5c ✅ DONE (2026-09-04)** — the member LISTS (`field_names`/`field_types`/`field_values`),
`tag_type`, and `@hasField`/`@hasDecl`/`@field`. The pinned zig's parallel-array shape made this
much smaller than the bullets below (written against the older slice-of-structs API) imply — what
remains of them is `decl_names` ordering and `inline for`, which is S6. Status update above.

**S5b ✅ DONE (2026-09-04)** — the declared integer width rides a type binding, so `bits` answers
through a `comptime T: type` param / alias and `u21`/`u32` key distinct instances. Status update above.

**S5a ✅ DONE (2026-09-04)** — the SCALAR half: `@typeInfo(T)` synthesized from `CType`,
the comptime `switch` over it (all three switch positions) with payload capture, and the
exactly-recoverable fields (`signedness`, `child`, `is_const`, `len`, plus `bits` from the
source spelling). Quoted tags fell out for free — `@"…"` was already an S9 lexer brick. See
the 2026-09-04 status update above for the fidelity rule and the cuts. **What remains below
is the AGGREGATE half**, which is the genuinely large part: comptime struct/slice values,
`fields`/`decls`, and the union-with-payload interpreter values.

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

**S6 ✅ DONE (2026-09-04)** — `inline for` over a comptime list, in all four shapes real std
writes: a single list, two lists in PARALLEL (the commonest, and the one grammar production this
brick added), a list alongside its indices, and a `[_]type{…}` literal. The capture binds COMPTIME
(a type into `_typeAliases`, a string/integer into `_comptimeValues`) and is shadow-restored per
copy. `@field` / `@hasField` / `@hasDecl` accept a captured name. Status update above.

The bullets below were written against the older `fields` API and the assumption of a single
operand shape; what actually landed is recorded in the status update. Two of them were already
true when the brick started:
- `inline while` (121 uses) has ridden the same machinery since Milestone T.
- `@field(x, "name")` with a literal name landed in S5c.

Still open here: **`inline for` over a comptime list of AGGREGATES** — which the pinned zig does
not need, since it exposes parallel arrays of scalars rather than a slice of field structs. If a
later zig reverts to `fields: []const StructField`, that is the shape to build.

- This + S5 is precisely what `std.fmt.format`'s per-field loops need.

### S7 — reification builtins + `@compileError` (M)

**S7 ✅ DONE (2026-09-06)** — `@Int` + `@bitSizeOf` (the operand that makes it useful), the
compile-time diagnostics, and a measured cut of the aggregate constructors. Status update above.
The `@compileError` bullet below anticipated a poison value threaded through the folder; what the
language actually specifies is analysis-time firing, which the existing folds already give — the one
place a poison IS needed is the top-level tombstone, and that is what landed.

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

  **Scope measured against the pinned `lib/std/array_list.zig` (2484 lines) + `std.zig:52`,
  2026-07-19 — this is a multi-brick sub-campaign (~8–15 bricks), not a couple of
  increments.** Read the source before sizing it; the blockers in order of weight:
  1. ~~**The returned struct has ~40 METHODS**~~ — **✅ DONE 2026-08-08 (the tentpole).** A reified
     struct now carries `const` members (incl. `const Self = @This();`) and METHODS: each method is
     declared under the mangled container (so it lowers to the ordinary `Container_method` free
     function and call sites bind through `_methods`) with its BODY deferred to a top-level drain —
     the W3a re-entrancy rule, since a reification fires from an arbitrary type position. The drain
     re-applies the reification's comptime seeds so the body's `T` matches its signature's. A
     receiverless method is reachable both through a type ALIAS and directly off the generic call
     (`std.ArrayList(u8).init(…)`), so the `init`/`.empty` constructor idiom works either way; and a
     reified type nests (a `Box(T)` field / return inside `Wrap(T)`, verified against zig). Oracle
     `generic-container-methods` == zig; example `examples/zig-generic-container/`. **Still cut:** a
     nested container member, and a generic / `type`-returning METHOD — the latter is exactly
     `Aligned`'s nested `pub fn SentinelSlice(comptime s: T) type`, so it returns as a G4 blocker.
     (Its other prerequisite, LEFT-TO-RIGHT comptime-param binding — `comptime start: T` typed by an
     earlier `comptime T: type` — **✅ DONE 2026-08-09**: `EvalTypeReturningCall` now resolves
     arguments in the same two phases `InstantiateGeneric` does.)
  2. **`std.ArrayList(T)` is `array_list.Aligned(T, null)`** — a type-returning fn whose
     body returns *another, cross-module* type-returning call. W4 rejects non-struct
     returns (`Non_struct_return_is_rejected` pin). New capability.
  3. **Mutable slices pervade** (`self.items.len += 1`, `self.items.ptr = new_memory.ptr`),
     but `Slice<T>.Len` is `readonly` — the fat pointer is immutable by design. Needs a
     mutable-slice form or an emitter rewrite that reconstructs the slice on field-assign.
  4. ~~**S4d — type-position module-graph fallback**~~ — **✅ DONE 2026-09-03** (see the status update
     above). `LowerType`'s dotted and call cases now fall through to `ResolveModulePath`, so a
     non-curated std TYPE reaches source; a navigated type carries its methods and enum members. The
     curated `StdGenericTypes["std.ArrayList"] → ZigList` peephole still intercepts `std.ArrayList`
     itself by design — retiring that peephole is G4's own step, once blockers 2/3/5 are cleared.
     The original entry, for the record: `LowerType`'s `Zig.CallArgs`/`Zig.Field`
     cases threw instead of falling through to `ResolveModulePath`, and the curated
     peephole intercepted first — so
     `std.ArrayList`/`std.mem.Alignment` could not reach source at all.
     The OPPOSITE direction of the same seam is now closed (2026-08-09): a curated path is never
     navigated, so configuring a std root no longer breaks the curated allocators (they were mutually
     exclusive — 6 oracle programs failed with `DOTCC_ZIG_LIB_DIR` set). The full zig oracle is green
     in BOTH configurations, and an opt-in leg runs WITH the std root so they can't drift apart again.
     S4d is what remains: letting a non-curated std TYPE fall back to source.
  5. Smaller, each bounded: `@memmove` (only `@memcpy` exists), `Allocator.Error!T`
     error-union methods (Milestone X reuse, but `Allocator.Error` set-member navigation is
     new), `comptime sentinel: T` params, `std.atomic.cache_line` const-nav + `comptime_int`
     in `growCapacity`.

  **Already cleared** (the edges, #106–#109): opaque-allocator `resize`/`remap`/
  `alignedAlloc`, `@addWithOverflow` + tuple destructuring, `@max`/`+|`, `@memcpy`, and the
  `Aligned`-shaped type-returning generic with a comptime-optional param (S4b/S4c). The
  `alignment = null` path — which is what `std.ArrayList` *is* — skips
  `a.toByteUnits() == @alignOf(T)`, so `std.mem.Alignment`'s comptime-enum handling is
  largely dodged for the default instantiation.
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
