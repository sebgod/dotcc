# Breaking "the wall" — comptime types / generics / anytype (2026-07-05, Fable)

> Untracked scratch plan, sibling to `fable-zig.md` / `fable-c.md`. Snapshot of
> main at `77dd565` (both prior scratch plans fully exhausted — every
> implementable item shipped; see their status markers).
>
> **The wall** (ZIG-SUPPORT.md § "Why these are out of scope", fable-zig.md
> § "The wall"): comptime TYPES / generics / `anytype` / a real `std`. This plan
> relitigates it deliberately — the original reasoning has gone stale in one
> load-bearing spot, and the ingredients to break the biggest brick have
> accumulated as side effects of other milestones.

## Why the wall is weaker than the docs claim

The out-of-scope reasoning rests on two facts; one no longer holds:

1. ~~"dotcc has no compile-time evaluation engine"~~ — **stale since Milestone T.**
   The `ComptimeInterpreter` (`IrBuilder.Comptime.cs`, shared with the C
   front-end) is a tree-walking interpreter over the typed IR with call frames,
   loops, and an eval-step budget. The doc self-contradicts two sentences later.
   Fix the wording regardless of this plan's fate.
2. "Monomorphization needs an interleaved Sema — a different compiler than
   dotcc's bottom-up pipeline." — **Half true.** A *general* Sema, yes. But
   demand-driven, call-site-keyed instantiation over **retained ASTs** fits
   dotcc's shape: the C front-end already retains parse subtrees per function
   (`FnDefSite`), ZigLowering lowers per-function, and the interpreter provides
   the binding frames. We don't interleave — we **re-enter lowering per
   instantiation, memoized**. C++-template-style, which is exactly what Zig
   generics are (a fresh body per type, instantiated at the call).

