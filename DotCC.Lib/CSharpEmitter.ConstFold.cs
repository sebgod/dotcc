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

    // Wrap an arithmetic op's emitted text with a folded ConstInt when both
    // operands are integer constants (else null — the text is still correct).
    // Additive pointer arithmetic (`p + n` / `p - n`) also carries the (decayed)
    // pointer type, so `(p + n)[0]` / `*(p + n)` resolve under sizeof.
    private EmitContent ArithFold(string text, Item a, string op, Item b) =>
        new EmitContent.Text(text, Ty: PtrArithType(a, op, b),
            ConstInt: FoldBinary(ConstOfItem(a), op, ConstOfItem(b)));

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
