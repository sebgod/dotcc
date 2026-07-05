#nullable enable

using System.Collections.Generic;

namespace DotCC.Ir;

// ---- operators ----------------------------------------------------------

/// <summary>Unary operators. The C# backend (<c>CSharpBackend</c>) maps each to its C# form.</summary>
public enum UnOp { Plus, Neg, BitNot, LogNot, PreInc, PreDec, PostInc, PostDec, AddrOf, Deref }

/// <summary>Binary operators (also reused as compound-assignment operators —
/// <c>+=</c> is <see cref="BinOp.Add"/>).</summary>
public enum BinOp
{
    Add, Sub, Mul, Div, Mod,
    Shl, Shr, BitAnd, BitOr, BitXor,
    Lt, Gt, Le, Ge, Eq, Ne,
    LogAnd, LogOr,
}

// ---- expressions --------------------------------------------------------

/// <summary>Base of the typed expression IR. Every expression carries its
/// synthesized <see cref="Type"/> (the half-built "type-synthesis layer" of the
/// legacy emitter, now first-class) and a source <see cref="Pos"/>.</summary>
public abstract record CExpr
{
    public required CType Type { get; init; }
    public SrcPos Pos { get; init; }
    /// <summary>True for an assignable lvalue (drives <c>&amp;</c>, assignment targets).</summary>
    public bool IsLValue { get; init; }
}

/// <summary>An integer constant. <see cref="Digits"/> is the target-neutral
/// numeric core — the source digits with octal normalised to decimal and the
/// suffix stripped; the backend re-adds a target suffix from <see cref="CExpr.Type"/>
/// via <see cref="ITarget.RenderIntLit"/>. <see cref="Value"/> is the folded value
/// when it fits a long (for const-expression contexts).</summary>
public sealed record LitInt(string Digits, long? Value) : CExpr;

/// <summary>A boolean literal — the Zig front-end's <c>true</c>/<c>false</c> (C has no bool
/// literal node; its <c>true</c>/<c>false</c> are <c>&lt;stdbool.h&gt;</c> macros → 1/0). The
/// backend renders it as C# <c>true</c>/<c>false</c>; its <see cref="CExpr.Type"/> is
/// <see cref="CType.Bool"/> (→ the store-normalising <c>CBool</c>, which takes a C# <c>bool</c>).</summary>
public sealed record LitBool(bool Value) : CExpr;

/// <summary>A floating constant. <see cref="Text"/> is the target-neutral decimal
/// spelling (a hex-float normalised to round-trippable decimal; a long-double
/// suffix dropped, an <c>f</c> kept); the backend emits it via
/// <see cref="ITarget.RenderFloatLit"/>.</summary>
public sealed record LitFloat(string Text) : CExpr;

/// <summary>A string literal — the raw adjacent quoted C segments (e.g.
/// <c>["\"a\\n\"", "\"b\""]</c>), NOT yet encoded. The backend decodes the C
/// escapes and emits its own representation (the C# backend: <c>Libc.L("…"u8)</c>
/// or a byte-array); keeping the IR free of target text is what lets a different
/// backend lower the same literal differently.</summary>
public sealed record LitStr(IReadOnlyList<string> Segments) : CExpr;

/// <summary>A C11 <c>char16_t</c> string literal (<c>u"…"</c>) — the raw adjacent
/// quoted segments with the <c>u</c> prefix already stripped (so each is a plain
/// <c>"…"</c> lexeme). Decays to <c>char16_t*</c> (C# <c>char*</c>). The backend
/// lowers it to a pooled, pinned <c>Libc.L16("…")</c> pointer over UTF-16 data —
/// the 16-bit-code-unit sibling of <see cref="LitStr"/>.</summary>
public sealed record LitU16Str(IReadOnlyList<string> Segments) : CExpr;

/// <summary>A C11 <c>char32_t</c> string literal (<c>U"…"</c>) — the raw adjacent
/// quoted segments with the <c>U</c> prefix already stripped. Decays to
/// <c>char32_t*</c> (C# <c>uint*</c>). The backend lowers it to a pooled, pinned
/// <c>Libc.L32("…")</c> pointer over UTF-32 data — the 32-bit-code-unit sibling of
/// <see cref="LitU16Str"/> (one code unit per Unicode scalar).</summary>
public sealed record LitU32Str(IReadOnlyList<string> Segments) : CExpr;

/// <summary>A reference to a resolved variable / parameter / function.</summary>
public sealed record VarRef(Symbol Sym) : CExpr;

/// <summary>A reference to an enumerator of a real (named) C# enum — renders as
/// <c>EnumName.Member</c>. <see cref="Symbol.ConstValue"/> carries the integer
/// value for constant-expression contexts (array bounds, case labels). An
/// enumerator of an anonymous, un-typedef'd enum has no enum type, so the builder
/// emits a plain <see cref="LitInt"/> for it instead.</summary>
public sealed record EnumConstRef(Symbol Sym) : CExpr;

/// <summary>A unary operation.</summary>
public sealed record Unary(UnOp Op, CExpr Operand) : CExpr;

/// <summary>A binary operation.</summary>
public sealed record Binary(BinOp Op, CExpr Left, CExpr Right) : CExpr;

/// <summary>An assignment. <see cref="CompoundOp"/> is null for a plain
/// <c>=</c>, or the arithmetic/bitwise op for a compound assignment (<c>+=</c>).</summary>
public sealed record Assign(BinOp? CompoundOp, CExpr Target, CExpr Value) : CExpr;

