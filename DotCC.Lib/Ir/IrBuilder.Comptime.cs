#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace DotCC.Ir;

// ---------------------------------------------------------------------------
// Milestone T — the unified compile-time interpreter.
//
// dotcc historically grew several ad-hoc constant folders (the C-side
// `ConstEval`/`ApplyConstBin`, the Zig-side `ZigConstEval`, `ConstEvalArraySize`).
// They diverged: the Zig one handled only literals + unary, the C one added
// binary arithmetic/bitwise but no relational/logical, neither computed wider
// than `long`. This file replaces them with ONE tree-walk over the lowered,
// typed `CExpr` — the seed of comptime VALUES (Milestone T). Both front-ends
// route their constant positions through it, so a feature added here (binary-op
// enum initializers, relational/logical folding, 128-bit arithmetic) lights up
// for both languages at once.
//
// The value domain is deliberately int / float / bool ONLY: NO `type` variant
// and NO pointer variant. That single omission is the firewall that keeps
// value-comptime from sliding into type-comptime — the moment a comptime
// expression would need a type-as-value or a comptime pointer, it simply isn't
// a `ComptimeValue` and the eval returns null (the caller decides whether that
// position requires a constant). Integer arithmetic is carried in
// <see cref="System.Int128"/> so a comptime computation that genuinely exceeds
// 64 bits (now that the i128/u128/__int128 types exist) has somewhere to live.
// ---------------------------------------------------------------------------
internal sealed partial class IrBuilder
{
    /// <summary>A side-effect-free compile-time VALUE — the result of evaluating
    /// the C/Zig value subset at dotcc compile time. No <c>type</c> variant and no
    /// pointer variant (the value-/type-comptime firewall — see the file header).</summary>
    internal abstract record ComptimeValue;

    /// <summary>A comptime integer, carried in 128 bits so a computation can exceed
    /// <c>long</c>. <see cref="Type"/> is the C type the value is typed at (drives the
    /// usual-arithmetic conversions and, eventually, the splice-back literal's carrier).</summary>
    internal sealed record CtInt(System.Int128 Value, CType Type) : ComptimeValue;

    /// <summary>A comptime floating value (always evaluated in <c>double</c>;
    /// <see cref="Type"/> records <c>float</c> vs <c>double</c> for splice typing).</summary>
    internal sealed record CtFloat(double Value, CType Type) : ComptimeValue;

    /// <summary>A comptime boolean — the result of a relational/equality/logical
    /// operator. Each front-end's adapter coerces it: C reads it as an <c>int</c>
    /// (0/1), Zig as a <c>bool</c>.</summary>
    internal sealed record CtBool(bool Value) : ComptimeValue;

    /// <summary>A comptime struct value (Milestone T — comptime aggregates) — a field-name → value
    /// map, MUTABLE in place so a comptime <c>c.field = v;</c> updates it and a later read sees the
    /// store. <see cref="Type"/> is the struct's <see cref="CType.Named"/>, which drives the
    /// splice-back field order/types (<see cref="SpliceStruct"/>) and the zero-fill of an
    /// <c>undefined</c>-initialized struct (<see cref="ZeroValue"/>). Still NO pointer variant — a
    /// struct value is a flat by-value record, the firewall holds (an array sibling, <c>CtArray</c>,
    /// is the next increment).</summary>
    internal sealed record CtStruct(Dictionary<string, ComptimeValue> Fields, CType Type) : ComptimeValue;

    /// <summary>A comptime fixed-array value (Milestone T — comptime aggregates / lookup tables) —
    /// N element values, MUTABLE in place so a comptime <c>t[i] = v;</c> updates an element and a
    /// later read sees it. <see cref="Element"/> is the element type, <see cref="Type"/> the array
    /// <see cref="CType.Array"/>. Splices back as a <see cref="StackArray"/>. Still NO pointer
    /// variant — the array is a flat by-value vector, the firewall holds.</summary>
    internal sealed record CtArray(ComptimeValue[] Elems, CType Element, CType Type) : ComptimeValue;

    // The eval-step budget. Expression-only folding (Milestone T part 1) is bounded by
    // the tree size, so this is a safety net here; comptime calls / `inline` loops
    // (later parts) lean on it to reject a non-terminating comptime computation
    // (Zig's `@setEvalBranchQuota` exists for exactly this reason).
    private int _comptimeSteps;
    private const int ComptimeStepBudget = 4_000_000;

