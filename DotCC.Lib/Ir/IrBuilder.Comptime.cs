#nullable enable

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

    // The eval-step budget. Expression-only folding (Milestone T part 1) is bounded by
    // the tree size, so this is a safety net here; comptime calls / `inline` loops
    // (later parts) lean on it to reject a non-terminating comptime computation
    // (Zig's `@setEvalBranchQuota` exists for exactly this reason).
    private int _comptimeSteps;
    private const int ComptimeStepBudget = 4_000_000;

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
    internal long? ConstEval(CExpr e)
    {
        _comptimeSteps = 0;
        return EvalComptime(e) switch
        {
            CtInt i when InLongRange(i.Value) => (long)i.Value,
            CtBool b => b.Value ? 1L : 0L,
            _ => null,
        };
    }

    private static bool InLongRange(System.Int128 v) =>
        v >= long.MinValue && v <= long.MaxValue;

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

            case Cast c:
                return EvalCast(c);

            case CondExpr q:
                return EvalComptime(q.Cond) is { } cnd ? EvalComptime(Truthy(cnd) ? q.Then : q.Else) : null;

            case Unary u:
                return EvalUnary(u);

            case Binary b:
                return EvalBinary(b);

            default:
                return null;
        }
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

        // Relational / equality → bool.
        if (b.Op is BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge or BinOp.Eq or BinOp.Ne)
        {
            return new CtBool(Compare(b.Op, l, r));
        }

        // A floating operand pulls the operation into double (arithmetic only;
        // bitwise/% on a float is not a valid constant expression → null).
        if (l is CtFloat || r is CtFloat)
        {
            double x = ToDouble(l), y = ToDouble(r);
            return b.Op switch
            {
                BinOp.Add => new CtFloat(x + y, CType.Double),
                BinOp.Sub => new CtFloat(x - y, CType.Double),
                BinOp.Mul => new CtFloat(x * y, CType.Double),
                BinOp.Div => new CtFloat(x / y, CType.Double),
                _ => null,
            };
        }

        // Integer arithmetic / bitwise, computed exact in 128 bits (wrap at 128,
        // never trap — the const-folding flavour). The result type is C's usual
        // arithmetic conversion of the operand types.
        System.Int128 a = ToInt128(l), c = ToInt128(r);
        var ty = CType.UsualArithmetic(TypeOf(l), TypeOf(r));
        return b.Op switch
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
}