/// <summary>A function call by name (a named function or libc builtin).
/// <see cref="ParamTypes"/> is the resolved callee's fixed-parameter types (null
/// when the callee has no known signature), used by the backend to coerce each
/// argument to its parameter type — C's implicit conversion at a call, which C#
/// requires made explicit. How a particular libc name renders (e.g. printf's
/// fluent form) is the backend's decision, keyed off <see cref="Callee"/>.
/// <see cref="CalleeSym"/> is the resolved callee symbol when the call binds to a
/// user function (null for libc builtins / unresolved names); the backend emits
/// its <see cref="Symbol.TargetName"/> so a TU-local <c>static</c> renamed out of
/// the way (internal-linkage collision with an external of the same name) is
/// called under its renamed identifier rather than the raw C name.</summary>
public sealed record Call(string Callee, IReadOnlyList<CExpr> Args,
    IReadOnlyList<CType>? ParamTypes = null, Symbol? CalleeSym = null) : CExpr;

/// <summary>A call through a computed function-pointer expression — <c>(*fp)(x)</c>,
/// <c>tbl[i](x)</c>, <c>s.fn(x)</c>. (A call of a named function or fn-ptr
/// variable uses <see cref="Call"/> instead.)</summary>
public sealed record IndirectCall(CExpr Callee, IReadOnlyList<CExpr> Args) : CExpr;

/// <summary>A cast (explicit or inserted by a coercion pass).</summary>
public sealed record Cast(CType Target, CExpr Operand) : CExpr;

/// <summary>Zig <c>@bitCast(x)</c> — reinterpret the bit pattern of <paramref name="Operand"/>
/// as <paramref name="Target"/> (same byte size; e.g. <c>f32</c>↔<c>u32</c>). Distinct from
/// <see cref="Cast"/> (a value conversion): codegen emits
/// <c>System.Runtime.CompilerServices.Unsafe.BitCast&lt;TFrom, TTo&gt;</c>, which is AOT-clean
/// and verifies the size match. Zig-only (the C front-end never produces it); the wat backend,
/// which Zig never targets, leaves it on the default throw like <c>Slice</c>/<c>ErrorUnion</c>.</summary>
public sealed record BitCast(CType Target, CExpr Operand) : CExpr;

/// <summary>A conditional (ternary) expression <c>c ? a : b</c>. Codegen wraps
/// the condition in <c>Cond.B(...)</c> for C-truthy semantics.</summary>
public sealed record CondExpr(CExpr Cond, CExpr Then, CExpr Else) : CExpr;

/// <summary>A C# switch EXPRESSION — <c>subject switch { labels =&gt; value, …, _ =&gt; value }</c>
/// — the lowering of Zig's switch-as-a-value (Milestone L). Each arm's <see cref="SwitchExprArm.Labels"/>
/// are constant patterns (rendered joined with <c>or</c>); a null-label arm is the <c>_</c> default
/// (Zig's <c>else</c>). Distinct from the <see cref="Switch"/> STATEMENT (no fall-through, yields a
/// value at the result location). Codegen self-parenthesizes (like <see cref="CondExpr"/>) and
/// coerces each arm to the result <see cref="CExpr.Type"/>. Zig-only.</summary>
public sealed record SwitchExpr(CExpr Subject, IReadOnlyList<SwitchExprArm> Arms) : CExpr;

/// <summary>One arm of a <see cref="SwitchExpr"/>: pattern <paramref name="Labels"/>
/// (null = the <c>_</c> default) yielding <paramref name="Value"/>. A label with a non-null
/// <see cref="SwitchLabel.HiExpr"/> is an inclusive range (Zig <c>lo...hi</c>) → a relational
/// pattern; a single-value label is a constant pattern.</summary>
public sealed record SwitchExprArm(IReadOnlyList<SwitchLabel>? Labels, CExpr Value);

/// <summary>An array subscript <c>base[index]</c> — an lvalue.</summary>
public sealed record Index(CExpr Base, CExpr Idx) : CExpr;

/// <summary>A struct/union member access — <c>base.field</c> (<see cref="Arrow"/>
/// false) or <c>base-&gt;field</c> (true). Both forms are legal C# in the unsafe
/// context user code lives in, so codegen emits the operator verbatim.</summary>
public sealed record Member(CExpr Base, string Field, bool Arrow) : CExpr;

/// <summary>A comma-separated expression sequence used in <c>for</c>-init /
/// <c>for</c>-update position (<c>i = 0, j = n</c>) — C# accepts the same list
/// there.</summary>
public sealed record CommaSeq(IReadOnlyList<CExpr> Items) : CExpr;

/// <summary>The C comma operator <c>e1, e2, …, eN</c> in value context: every
/// operand is evaluated left-to-right and all but the last are discarded; the
/// value and type are the last operand's. C# has no comma operator, so codegen
/// picks a lowering by position: a statement-level comma becomes one statement
/// per operand; a value-context comma becomes a left-to-right ValueTuple whose
/// <c>.ItemN</c> picks the last (or, when a leading operand is <c>void</c> — a
/// void call or a <c>(void)x</c> discard — an immediately-invoked delegate, the
/// only form that keeps the side effects lazy inside a short-circuit).</summary>
public sealed record CommaOp(IReadOnlyList<CExpr> Items) : CExpr;

/// <summary><c>sizeof</c> — of a type (<c>sizeof(int)</c>) or, when synthesized
/// from <c>sizeof expr</c>, of the operand's type. Codegen prints C#'s
/// <c>sizeof(T)</c> for a scalar/struct, or <c>count * sizeof(elem)</c> for an
/// array (which lowered to a pointer, so C#'s <c>sizeof</c> would be wrong).</summary>
public sealed record SizeOfExpr(CType Of) : CExpr;