    // The largest comptime array (element count) the interpreter will materialize — a backstop on
    // a `var t: [N]T = undefined;` with an absurd N (the fill loop is step-budgeted, but the array
    // allocation itself is not). Generous: a comptime lookup table is typically ≤ a few thousand.
    private const long ComptimeArrayCap = 1 << 20;

    // The active comptime call frame: a locals/params environment keyed by Symbol IDENTITY
    // (Symbol is a reference type — the SAME instance is shared by every VarRef/LocalDecl that
    // names it, so reference equality is the correct key under shadowing). Null outside a comptime
    // function call (plain const folding has no locals). Saved/restored around each nested call, so
    // recursion (e.g. fib) gets a fresh frame.
    private Dictionary<Symbol, ComptimeValue>? _comptimeFrame;

    // Whether a function CALL may be interpreted. A C function call is NOT a constant expression
    // (C §6.6), so the C const-folding entry (`ConstEval`) leaves this false and a call folds to
    // null (rejected as non-constant). Only the Zig `comptime` resolver enables it — comptime
    // function evaluation is a Zig-only capability in this milestone.
    private bool _comptimeAllowCalls;

    private void StepComptime()
    {
        if (++_comptimeSteps > ComptimeStepBudget)
        {
            throw new IrUnsupportedException(
                "comptime evaluation exceeded the step budget — a non-terminating comptime expression? "
                + "(dotcc does not model Zig's @setEvalBranchQuota)");
        }
    }

    /// <summary>Evaluate an integer constant expression to a signed <c>long</c>, or null
    /// if it is not a constant the interpreter folds (or its value does not fit
    /// <c>long</c>). The constant-expression entry point for both front-ends: C array
    /// bounds, enum/case/bit-field/designator values, and Zig array sizes / enum
    /// initializers all route here. A relational/logical result reads as C's <c>int</c>
    /// (0/1).</summary>
    internal long? ConstEval(CExpr e) =>
        TryEvalTop(e, allowCalls: false) switch
        {
            CtInt i when InLongRange(i.Value) => (long)i.Value,
            CtBool b => b.Value ? 1L : 0L,
            _ => null,
        };

    /// <summary>The top-level eval entry: reset the step budget + call frame, then evaluate. A
    /// <see cref="ComptimeAbort"/> (a body construct the interpreter doesn't evaluate) maps to null
    /// (not a compile-time constant); the step-budget overflow surfaces as a loud error.</summary>
    private ComptimeValue? TryEvalTop(CExpr e, bool allowCalls)
    {
        _comptimeSteps = 0;
        _comptimeFrame = null;
        _comptimeAllowCalls = allowCalls;
        try { return EvalComptime(e); }
        catch (ComptimeAbort) { return null; }
    }

    private static bool InLongRange(System.Int128 v) =>
        v >= long.MinValue && v <= long.MaxValue;

    /// <summary>Resolve a deferred <c>comptime EXPR</c> to a spliced literal <see cref="CExpr"/>, or
    /// null if it does not evaluate to a compile-time constant value. The Zig front-end's post-pass
    /// calls this once every function body is lowered, so a comptime call can interpret its callee.</summary>
    internal CExpr? ResolveComptimeFold(CExpr inner) =>
        TryEvalTop(inner, allowCalls: true) is { } v ? Splice(v) : null;

    /// <summary>Re-materialize a <see cref="ComptimeValue"/> as an IR literal, so the rest of the
    /// pipeline (lower → emit) sees an ordinary constant. Int / float / bool splice to the matching
    /// literal; a comptime STRUCT splices to a <see cref="StructInit"/> (the array sibling is a later
    /// increment).</summary>
    private CExpr Splice(ComptimeValue v) => v switch
    {
        CtInt i => SpliceInt(i),
        CtFloat f => new LitFloat(FormatComptimeFloat(f.Value)) { Type = f.Type },
        CtBool b => new LitBool(b.Value) { Type = CType.Bool },
        CtStruct s => SpliceStruct(s),
        CtArray a => SpliceArray(a),
        _ => throw new IrUnsupportedException("comptime value cannot be spliced back (int/float/bool/struct/array)"),
    };

    /// <summary>Splice a comptime array value back as a <see cref="StackArray"/> — a dense element
    /// list (each element recursively spliced). At a local <c>const</c> use site this lowers to a
    /// <c>stackalloc</c>; the post-pass re-homes a global one into a pinned, program-lifetime store.</summary>
    private CExpr SpliceArray(CtArray a)
    {
        var elems = new List<CExpr>(a.Elems.Length);
        foreach (var e in a.Elems) { elems.Add(Splice(e)); }
        return new StackArray(a.Element, elems) { Type = a.Type };
    }

