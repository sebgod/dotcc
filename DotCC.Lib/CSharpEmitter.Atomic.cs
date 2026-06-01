#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- _Atomic lowering (C11) -------------------------------------------
    // An `_Atomic`-qualified lvalue is read/written through the seq-cst Atomic.*
    // helpers (Interlocked-backed full fences) — C11 atomics default to
    // memory_order_seq_cst. Same tag-and-react shape as volatile: the lvalue node
    // emits the seq-cst read (`Atomic.Load(ref lv)`) by default and tags itself;
    // the write-context parents (assignment → Atomic.Store, compound-assign →
    // Atomic.AddFetch/…, ++/-- → Atomic.AddFetch/FetchAdd, & → bare address) emit
    // the store/RMW form instead. The seq-cst load/store/RMW themselves live in
    // DotCC.Libc/AtomicLib.cs.
    //
    // Scope (A1): the type-specifier `_Atomic T` / `_Atomic(T)` on eligible scalar
    // lvalues — local, param, file-scope var, struct/union member. The named
    // <stdatomic.h> functions (atomic_load/store/fetch_add/compare_exchange/…) are
    // phase A2, lowered in Visit(C.Call) onto the same Atomic.* helpers.

    // C# types Atomic.* covers (4-/8-byte unmanaged INumber scalars). NOT the
    // narrow ints (byte/short/…, reinterpret would over-read), NOT bool/CBool/enum,
    // NOT pointers (can't be a generic arg) — those fall back to a plain access.
    private static readonly HashSet<string> _atomicEligible = new(StringComparer.Ordinal)
    {
        "int", "uint", "long", "ulong", "nint", "nuint", "float", "double",
    };

    internal static bool IsAtomicEligible(string csType) => _atomicEligible.Contains(csType);

    // Bitwise-RMW eligibility (`&= |= ^=`): integers only (no float/double).
    private static bool IsAtomicBitwise(string csType) => csType is
        "int" or "uint" or "long" or "ulong" or "nint" or "nuint";

    private readonly HashSet<string> _localAtomic = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalAtomic = new(StringComparer.Ordinal);
    // structType → (fieldName → field C# type). The type is needed so a member
    // atomic store/RMW can cast the rhs to it (the generic Atomic.* helpers infer T
    // from both args, and C's int→uint/float conversions aren't implicit in C#).
    private readonly Dictionary<string, Dictionary<string, string>> _structAtomicFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingAtomicFields = new(StringComparer.Ordinal);
    private readonly List<string> _pendingAtomicParams = new();

    // The Atomic flag set by Visit(TypeAtomic)/(TypeAtomicParen) on a Type result.
    private static bool AtomicOf(Item typeItem) =>
        typeItem.Content is EmitContent.Text { Atomic: true };

    // The bare lvalue of an atomic-lvalue expression (set by the lvalue nodes).
    private static string? ALValueOf(Item exprItem) =>
        (exprItem.Content as EmitContent.Text)?.AtomicLValue;

    private void DrainAtomicFields(params string[] typeNames)
    {
        if (_pendingAtomicFields.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structAtomicFields[tn] = new Dictionary<string, string>(_pendingAtomicFields, StringComparer.Ordinal);
            }
            _pendingAtomicFields.Clear();
        }
    }

    private bool IsAtomicVar(string rawName) =>
        _localAtomic.Contains(rawName)
        || (!_localNames.Contains(rawName) && _globalAtomic.Contains(rawName));

    // The atomic field's C# type if `field` is an atomic member of the struct that
    // baseItem's CType names, else null.
    private string? FieldAtomic(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = s.CsType.TrimEnd('*');
        return _structAtomicFields.TryGetValue(t, out var m) && m.TryGetValue(field, out var ct) ? ct : null;
    }

    // The seq-cst READ form for an atomic lvalue `lv` (eligible type), tagged with
    // the bare lvalue so a write-context parent can recover it.
    private static EmitContent.Text AtomicReadOf(string lv, CType? ty) =>
        new($"Atomic.Load(ref {lv})", Ty: ty, AtomicLValue: lv);

    // The seq-cst compound-assign for `lv op= rhs` — maps to the *Fetch helper that
    // returns the NEW value (matching C's `x op= n` expression value). `op` is the
    // C operator without the `=`. Bitwise ops require an integer lvalue. Throws for
    // an operator with no atomic primitive (`*= /= %= <<= >>=` — vanishingly rare on
    // atomics) rather than silently emit a non-atomic read-modify-write.
    private string AtomicCompound(string lv, string csType, string op, string rhs)
    {
        var helper = op switch
        {
            "+" => "AddFetch",
            "-" => "SubFetch",
            "&" when IsAtomicBitwise(csType) => "AndFetch",
            "|" when IsAtomicBitwise(csType) => "OrFetch",
            "^" when IsAtomicBitwise(csType) => "XorFetch",
            _ => throw new CompileException(
                $"atomic compound assignment `{op}=` on `{csType}` isn't supported "
                + "(no lock-free primitive); use an explicit atomic_compare_exchange loop"),
        };
        // Cast the rhs to the lvalue type so the generic Atomic.* call infers T
        // (both args same type) and C's int→uint/float conversions are honoured.
        return $"Atomic.{helper}(ref {lv}, ({csType})({rhs}))";
    }

    // `++x` / `x++` (and `--`) on an atomic lvalue → a seq-cst step. Postfix yields
    // the OLD value (Fetch*); prefix yields the NEW value (*Fetch) — matching C. The
    // `1` literal converts to the helper's element type. Returns null for a
    // non-atomic operand so the caller falls through to the volatile / plain form.
    private EmitContent? AtomicStep(Item operand, bool isPost, string addSub)
    {
        if (ALValueOf(operand) is not string alv) { return null; }
        var helper = (isPost, addSub) switch
        {
            (true, "+") => "FetchAdd",
            (true, _) => "FetchSub",
            (false, "+") => "AddFetch",
            (false, _) => "SubFetch",
        };
        // `(ct)1` so the generic call infers T = the lvalue type.
        var ct = (TyOf(operand) as CType.Sized)?.CsType ?? "int";
        return $"Atomic.{helper}(ref {alv}, ({ct})1)";
    }
}