/// <summary><c>offsetof(T, member-designator)</c> — the byte offset of the member
/// within struct/union <paramref name="StructType"/>. Codegen computes it via the
/// address-through-a-null-pointer idiom (<c>(nint)&amp;((T*)null)-&gt;m</c>), so it
/// respects the real .NET blittable layout (alignment included).
/// <see cref="Path"/> is the designator as segments — one field name in the
/// common case, or a dotted walk into nested members (C99 7.17:
/// <c>offsetof(struct S, value.type.f)</c>, chibi's <c>sexp_offsetof</c>).
/// <see cref="MemberType"/> is the FINAL member's declared type (null if the
/// struct/field isn't modelled) — a neutral fact from which the backend decides
/// rendering: an array member that lowers to a C# <c>fixed</c> buffer already
/// evaluates to its own address, so the backend omits the <c>&amp;</c> (taking it
/// would be CS0211).</summary>
public sealed record OffsetOf(CType StructType, IReadOnlyList<string> Path, CType? MemberType) : CExpr;

/// <summary>A positional struct/union aggregate initializer — lowered from
/// <c>struct Point p = {3, 4}</c>. Codegen emits a C# object initializer
/// <c>new Point { x = 3, y = 4 }</c>; <see cref="Members"/> pairs each supplied
/// field (in declaration order) with its value. Fields the brace list doesn't
/// reach are omitted, so they take C#'s zero default — exactly C's partial-init
/// rule. A nested brace over a struct/union-typed field is itself a
/// <see cref="StructInit"/>.</summary>
public sealed record StructInit(IReadOnlyList<FieldInit> Members) : CExpr;

/// <summary>One member of a <see cref="StructInit"/>: the field name, its
/// declared <see cref="FieldType"/> (so codegen coerces the value as C would at
/// the store), and the value expression.</summary>
public readonly record struct FieldInit(string Name, CType FieldType, CExpr Value);

/// <summary>An array aggregate as a value — a C99 array compound literal
/// (<c>(int[]){1,2,3}</c>) or any array initializer. Codegen lowers it to a C#
/// <c>stackalloc T[]{ … }</c>, valid in initializer position (a stackalloc can't
/// escape to a pointer elsewhere in C#). <see cref="Elems"/> is the dense element
/// list the builder already computed (designators resolved, dimensions
/// zero-filled).</summary>
public sealed record StackArray(CType Element, IReadOnlyList<CExpr> Elems) : CExpr;

/// <summary>A file-scope / static-local array's backing store — a pinned, rooted
/// managed array exposed as a stable <c>T*</c> (the <c>Libc.GlobalArray*</c>
/// helpers). Unlike <see cref="StackArray"/> (a block-local <c>stackalloc</c>),
/// this persists for the program lifetime. <see cref="Elems"/> is the dense
/// initializer (null for a zeroed array, where <see cref="Count"/> gives the
/// length). Codegen picks the helper by element kind: a pointer / function-pointer
/// element can't be a C# generic type argument, so it round-trips through a pinned
/// <c>nint[]</c> reinterpreted as <c>T**</c>.</summary>
public sealed record PinnedArray(CType Element, IReadOnlyList<CExpr>? Elems, CExpr? Count) : CExpr;

/// <summary>A Zig array-by-value return (the Milestone K "array-by-value return" cut, made
/// sound). A function returning a fixed array <c>[N]T</c> decays its return type to <c>T*</c> in
/// the emitted C# signature, but <c>return t;</c> of a <c>stackalloc</c>'d array local would hand
/// back a pointer into the callee's dead frame (a dangling pointer) — yet Zig arrays are VALUE
/// types, so the caller must receive a copy that outlives the call. Codegen copies the
/// <see cref="Count"/> elements of <see cref="Source"/> (a <c>T*</c>) into a heap-owned buffer and
/// returns the pointer (<c>ZigAlloc.CopyArrayResult&lt;T&gt;(src, n)</c>). <see cref="Element"/> is
/// the element type. <see cref="CExpr.Type"/> is the array type, so the return coercion is a no-op.
/// V1: the buffer is NOT freed (leaked — sound values, unfreed); a caller-allocated result pointer
/// (sret) would avoid the leak but rewrites the call ABI. Zig-lowering / C#-target only (the C
/// front-end never produces it; the wat backend, which Zig never targets, leaves it on the
/// default throw like <c>SliceNew</c>).</summary>
public sealed record ArrayByValReturn(CExpr Source, CType Element, int Count) : CExpr;

/// <summary>A curated <c>std.mem</c> helper or <c>@memcpy</c>/<c>@memset</c> mem-builtin, rendered
/// as <c>ZigMem.{Method}&lt;{Element}&gt;(args)</c> (the runtime <see cref="Libc.ZigMem"/> class,
/// auto-spliced like <see cref="Libc.ZigAlloc"/>). The BACKEND supplies the C# element-type name
/// from <see cref="Element"/> (the frontend renders no C# types) — the same shape
/// <see cref="AllocCall"/> / <see cref="ArrayByValReturn"/> use for their generic runtime calls.
/// One node covers <c>eql</c> (→ <see cref="CExpr.Type"/> <c>bool</c>), <c>copyForwards</c> /
/// <c>@memcpy</c> / <c>@memset</c> (→ <c>void</c>), and <c>span</c> (→ a <c>[]const T</c> slice).
/// Zig-lowering / C#-target only (the C front-end never produces it; the wat backend, which Zig
/// never targets, leaves it on the default throw like <see cref="SliceNew"/>).</summary>
public sealed record ZigMemCall(string Method, CType Element, IReadOnlyList<CExpr> Args) : CExpr;

/// <summary>The stack-value replacement for a promoted <c>malloc</c> — the
/// malloc→stack peephole rewrites <c>T* p = (T*)malloc(sizeof(T))</c> (used only
/// via <c>-&gt;</c> and freed, never escaping) to <c>T p = new T()</c>. Codegen
/// emits <c>new T()</c> (a zero-initialized struct value).</summary>
public sealed record StackNew(CType StructType) : CExpr;