    /// <summary>Splice a comptime struct value back as a <see cref="StructInit"/> object initializer,
    /// emitting each field in DECLARED order (from the struct field table) with its declared type, so
    /// the backend renders <c>new T { f = …, … }</c>. A field the comptime value never set (an
    /// unsupplied member of a partial init) is omitted, taking C#'s zero default — exactly C's
    /// partial-init rule. Anonymous padding bit-fields have no member, so they are skipped.</summary>
    private CExpr SpliceStruct(CtStruct s)
    {
        if (s.Type.Unqualified is not CType.Named named || !_structFields.TryGetValue(named.Name, out var fields))
        {
            throw new IrUnsupportedException("comptime struct value cannot be spliced (unknown struct type)");
        }
        var members = new List<FieldInit>();
        foreach (var f in fields)
        {
            if (f.Name.Length == 0) { continue; }                       // anonymous padding bit-field
            if (!s.Fields.TryGetValue(f.Name, out var fv)) { continue; } // unsupplied → C# zero default
            members.Add(new FieldInit(f.Name, f.Type, Splice(fv)));
        }
        return new StructInit(members) { Type = named };
    }

    /// <summary>The zero comptime value of a type — for an <c>undefined</c> / default-initialized
    /// comptime local. A scalar zeroes; a struct zero-fills every (named) field recursively. Returns
    /// null when the type has no comptime zero (a pointer, an array — not yet, an aggregate with an
    /// unmodelled field), so the position is "not a compile-time constant".</summary>
    private ComptimeValue? ZeroValue(CType t)
    {
        var u = t.Unqualified;
        if (u is CType.Prim p)
        {
            if (p.Name == "_Bool") { return new CtBool(false); }
            return p.Integer ? new CtInt(System.Int128.Zero, t) : new CtFloat(0.0, t);
        }
        if (u is CType.Named named && _structFields.TryGetValue(named.Name, out var fields))
        {
            var map = new Dictionary<string, ComptimeValue>(System.StringComparer.Ordinal);
            foreach (var f in fields)
            {
                if (f.Name.Length == 0) { continue; }   // anonymous padding bit-field — no member
                if (ZeroValue(f.Type) is not { } fv) { return null; }   // a field we can't zero yet
                map[f.Name] = fv;
            }
            return new CtStruct(map, named);
        }
        if (u is CType.Array arr && arr.Count is int ac)
        {
            if (ac < 0 || ac > ComptimeArrayCap) { return null; }
            var elems = new ComptimeValue[ac];
            for (int k = 0; k < ac; k++)
            {
                // A fresh zero per element — array elements are independent (a struct element must
                // not share one mutable CtStruct reference across slots).
                if (ZeroValue(arr.Element) is not { } ev) { return null; }
                elems[k] = ev;
            }
            return new CtArray(elems, arr.Element, arr);
        }
        return null;
    }

    private static CExpr SpliceInt(CtInt i)
    {
        // A non-negative magnitude splices straight to a LitInt; the fast-path Value is set when it
        // fits long, else left null so the literal rides the 128-bit decimal-Digits path (Milestone ß).
        if (i.Value >= System.Int128.Zero)
        {
            long? fast = i.Value <= (System.Int128)long.MaxValue ? (long)i.Value : null;
            return new LitInt(i.Value.ToString(CultureInfo.InvariantCulture), fast) { Type = i.Type };
        }
        // Int128.MinValue has no in-range positive magnitude — splice it as a signed-decimal literal.
        if (i.Value == System.Int128.MinValue)
        {
            return new LitInt(i.Value.ToString(CultureInfo.InvariantCulture), null) { Type = i.Type };
        }
        // A negative value splices as -(magnitude), matching how literals are otherwise carried.
        var mag = -i.Value;
        long? fastMag = mag <= (System.Int128)long.MaxValue ? (long)mag : null;
        CExpr lit = new LitInt(mag.ToString(CultureInfo.InvariantCulture), fastMag) { Type = i.Type };
        return new Unary(UnOp.Neg, lit) { Type = i.Type };
    }

    private static string FormatComptimeFloat(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        // Keep a decimal point / exponent so the backend renders a FLOATING literal, not an integer.
        return s.IndexOfAny(['.', 'e', 'E', 'n', 'N', 'i', 'I']) >= 0 ? s : s + ".0";
    }

