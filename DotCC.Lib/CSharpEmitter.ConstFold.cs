#nullable enable

using System.Collections.Generic;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- compile-time integer-constant folding ----------------------------
    // Some C positions need an integer CONSTANT EXPRESSION that C# wants as a
    // literal — notably an array-member bound that lowers to a `fixed[N]` /
    // `[InlineArray(N)]` (e.g. Lua's `lu_byte extra_[sizeof(void*)]`). dotcc folds
    // the common forms here: an integer literal, a `sizeof`, and +/-/*// arithmetic
    // over them, seen through parentheses. The value rides up on
    // `EmitContent.Text.ConstInt`; this reads it (and a bare `sizeof` marker).

    // The folded integer value of an expression Item, or null if not foldable.
    private int? ConstOfItem(Item it) => it.Content switch
    {
        EmitContent.SizeofType st => SizeofConst(ResolveTypedef(st.TypeName)),  // sizeof(lu_byte) → sizeof(byte)
        EmitContent.Text t => t.ConstInt,
        _ => null,
    };

    // Byte size of a C# type for `sizeof` folding (64-bit target — matches dotcc's
    // <stdint.h> model: `long`/pointer = 8). Returns null for an aggregate/unknown
    // type whose layout dotcc can't fold (the caller then keeps the textual form).
    private static int? SizeofConst(string csType)
    {
        var t = csType.Trim();
        if (t.EndsWith("*", System.StringComparison.Ordinal)) { return 8; }  // any pointer
        return t switch
        {
            "byte" or "sbyte" or "bool" => 1,
            "short" or "ushort" => 2,
            "int" or "uint" or "float" => 4,
            "long" or "ulong" or "double" or "nint" or "nuint" => 8,
            _ => null,  // struct/union/enum/unknown — needs layout, not folded
        };
    }

    // A binary ARITHMETIC operator (`+ - * / %`): reconcile the operands per C's
    // usual arithmetic conversions (ReconcileInt — see below), keep additive
    // pointer-arith typing, and fold a constant result when both operands are
    // integer constants. Additive pointer arithmetic (`p + n` / `p - n`) carries
    // the (decayed) pointer type so `(p + n)[0]` / `*(p + n)` resolve under
    // sizeof; the reconciled integer result type is the fallback otherwise.
    private EmitContent ArithFold(Item a, string op, Item b)
    {
        var (sa, sb, rty) = ReconcileInt(a, b);
        return new EmitContent.Text($"({sa} {op} {sb})",
            Ty: PtrArithType(a, op, b) ?? rty,
            ConstInt: FoldBinary(ConstOfItem(a), op, ConstOfItem(b)));
    }

    // ---- C usual arithmetic conversions (§6.3.1.8) ------------------------
    // dotcc maps C's integer types onto C# (`size_t`/`lua_Unsigned` → `ulong`,
    // `lu_byte` → `byte`, …). C# performs MOST of C's usual arithmetic conversions
    // implicitly, but diverges in two ways this layer corrects:
    //   • CS0034 ERROR — a 64-bit-rank UNSIGNED (`ulong`/`nuint`) mixed with any
    //     SIGNED integer (`int`/`long`/`nint`, and C# `sizeof`, which is `int`) has
    //     NO common type in C#. C converts the signed operand to the unsigned type.
    //   • WRONG TYPE — `uint op int` is `long` in C# but `unsigned int` in C.
    // Both are fixed the same way: compute C's common type, and when it's unsigned,
    // cast any signed operand to it (C's wraparound conversion). The result is
    // tagged with the common type so it propagates up nested expressions
    // (`(size_t)a + (uint)b * sizeof(T)`) and into Cond.B / stores / args. Pairs
    // C# already handles identically to C (both signed, or a smaller-or-unsigned
    // operand C# widens for free) get NO cast — only the divergent ones do, keeping
    // the emitted text tight.

    // The lowered C# integer type of a binary operand, resolved through scalar
    // typedefs (`size_t` → `ulong`). A `sizeof` marker / C# `sizeof(T)` is `int`.
    // Null when the operand isn't a known integer scalar (pointer, float, struct,
    // unknown) — those don't take the integer reconcile.
    private string? IntOperandType(Item it)
    {
        if (it.Content is EmitContent.SizeofType) { return "int"; }  // C# sizeof is int
        if (TyOf(it) is CType.Sized s)
        {
            var r = ResolveTypedef(s.CsType);
            return IsIntegerCsType(r) ? r : null;
        }
        return null;
    }

    // C's common type for a pair of lowered C# integer types, after integer
    // promotion (sub-int types — byte/sbyte/short/ushort — promote to int). Same
    // sign → the higher conversion rank; mixed sign → the unsigned type when its
    // rank ≥ the signed's (true for every pair in dotcc's LP64 model, where a wider
    // signed type — long/nint at rank 3 — always represents a narrower unsigned —
    // uint at rank 2 — so the signed wins there). Null if either side isn't a
    // known integer.
    private static string? IntCommonType(string a, string b)
    {
        static string Promote(string t) => t is "byte" or "sbyte" or "short" or "ushort" ? "int" : t;
        var ca = Promote(a);
        var cb = Promote(b);
        if (ca == cb) { return ca; }
        static (int Rank, bool Unsigned) Info(string t) => t switch
        {
            "int" => (2, false),
            "uint" => (2, true),
            "long" or "nint" => (3, false),
            "ulong" or "nuint" => (3, true),
            _ => (0, false),
        };
        var (ra, ua) = Info(ca);
        var (rb, ub) = Info(cb);
        if (ra == 0 || rb == 0) { return null; }
        if (ua == ub) { return ra >= rb ? ca : cb; }                 // same sign → higher rank
        var (unsignedT, unsignedRank) = ua ? (ca, ra) : (cb, rb);
        var (_, signedRank) = ua ? (cb, rb) : (ca, ra);
        return unsignedRank >= signedRank ? unsignedT : (ua ? cb : ca);
    }

    // IntDecay (enum→int) on both operands, then C's usual arithmetic conversion:
    // when the common type is unsigned, cast each SIGNED operand to it (C# has no
    // implicit signed→unsigned conversion, and `uint op int` would otherwise widen
    // to `long`). The result type is the common type when both operands are known
    // integers; null leaves the operator to type itself (pointers / floats / mixed
    // unknowns are untouched).
    private (string A, string B, CType? Ty) ReconcileInt(Item a, Item b)
    {
        var sa = IntDecay(a);
        var sb = IntDecay(b);
        if (IntOperandType(a) is not string ta || IntOperandType(b) is not string tb)
        {
            return (sa, sb, null);
        }
        if (IntCommonType(ta, tb) is not string common) { return (sa, sb, null); }
        if (NeedsUnsignedCast(ta, common)) { sa = $"({common})({sa})"; }
        if (NeedsUnsignedCast(tb, common)) { sb = $"({common})({sb})"; }
        return (sa, sb, new CType.Sized(common));
    }

    // A signed operand needs an explicit cast only when the common type is unsigned
    // — C# won't implicitly convert sbyte/short/int/long/nint to uint/ulong/nuint.
    // Unsigned or sub-int operands C# widens for free (byte→uint, uint→ulong,
    // byte→int) are left as-is.
    private static bool NeedsUnsignedCast(string operandType, string common) =>
        common is "uint" or "ulong" or "nuint"
        && operandType is "sbyte" or "short" or "int" or "long" or "nint";

    // ---- store conversions (init / assignment / return) -------------------
    // C allows an implicit integer conversion at a store even when it narrows or
    // changes signedness; C# requires an explicit cast (CS0266). dotcc coerces the
    // value to the target type, inserting `(target)(value)` exactly when C# would
    // NOT convert implicitly. A width-NARROWING store (target holds fewer bytes
    // than the source — `lu_byte b = some_int;`) is additionally recorded with the
    // -Wconversion gate (off by default, opt-in like gcc/clang -Wconversion).
    private string CoerceStore(string value, CType? sourceTy, int? constValue, string targetCs, int line)
    {
        if (sourceTy is not CType.Sized s) { return value; }
        var src = ResolveTypedef(s.CsType);
        var tgt = ResolveTypedef(targetCs);
        if (!IsIntegerCsType(src) || !IsIntegerCsType(tgt) || src == tgt) { return value; }
        // A constant that FITS the target needs no cast and is no narrowing — C#'s
        // implicit constant conversion accepts `byte b = 5;` / `uint u = 0;`, and
        // gcc -Wconversion only warns when the constant is OUT of range.
        if (constValue is int cv && ConstFitsTarget(cv, tgt)) { return value; }
        if (CsImplicitInt(src, tgt)) { return value; }   // C# widens for free
        // C# rejects this conversion (CS0266); insert the cast C allows implicitly.
        // Warn only when it's a genuine width-narrowing (the user-facing meaning of
        // "narrowing") — a same-width sign change (int↔uint) is not flagged.
        if (IntWidth(tgt) is int tw && IntWidth(src) is int sw && tw < sw)
        {
            _conversionGate?.Narrowing(s.CsType, targetCs, _currentFunctionName, line);
        }
        // Reaching here with a known constant means it did NOT fit (the fit guard
        // returned early otherwise) — a C# constant cast out of range is a compile
        // error (CS0221) unless wrapped in `unchecked`. Runtime casts truncate
        // unchecked by default, so a non-constant value needs no wrapper.
        return constValue is not null
            ? $"unchecked(({targetCs})({value}))"
            : $"({targetCs})({value})";
    }

    // Byte width of a lowered C# integer type (the LP64 model — long/pointer = 8).
    private static int? IntWidth(string t) => t switch
    {
        "byte" or "sbyte" => 1,
        "short" or "ushort" => 2,
        "int" or "uint" => 4,
        "long" or "ulong" or "nint" or "nuint" => 8,
        _ => null,
    };

    // Whether an integer CONSTANT value fits the target type's range — C#'s
    // implicit constant-expression conversion accepts these with no cast (and a
    // fitting constant is no narrowing). The value arrives as `int` (ConstInt), so
    // any 32-bit-or-wider signed target trivially fits, and an unsigned ≥32-bit
    // target fits any non-negative value.
    private static bool ConstFitsTarget(int v, string tgt) => tgt switch
    {
        "byte" => v >= 0 && v <= 255,
        "sbyte" => v >= -128 && v <= 127,
        "short" => v >= -32768 && v <= 32767,
        "ushort" => v >= 0 && v <= 65535,
        "int" or "long" or "nint" => true,
        "uint" or "ulong" or "nuint" => v >= 0,
        _ => false,
    };

    // True when C# IMPLICITLY converts integer `src` to `tgt` (the value range of
    // src ⊆ tgt): an unsigned source fits any strictly-wider type; a signed source
    // fits only a strictly-wider SIGNED type. Equal types are handled by the
    // caller. Conservative — it never claims an implicit conversion that doesn't
    // exist (a false "no" just yields a harmless redundant cast, never a CS0266).
    private static bool CsImplicitInt(string src, string tgt)
    {
        if (IntWidth(src) is not int sw || IntWidth(tgt) is not int tw) { return false; }
        var srcUnsigned = src is "byte" or "ushort" or "uint" or "nuint";
        var tgtUnsigned = tgt is "byte" or "ushort" or "uint" or "nuint";
        return srcUnsigned ? tw > sw : (!tgtUnsigned && tw > sw);
    }

    // The result type of additive pointer arithmetic. `p ± int` yields a pointer
    // of the same element type as `p` (an array operand decays to its element
    // pointer, so `(arr + n)[k]` strides correctly); `p - q` (both pointers) is a
    // ptrdiff (integer), so we don't claim a pointer type there. Only `+`/`-`
    // (the C ops with pointer semantics) — `*`/`/`/`%` never produce a pointer.
    private CType? PtrArithType(Item a, string op, Item b)
    {
        if (op != "+" && op != "-") { return null; }
        var ta = DecayToPointer(TyOf(a));
        var tb = DecayToPointer(TyOf(b));
        if (ta is not null && tb is not null) { return null; }  // ptr ± ptr → difference
        return ta ?? tb;
    }

    // Decay an indexable CType to the pointer it becomes in arithmetic: an array
    // `T[N]` (or [InlineArray]) → `T*`; an existing pointer stays; a PtrToArr
    // stays (it subscripts with the array stride); a scalar isn't a pointer → null.
    private static CType? DecayToPointer(CType? t) => t switch
    {
        CType.Sized s when s.CsType.EndsWith("*", System.StringComparison.Ordinal) => s,
        CType.Arr a => a.Element is CType.Sized es ? new CType.Sized(es.CsType + "*") : a,
        CType.InlineArr ia => ia.Element is CType.Sized es ? new CType.Sized(es.CsType + "*") : ia,
        CType.PtrToArr => t,
        _ => null,
    };

    // Byte size of a synthesized CType for `sizeof expr` folding — array is
    // count*sizeof(element) (the same shape SizeofText emits textually).
    private int? SizeofConstOfCType(CType t) => t switch
    {
        CType.Arr a => SizeofConstOfCType(a.Element) is int e ? a.Count * e : null,
        CType.InlineArr ia => SizeofConstOfCType(ia.Element) is int e ? ia.Count * e : null,
        CType.PtrToArr => 8,
        CType.Sized s => SizeofConst(s.CsType),
        _ => null,
    };

    // Fold `a OP b` of two known constants (used by the arithmetic visitors).
    private static int? FoldBinary(int? a, string op, int? b)
    {
        if (a is not int x || b is not int y) { return null; }
        return op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" when y != 0 => x / y,
            "%" when y != 0 => x % y,
            "<<" => x << y,
            ">>" => x >> y,
            _ => null,
        };
    }

    // ---- typedef → underlying primitive resolution ------------------------
    // `typedef unsigned char lu_byte;` records lu_byte → "byte" here (chained, so
    // `typedef lu_byte TStatus;` records TStatus → "byte" too). Used where the
    // UNDERLYING primitive matters even though uses carry the alias — currently the
    // fixed-buffer-element check (`fixed byte`, not `fixed lu_byte`, which C# rejects)
    // and `sizeof` folding of an aliased type.
    private readonly Dictionary<string, string> _typedefUnderlying = new(System.StringComparer.Ordinal);

    // Resolve a type name through the simple-typedef chain to its underlying C#
    // type; returns the input unchanged if it isn't a (chained) simple alias.
    private string ResolveTypedef(string name)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        while (_typedefUnderlying.TryGetValue(name, out var under) && seen.Add(name)) { name = under; }
        return name;
    }
}