/// <summary>A C23 empty initializer (<c>{}</c> / <c>(T){}</c>) — a zero value of
/// the carried <see cref="CExpr.Type"/>. Codegen emits <c>default(T)</c>, which
/// zero-fills a scalar, pointer, struct, or union uniformly.</summary>
public sealed record DefaultLit : CExpr;

/// <summary><c>va_arg(ap, T)</c> — pull the next variadic argument of type
/// <see cref="Target"/> from the <see cref="Ap"/> cursor. Codegen lowers it to the
/// matching <c>VaList</c> accessor (<c>(T)ap.Next()</c>, or <c>(T)ap.NextPtr()</c>
/// for a pointer target). Special syntax — its second operand is a type — so it's
/// a dedicated node rather than an ordinary call.</summary>
public sealed record VaArgGet(CExpr Ap, CType Target) : CExpr;

/// <summary>A parenthesized sub-expression — kept so codegen can preserve
/// explicit grouping (precedence-driven parens come later).</summary>
public sealed record Paren(CExpr Inner) : CExpr;

/// <summary>A deferred Zig <c>comptime EXPR</c> value (Milestone T). The inner expression
/// must evaluate to a compile-time constant; the result is spliced into <see cref="Resolved"/>
/// by a post-pass that runs AFTER every function body is lowered — so a <c>comptime fib(10)</c>
/// can interpret its callee, whose body may be defined later or be a forward reference.
/// The backend forwards transparently to <see cref="Resolved"/> (a <see cref="LitInt"/> /
/// <see cref="LitFloat"/> / <see cref="LitBool"/> / aggregate literal). A mutable property —
/// the node is shared by reference in the IR, so resolving it in place patches every use.</summary>
public sealed record ComptimeFold(CExpr Inner) : CExpr
{
    public CExpr? Resolved { get; set; }
}

/// <summary>The null pointer constant (C23 <c>nullptr</c>). A typed node rather
/// than a literal so the backend spells it per target (the C# backend: <c>null</c>).</summary>
public sealed record NullPtr : CExpr;

/// <summary>A Zig <c>a orelse b</c> over a VALUE optional (<c>?T</c> → C# <c>T?</c>):
/// lowers to C#'s null-coalescing <c>a ?? b</c> — single-evaluation of the left, lazy
/// right, exactly Zig's <c>orelse</c> semantics. (An optional-POINTER <c>orelse</c> uses
/// a <see cref="CondExpr"/> instead — C# <c>??</c> doesn't apply to pointer types.) The C
/// front-end never produces this; it is a Zig-lowering / C#-target construct.</summary>
public sealed record NullCoalesce(CExpr Left, CExpr Right) : CExpr;

/// <summary>A Zig <c>return e;</c> in a <c>!T</c> function, lowered to a success error
/// union — <c>ErrUnion&lt;T&gt;.Ok(payload)</c>. <see cref="Payload"/> is null for a
/// <c>!void</c> success (<c>return;</c> / fall-off → <c>ErrUnion&lt;Unit&gt;.Ok(default)</c>).
/// <see cref="CExpr.Type"/> is the <see cref="CType.ErrorUnion"/> the function returns, so
/// the backend reads the payload type from it. The C front-end never produces this; it is a
/// Zig-lowering / C#-target construct (Milestone B2).</summary>
public sealed record ErrUnionOk(CExpr? Payload) : CExpr;

/// <summary>A Zig <c>return error.Foo;</c>, lowered to an error error union —
/// <c>ErrUnion&lt;T&gt;.Err(code)</c>. <see cref="Code"/> is the error's stable code in the
/// flat global error set (non-zero). <see cref="CExpr.Type"/> is the
/// <see cref="CType.ErrorUnion"/> the function returns. Zig-lowering / C#-target only.</summary>
public sealed record ErrUnionErr(int Code) : CExpr;

/// <summary>A Zig <c>try e</c> — unwrap the payload of the error union <see cref="Inner"/>,
/// or propagate its error by throwing <c>ZigErrorReturn</c> (caught at the enclosing
/// function's emitted boundary). Lowers to <c>ErrUnion.Try(inner)</c>; <see cref="CExpr.Type"/>
/// is the unwrapped payload type. The early-return-out-of-an-expression construct, modeled on
/// the <c>setjmp</c> lowering. Zig-lowering / C#-target only (Milestone B2).</summary>
public sealed record ZigTry(CExpr Inner) : CExpr;

/// <summary>A Zig <c>u catch fallback</c> — the payload of the error union
/// <see cref="Union"/> on success, else <see cref="Fallback"/> (no propagation). Lowers to
/// <c>ErrUnion.Catch(union, fallback)</c>; <see cref="CExpr.Type"/> is the payload type. The
/// lowering only builds this when <see cref="Fallback"/> is side-effect-free, so the eager
/// helper matches Zig's lazy semantics. Zig-lowering / C#-target only (Milestone B2).</summary>
public sealed record ZigCatch(CExpr Union, CExpr Fallback) : CExpr;

/// <summary>A Zig slice value constructed from a <c>{ ptr, len }</c> pair — the result of an
/// array→slice coercion (a string literal / <c>[N]T</c>) or the slicing operator
/// <c>a[lo..hi]</c>. Lowers to <c>new Slice&lt;T&gt;(ptr, len)</c> (or <c>ConstSlice&lt;T&gt;</c>
/// when <see cref="Const"/>). <see cref="Ptr"/> renders to a <c>T*</c>, <see cref="Len"/> to the
/// element count (a <c>ulong</c>); <see cref="Element"/> is the unqualified element type.
/// <see cref="CExpr.Type"/> is the <see cref="CType.Slice"/> produced. The C front-end never
/// produces this; it is a Zig-lowering / C#-target construct (Milestone E).</summary>
public sealed record SliceNew(CExpr Ptr, CExpr Len, CType Element, bool Const) : CExpr;