    /// <summary>The interpreter core: a tree-walk of the lowered, typed
    /// <see cref="CExpr"/>. Returns the <see cref="ComptimeValue"/>, or null when the
    /// expression is not a foldable compile-time value (a variable, a call, a pointer,
    /// an un-foldable cast, division by zero …). Pure and side-effect-free.</summary>
    private ComptimeValue? EvalComptime(CExpr e)
    {
        StepComptime();
        switch (e)
        {
            case LitInt i:
                // `Value` is the fast path (fits long); past long the magnitude lives in
                // the decimal `Digits` (a u128-range / i128 literal) — parse it into 128 bits.
                if (i.Value is { } lv) { return new CtInt(lv, i.Type); }
                return System.Int128.TryParse(i.Digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big)
                    ? new CtInt(big, i.Type)
                    : null;

            case LitBool lb:
                return new CtBool(lb.Value);

            case LitFloat lf:
                return double.TryParse(lf.Text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? new CtFloat(d, lf.Type)
                    : null;

            case EnumConstRef ec:
                return new CtInt(ec.Sym.ConstValue, ec.Type.Unqualified is CType.Prim or CType.Enum ? ec.Type : CType.Int);

            case SizeOfExpr s:
                return SizeOfConst(s.Of) is { } sz ? new CtInt(sz, CType.SizeT) : null;

            case OffsetOf o:
                return StructCanonical(o.StructType) is { } sn && OffsetOfConstPath(sn, o.Path) is { } off
                    ? new CtInt(off, CType.SizeT)
                    : null;

            case Paren p:
                return EvalComptime(p.Inner);

            case ComptimeFold cf:
                // A `comptime EXPR` value. If the post-pass already resolved it, read the spliced
                // literal; otherwise evaluate the inner inline (an array-size or other type position
                // needs the value DURING lowering, before the post-pass runs).
                return EvalComptime(cf.Resolved ?? cf.Inner);

            case Cast c:
                return EvalCast(c);

            case CondExpr q:
                return EvalComptime(q.Cond) is { } cnd ? EvalComptime(Truthy(cnd) ? q.Then : q.Else) : null;

            case Unary u:
                return EvalUnary(u);

            case Binary b:
                return EvalBinary(b);

            // Inside a comptime call frame: read a local/param, mutate one, or recurse into a call.
            // Outside a frame (`_comptimeFrame` null — plain const folding) a variable read is not a
            // compile-time constant, so these fall through to null.
            case VarRef v:
                return _comptimeFrame is { } fr && fr.TryGetValue(v.Sym, out var bound) ? bound : null;

            case Assign a:
                return EvalComptimeAssign(a);

            case Call c:
                return EvalComptimeCall(c);

            // --- comptime aggregates (Milestone T) -----------------------------
            // A struct value: build from a struct initializer, read a field by value, or zero-fill an
            // `undefined`/default. (`c.field = v` is the write side — see EvalComptimeAssign.)
            case StructInit si:
                return EvalComptimeStructInit(si);

            case Member mem when !mem.Arrow:
            {
                return EvalComptime(mem.Base) is CtStruct ms && ms.Fields.TryGetValue(mem.Field, out var mv)
                    ? mv : null;
            }

            // An array literal value (`.{…}` / `[N]T{…}` in a comptime body, or a return of one).
            case StackArray sa:
            {
                var elems = new ComptimeValue[sa.Elems.Count];
                for (int k = 0; k < sa.Elems.Count; k++)
                {
                    if (EvalComptime(sa.Elems[k]) is not { } ev) { return null; }
                    elems[k] = RetypeTo(ev, sa.Element);
                }
                return new CtArray(elems, sa.Element, sa.Type);
            }

            // An array element read `t[i]` — the base is a comptime array, the index a comptime int.
            case Index ix:
            {
                if (EvalComptime(ix.Base) is not CtArray arr || EvalComptime(ix.Idx) is not CtInt ixi) { return null; }
                long n = (long)ixi.Value;
                return n >= 0 && n < arr.Elems.Length ? arr.Elems[n] : null;   // OOB → not foldable
            }

            // A `[N]T` by-value return (Increment A's node) is transparent at comptime — the heap
            // copy is a runtime concern; the comptime VALUE is just the source array.
            case ArrayByValReturn abr:
                return EvalComptime(abr.Source);

            case DefaultLit dl:
                return ZeroValue(dl.Type);

            default:
                return null;
        }
    }

    /// <summary>Evaluate a struct initializer at comptime: start from a fully zero-filled struct (so
    /// an unsupplied field reads as zero — C's partial-init rule), then overlay each supplied member
    /// (retyped to its field type). Null if the struct type or a member value is not foldable.</summary>
    private ComptimeValue? EvalComptimeStructInit(StructInit si)
    {
        if (si.Type.Unqualified is not CType.Named named || ZeroValue(named) is not CtStruct st) { return null; }
        foreach (var fi in si.Members)
        {
            if (EvalComptime(fi.Value) is not { } v) { return null; }
            st.Fields[fi.Name] = RetypeTo(v, fi.FieldType);
        }
        return st;
    }

    /// <summary>Fold a cast at comptime. An arithmetic target converts/re-types the
    /// value (int↔float, bool→int); a non-arithmetic target (a pointer cast) is
    /// transparent — the operand's value flows through unchanged, matching the legacy
    /// integer-constant-expression rule (a C constant expression cast does not
    /// truncate). Truncation to the target width is the backend's job at emit.</summary>
    private ComptimeValue? EvalCast(Cast c)
    {
        if (EvalComptime(c.Operand) is not { } v) { return null; }
        if (c.Target.Unqualified is not CType.Prim p) { return v; }
        if (p.Integer)
        {
            return new CtInt(v switch
            {
                CtInt i => i.Value,
                CtBool b => b.Value ? System.Int128.One : System.Int128.Zero,
                CtFloat f => (System.Int128)f.Value,
                _ => System.Int128.Zero,
            }, c.Target);
        }
        return new CtFloat(ToDouble(v), c.Target);
    }

    private ComptimeValue? EvalUnary(Unary u)
    {
        if (EvalComptime(u.Operand) is not { } v) { return null; }
        return u.Op switch
        {
            UnOp.Plus => v,
            UnOp.Neg => v switch
            {
                CtInt i => new CtInt(unchecked(-i.Value), i.Type),
                CtFloat f => new CtFloat(-f.Value, f.Type),
                CtBool b => new CtInt(b.Value ? System.Int128.NegativeOne : System.Int128.Zero, CType.Int),
                _ => null,
            },
            UnOp.BitNot => v switch
            {
                CtInt i => new CtInt(~i.Value, i.Type),
                CtBool b => new CtInt(~(b.Value ? System.Int128.One : System.Int128.Zero), CType.Int),
                _ => null,
            },
            UnOp.LogNot => new CtBool(!Truthy(v)),
            _ => null,   // ++/--/&/* are not compile-time values
        };
    }

    private ComptimeValue? EvalBinary(Binary b)
    {
        // Logical operators short-circuit and yield a bool.
        if (b.Op is BinOp.LogAnd or BinOp.LogOr)
        {
            if (EvalComptime(b.Left) is not { } lo) { return null; }
            var lb = Truthy(lo);
            if (b.Op == BinOp.LogAnd && !lb) { return new CtBool(false); }
            if (b.Op == BinOp.LogOr && lb) { return new CtBool(true); }
            return EvalComptime(b.Right) is { } ro ? new CtBool(Truthy(ro)) : null;
        }

        if (EvalComptime(b.Left) is not { } l || EvalComptime(b.Right) is not { } r) { return null; }
        return CombineBin(b.Op, l, r);
    }

    /// <summary>Apply a non-logical binary operator to two already-evaluated comptime values.
    /// Shared by <see cref="EvalBinary"/> and compound assignment. Relational/equality → bool;
    /// a floating operand pulls into <c>double</c>; otherwise integer arithmetic/bitwise computed
    /// exact in 128 bits (wrap at 128, never trap — the const-folding flavour) at C's usual
    /// arithmetic-conversion result type.</summary>
    private ComptimeValue? CombineBin(BinOp op, ComptimeValue l, ComptimeValue r)
    {
        if (op is BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge or BinOp.Eq or BinOp.Ne)
        {
            return new CtBool(Compare(op, l, r));
        }

        if (l is CtFloat || r is CtFloat)
        {
            double x = ToDouble(l), y = ToDouble(r);
            return op switch
            {
                BinOp.Add => new CtFloat(x + y, CType.Double),
                BinOp.Sub => new CtFloat(x - y, CType.Double),
                BinOp.Mul => new CtFloat(x * y, CType.Double),
                BinOp.Div => new CtFloat(x / y, CType.Double),
                _ => null,
            };
        }

        System.Int128 a = ToInt128(l), c = ToInt128(r);
        var ty = CType.UsualArithmetic(TypeOf(l), TypeOf(r));
        return op switch
        {
            BinOp.Add => new CtInt(unchecked(a + c), ty),
            BinOp.Sub => new CtInt(unchecked(a - c), ty),
            BinOp.Mul => new CtInt(unchecked(a * c), ty),
            BinOp.Div => c != System.Int128.Zero ? new CtInt(a / c, ty) : null,
            BinOp.Mod => c != System.Int128.Zero ? new CtInt(a % c, ty) : null,
            BinOp.Shl => new CtInt(unchecked(a << (int)c), ty),
            BinOp.Shr => new CtInt(a >> (int)c, ty),
            BinOp.BitAnd => new CtInt(a & c, ty),
            BinOp.BitOr => new CtInt(a | c, ty),
            BinOp.BitXor => new CtInt(a ^ c, ty),
            _ => null,
        };
    }

    private bool Compare(BinOp op, ComptimeValue l, ComptimeValue r)
    {
        int cmp;
        if (l is CtFloat || r is CtFloat) { cmp = ToDouble(l).CompareTo(ToDouble(r)); }
        else { cmp = ToInt128(l).CompareTo(ToInt128(r)); }
        return op switch
        {
            BinOp.Lt => cmp < 0,
            BinOp.Gt => cmp > 0,
            BinOp.Le => cmp <= 0,
            BinOp.Ge => cmp >= 0,
            BinOp.Eq => cmp == 0,
            BinOp.Ne => cmp != 0,
            _ => false,
        };
    }

    private static bool Truthy(ComptimeValue v) => v switch
    {
        CtBool b => b.Value,
        CtInt i => i.Value != System.Int128.Zero,
        CtFloat f => f.Value != 0.0,
        _ => false,
    };

    private static System.Int128 ToInt128(ComptimeValue v) => v switch
    {
        CtInt i => i.Value,
        CtBool b => b.Value ? System.Int128.One : System.Int128.Zero,
        _ => System.Int128.Zero,
    };

    private static double ToDouble(ComptimeValue v) => v switch
    {
        CtFloat f => f.Value,
        CtInt i => (double)i.Value,
        CtBool b => b.Value ? 1.0 : 0.0,
        _ => 0.0,
    };

    private static CType TypeOf(ComptimeValue v) => v switch
    {
        CtInt i => i.Type,
        CtFloat f => f.Type,
        _ => CType.Int,   // a bool participates in arithmetic as int
    };

    // ---- comptime function calls (Milestone T, part 2b) -------------------
    //
    // A `comptime f(args)` interprets the callee's already-lowered body in a fresh call frame.
    // Control flow inside the body unwinds through these signals; the step budget bounds it.

    /// <summary>A comptime <c>return</c> — carries the value back to the call boundary.</summary>
    private sealed class ComptimeReturn : System.Exception { public ComptimeValue? Value; }

    /// <summary>A comptime <c>break</c> — unwinds to the nearest enclosing comptime loop.</summary>
    private sealed class ComptimeBreak : System.Exception { }

    /// <summary>A comptime <c>continue</c> — unwinds to the nearest enclosing comptime loop.</summary>
    private sealed class ComptimeContinue : System.Exception { }

    /// <summary>The body contains a construct the comptime interpreter does not evaluate (a goto,
    /// a switch, a pointer/aggregate op, a read of a non-frame symbol, …). Caught at the top-level
    /// entry, where it maps to "not a compile-time constant" — the caller decides if that position
    /// required one.</summary>
    private sealed class ComptimeAbort : System.Exception
    {
        public ComptimeAbort(string reason) : base(reason) { }
    }

    /// <summary>Interpret a function call at compile time: evaluate the arguments in the caller's
    /// frame, bind them to the callee's parameters in a fresh frame, walk the lowered body, and
    /// return the value carried by its <c>return</c>. Null when the call cannot be a compile-time
    /// value — calls disabled (the C path), an extern/libc/runtime function (no body), a variadic,
    /// an arity mismatch, or a non-constant argument. The result is re-typed to the function's
    /// declared return type so the spliced literal carries the right carrier.</summary>
    private ComptimeValue? EvalComptimeCall(Call c)
    {
        if (!_comptimeAllowCalls || c.CalleeSym is not { } cs) { return null; }
        var fn = FindFuncDef(cs);
        if (fn is null || fn.Variadic || fn.Params.Count != c.Args.Count) { return null; }

        var argVals = new ComptimeValue[c.Args.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            if (EvalComptime(c.Args[i]) is not { } av) { return null; }
            argVals[i] = av;
        }

        var frame = new Dictionary<Symbol, ComptimeValue>();   // Symbol identity (reference) keys
        for (int i = 0; i < fn.Params.Count; i++)
        {
            frame[fn.Params[i]] = RetypeTo(argVals[i], fn.Params[i].Type);
        }

        var saved = _comptimeFrame;
        _comptimeFrame = frame;
        try
        {
            EvalComptimeStmt(fn.Body);
            return null;   // fell off the end with no `return` value — treat as non-constant
        }
        catch (ComptimeReturn r)
        {
            return r.Value is { } rv ? RetypeTo(rv, c.Type) : null;
        }
        finally
        {
            _comptimeFrame = saved;
        }
    }

    /// <summary>The lowered <see cref="FuncDef"/> for a callee symbol, by reference identity (the
    /// same <see cref="Symbol"/> instance is shared by the declaration and every call site), or null
    /// if the function has no lowered body (extern / not a user function).</summary>
    private FuncDef? FindFuncDef(Symbol sym)
    {
        foreach (var f in Functions)
        {
            if (ReferenceEquals(f.Sym, sym)) { return f; }
        }
        return null;
    }

    /// <summary>Re-type a comptime scalar to a target arithmetic type (so a parameter binding /
    /// return value carries the declared type, driving usual-arithmetic + the splice carrier).
    /// A bool target / non-arithmetic target leaves the value unchanged.</summary>
    private static ComptimeValue RetypeTo(ComptimeValue v, CType t)
    {
        if (t.Unqualified is not CType.Prim p) { return v; }
        if (p.Integer)
        {
            return v switch
            {
                CtInt i => new CtInt(i.Value, t),
                CtFloat f => new CtInt((System.Int128)f.Value, t),
                _ => v,   // a bool stays a bool
            };
        }
        return v switch
        {
            CtInt i => new CtFloat((double)i.Value, t),
            CtFloat f => new CtFloat(f.Value, t),
            _ => v,
        };
    }

    /// <summary>Apply a comptime assignment (simple or compound) to a frame local or a struct field,
    /// returning the stored value. A local/param l-value (<c>x = v</c>) or a struct member
    /// (<c>c.field = v</c>, mutating the frame's struct value in place) is assignable at comptime;
    /// anything else aborts.</summary>
    private ComptimeValue? EvalComptimeAssign(Assign a)
    {
        if (_comptimeFrame is null) { throw new ComptimeAbort("comptime assignment outside a call frame"); }
        if (EvalComptime(a.Value) is not { } rhs) { return null; }

        switch (a.Target)
        {
            // A local / parameter.
            case VarRef vr:
                if (a.CompoundOp is { } vop)
                {
                    if (!_comptimeFrame.TryGetValue(vr.Sym, out var vcur) || CombineBin(vop, vcur, rhs) is not { } vcomb)
                    {
                        return null;
                    }
                    rhs = vcomb;
                }
                return _comptimeFrame[vr.Sym] = RetypeTo(rhs, vr.Sym.Type);

            // A struct field — `c.field = v`. EvalComptime(m.Base) returns the SAME CtStruct the
            // frame holds (by reference), so mutating its field map writes through to the local; a
            // nested `c.inner.field = v` likewise mutates the nested struct in place.
            case Member m when !m.Arrow && EvalComptime(m.Base) is CtStruct st:
                var ftype = StructFieldType(st.Type, m.Field);
                if (a.CompoundOp is { } mop)
                {
                    if (!st.Fields.TryGetValue(m.Field, out var mcur) || CombineBin(mop, mcur, rhs) is not { } mcomb)
                    {
                        return null;
                    }
                    rhs = mcomb;
                }
                var stored = ftype is { } ft ? RetypeTo(rhs, ft) : rhs;
                st.Fields[m.Field] = stored;
                return stored;

            // An array element — `t[i] = v` (mutates the frame's array value in place).
            case Index ix when EvalComptime(ix.Base) is CtArray arr:
                if (EvalComptime(ix.Idx) is not CtInt iidx) { return null; }
                long ai = (long)iidx.Value;
                if (ai < 0 || ai >= arr.Elems.Length) { throw new ComptimeAbort("comptime array index out of bounds"); }
                if (a.CompoundOp is { } iop)
                {
                    if (CombineBin(iop, arr.Elems[ai], rhs) is not { } icomb) { return null; }
                    rhs = icomb;
                }
                var istored = RetypeTo(rhs, arr.Element);
                arr.Elems[ai] = istored;
                return istored;

            default:
                throw new ComptimeAbort("comptime assignment target must be a local variable, struct field, or array element");
        }
    }

    /// <summary>Walk a statement of a comptime function body. Supports the side-effect-free
    /// control-flow subset — blocks, local decls, expression statements (assignments), if/else,
    /// while/do-while/for loops (break/continue), and return. Any other statement aborts the
    /// evaluation (→ the value is not compile-time-constant). The step budget bounds every loop.</summary>
    private void EvalComptimeStmt(CStmt s)
    {
        StepComptime();
        switch (s)
        {
            case Block b:
                foreach (var st in b.Stmts) { EvalComptimeStmt(st); }
                break;

            case Seq q:
                foreach (var st in q.Stmts) { EvalComptimeStmt(st); }
                break;

            case DeclStmt d:
                foreach (var decl in d.Decls)
                {
                    if (decl.Init is not { } init) { continue; }   // uninitialized — bound on first store
                    _comptimeFrame![decl.Sym] = EvalComptime(init) is { } v
                        ? RetypeTo(v, decl.Sym.Type)
                        : throw new ComptimeAbort("non-constant comptime local initializer");
                }
                break;

            // A `[N]T` array local — `var t: [N]T = undefined;` (zero-filled) or a brace init. Bound
            // to a comptime array value the body then fills via `t[i] = …`. The element count is
            // capped so an absurd `[1<<30]T` can't blow up the interpreter (the loop that fills it is
            // step-budgeted, but the allocation itself is not).
            case ArrayDecl ad:
            {
                ComptimeValue[] elems;
                if (ad.Inits is { } inits)
                {
                    elems = new ComptimeValue[inits.Count];
                    for (int k = 0; k < inits.Count; k++)
                    {
                        elems[k] = EvalComptime(inits[k]) is { } ev ? RetypeTo(ev, ad.Element)
                            : throw new ComptimeAbort("non-constant comptime array initializer");
                    }
                }
                else
                {
                    if (ad.CountExpr is null || EvalComptime(ad.CountExpr) is not CtInt cn)
                    {
                        throw new ComptimeAbort("comptime array declaration needs a constant size");
                    }
                    long n = (long)cn.Value;
                    if (n < 0 || n > ComptimeArrayCap)
                    {
                        throw new IrUnsupportedException(
                            $"comptime array of {n} elements exceeds the interpreter cap ({ComptimeArrayCap})");
                    }
                    elems = new ComptimeValue[n];
                    for (int k = 0; k < n; k++)
                    {
                        elems[k] = ZeroValue(ad.Element)
                            ?? throw new ComptimeAbort("comptime array element type has no compile-time zero");
                    }
                }
                _comptimeFrame![ad.Sym] = new CtArray(elems, ad.Element, ad.Sym.Type);
                break;
            }

            case ExprStmt e:
                EvalComptime(e.Expr);   // evaluated for its effect on the frame (assignments)
                break;

            case If i:
                if (EvalComptime(i.Cond) is not { } cnd) { throw new ComptimeAbort("non-constant comptime condition"); }
                if (Truthy(cnd)) { EvalComptimeStmt(i.Then); }
                else if (i.Else is { } el) { EvalComptimeStmt(el); }
                break;

            case While w:
                while (EvalComptime(w.Cond) is { } wc && Truthy(wc))
                {
                    StepComptime();
                    try { EvalComptimeStmt(w.Body); }
                    catch (ComptimeContinue) { }
                    catch (ComptimeBreak) { break; }
                }
                break;

            case DoWhile dw:
                do
                {
                    StepComptime();
                    try { EvalComptimeStmt(dw.Body); }
                    catch (ComptimeContinue) { }
                    catch (ComptimeBreak) { break; }
                }
                while (EvalComptime(dw.Cond) is { } dc && Truthy(dc));
                break;

            case For fo:
                if (fo.Init is { } ini) { EvalComptimeStmt(ini); }
                while (fo.Cond is null || (EvalComptime(fo.Cond) is { } fc && Truthy(fc)))
                {
                    StepComptime();
                    try { EvalComptimeStmt(fo.Body); }
                    catch (ComptimeContinue) { }
                    catch (ComptimeBreak) { break; }
                    if (fo.Post is { } post) { EvalComptime(post); }
                }
                break;

            case Return r:
                throw new ComptimeReturn { Value = r.Value is { } rv ? EvalComptime(rv) : null };

            case Break:
                throw new ComptimeBreak();

            case Continue:
                throw new ComptimeContinue();

            default:
                throw new ComptimeAbort("comptime: unsupported statement " + s.GetType().Name);
        }
    }
}
