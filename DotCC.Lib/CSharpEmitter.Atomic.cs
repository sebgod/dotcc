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

    // ---- <stdatomic.h> atomic typedefs ------------------------------------
    // `typedef _Atomic T atomic_X;` records the alias → underlying C# type here, and
    // Visit(TypeName) returns the underlying with the Atomic flag set, so `atomic_int
    // x;` is treated exactly like `_Atomic int x;`. (No `using` alias is emitted —
    // uses lower straight to the underlying type.)
    private readonly Dictionary<string, string> _atomicTypedefs = new(StringComparer.Ordinal);

    // ---- <stdatomic.h> generic functions ----------------------------------
    // C11's atomic_* "generic functions" are type-generic (via _Generic) in a real
    // header; dotcc intercepts them by NAME in Visit(C.Call) and lowers onto the
    // seq-cst Atomic.* helpers. The first argument is always a pointer to the atomic
    // object — `ref *(arg0)` is the location, and arg0's pointer CType gives the
    // pointee type (to cast value args, since C's int→uint/float conversions aren't
    // implicit in C#). The `_explicit` variants carry trailing memory_order args
    // which we ignore (every order we honour maps to a full barrier — the safe
    // over-approximation, same as volatile). Returns null when `callee` isn't an
    // atomic builtin, so Visit(C.Call) falls through to the normal call path.
    private EmitContent? LowerAtomicCall(string callee, EmitContent.Args ac)
    {
        if (!callee.StartsWith("atomic_", StringComparison.Ordinal)) { return null; }
        var args = ac.Values;
        var types = ac.ArgTypes;
        // Drop an `_explicit` suffix — the logic is identical; trailing memory_order
        // args are simply never referenced (we index by position).
        var name = callee.EndsWith("_explicit", StringComparison.Ordinal)
            ? callee[..^"_explicit".Length] : callee;

        // Pointee C# type of arg0 (a `T*`), or null if undeterminable.
        string? Pointee() =>
            types is not null && types.Count > 0 && types[0] is CType.Sized s
            && s.CsType.EndsWith("*", StringComparison.Ordinal)
                ? s.CsType[..^1].TrimEnd() : null;
        // The atomic object as a ref-able location: `*(arg0)`.
        string Obj() => $"*({args[0]})";
        // True if the pointee has a lock-free Atomic.* primitive.
        bool Eligible() => Pointee() is string p && IsAtomicEligible(p);
        CompileException Unsupported(string why) => new(
            $"`{callee}` on a non-lock-free type isn't supported ({why}); dotcc's atomic "
            + "primitives cover 4-/8-byte int/uint/long/ulong/nint/nuint/float/double");

        switch (name)
        {
            case "atomic_load":
                return Eligible()
                    ? Typed($"Atomic.Load(ref {Obj()})", new CType.Sized(Pointee()!))
                    : Typed($"({Obj()})", Pointee() is string lp ? new CType.Sized(lp) : null);

            case "atomic_store":
            case "atomic_init":  // init is a (non-atomic) plain store in C anyway
                if (name == "atomic_init" || !Eligible()) { return $"({Obj()} = ({Cast(Pointee(), args[1])}))"; }
                return $"Atomic.Store(ref {Obj()}, {Cast(Pointee(), args[1])})";

            case "atomic_exchange":
                if (!Eligible()) { throw Unsupported("exchange needs a lock-free primitive"); }
                return Typed($"Atomic.Exchange(ref {Obj()}, {Cast(Pointee(), args[1])})", new CType.Sized(Pointee()!));

            case "atomic_compare_exchange_strong":
            case "atomic_compare_exchange_weak":
                if (!Eligible()) { throw Unsupported("compare-exchange needs a lock-free primitive"); }
                // bool result → CBool (usable as a C int / in conditions). expected
                // (arg1) is a pointer; desired (arg2) casts to the pointee type.
                return $"((CBool)Atomic.CompareExchange(ref {Obj()}, ref *({args[1]}), {Cast(Pointee(), args[2])}))";

            case "atomic_fetch_add": return Rmw("FetchAdd", Pointee(), args, Eligible(), Unsupported);
            case "atomic_fetch_sub": return Rmw("FetchSub", Pointee(), args, Eligible(), Unsupported);
            case "atomic_fetch_or":  return Rmw("FetchOr",  Pointee(), args, Eligible(), Unsupported);
            case "atomic_fetch_and": return Rmw("FetchAnd", Pointee(), args, Eligible(), Unsupported);
            case "atomic_fetch_xor": return Rmw("FetchXor", Pointee(), args, Eligible(), Unsupported);

            // atomic_flag — dotcc models the flag as an int (see <stdatomic.h>).
            case "atomic_flag_test_and_set":
                return $"((CBool)(Atomic.Exchange(ref {Obj()}, 1) != 0))";
            case "atomic_flag_clear":
                return $"Atomic.Store(ref {Obj()}, 0)";

            case "atomic_thread_fence": return "Atomic.ThreadFence()";
            case "atomic_signal_fence": return "Atomic.SignalFence()";

            // Our eligible atomics are always lock-free; report 1 (true). A
            // non-eligible (lock-based-in-C) object reports 0.
            case "atomic_is_lock_free":
                return Typed(Eligible() ? "1" : "0", new CType.Sized("int"));

            default:
                return null;  // not an atomic builtin we recognise → normal call
        }
    }

    // `(pointee)(arg)` — cast a value arg to the atomic's element type so the generic
    // Atomic.* call infers T and C's implicit conversions are honoured. Falls back to
    // the bare arg when the pointee type is unknown.
    private static string Cast(string? pointee, string arg) =>
        pointee is null ? arg : $"({pointee})({arg})";

    private EmitContent Rmw(string helper, string? pointee, IReadOnlyList<string> args, bool eligible, Func<string, CompileException> unsupported)
    {
        if (!eligible) { throw unsupported($"{helper} needs a lock-free primitive"); }
        // Fetch* returns the OLD value (the C atomic_fetch_* result), typed as the pointee.
        return Typed($"Atomic.{helper}(ref *({args[0]}), {Cast(pointee, args[1])})", new CType.Sized(pointee!));
    }
}