/// <summary>A Zig allocator <c>a.alloc(T, n)</c> (Milestone F). <see cref="Element"/> is the
/// element type <c>T</c>, <see cref="Count"/> the element count <c>n</c>, <see cref="OomCode"/>
/// the <c>error.OutOfMemory</c> code returned on a failed allocation. <see cref="CExpr.Type"/>
/// is <c>ErrorUnion(Slice(Element))</c> — matching Zig's <c>Error![]T</c> — so it composes with
/// <c>try</c>/<c>catch</c>. <see cref="Receiver"/> null = the DEVIRTUALIZED C-heap default →
/// <c>ZigAlloc.AllocCHeap&lt;T&gt;(n, oom)</c> (a direct <c>Libc.malloc</c>, no vtable); non-null
/// = an opaque/runtime allocator → <c>recv.Alloc&lt;T&gt;(n, oom)</c> (the indirect vtable
/// dispatch inside the runtime <c>Allocator</c>). <see cref="FbaCtx"/> non-null ⇒ a FBA-SITE
/// devirtualization (Milestone U): a provable <c>fba.allocator()</c> result → a direct
/// <c>ZigAlloc.AllocFba&lt;T&gt;(&amp;fba, n, oom)</c> (the <c>&amp;fba</c> context, no vtable);
/// it takes precedence over <see cref="Receiver"/>. Zig-lowering / C#-target only.</summary>
public sealed record AllocCall(CExpr? Receiver, CType Element, CExpr Count, int OomCode, CExpr? FbaCtx = null) : CExpr;

/// <summary>A Zig allocator <c>a.free(slice)</c> (Milestone F). <see cref="SliceExpr"/> is the
/// slice to free, <see cref="Element"/> its element type. <see cref="Receiver"/> null = the
/// DEVIRTUALIZED C-heap default → <c>ZigAlloc.FreeCHeap&lt;T&gt;(slice)</c> (a direct
/// <c>Libc.free</c>); non-null = an opaque/runtime allocator → <c>recv.Free&lt;T&gt;(slice)</c>
/// (indirect). <see cref="FbaCtx"/> non-null ⇒ FBA-site devirt (Milestone U) →
/// <c>ZigAlloc.FreeFba&lt;T&gt;(&amp;fba, slice)</c>, precedence over <see cref="Receiver"/>.
/// <see cref="CExpr.Type"/> is <c>void</c>. Zig-lowering / C#-target only.</summary>
public sealed record FreeCall(CExpr? Receiver, CExpr SliceExpr, CType Element, CExpr? FbaCtx = null) : CExpr;

/// <summary>A Zig allocator <c>a.create(T)</c> (Milestone U) — single-object allocation,
/// Zig's <c>Error!*T</c>. <see cref="Element"/> is <c>T</c>, <see cref="OomCode"/> the
/// <c>error.OutOfMemory</c> code. <see cref="CExpr.Type"/> is <c>ErrorUnion(Pointer(Element))</c>,
/// so it composes with <c>try</c> (which casts the unwrapped <c>nuint</c> back to <c>T*</c>).
/// The runtime carrier is <c>ErrUnion&lt;nuint&gt;</c>: a pointer cannot be an <c>ErrUnion&lt;T&gt;</c>
/// generic argument, so the address rides as a <c>nuint</c>. <see cref="Receiver"/> null = the
/// DEVIRTUALIZED C-heap default → <c>ZigAlloc.CreateCHeap&lt;T&gt;(oom)</c>; non-null = an
/// opaque/runtime allocator → <c>recv.Create&lt;T&gt;(oom)</c>. <see cref="FbaCtx"/> non-null ⇒
/// FBA-site devirt (Milestone U) → <c>ZigAlloc.CreateFba&lt;T&gt;(&amp;fba, oom)</c>, precedence
/// over <see cref="Receiver"/>. Zig-lowering / C#-target only.</summary>
public sealed record CreateCall(CExpr? Receiver, CType Element, int OomCode, CExpr? FbaCtx = null) : CExpr;

/// <summary>A Zig allocator <c>a.destroy(p)</c> (Milestone U) — free a single object from
/// <see cref="CreateCall"/>. <see cref="Ptr"/> is the <c>*T</c> to free, <see cref="Element"/> its
/// pointee type (the byte size at free). <see cref="Receiver"/> null = the DEVIRTUALIZED C-heap
/// default → <c>ZigAlloc.DestroyCHeap&lt;T&gt;(p)</c> (a direct <c>Libc.free</c>); non-null = an
/// opaque/runtime allocator → <c>recv.Destroy&lt;T&gt;(p)</c>. <see cref="FbaCtx"/> non-null ⇒
/// FBA-site devirt (Milestone U) → <c>ZigAlloc.DestroyFba&lt;T&gt;(&amp;fba, p)</c>, precedence
/// over <see cref="Receiver"/>. <see cref="CExpr.Type"/> is <c>void</c>. Zig-lowering / C#-target only.</summary>
public sealed record DestroyCall(CExpr? Receiver, CExpr Ptr, CType Element, CExpr? FbaCtx = null) : CExpr;