Accumulated ingredients (each built for something else):
- ComptimeInterpreter + `comptime var/const` binding/substitution (T).
- `_Generic`'s lowering-time type-matcher on synthesized `CType` (C dividend #3).
- `constexpr`/ConstEval substitution shape (C dividend #4).
- On-the-fly container registration is a *named cut* with a known design
  (in-function containers, fable-zig.md group 4) — exactly the primitive a
  type-returning fn needs.
- `Slice<T>`/`ErrUnion<T>`/`Nullable<T>` prove the runtime side handles C#
  generics cleanly; the curated-paths resolver (ZigMem/ZigAlloc) is the template
  for a curated std tier.
- The zig 0.17 differential oracle validates every step — these are all programs
  real zig compiles natively.

**What can't map to C# generics — and doesn't need to:** a Zig generic body is
*shape-changing* per T (fields, arms, even arity via comptime branches), so we
never emit an open C# generic for USER code — we emit one concrete C# method /
struct per instantiation (`max__i32`, `ArrayList__i32`). C# generics are only
used where WE author the body (curated runtime types, W0).

## Milestones

### W0 — curated `std.ArrayList(T)` (S/M, independent — the appetizer, no wall-breaking needed)

> ✅ **DONE 2026-07-05** (branch `feat/zig-arraylist`). Shipped as planned with ONE
> plan correction: the pinned zig 0.17 has ONLY the **unmanaged** API — `init(alloc)`
> no longer exists (probed: "no member named 'init'"; zig 0.15 re-pointed
> `std.ArrayList` at the unmanaged variant) — so the curated surface is `.empty` +
> per-call allocator (`append(alloc, v)`, `deinit(alloc)`), `pop() → ?T`, `items`,
> `capacity`, `appendSlice`, `clearRetainingCapacity`; the managed API is rejected BY
> NAME with the migration hint. NO grammar change (the type-position call parses via
> `Type → ErrUnion → Suffix → callArgs`); `CType.ZigList(Element)` + `ZigListCall` IR
> (instance methods — a C# struct method on an lvalue mutates in place) + the
> `ZigList<T>` runtime. `capacity`'s VALUE is a documented non-observable (dotcc
> doubles; zig's curve differs — 16 vs 33). GOTCHA: the OOM literal must be
> Int-typed (a UShort LitInt renders `1u` → CS1503 against `ushort oom`).
> Validation: unit 1458/1458 (+12), zig oracle 151/151 LOCALLY incl. the new
> `arraylist` differential (byte-identical), functional 215/0,
> `examples/zig-arraylist/`. Cuts: pointer-to-list receivers, `insert`/`remove`/
> `toOwnedSlice`/… (loud, demand-driven).

`std.ArrayList(i32)` via the curated-paths resolver → a hand-written C# generic
runtime type (`ZigList<T>`, unmanaged T), allocator-bridged like ZigAlloc:
`init(alloc)` stores the fat-pointer allocator, `deinit`/`append`/`appendSlice`/
`pop`/`items` (a `Slice<T>` view)/`clearRetainingCapacity`. No user-code
monomorphization involved — WE own the body, so a real C# generic works (the
`Slice<T>` precedent). Ships the single most-used std generic before the core
ladder even starts; validates allocator-under-generics. Cut: `std.ArrayList`
methods beyond the curated set error loudly (resolver behavior, as today).

### W1 — `type` as a comptime VALUE (M — the foundation)

> ✅ **DONE 2026-07-06** (branch `feat/zig-type-values`). Shipped as a LOWERING-only
> increment — NO grammar change (Zig's "types are values": the `const T = <type>;`
> RHS already parses via `CurlySuffix → Type`, so `const T = i32;` / `*T` / `?T` /
> `[]T` / `@TypeOf(x)` / `std.ArrayList(i32)` all reach lowering as type-former
> Items). A new function-flat `_typeAliases` map is recognized in
> `TryComptimeConstBinding` (which BOTH the top-level pass-0 and the in-function
> `DeclOrComptime` paths call — so a local `const T = @TypeOf(a);` works for free),
> suppressed from a runtime global by `IsComptimeBound`, and resolved by
> `LowerTypeName` ahead of containers/primitives. `@TypeOf(expr)` → the operand's
> synthesized `CType`, unevaluated (lowered into a throwaway hoist buffer). A runtime
> `var t: type` is rejected loudly. **PLAN CORRECTION:** the interpreter `TypeVal`
> variant was NOT added — for W1 a lowering-time type env is sufficient and adding an
> unconsumed interpreter variant would be dead code; `TypeVal` moves to W3, where a
> comptime FUNCTION genuinely computes/returns a type. `LowerPrim` refactored to a
> non-throwing `TryLowerPrim` for the "is this a type name?" probe. Validation: unit
> 1467/1467 (+9 `ZigTypeValueTests`), zig oracle 152/152 (new `type-value`
> differential, byte-identical vs real zig 0.17), functional 215/0,
> `examples/zig-type-values/`. **Known leniency:** `_typeAliases` is function-flat
> (like `_imports`) — a local alias name leaks across functions (accept-more, not a
> miscompile of a valid program); W3's per-instantiation frames formalize scoping.
> **Cut:** top-level `const T = @TypeOf(global)` (globals aren't lowered at pass 0);
> nested type paths (`Container.Inner`).

- Interpreter value domain += `TypeVal` (carries a `CType`, plus the container
  AST handle for W4's struct values).
- `const T = i32;` — a comptime binding whose value is a type; `var x: T = 5;`
  resolves T through the frame at lowering (the constexpr substitution shape).
- Type operators compose over TypeVal: `?T`, `*T`, `[]T`, `[N]T`, `E!T`.
- `@TypeOf(expr)` → the lowered expr's synthesized CType (synthesis exists
  everywhere already; do NOT evaluate the operand — typeof is unevaluated).
- **Grammar probe first**: `type` in type/expr position. Likely a contextual
  seeding (PredefinedTypeNames / TypeNameRewriter precedent — no fake keyword,
  per the standing rule) rather than a new terminal; `comptime T: type` is W3's
  grammar, not W1's.
- Comptime-only discipline: a RUNTIME `var t: type` is illegal in real Zig too —
  reject loudly (oracle-aligned).

### W2 — in-function containers (S/M — the already-named cut, now load-bearing)

> ✅ **DONE 2026-07-06** (branch `feat/zig-in-fn-containers`). Shipped struct-only,
> fields-only. Grammar: ONE new production `Stmt -> ContainerDecl` — conflict-free
> (the `const IDENT = struct/enum/…` split already resolves at the Decl level; the
> shared post-`=` productions resolve it identically under Stmt). Lowering:
> `LowerLocalStruct` registers the layout on the fly during pass-2 body lowering
> under a function-mangled IR name (`<fn>__<P>`) — unique per (function, container),
> so two bodies' like-named-but-differently-shaped locals never collide (RegisterStructType
> is idempotent-by-name, so a self-guard `_localContainers` also rejects a real
> redeclaration loudly). The plain name → mangled type goes into `_containerTypes`
> **shadow-saved** (`_localContainerShadows`) and restored at body exit, so a local
> `Point` does NOT leak over a top-level `Point` in a sibling function (the one flat-map
> leak that WOULD be a miscompile of a valid program — fixed here, a down-payment on
> W3's scoping obligation). Self-referential fields (`next: *P`) resolve (mapping
> installed before the layout). Emits no runtime decl. **V1 cuts (loud):** local
> enum/union (struct only), method / const member in a local struct (need the pass-1
> free-function / container-const machinery). Validation: unit 1473/1473 (+6
> `ZigInFnContainerTests`), zig oracle 153/153 (new `in-fn-struct`, byte-identical vs
> real zig 0.17: `p=3,4 area=42 sum=60`), functional 215/0, `examples/zig-in-fn-struct/`.
> This is the reify-a-struct primitive W4 calls.

`const Point = struct { x: i32, y: i32 };` inside a fn body → register the
container into the module type section ON THE FLY during body lowering (today
only the top-level pre-registration pass exists). Mangle on collision
(`fnname__Point`). Independent of W1; do early. This is the reify-a-struct
primitive W4 calls from inside the interpreter.

### W3 — generic functions: `comptime` params + monomorphization (L — the core)

> ✅ **W3a DONE 2026-07-06** (branch `feat/zig-comptime-params`) — comptime **VALUE**
> params, the monomorphization SPINE. Shipped: grammar `Param -> 'comptime' IDENT ':'
> Type` (one production, conflict-free — leading `comptime` terminal ⇒ disjoint
> FIRST-set). A comptime-value param makes the fn a TEMPLATE (`_genericFns`, retained
> AST); it is NOT lowered in pass 2 (skipped via `AddFnEntry`), its symbol carries only
> the RUNTIME-param signature. A call routes to `InstantiateGeneric`: `ConstEval` each
> comptime arg → resolved value, mangle `fn__value` (= memo key; negatives spell `nV`),
> declare the instance symbol once, enqueue. **Re-entrancy audit CONCLUSION: a DEFERRED
> WORKLIST, not synchronous re-entry** — the per-fn state (temp counters, hoist buffer,
> label/loop stacks, `_currentFn*`, symbol scope) makes mid-body re-entry untenable, so
> instances drain AFTER pass 2 (cursor loop, picks up transitive/recursive appends;
> `MaxInstantiations=1024` backstop) — each body lowers at top level, clean state. Each
> instance seeds its comptime params into `_comptimeVars` (the existing `comptime var`
> mechanism) keyed by a FRESH symbol → `N↦10` and `N↦100` are inherently isolated (the
> **scoping obligation's value-param answer** — symbol-identity keying, no frame stack
> needed). Bonus: **comptime-if folding + dead-code-after-a-comptime-terminator** (gated
> on `_inGenericInstance`) — a comptime-known `if` folds to its taken branch and code
> after a taken `return` is dropped, so a RECURSIVE generic (`fib`) prunes its base case
> and terminates. Validation: unit 1482/1482 (+9 `ZigComptimeParamTests`), zig oracle
> 154/154 (new `comptime-param`, byte-identical vs real zig 0.17:
> `addN10=15…/pow…/fib10=55`), functional 215/0, `examples/zig-comptime-param/`.
> **W3a cuts → W3b inherits:** comptime TYPE params (`comptime T: type` — the signature
> then DEPENDS on T, so it must lower per-instantiation, not at template time — this is
> the big lever); a generic METHOD; a value-dependent type (`[n]u8` — same template-time
> signature limitation); a non-`ConstEval`-able comptime arg (a call needs `comptime f()`);
> a comptime-if in EXPRESSION (ternary) position (folded only as a statement today).
> **The flat-maps→frames scoping refactor is NOT yet done** — value-param isolation came
> for free via symbol-identity keying; it becomes load-bearing at W3b (a `const U = T;`
> body alias in two instances would collide in the function-flat `_typeAliases`).

> ✅ **W3b DONE 2026-07-06** (branch `feat/zig-comptime-type-params`) — comptime **TYPE**
> params, the core generic case. **NO grammar change** — the W3a production `Param ->
> 'comptime' IDENT ':' Type` already parses `comptime T: type` (`type` lexes as a plain
> `Zig.Ident`); W3a merely REJECTED it. Shipped: `ParamInfo` now carries the RAW type AST +
> a `ParamKind` (Runtime / ComptimeValue / ComptimeType) and lowers lazily — because a
> type-param generic's runtime/return types DEPEND on `T`, its signature CANNOT be lowered
> at template time. `DeclareFn` gives such a generic a placeholder symbol (never called
> directly) + retains the template. At a call, `InstantiateGeneric` resolves each type arg
> in the CALLER's env (`LowerType`, so an alias → its aliased type — **keyed by RESOLVED
> type**, `const I=i32; maxOf(I,…) ≡ maxOf(i32,…)`), mangles by the resolved type
> (`MangleType`: `i32`/`u32`/`f64`/`bool`/name/`p_…`), seeds `_typeAliases[T]` SHADOW-SAVED,
> and lowers the concrete per-instance signature (`int maxOf__i32(int,int)` vs `double
> maxOf__f64(double,double)`). The instance body drains from the SAME W3a worklist; the body
> re-seeds `T` (shadow-saved via `_typeAliasShadows`, restored at body exit like W2's
> `_localContainerShadows`), so `@sizeOf(T)` / a local `var x: T` / a cast resolve. **Scoping
> answer:** the plan wanted a general flat-maps→frames refactor; the shadow-save/restore of
> the type-param seed (both around the call-site signature lowering AND around the body) is
> the load-bearing part — because draining is SEQUENTIAL at top level, save/restore isolates
> `T↦i32` from a sibling/nested `T↦f64` with NO global frame stack. A body-local `const U =
> T;` keeps the W1 alias leniency (re-declared per instance, never stale-read across them).
> The broad flat-maps→frames refactor stays deferred (a cleanup, not a correctness gap under
> sequential draining). **W3b cuts (loud) → W4 inherits:** a `type`-RETURNING function
> (`fn Pair(comptime T: type) type` — reified via W2's on-the-fly registration, W4); a
> generic METHOD; a comptime `if (T == i32)` type-comparison in a body (needs interpreter
> type VALUES — deferred from W1); a type-FORMER type argument (`maxOf([]u8, …)` — doesn't
> parse in an argument position). Validation: unit 1488/1488 (+7 `ZigComptimeTypeParamTests`,
> −1 the flipped W3a rejection pin), zig oracle 155/155 (new `comptime-type-param`,
> byte-identical vs real zig 0.17: `max_i32=7 max_f64=2.5` / `add_i64=105 add_f32=3.5` /
> `sz_i32=4 sz_i64=8 sz_f64=8`), functional 215/0, `examples/zig-comptime-type-param/`.

- Grammar: `comptime IDENT : Type` param form (+ `type` as the param type via
  W1's spelling). `anytype` params reserved for W5.
- A call to a fn with comptime params does NOT lower the callee once:
  1. Evaluate the comptime args (types → TypeVal via W1; values → existing
     comptime eval). **Key by RESOLVED value** (i32-via-alias ≡ i32).
  2. Instantiation key `(fn, comptime-arg-tuple)`; memoize. First hit:
     **re-lower the retained fn AST** with the comptime frame pre-seeded
     (T ↦ i32), emit under a deterministic mangled name (`max__i32`;
     nested: `foo__ArrayList__i32`).
  3. Rewrite the call site to the mangled name, comptime args dropped.
- Transitive instantiation via a worklist; **depth cap** (recursive generics);
  diagnostics carry the instantiation trace ("while instantiating max(T=i32)").
- Comptime VALUE params (`comptime n: usize`) ride the same key machinery —
  `fn repeat(comptime n: usize, c: u8)` instantiates per n (cap the key-space
  growth with a friendly error, like the inline-for unroll budget).
- **Re-entrancy audit**: ZigLowering per-fn state (hoist buffer, scope stack,
  temp counters) must save/restore around a nested instantiation — the setjmp
  lesson (post-clone identity) says probe this with a failing test FIRST.
- **Scoping formalization (queued here deliberately — from the 2026-07-06
  compiler review)**: ZigLowering's name→binding maps are FUNCTION-FLAT today
  (`_imports`, `_typeAliases`, `_defaultAllocatorBindings`, `_errorSets`, …) — a
  local binding leaks across functions. Individually each is a documented
  accept-more leniency (never a miscompile of a valid program), but
  monomorphization makes it load-bearing: an instantiation's `T ↦ i32` binding
  MUST NOT leak into or collide with a sibling instantiation's `T ↦ f64`. Fold
  the flat maps into a scoped environment (per-fn / per-instantiation frame
  stack, pushed around each re-lower) as part of W3's frame machinery — it is a
  prerequisite of the re-entrancy audit above, not an optional cleanup.

### W4 — type-returning functions (L — the ArrayList shape)

> ✅ **W4 DONE 2026-07-06** (branch `feat/zig-type-returning-fn`) — type-RETURNING
> functions, the last generative brick. `fn Pair(comptime T: type) type { return struct { a:
> T, b: T }; }` is a COMPTIME type constructor (emits no runtime code); each use in a TYPE
> position REIFIES a fresh struct per resolved type argument (`Pair__i32`, `Pair__f64`),
> memoized. **Plan correction:** the body does NOT need the full ComptimeInterpreter — V1's
> body is a single `return struct {…}`, so reification is a focused lowering-time op (seed the
> type params, register the struct via the W2 primitive), not an interpreter run. The
> interpreter (and `if (T==f32)` comptime branching in the body) moves to a later brick.
> **ONE grammar production:** `Stmt -> 'return' 'struct' '{' FieldDecls '}' ';'` (+ empty) —
> a `struct` is not an `RhsExpr` (kept out deliberately), so `return struct` is a conflict-free
> 1-token shift, exactly like `const IDENT = struct`. Full-built clean. Lowering: `DeclareFn`
> detects `IsTypeKeyword(retType)` → registers a `TypeReturningGenericInfo` (placeholder symbol +
> params + body), never lowered as a runtime fn (`AddFnEntry` skips it, like a generic).
> `EvalTypeReturningCall` (hooked into `LowerType`'s CallArgs/CallNoArgs case, `TryTypeAliasRhs`,
> and a pass-1.5 re-try for top-level `const P = Pair(i32)`): resolves each type arg in the
> caller's env (alias → resolved type ⇒ keyed by RESOLVED type), mangles by `MangleType`, and
> reifies via `RegisterStruct` with the type params seeded (shadow-saved) into `_typeAliases`.
> `@This()` resolves to the in-progress type (`_currentContainer` + the `_containerTypes[mangled]`
> mapping installed BEFORE the fields lower), so a self-referential `?*const @This()` field / a
> recursive `Pair(T)` in the body works; memoized by mangled name. Fields-only (a method / const
> in the returned struct is a loud cut, like W2). **Ordering:** a type fn used in another fn's
> SIGNATURE must be declared earlier (pass-1 signature lowering is source-order); bodies (pass 2)
> and top-level aliases (pass-1.5 re-try) work regardless — a documented cut. **W4 cuts (loud) →
> later:** a method / const member in the returned struct; a non-struct return (bare `T`, enum /
> union, type-former); a multi-statement / comptime-`if`-branching body (the interpreter case);
> a runtime / comptime-VALUE parameter on the type fn. Validation: unit 1494/1494 (+6
> `ZigTypeReturningFnTests`), zig oracle 156/156 (new `type-returning-fn`, byte-identical vs real
> zig 0.17: `pi=3,4 pf=1.5,2.5` / `head=10 tail=20`), functional 215/0,
> `examples/zig-type-returning-fn/`.

`fn Pair(comptime T: type) type { return struct { a: T, b: T }; }` —
`const P = Pair(i32);` runs the body IN THE INTERPRETER at lowering time
(existing frames + step budget); `return struct {…}` reifies via W2's on-the-fly
registration → TypeVal, memoized per key (`Pair__i32`). Methods declared inside
the returned container keep their ASTs and instantiate via W3 when called
(`Self` already concrete). Comptime branching inside the body (`if (T == f32)`)
falls out of the interpreter for free — that's the shape-changing power C#
generics can't express and monomorphization can.

### W5 — `anytype` (M — cheap after W3)

> ✅ **W5 DONE 2026-07-06** (branch `feat/zig-anytype`) — `anytype` parameters, the
> monomorphization capstone. **NO grammar change** (`anytype` lexes as a bare identifier and
> parses as a `Type` via `Param -> IDENT ':' Type`, exactly like `type` in W1/W3b — it was only
> a comment in the grammar). Shipped as predicted "cheap after W3": a new `ParamKind.AnyType`
> classified in `CollectParamInfos` (a `Zig.Param` whose type is the `anytype` keyword), routed
> through the SAME W3b per-instantiation branch in `DeclareFn` (`hasTypeParam || hasAnyType` →
> placeholder symbol + `_genericFns` template). The one genuinely NEW idea is the HYBRID nature:
> an anytype param is a monomorphization key AND a runtime slot (a comptime TYPE arg is neither —
> it's a compile-time-only type spelling). In `InstantiateGeneric`: phase 1 INFERS each anytype
> param's type from the actual argument via `InferArgType` (a throwaway-hoist `LowerExpr(arg).Type`,
> mirroring `TypeOfBuiltin` so the real arg lowers once in `BuildCall`); the inferred type is seeded
> (shadow-saved) into a new `_anytypeSeeds` map, mangled into the instance name (`add__i32_i32`), AND
> the arg is added to `runtimeArgItems` + the param to `runtimeParams` with the inferred type. The
> `@TypeOf(param)` signature-resolution problem (the param isn't an in-scope symbol at signature-lowering
> time) is solved by `TypeOfBuiltin` consulting `_anytypeSeeds` FIRST, ahead of its LowerExpr path — so
> a `@TypeOf(a)` return type resolves to the inferred seed. In the instance BODY the param is an ordinary
> runtime symbol of the inferred type (no seed needed), so duck-typed use (`p.x`, `s.len`, arithmetic)
> lowers against the concrete type; a mismatch fails per-instantiation. **Cuts (loud):** a generic METHOD
> (free functions only, like W3); an `anytype` param on an `extern` prototype (rejected in `DeclareExternFn`,
> no C-ABI slot); a `comptime x: anytype` (redundant) is not classified specially. Validation: unit
> 1501/1501 (+7 `ZigAnytypeParamTests`), functional 215/0, `examples/zig-anytype/`; the `anytype-param`
> zig-oracle (opt-in; hand-checked valid zig 0.17, dotcc byte-output `add_i=7 add_f=4.0` / `max_i=7
> max_f=4.5` / `x=42 len=4`) runs in CI. **GOTCHA (the recurring one):** the oracle program must be
> valid in BOTH — `@as`-type the args and `@intCast` the usize→i32.

Per-call-site inference: for each anytype param, T := @TypeOf(actual arg), then
the W3 key machinery. Duck-typed member access in the body lowers against the
concrete type; a miss errors PER-INSTANTIATION with the trace — exactly real
Zig's (and C++ templates') behavior, oracle-aligned.

### W6 — curated `std.debug.print` / basic `std.fmt` (M — separable any time)

> ✅ **W6 DONE 2026-07-06** (branch `feat/zig-debug-print`) — curated `std.debug.print`, the last brick.
> `std.debug.print("{d} {s}\n", .{n, s})` writes to STDERR (as real Zig does). NO grammar change — it's
> a curated method-call path in `LowerMethodCall` (like the `std.mem` helpers), hooked when
> `TryResolveStdPath(fld.Arg0) == "std.debug"`. The comptime format literal is parsed AT LOWERING TIME
> (`TranslateZigDebugFormat`) walking the ESCAPED lexeme so backslash escapes pass through untouched:
> `{{`/`}}` → literal braces, a literal `%` → `%%`, and each `{spec}` → the C conversion, pairing
> positionally with the tuple elements (extracted from the `.{…}` `AnonStructInit`). Lowers to
> `new Call("fprintf", [stderrRef, fmtLitStr, args…])` — the backend's existing printf-family fluent path
> emits `fprintf(stderr, "…").Arg(…).Done()`. **KEY INSIGHT (kept the format translation trivial):** the
> runtime `PrintfBuilder` keys formatting off the actual `.Arg(…)` overload (int/long/ulong/byte*), NOT the
> length modifier — so `{d}`/`{}` → `%d` for EVERY integer width (an i64 `{d}` prints the full 64-bit
> value), `{x}`/`{X}`→`%x`/`%X`, `{c}`→`%c`, `{s}`→`%s`; no `%lld`/`%zu` bookkeeping needed. `stderr` is a
> synthetic `VarRef` to a `FromSystemHeader` Symbol (TargetName `stderr`, rendered verbatim, surfaced by
> `using static Libc`, routed to `Console.Error` by the runtime `WriterFor`). **HARNESS CHANGE (stderr was
> the real design constraint):** `std.debug.print` → stderr, but the oracle discarded zig's stderr and the
> functional harness captured only `Console.Out` — so an stdout assertion would pass VACUOUSLY. Added
> `FixtureRunner.CompileAndRunCapturingStreams` (captures `Console.Error` too) + `ZigOracle.CompileAndRunStreams`
> (returns the binary's stderr); `Dotcc_matches_zig` now compares the COMBINED `stdout+stderr` (each captured
> separately, concatenated in a fixed order → order-stable; no oracle program mixes the two). All existing
> cases have empty stderr, so combined == stdout == their expected. **Cuts (loud):** a float / bool / slice /
> struct arg (Zig's decimal-float / true-false / .len-bytes formatting can't match a C conversion
> byte-for-byte); a width/named specifier (`{d:0>5}`) / `{any}`; a non-literal format; a non-`print`
> `std.debug` member. Validation: unit 1510/1510 (+9 `ZigDebugPrintTests`), functional 215/0,
> `examples/zig-debug-print/`; the `debug-print` zig-oracle (hand-checked valid zig 0.17, dotcc stderr
> byte-output `hello world! n=42 big=5000000000` / `hex=ff up=FF char=A` / `point {x=3, y=7} pct=100%`)
> runs in CI. **The entire planned arc W0–W6 is now COMPLETE.**

The biggest remaining std idiom: `std.debug.print("{d} {s}\n", .{n, s})`.
No reflection needed (AOT rule): dotcc parses the format string AT LOWERING TIME
(the printf-builder precedent) and pairs placeholders with the tuple elements
positionally (tuple types + arity>7 already shipped). Curated subset: `{}`,
`{d}`, `{s}`, `{c}`, `{x}`, `{any}` on scalars/slices; the rest errors loudly.
Doesn't need W1–W5 (the tuple is right there in the call) — placed late only
because W0 covers more urgent ground.

## Sequencing

```
W0 ✅ (appetizer, independent) ────────────────────────────┐
W1 type-values ✅ → W2 in-fn containers ✅ → W3a value-comptime params ✅ → W3b type params ✅ → W4 type-returning fns ✅ → W5 anytype ✅
W6 std.debug.print ✅ (independent) ────────────────────────┘
```

**★ The entire planned arc W0–W6 is COMPLETE (all merged to main).** What's left is only
deliberate, non-arc work: widening the curated `std` set on demand (behind the same
curated-paths resolver), and the permanent exclusions below.

Each W = one lalr-feature-loop increment (own branch/PR, three-legged
validation, oracle programs hand-checked against zig 0.17 rules before push).
W3 was the risk center — SPLIT (as pre-authorized): **W3a (value-comptime params)
SHIPPED** with the full monomorphization spine (worklist, memoization, mangling,
call-site rewrite, comptime-if folding); the re-entrancy audit concluded a deferred
worklist (no synchronous re-entry). **W3a + W3b SHIPPED** — the full monomorphization
spine plus the harder type-param half (per-instantiation signatures, keyed by resolved
type; the type-param seed is shadow-saved around both the call-site signature lowering and
the instance body, which — because draining is sequential at top level — isolates `T↦i32`
from a sibling/nested `T↦f64` without a general frame-stack refactor). **W4
(type-returning functions) SHIPPED** — `fn Pair(comptime T: type) type { return struct {…}; }`
reifies a fresh struct per resolved type argument via W2's on-the-fly registration (a
plan correction: V1's single-`return struct` body needs no interpreter run, just seeded
lowering-time reification — the interpreter + comptime-`if` branching moves to a later
brick). **W5 (`anytype`) SHIPPED** — as predicted "cheap after W3": no grammar change, a new
`ParamKind.AnyType` routed through the W3b per-instantiation branch, the one new idea being the
HYBRID nature (a monomorphization key AND a runtime slot; the type inferred via `@TypeOf(arg)`,
seeded into `_anytypeSeeds` so a `@TypeOf(param)` signature resolves, the arg still passed at
runtime). **W6 (`std.debug.print`) SHIPPED** — a curated method-call path (no grammar change), the
comptime format parsed at lowering time and lowered to a stderr `fprintf` over the existing
printf-builder; the format translation stayed trivial because the runtime builder keys off the
`.Arg(…)` overload, not the length modifier. The stderr target forced a small harness change
(capture + compare `Console.Error` / the binary's stderr, folded into the combined-output
comparison). **The entire planned arc W0–W6 is now COMPLETE and merged to main.** What remains is
only deliberate non-arc work: widening the curated `std` set on demand, and the permanent
exclusions below.