/// <summary>A Zig allocator <c>a.realloc(slice, n)</c> (Milestone U) — grow/shrink a slice (may
/// move), Zig's <c>Error![]T</c>. <see cref="OldSlice"/> is the existing slice, <see cref="Element"/>
/// its element type, <see cref="NewCount"/> the new element count, <see cref="OomCode"/> the
/// <c>error.OutOfMemory</c> code. <see cref="CExpr.Type"/> is <c>ErrorUnion(Slice(Element))</c> (so it
/// composes with <c>try</c>/<c>catch</c>, like <see cref="AllocCall"/>). The devirt fork mirrors
/// <see cref="AllocCall"/>: <see cref="FbaCtx"/> non-null ⇒ FBA-devirt (<c>ZigAlloc.ReallocFba</c>);
/// else <see cref="Receiver"/> null ⇒ C-heap-devirt (<c>ZigAlloc.ReallocCHeap</c>, a direct
/// <c>Libc.realloc</c>), non-null ⇒ indirect (<c>recv.Realloc</c>, emulated via the 2-fn vtable).
/// Zig-lowering / C#-target only.</summary>
public sealed record ReallocCall(CExpr? Receiver, CExpr OldSlice, CType Element, CExpr NewCount, int OomCode, CExpr? FbaCtx = null) : CExpr;

/// <summary>A Zig tuple literal <c>.{ a, b, … }</c> (Milestone G), at a tuple sink or with an
/// inferred type. <see cref="Elements"/> are the positional element expressions in order;
/// <see cref="TupleType"/> is the <see cref="CType.Tuple"/> produced (and <see cref="CExpr.Type"/>).
/// The C# backend renders it <c>new System.ValueTuple&lt;T1, …&gt;(e1, …)</c>, coercing each element
/// to its declared element type. Zig-lowering / C#-target only.</summary>
public sealed record TupleNew(IReadOnlyList<CExpr> Elements, CType TupleType) : CExpr;

/// <summary>A Zig tuple index <c>t[N]</c> with a literal <c>N</c> (Milestone G) → the Nth element
/// (zero-based). The C# backend renders it <c><see cref="Tuple"/>.Item{Index+1}</c>;
/// <see cref="Element"/> is the element type (and <see cref="CExpr.Type"/>). Used both for an
/// explicit <c>t[N]</c> and for the per-binder reads a destructure desugars into. Zig-lowering /
/// C#-target only.</summary>
public sealed record TupleIndex(CExpr Tuple, int Index, CType Element) : CExpr;

/// <summary>A bare identifier the binder left unresolved — a runtime/library symbol
/// surfaced by name (the <c>&lt;complex.h&gt;</c> imaginary unit), or an
/// incremental-growth safety net for a name not in any header. Carries the RAW
/// source name; the backend escapes it for emission. (Replaces the old verbatim-C#
/// <c>Raw</c> escape hatch — the IR no longer carries output-language text.)</summary>
public sealed record NameRef(string RawName) : CExpr;

// ---- statements ---------------------------------------------------------

/// <summary>Base of the statement IR.</summary>
public abstract record CStmt
{
    public SrcPos Pos { get; init; }
}

/// <summary>A brace block with its own lexical scope.</summary>
public sealed record Block(IReadOnlyList<CStmt> Stmts) : CStmt;

/// <summary>A flat statement sequence WITHOUT scope braces — one C statement that
/// lowers to several target statements sharing the enclosing scope. Produced by a
/// multi-declarator declaration whose items need different stmt kinds
/// (<c>char *str=NULL, numbuf[LEN];</c> → a <see cref="DeclStmt"/> + an
/// <see cref="ArrayDecl"/>); a <see cref="Block"/> would wrongly scope the names.</summary>
public sealed record Seq(IReadOnlyList<CStmt> Stmts) : CStmt;

/// <summary>One or more local declarations from a single declaration statement
/// (<c>int a = 0, b;</c>).</summary>
public sealed record DeclStmt(IReadOnlyList<LocalDecl> Decls) : CStmt
{
    /// <summary>Set when the C23 <c>[[maybe_unused]]</c> attribute prefixed this
    /// block-scope declaration. The C# backend brackets the emitted local(s) with
    /// <c>#pragma warning disable / restore CS0168</c> (declared, never used) +
    /// <c>CS0219</c> (assigned, never used) — the faithful lowering of C's "don't
    /// warn if this stays unused". An init-only flag (default <c>false</c>) so the
    /// many <c>new DeclStmt(…)</c> temp sites are untouched and rewrite passes that
    /// re-emit with <c>with { … }</c> preserve it. No counterpart on a
    /// function/file-scope declaration — C# never warns on an unused
    /// <c>internal</c>/<c>public</c> member, so <c>[[maybe_unused]]</c> there is a
    /// genuine no-op.</summary>
    public bool MaybeUnused { get; init; }
}

/// <summary>An expression used for its side effects (<c>a = b;</c>, <c>f();</c>).</summary>
public sealed record ExprStmt(CExpr Expr) : CStmt;

public sealed record If(CExpr Cond, CStmt Then, CStmt? Else) : CStmt;
public sealed record While(CExpr Cond, CStmt Body) : CStmt;
public sealed record DoWhile(CStmt Body, CExpr Cond) : CStmt;

/// <summary>A C <c>for</c>. <see cref="Init"/> is a DeclStmt or ExprStmt (or
/// null); <see cref="Cond"/>/<see cref="Post"/> are optional.</summary>
public sealed record For(CStmt? Init, CExpr? Cond, CExpr? Post, CStmt Body) : CStmt;

public sealed record Return(CExpr? Value) : CStmt;
public sealed record Break : CStmt;
public sealed record Continue : CStmt;

/// <summary>A <c>goto label;</c>. <see cref="Label"/> is the RAW C label name; the
/// backend escapes it for emission (C# keyword collisions).</summary>
public sealed record Goto(string Label) : CStmt;

/// <summary>A labeled statement <c>name: body</c> (<see cref="Name"/> is the RAW C
/// label name; the backend escapes it for emission).</summary>
public sealed record Labeled(string Name, CStmt Body) : CStmt;

/// <summary>The desugared form of a recognised <c>setjmp</c>/<c>longjmp</c> guard
/// (<c>if (setjmp(env) [== 0]) THEN [else ELSE]</c>). Real C's "setjmp returns
/// twice" has no structured-control-flow equivalent, so codegen lowers this to
/// <c>env = new LongJmpToken(); try { TryBody } catch (LongJmpException __jmp)
/// when (__jmp.Token == env) { CatchBody }</c>. <see cref="TryBody"/> is the path
/// taken on setjmp's direct (zero) return; <see cref="CatchBody"/> is the longjmp
/// re-entry path (null for the no-recovery swallow shape — Lua's <c>LUAI_TRY</c>).
/// <see cref="Env"/> renders to the <c>jmp_buf</c> lvalue that is freshly armed
/// and matched on, so nested setjmps stay disambiguated by token identity.</summary>
public sealed record SetjmpGuard(CExpr Env, CStmt? TryBody, CStmt? CatchBody) : CStmt;

/// <summary>The desugared form of a VALUE-CAPTURING <c>setjmp</c> — <c>T r =
/// setjmp(env); …rest…</c> / <c>r = setjmp(env); …rest…</c> / <c>switch
/// (setjmp(env)) {…}</c>. Unlike <see cref="SetjmpGuard"/> (an <c>if</c> that only
/// tests zero-vs-nonzero), this preserves the actual value <c>longjmp</c> passes,
/// which the <c>rest</c> (a <c>switch</c>/<c>if</c> on <see cref="Target"/>) branches
/// on. Real C's "setjmp returns twice" is modeled faithfully with goto-restart —
/// the backend emits:
/// <code>
///   Env = new LongJmpToken();
///   __setjmp_Id:
///   try { Body }
///   catch (LongJmpException __jmp) when (__jmp.Token == Env)
///   { Target = (T)__jmp.Value; goto __setjmp_Id; }
/// </code>
/// <see cref="Body"/> runs first with <see cref="Target"/> already reset to 0 (a
/// preceding decl/assignment the builder emits); a matching <c>longjmp</c> is caught,
/// <see cref="Target"/> takes the jump value, and the body re-runs from the label —
/// so its own branch on the value diverges to the recovery path exactly as C resumes
/// after <c>setjmp</c>. The synthetic label is unique via <see cref="Id"/>.</summary>
public sealed record SetjmpCapture(CExpr Env, CExpr Target, CStmt Body, int Id) : CStmt;

/// <summary>A Zig <c>defer</c> / <c>errdefer</c>-guarded region (Milestone H). Produced
/// by <c>ZigLowering</c>'s block restructuring: each <c>defer</c>/<c>errdefer</c> wraps the
/// statements that follow it within its block, so nesting in lexical order yields Zig's LIFO
/// cleanup. The C# backend renders it via the try-precedent of <see cref="SetjmpGuard"/>:
/// <c>defer</c> (<see cref="OnErrorOnly"/> = false) → <c>try { Body } finally { Cleanup }</c>
/// (the finally fires on EVERY exit — fall-through, return, break, continue, throw);
/// <c>errdefer</c> (<see cref="OnErrorOnly"/> = true) → <c>try { Body } catch (ZigErrorReturn)
/// { Cleanup; throw; }</c> (fires only on the propagating-error path, then re-throws to the
/// enclosing <c>!T</c> boundary that converts it to an <c>Err</c> return).</summary>
public sealed record DeferGuard(CStmt Body, CStmt Cleanup, bool OnErrorOnly) : CStmt;

/// <summary>An unconditional <c>throw new ZigErrorReturn(Code);</c> (Milestone H). A Zig
/// <c>return error.X;</c> normally lowers to a DIRECT <see cref="ErrUnionErr"/> return, but a
/// C# <c>catch</c> can't observe a direct return — so when the enclosing function carries an
/// <c>errdefer</c>, the error return is routed through this throw instead, so it propagates
/// through the errdefer <c>catch</c>(es) on the stack (and the <c>!T</c> boundary catch still
/// converts it back to an <c>Err</c>). Flow-terminating, like a C# <c>throw</c>.</summary>
public sealed record ZigErrorThrow(int Code) : CStmt;

/// <summary>A C <c>switch (Subject) { … }</c>, lowered to a C# switch. The body is
/// pre-grouped into <see cref="Sections"/> (the grammar parses <c>case E:</c> /
/// <c>default:</c> as statement-level labels — possibly Duff's-device-nested — so
/// the builder flattens and groups them). C lets a section fall into the next; C#
/// forbids implicit fall-through (CS0163) and a final case falling out (CS8070),
/// so codegen inserts the explicit jump C performs (<c>goto case</c> /
/// <c>goto default</c> / a trailing <c>break</c>) on any section that doesn't
/// already end control flow.</summary>
public sealed record Switch(CExpr Subject, IReadOnlyList<SwitchSection> Sections) : CStmt;

/// <summary>One case section: its (stacked) labels and the statements that follow
/// up to the next label.</summary>
public sealed record SwitchSection(IReadOnlyList<SwitchLabel> Labels, IReadOnlyList<CStmt> Body);

/// <summary>A <c>case E:</c> / <c>default:</c> label that appears NESTED inside
/// another statement of a switch body rather than at the switch's top level —
/// Duff's device (<c>case 7:</c> interleaved into a <c>do…while</c>). The grammar
/// accepts a case/default label anywhere; <see cref="Switch"/> only models the
/// top-level sections, so a nested one becomes this free-standing labeled
/// statement. Codegen prints it verbatim as <c>case E:</c> / <c>default:</c>
/// followed by <see cref="Body"/> — structurally faithful (C# rejects a case
/// label inside a nested block, which is the known Duff's limitation).</summary>
public sealed record CaseLabelStmt(CExpr? CaseExpr, CStmt Body) : CStmt;