## What stays behind the wall (do not relitigate — the reasoning still holds)

- **`async`/`suspend`** — managed-target root (caller-owned `@Frame` vs .NET
  scheduler), AND removed from pinned zig 0.17 (the oracle literally cannot
  validate it).
- **Inline `asm`** — both backends target a VM; untranslatable by construction.
- **`std` — three honest tiers, NOT one flat "curated forever"** (corrected 2026-07-06;
  full reasoning in ZIG-SUPPORT.md § "Why these are out"): (1) **leaf std** (`std.mem`,
  `std.math`, `ArrayList`) is ordinary Zig and can increasingly be *compiled from upstream
  source* as the front-end fills in — curation (`ZigList<T>`, the `std.mem` helpers, W6's
  format parse) is a *shortcut*, not a language wall; (2) **std's comptime-reflection codegen**
  (`switch (@typeInfo(T))` that `std.fmt`/`std.meta` lean on) is the one real *unbuilt* brick —
  buildable on the Milestone-T interpreter, just not built; (3) **std's platform floor**
  (`std.os`/`std.fs`/`std.Thread` = syscalls + inline `asm`) is the ONLY permanent part — a
  managed target has no syscall surface, so it must be *redirected* onto the BCL like C's libc
  already is, not lowered.
- **Safe-mode overflow traps on plain `+`** — implementable (C# `checked`) but a
  semantics/perf FLIP, not a feature add; separate decision if ever.
- `@typeInfo` full reflection / `usingnamespace` — narrow subsets only on demonstrated
  demand (this IS tier (2) above — the unbuilt-but-buildable brick).

## Doc debt to pay as bricks fall

- ZIG-SUPPORT.md § "Why these are out of scope": fix the stale "no compile-time
  evaluation engine" line NOW (pre-W1); shrink the wall section per milestone
  until only the managed-target root remains.
- fable-zig.md § "The wall (do not relitigate)" — superseded by this file.