/// <summary>A C23 <c>[[fallthrough]];</c> marker (opt-in <c>-Wimplicit-fallthrough</c>).
/// dotcc's switch lowering already synthesizes C's implicit fall-through jump, so the
/// attribute carries NO codegen — this node exists only so <see cref="IrBuilder.BuildSwitch"/>
/// can tell an intentional fall-through from an accidental one and suppress the warning on the
/// former. Emits nothing (both backends) and does NOT terminate control flow, so the
/// synthesized <c>goto case</c> is still appended after it.</summary>
public sealed record FallthroughMarker : CStmt;

/// <summary>A <c>case E:</c> (<see cref="CaseExpr"/> set) or <c>default:</c>
/// (null) label. The case expression must be a constant per C# rules — an integer
/// literal, or an enumerator which codegen decays to <c>(int)EnumName.Member</c>
/// (still a constant), matching the int-decayed switch subject. When
/// <paramref name="HiExpr"/> is non-null the label is an INCLUSIVE range
/// <c>[CaseExpr, HiExpr]</c> (Zig <c>lo...hi</c>), rendered as a C# relational
/// pattern <c>>= lo and &lt;= hi</c> (both bounds comptime-known constants). Zig-only;
/// the C front-end never sets it (positional <c>new SwitchLabel(expr)</c> keeps it null).</summary>
public readonly record struct SwitchLabel(CExpr? CaseExpr, CExpr? HiExpr = null);

// ---- declarations / translation unit ------------------------------------

/// <summary>A local array declaration, lowered to a C# <c>stackalloc</c>.
/// <see cref="Inits"/> non-null is the brace-initialized form
/// (<c>stackalloc T[]{ … }</c>); otherwise <see cref="CountExpr"/> gives the
/// dimension (<c>stackalloc T[n]</c>, C# zero-fills).</summary>
public sealed record ArrayDecl(Symbol Sym, CType Element, CExpr? CountExpr, IReadOnlyList<CExpr>? Inits) : CStmt;

/// <summary>A local variable declaration with optional initializer.</summary>
public sealed record LocalDecl(Symbol Sym, CExpr? Init);

/// <summary>The C# memory layout a non-union aggregate is rendered with. <c>Default</c> emits no
/// attribute (C# value-type default, which the runtime treats as sequential). <c>Sequential</c>
/// pins it explicitly (Zig <c>extern struct</c> — guaranteed C-ABI layout). <c>Packed</c> emits
/// <c>[StructLayout(Sequential, Pack=1)]</c> (Zig <c>packed struct</c> — no inter-field padding).
/// A union always uses explicit overlapping layout regardless of this (see
/// <see cref="StructTypeDef.IsUnion"/>).</summary>
public enum AggregateLayout { Default, Sequential, Packed }

/// <summary>A struct or union type definition. Codegen renders it into the
/// top-level type-declarations section (a plain <c>unsafe struct</c>, or an
/// explicit-layout one for a union). Field types are also registered in the
/// builder's struct table so member access resolves a field's type.
/// <see cref="Layout"/> drives an optional <c>[StructLayout]</c> attribute for a
/// non-union aggregate (Zig <c>extern</c>/<c>packed struct</c>).</summary>
public sealed record StructTypeDef(string Name, IReadOnlyList<StructField> Fields, bool IsUnion, AggregateLayout Layout = AggregateLayout.Default);

/// <summary>One field of a <see cref="StructTypeDef"/>. <see cref="BitWidth"/> is
/// <c>null</c> for a normal field, or the declared width of a bit-field —
/// including <c>0</c> for a zero-width anonymous bit-field (<c>int : 0;</c>),
/// which forces the following bit-field onto a fresh storage unit. An anonymous
/// bit-field (padding, <c>int : 3;</c>) carries an empty <see cref="Name"/>. The
/// backend packs consecutive same-size bit-fields into one shared backing field
/// (MSVC storage-unit layout) + masked/sign-extended accessor properties — so
/// <c>sizeof</c> and member offsets match C's layout while reads/writes keep C's
/// exact value semantics (modular truncation, signed sign-extension).</summary>
public readonly record struct StructField(string Name, CType Type, int? BitWidth = null)
{
    /// <summary>True for any bit-field — named, anonymous, or zero-width.</summary>
    public bool IsBitField => BitWidth is not null;

    /// <summary>True for an anonymous bit-field (padding: a width but no name). It
    /// reserves bits in the layout but has no accessible member, so positional
    /// initializers skip it.</summary>
    public bool IsAnonBitField => BitWidth is not null && Name.Length == 0;
}

/// <summary>A C <c>enum</c> type definition (tagged or typedef-named). Codegen
/// renders it into the top-level type-declarations section as a real
/// <c>enum Name : Underlying { … }</c>. <see cref="Members"/> pairs each
/// enumerator with its (auto-incremented or explicit) integer value.</summary>
public sealed record EnumTypeDef(string Name, CType Underlying, IReadOnlyList<EnumMember> Members);

/// <summary>One enumerator of an <see cref="EnumTypeDef"/>: its C name and value.</summary>
public readonly record struct EnumMember(string Name, long Value);

/// <summary>A function parameter (or function-pointer parameter): its type and
/// name. Used while building signatures — a named record rather than a loose
/// tuple so members read as <c>.Type</c> / <c>.Name</c>.</summary>
public readonly record struct ParamInfo(CType Type, string Name);

/// <summary>A function definition: signature symbol + parameter symbols + body.</summary>
public sealed record FuncDef(Symbol Sym, IReadOnlyList<Symbol> Params, Block Body, bool Variadic);

/// <summary>A file-scope variable.</summary>
public sealed record GlobalVar(Symbol Sym, CExpr? Init);

/// <summary>The whole compiled unit: the typed IR a backend consumes.</summary>
public sealed class TranslationUnit
{
    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
}
