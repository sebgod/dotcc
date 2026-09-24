#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
// position requires a constant). One deliberate hole (the comptime-engine segment
// E1): the aggregates are MUTABLE references, so a pointer to a comptime struct or
// array evaluates to the aggregate itself (`self: *Acc`, `fill(&buf, v)`), with no
// pointer variant; a pointer to a scalar still is not a value. Integer arithmetic is carried in
// <see cref="System.Int128"/> so a comptime computation that genuinely exceeds
// 64 bits (now that the i128/u128/__int128 types exist) has somewhere to live.
// ---------------------------------------------------------------------------
internal sealed partial class IrModule
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
    internal sealed record CtStruct(Dictionary<string, ComptimeValue> Fields, CType Type) : ComptimeValue
    {
        /// <summary>For the payload of a tagged UNION (an overlaid C# struct), the variant last written: only
        /// it splices back, since writing every overlaid field would let the last one clobber it.</summary>
        public string? Active { get; set; }
    }

    /// <summary>A comptime fixed-array value (Milestone T — comptime aggregates / lookup tables) —
    /// N element values, MUTABLE in place so a comptime <c>t[i] = v;</c> updates an element and a
    /// later read sees it. <see cref="Element"/> is the element type, <see cref="Type"/> the array
    /// <see cref="CType.Array"/>. Splices back as a <see cref="StackArray"/>. Still NO pointer
    /// variant — the array is a flat by-value vector, the firewall holds.</summary>
    internal sealed record CtArray(ComptimeValue[] Elems, CType Element, CType Type) : ComptimeValue;

    /// <summary>A comptime <c>null</c> (the comptime engine's E2): what a <c>?comptime_int</c> function
    /// such as <c>std.simd.suggestVectorLength</c> returns when it has no answer. A present optional is
    /// just its payload, so this is the only optional value there is. <see cref="Type"/> is the optional
    /// (or pointer) type it was typed at; it splices back as <c>default(T)</c>.</summary>
    internal sealed record CtNull(CType Type) : ComptimeValue;

    /// <summary>The result of a comptime call to a <c>void</c> function (<c>std.debug.assert(…)</c>): no value, but
    /// not a failure either. It is only ever discarded.</summary>
    /// <summary>A comptime ERROR (the failure side of an error union: <c>error.Overflow</c> from
    /// <c>std.math.ceilPowerOfTwo</c>). A success is just its payload, so this is the only error-union value there is.</summary>
    internal sealed record CtError(System.Int128 Code) : ComptimeValue;

    internal sealed record CtVoid : ComptimeValue
    {
        /// <summary>The one void value.</summary>
        public static readonly CtVoid Value = new();
    }

    /// <summary>A comptime SLICE (the comptime engine, for std.fmt's format strings): a window of
    /// <see cref="Length"/> elements of a comptime array starting at <see cref="Offset"/>. A string literal is a
    /// byte array, so <c>fmt[a..b]</c>, <c>.len</c> and <c>fmt[i]</c> evaluate. <see cref="Type"/> is the slice
    /// type; a byte slice splices back as a string literal.</summary>
    internal sealed record CtSlice(CtArray Backing, long Offset, long Length, CType Type) : ComptimeValue;

    /// <summary>A pointer to an element of a comptime array (a slice's <c>.Ptr</c>, moved by pointer
    /// arithmetic): the one pointer the interpreter models, since it never leaves the array it points into.</summary>
    internal sealed record CtElemPtr(CtArray Backing, long Index, CType Type) : ComptimeValue;

    /// <summary>The comptime engine's E3: comptime variables that outlive one evaluation, keyed by their
    /// <see cref="Symbol"/>. A Zig <c>comptime var s: S = .{…}</c> of an aggregate type lives here while its
    /// function lowers, so each <c>comptime s.method()</c> sees (and mutates) the value the previous one
    /// left. Read after the call frame; assigned in place.</summary>
    internal Dictionary<Symbol, ComptimeValue> ComptimeGlobals { get; } = new();

    /// <summary>A front end's top-level CONST aggregates by symbol, with their initializers: a comptime
    /// evaluation reading one evaluates the initializer, once (<see cref="EvalConstGlobal"/>).</summary>
    internal Dictionary<Symbol, CExpr> ConstGlobalInits { get; } = new();

    /// <summary>Each evaluated <see cref="ConstGlobalInits"/> entry (a const is never written, so one value
    /// serves every read); null marks one being evaluated, so a self-reference stops.</summary>
    private readonly Dictionary<Symbol, ComptimeValue?> _constGlobalValues = new();

    /// <summary>The comptime value of a top-level const aggregate (see <see cref="ConstGlobalInits"/>).</summary>
    private ComptimeValue? EvalConstGlobal(Symbol sym, CExpr init)
    {
        if (_constGlobalValues.TryGetValue(sym, out var known)) { return known; }
        _constGlobalValues[sym] = null;
        var saved = _comptimeFrame;
        _comptimeFrame = null;   // an initializer reads no caller's locals
        try
        {
            var value = EvalComptime(init);
            _constGlobalValues[sym] = value;
            return value;
        }
        finally
        {
            _comptimeFrame = saved;
        }
    }

    /// <summary>Evaluate <paramref name="e"/> to a comptime value, calls included (the comptime engine's
    /// E3: the initial value of a comptime aggregate variable). Null when it is not a compile-time value.
    /// The result is a fresh copy, never an alias of a value another variable holds.</summary>
    internal ComptimeValue? EvalComptimeValue(CExpr e) =>
        TryEvalTop(e, allowCalls: true) is { } v ? CloneComptime(v) : null;

    /// <summary>A short, stable digest of a comptime value's contents (FNV-1a over a canonical spelling), for keying
    /// a generic instance by a comptime STRUCT argument (<c>comptime cpu: std.Target.Cpu</c>).</summary>
    internal static string ComptimeDigest(ComptimeValue v)
    {
        var sb = new System.Text.StringBuilder();
        void Spell(ComptimeValue x)
        {
            switch (x)
            {
                case CtInt i: sb.Append('i').Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
                case CtFloat f: sb.Append('f').Append(f.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case CtBool b: sb.Append(b.Value ? 'T' : 'F'); break;
                case CtNull: sb.Append('n'); break;
                case CtArray a: sb.Append('['); foreach (var e in a.Elems) { Spell(e); sb.Append(','); } sb.Append(']'); break;
                case CtSlice sl:
                    sb.Append('<');
                    for (var k = 0; k < sl.Length; k++) { Spell(sl.Backing.Elems[sl.Offset + k]); sb.Append(','); }
                    sb.Append('>');
                    break;
                case CtStruct s:
                    sb.Append('{');
                    foreach (var kv in s.Fields.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
                    {
                        sb.Append(kv.Key).Append('=');
                        Spell(kv.Value);
                        sb.Append(';');
                    }
                    sb.Append('}');
                    break;
                default: sb.Append('?'); break;
            }
        }
        Spell(v);
        var h = 2166136261u;
        foreach (var ch in sb.ToString()) { h = unchecked((h ^ ch) * 16777619u); }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Splice a comptime value back as an IR literal (see <see cref="Splice"/>); null when it has no C#
    /// literal form (a non-zero inline array), so a runtime use of it stays loud.</summary>
    internal CExpr? SpliceComptimeValue(ComptimeValue v)
    {
        try { return Splice(v); }
        catch (UnspliceableComptime) { return null; }
    }

    /// <summary>A deep copy of a comptime value: struct and array values are mutable references, so a
    /// value stored under a second name must not share them.</summary>
    private static ComptimeValue CloneComptime(ComptimeValue v) => v switch
    {
        CtStruct s => new CtStruct(s.Fields.ToDictionary(kv => kv.Key, kv => CloneComptime(kv.Value)), s.Type) { Active = s.Active },
        CtArray a => new CtArray(a.Elems.Select(CloneComptime).ToArray(), a.Element, a.Type),
        _ => v,
    };

    // The eval-step budget. Expression-only folding (Milestone T part 1) is bounded by
    // the tree size, so this is a safety net here; comptime calls / `inline` loops
    // (later parts) lean on it to reject a non-terminating comptime computation
    // (Zig's `@setEvalBranchQuota` exists for exactly this reason).
    private int _comptimeSteps;

    /// <summary>The budget dotcc has always used, and the floor
    /// <see cref="ComptimeStepBudget"/> can never be lowered below.</summary>
    internal const int DefaultComptimeStepBudget = 4_000_000;

    /// <summary>The eval-step budget in force. Raised by Zig's <c>@setEvalBranchQuota</c>
    /// (road-to-zig-std S7) and never lowered — zig's own rule for that builtin, and the reason the
    /// unit mismatch is harmless: zig counts BACKWARD BRANCHES while this counts eval STEPS, a
    /// strictly finer unit, so a quota that suffices there suffices here when taken as a floor.</summary>
    internal long ComptimeStepBudget { get; set; } = DefaultComptimeStepBudget;

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
                $"comptime evaluation exceeded the step budget ({ComptimeStepBudget}) — a non-terminating "
                + "comptime expression? Zig's `@setEvalBranchQuota(n)` raises it (never lowers it)");
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

    /// <summary>Evaluate an integer constant expression in 128 bits, for a value beyond
    /// <c>long</c>: an <c>enum(u64)</c> member of <c>maxInt(u64)</c> (std.Io.Limit's <c>unlimited</c>).
    /// Null when it is not a constant the interpreter folds.</summary>
    internal System.Int128? ConstEval128(CExpr e) =>
        TryEvalTop(e, allowCalls: false) switch
        {
            CtInt i => i.Value,
            CtBool b => b.Value ? 1 : 0,
            _ => null,
        };

    /// <summary>The top-level eval entry: reset the step budget + call frame, then evaluate. A
    /// <see cref="ComptimeAbort"/> (a body construct the interpreter doesn't evaluate) maps to null
    /// (not a compile-time constant); the step-budget overflow surfaces as a loud error.</summary>
    private ComptimeValue? TryEvalTop(CExpr e, bool allowCalls)
    {
        // Re-entrant: a body lowered on demand (DemandFuncBody) may fold a constant of its own while an
        // outer evaluation is suspended mid-call, so the outer frame, budget and mode are put back.
        var (steps, frame, calls) = (_comptimeSteps, _comptimeFrame, _comptimeAllowCalls);
        _comptimeSteps = 0;
        _comptimeFrame = null;
        _comptimeAllowCalls = allowCalls;
        try { return EvalComptime(e); }
        catch (ComptimeAbort ex) { ComptimeMiss ??= ex.Message; return null; }
        catch (ComptimeGoto) { return null; }   // a backward or stray jump: not evaluated
        finally { (_comptimeSteps, _comptimeFrame, _comptimeAllowCalls) = (steps, frame, calls); }
    }

    /// <summary>The comptime engine's E2 hook: asked for a callee whose body is not lowered yet, it lowers
    /// the body now (the Zig front-end's on-demand lowering) and answers whether it did. Installed by the
    /// Zig front-end for the length of its lowering, null otherwise (the C front-end has no deferred
    /// bodies).</summary>
    internal System.Func<Symbol, bool>? DemandFuncBody { get; set; }

    private static bool InLongRange(System.Int128 v) =>
        v >= long.MinValue && v <= long.MaxValue;

    /// <summary>Resolve a deferred <c>comptime EXPR</c> to a spliced literal <see cref="CExpr"/>, or
    /// null if it does not evaluate to a compile-time constant value. The Zig front-end's post-pass
    /// calls this once every function body is lowered, so a comptime call can interpret its callee.</summary>
    internal CExpr? ResolveComptimeFold(CExpr inner)
    {
        ComptimeMiss = null;
        if (TryEvalTop(inner, allowCalls: true) is not { } v) { return null; }
        // A value with no C# literal form (a struct with a non-zero inline-array field: std.Target's
        // `Feature.Set{ .ints = … }`) keeps the expression it came from. The fold's callee is interpreted, so
        // it is pure, and running it yields the same value.
        try { return Splice(v); }
        catch (UnspliceableComptime) { return inner; }
    }

    /// <summary>A comptime value C# cannot write as a literal (see <see cref="ResolveComptimeFold"/>).</summary>
    private sealed class UnspliceableComptime : System.Exception { }

    /// <summary>Why the most recent comptime evaluation stopped (the construct the interpreter does not
    /// evaluate), for the "did not evaluate" diagnostic. Null when nothing was recorded.</summary>
    internal string? ComptimeMiss { get; private set; }

    /// <summary>The loud error for a <c>comptime</c> value that does not fold, naming where the
    /// interpreter stopped when it knows.</summary>
    internal IrUnsupportedException ComptimeFoldFailure(CExpr inner) => new(
        "`comptime` expression did not evaluate to a compile-time constant value"
        + (inner is Call { CalleeSym: { } callee } ? $" (a call to '{callee.Name}')" : "")
        + (ComptimeMiss is { } why ? $"; the interpreter stopped at {why}" : ""));

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
        CtNull n => new DefaultLit { Type = n.Type },
        CtSlice sl => SpliceSlice(sl),
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
        if (s.Type.Unqualified is not CType.Named named || !StructFields.TryGetValue(named.Name, out var fields))
        {
            throw new IrUnsupportedException("comptime struct value cannot be spliced (unknown struct type)");
        }
        var isUnion = StructIsUnion.GetValueOrDefault(named.Name);
        var members = new List<FieldInit>();
        foreach (var f in fields)
        {
            if (f.Name.Length == 0) { continue; }                       // anonymous padding bit-field
            if (isUnion && f.Name != s.Active) { continue; }            // an overlaid union: the active variant only
            if (!s.Fields.TryGetValue(f.Name, out var fv)) { continue; } // unsupplied → C# zero default
            // An array field is inline storage, which a C# object initializer cannot set: an all-zero one is
            // the zero default, anything else has no literal form.
            if (f.Type.Unqualified is CType.Array)
            {
                if (IsZero(fv)) { continue; }
                throw new UnspliceableComptime();
            }
            // An enum field (a union's tag) takes its value as the enum type: C# has no implicit int → enum.
            var spliced = Splice(fv);
            if (f.Type.Unqualified is CType.Enum && fv is CtInt) { spliced = new Cast(f.Type, spliced) { Type = f.Type }; }
            members.Add(new FieldInit(f.Name, f.Type, spliced));
        }
        return new StructInit(members) { Type = named };
    }

    /// <summary>Splice a comptime slice back. A byte slice (a comptime string, std.fmt's
    /// <c>Placeholder.specifier_arg</c>) becomes a string literal viewed as the slice; any other element type is
    /// not spliced yet.</summary>
    private CExpr SpliceSlice(CtSlice sl)
    {
        if (sl.Type.Unqualified is not CType.Slice { Element: var elem }
            || elem.Unqualified is not CType.Prim { Integer: true, Bytes: 1 })
        {
            throw new IrUnsupportedException("comptime slice value cannot be spliced (only a byte slice is, as a string)");
        }
        var sb = new System.Text.StringBuilder("\"");
        for (var k = 0; k < sl.Length; k++)
        {
            var b = sl.Backing.Elems[sl.Offset + k] is CtInt ci ? (int)(ci.Value & 0xFF) : 0;
            sb.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append('"');
        var segs = new List<string> { sb.ToString() };
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        var lit = new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
        var len = new LitInt(sl.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), sl.Length) { Type = CType.ULong };
        return new SliceNew(lit, len, elem, elem.IsConst) { Type = sl.Type };
    }

    /// <summary>True for a comptime value that is all zeros (an integer 0, a false, an array or struct of them).</summary>
    private static bool IsZero(ComptimeValue v) => v switch
    {
        CtInt i => i.Value == 0,
        CtBool b => !b.Value,
        CtFloat f => f.Value == 0,
        CtArray a => a.Elems.All(IsZero),
        CtStruct s => s.Fields.Values.All(IsZero),
        CtNull => true,
        _ => false,
    };

    /// <summary>A string literal as the comptime byte array it denotes (its NUL included, as its C type counts it).</summary>
    private static CtArray StringBytes(LitStr ls)
    {
        var bytes = DotCC.EmitHelpers.StringByteValues(ls.Segments);
        var count = ls.Type.Unqualified is CType.Array { Count: { } n } && n > bytes.Count ? n : bytes.Count + 1;
        var elems = new ComptimeValue[count];
        for (var k = 0; k < count; k++) { elems[k] = new CtInt(k < bytes.Count ? bytes[k] : 0, CType.UChar); }
        return new CtArray(elems, CType.UChar, new CType.Array(CType.UChar, count));
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
        // An enum tag zeroes to its first value; an optional / pointer to null; a slice to the empty one.
        if (u is CType.Enum) { return new CtInt(System.Int128.Zero, t); }
        if (u is CType.Optional or CType.Pointer) { return new CtNull(t); }
        if (u is CType.Slice { Element: var se })
        {
            return new CtSlice(new CtArray(System.Array.Empty<ComptimeValue>(), se, new CType.Array(se, 0)), 0, 0, t);
        }
        if (u is CType.Named named && StructFields.TryGetValue(named.Name, out var fields))
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
            // A narrow UNSIGNED value (`u16` from std.atomic.cacheLineForCpu): C#'s only unsigned literal is a
            // `uint`, which does not narrow implicitly, so it is an int literal cast to the type.
            if (i.Type.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: < 4 } && fast is { } small)
            {
                var intLit = new LitInt(i.Value.ToString(CultureInfo.InvariantCulture), small) { Type = CType.Int };
                return new Cast(i.Type, intLit) { Type = i.Type };
            }
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
                // A LIVE reference to a comptime aggregate variable (E3) reads the current value instead.
                return EvalComptime(cf.Live ? cf.Inner : cf.Resolved ?? cf.Inner);

            case Cast c:
                return EvalCast(c);

            case CondExpr q:
                return EvalComptime(q.Cond) is { } cnd ? EvalComptime(Truthy(cnd) ? q.Then : q.Else) : null;

            // A value switch (`switch (cpu.arch) { .x86_64, .aarch64 => 128, … }` in std.atomic.cacheLineForCpu).
            case SwitchExpr se:
            {
                if (EvalComptime(se.Subject) is not CtInt subject) { return null; }
                CExpr? chosen = null;
                CExpr? fallback = null;
                foreach (var arm in se.Arms)
                {
                    if (arm.Labels is null) { fallback ??= arm.Value; continue; }
                    if (arm.Labels.Any(l => LabelMatches(l, subject.Value))) { chosen = arm.Value; break; }
                }
                return (chosen ?? fallback) is { } value ? EvalComptime(value) : null;
            }

            // A pointer to a comptime AGGREGATE is the aggregate itself (the comptime-engine segment E1): a
            // CtStruct / CtArray is a mutable reference, so `&a` handed to a `self: *@This()` method and a
            // store through it (`self.n += v`) mutate the caller's value in place, by-reference for free. A
            // pointer to a SCALAR is still not a comptime value: the firewall holds for everything that
            // would need a pointer variant.
            case Unary { Op: UnOp.AddrOf or UnOp.Deref } pu:
            {
                var pointee = EvalComptime(pu.Operand);
                return pointee is CtStruct or CtArray ? pointee : EvalUnary(pu);
            }
            // `i++` / `--i` (a lowered `for` loop's step): the compound assignment by one, yielding the old
            // value (post) or the new one (pre).
            case Unary { Op: UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec } step:
            {
                var before = EvalComptime(step.Operand);
                var one = new LitInt("1", 1) { Type = CType.Int };
                var op = step.Op is UnOp.PreInc or UnOp.PostInc ? BinOp.Add : BinOp.Sub;
                var after = EvalComptimeAssign(new Assign(op, step.Operand, one) { Type = step.Operand.Type });
                return step.Op is UnOp.PostInc or UnOp.PostDec ? before : after;
            }
            case Unary u:
                return EvalUnary(u);

            // `s.Ptr + i` / `p - i`: an element pointer moves within its array.
            case Binary { Op: BinOp.Add or BinOp.Sub, Left.Type: var plt } pb when plt?.Unqualified is CType.Pointer or CType.Array:
            {
                // An array operand (a string literal, `"abc" + 1`) decays to a pointer to its first element.
                var leftPtr = EvalComptime(pb.Left) switch
                {
                    CtElemPtr ep => ep,
                    CtArray arr => new CtElemPtr(arr, 0, new CType.Pointer(arr.Element)),
                    _ => null,
                };
                if (leftPtr is not { } lp || EvalComptime(pb.Right) is not CtInt ptrStep) { return null; }
                var moved = pb.Op == BinOp.Add ? lp.Index + (long)ptrStep.Value : lp.Index - (long)ptrStep.Value;
                return new CtElemPtr(lp.Backing, moved, lp.Type);
            }

            case Binary b:
                return EvalBinary(b);

            // Inside a comptime call frame: read a local/param, mutate one, or recurse into a call.
            // Outside a frame (`_comptimeFrame` null — plain const folding) a variable read is not a
            // compile-time constant, so these fall through to null.
            case VarRef v:
                // A C23 `constexpr` object IS its value in any constant expression —
                // this single substitution lights up every ICE position at once
                // (array bounds, case labels, _Static_assert, bit-field widths,
                // _Generic). Checked before the frame: a constexpr is never a
                // frame-bound param, and a comptime call body may legitimately
                // read one.
                if (v.Sym.IsConstexpr)
                {
                    var ct = v.Sym.Type.Unqualified;
                    return new CtInt(v.Sym.ConstValue, ct is CType.Prim or CType.Enum ? ct : CType.Int);
                }
                if (_comptimeFrame is { } fr && fr.TryGetValue(v.Sym, out var bound)) { return bound; }
                if (ComptimeGlobals.TryGetValue(v.Sym, out var global)) { return global; }
                return ConstGlobalInits.TryGetValue(v.Sym, out var constInit) ? EvalConstGlobal(v.Sym, constInit) : null;

            case Assign a:
                return EvalComptimeAssign(a);

            case Call c:
                return EvalComptimeCall(c);

            // --- comptime aggregates (Milestone T) -----------------------------
            // A struct value: build from a struct initializer, read a field by value, or zero-fill an
            // `undefined`/default. (`c.field = v` is the write side — see EvalComptimeAssign.)
            case StructInit si:
                return EvalComptimeStructInit(si);

            // `s.f`, and `p->f` through a pointer to a comptime struct (E1: the pointer is the struct).
            case Member { Base.Type: var sbt, Field: "Ptr" or "Len" } sm when sbt?.Unqualified is CType.Slice:
            {
                if (EvalComptime(sm.Base) is not CtSlice slv) { return null; }
                return sm.Field == "Len"
                    ? new CtInt(slv.Length, CType.ULong)
                    : new CtElemPtr(slv.Backing, slv.Offset, sm.Type);
            }

            case Member { Base.Type: var ebt, Field: "IsErr" or "Value" or "Code" } em when ebt?.Unqualified is CType.ErrorUnion:
            {
                if (EvalComptime(em.Base) is not { } eu) { return null; }
                return em.Field switch
                {
                    "IsErr" => new CtBool(eu is CtError),
                    "Code" => eu is CtError err ? new CtInt(err.Code, CType.ErrorSet) : new CtInt(0, CType.ErrorSet),
                    _ => eu is CtError ? throw new ComptimeAbort("comptime: `.Value` of an error") : eu,
                };
            }

            // An OPTIONAL's `.HasValue` / `.Value` (a lowered `orelse` / `if (opt) |x|`): a present optional
            // is its payload, an absent one a CtNull (E3).
            case Member { Base.Type: var obt, Field: "HasValue" or "Value" } om when obt?.Unqualified is CType.Optional:
            {
                if (EvalComptime(om.Base) is not { } ov) { return null; }
                if (om.Field == "HasValue") { return new CtBool(ov is not CtNull); }
                return ov is CtNull ? throw new ComptimeAbort("comptime: `.Value` of a null optional") : ov;
            }
            case Member mem:
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
                var (arr, start) = EvalComptime(ix.Base) switch
                {
                    CtArray a => (a, 0L),
                    CtElemPtr ep => (ep.Backing, ep.Index),   // `s.Ptr[i]`, a slice's element
                    CtSlice sl => (sl.Backing, sl.Offset),
                    _ => ((CtArray?)null, 0L),
                };
                if (arr is null || EvalComptime(ix.Idx) is not CtInt ixi) { return null; }
                long n = start + (long)ixi.Value;
                return n >= 0 && n < arr.Elems.Length ? arr.Elems[n] : null;   // OOB → not foldable
            }

            // A string literal is its byte array; a slice is a window onto an array (std.fmt's format strings).
            case LitStr ls:
                return StringBytes(ls);

            case SliceNew sliceNew:
            {
                var (backing, at) = EvalComptime(sliceNew.Ptr) switch
                {
                    CtArray a => (a, 0L),
                    CtElemPtr ep => (ep.Backing, ep.Index),
                    _ => ((CtArray?)null, 0L),
                };
                if (backing is null || EvalComptime(sliceNew.Len) is not CtInt sliceLen) { return null; }
                return new CtSlice(backing, at, (long)sliceLen.Value, sliceNew.Type);
            }

            // A `[N]T` by-value return (Increment A's node) is transparent at comptime — the heap
            // copy is a runtime concern; the comptime VALUE is just the source array.
            case ArrayByValReturn abr:
                return EvalComptime(abr.Source);

            case DefaultLit dl:
                return dl.Type.Unqualified is CType.Optional ? new CtNull(dl.Type) : ZeroValue(dl.Type);

            case NullPtr np:
                return new CtNull(np.Type);

            // Error unions (std.math.ceilPowerOfTwo … `catch unreachable` in std.simd): a success is its payload, a
            // failure a CtError; `try` passes the error out of the call, `catch` takes the fallback on one.
            case ErrUnionOk euOk:
                return euOk.Payload is { } okPayload ? EvalComptime(okPayload) : CtVoid.Value;
            case ErrUnionErr euErr:
                return EvalComptime(euErr.Code) is CtInt code ? new CtError(code.Value) : null;
            case ZigTry zt:
            {
                var tried = EvalComptime(zt.Inner);
                if (tried is CtError) { throw new ComptimeReturn { Value = tried }; }
                return tried;
            }
            case ZigCatch zc:
                return EvalComptime(zc.Union) is { } caught ? caught is CtError ? EvalComptime(zc.Fallback) : caught : null;

            // `a orelse b`: the payload, or the fallback when `a` is null (E3).
            case NullCoalesce nc:
                return EvalComptime(nc.Left) is { } left ? left is CtNull ? EvalComptime(nc.Right) : left : null;

            default:
                ComptimeMiss ??= "a " + e.GetType().Name + " expression";
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
            st.Active = fi.Name;   // meaningful for a union payload: the variant written
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
        if (v is CtNull) { return c.Target.Unqualified is CType.Optional or CType.Pointer ? new CtNull(c.Target) : null; }
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

    /// <summary>A comptime <c>goto</c> — unwinds to the statement list that holds its label AFTER the
    /// jump (a labeled value block's <c>break :blk v</c> lowers to one). A backward jump is not
    /// evaluated.</summary>
    private sealed class ComptimeGoto : System.Exception
    {
        public ComptimeGoto(string label) { Label = label; }
        public string Label { get; }
    }

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
        // The bit-count builtins over a value only known during the evaluation (`@popCount(self.used_args)`
        // in std.fmt.ArgState): the runtime helpers they lower to, computed at the operand's width.
        if (c is { Callee: "ZigMath.PopCount" or "ZigMath.Clz" or "ZigMath.Ctz", Args: [var bitArg] })
        {
            if (EvalComptime(bitArg) is not CtInt bi
                || (bitArg.Type ?? bi.Type).Unqualified is not CType.Prim { Integer: true, Bytes: 1 or 2 or 4 or 8 } bp)
            {
                return null;
            }
            var width = bp.Bytes * 8;
            var bits = unchecked((ulong)bi.Value) & (width == 64 ? ulong.MaxValue : (1UL << width) - 1);
            var count = c.Callee switch
            {
                "ZigMath.PopCount" => System.Numerics.BitOperations.PopCount(bits),
                "ZigMath.Clz" => bits == 0 ? width : System.Numerics.BitOperations.LeadingZeroCount(bits) - (64 - width),
                _ => bits == 0 ? width : System.Numerics.BitOperations.TrailingZeroCount(bits),
            };
            return new CtInt(count, CType.Int);
        }
        // `@max` / `@min` and zig's integer division builtins over values known only during the evaluation
        // (`@max(8, ceilPowerOfTwo(…))` in std.simd): the runtime helpers they lower to, over 128-bit integers.
        if (c is { Callee: "ZigMath.Max" or "ZigMath.Min" or "ZigMath.DivTrunc" or "ZigMath.DivFloor" or "ZigMath.Rem" or "ZigMath.Mod",
                   Args: [var lhsArg, var rhsArg] })
        {
            if (EvalComptime(lhsArg) is not CtInt l || EvalComptime(rhsArg) is not CtInt r) { return null; }
            if (c.Callee is not ("ZigMath.Max" or "ZigMath.Min") && r.Value == 0) { throw new ComptimeAbort("comptime division by zero"); }
            var result = c.Callee switch
            {
                "ZigMath.Max" => System.Int128.Max(l.Value, r.Value),
                "ZigMath.Min" => System.Int128.Min(l.Value, r.Value),
                "ZigMath.DivTrunc" => l.Value / r.Value,
                "ZigMath.DivFloor" => l.Value / r.Value - ((l.Value % r.Value != 0) && ((l.Value < 0) != (r.Value < 0)) ? 1 : 0),
                "ZigMath.Rem" => l.Value % r.Value,
                _ => ((l.Value % r.Value) + r.Value) % r.Value,
            };
            return new CtInt(result, c.Type);
        }
        if (c.Callee == "__dotcc_unreachable")
        {
            throw new ComptimeAbort("`unreachable` (or a `@compileError` on a path the evaluation took)"
                + (_comptimeCallStack.Count > 0 ? $" in '{string.Join("' called from '", _comptimeCallStack)}'" : ""));
        }
        if (!_comptimeAllowCalls || c.CalleeSym is not { } cs)
        {
            ComptimeMiss ??= $"a call to '{c.Callee}' (not a function the interpreter runs)";
            return null;
        }
        var fn = FindFuncDef(cs);
        if (fn is null || fn.Variadic || fn.Params.Count != c.Args.Count)
        {
            ComptimeMiss ??= $"a call to '{cs.Name}' (no lowered body with {c.Args.Count} parameters)";
            return null;
        }

        var argVals = new ComptimeValue[c.Args.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            if (EvalComptime(c.Args[i]) is not { } av)
            {
                ComptimeMiss ??= $"argument {i + 1} of a call to '{cs.Name}'";
                return null;
            }
            argVals[i] = av;
        }

        var frame = new Dictionary<Symbol, ComptimeValue>();   // Symbol identity (reference) keys
        for (int i = 0; i < fn.Params.Count; i++)
        {
            frame[fn.Params[i]] = RetypeTo(argVals[i], fn.Params[i].Type);
        }

        var saved = _comptimeFrame;
        _comptimeFrame = frame;
        _comptimeCallStack.Push(cs.Name);
        try
        {
            EvalComptimeStmt(fn.Body);
            // A `void` function returns by falling off its end (`std.debug.assert`).
            if (c.Type.Unqualified is CType.VoidType) { return CtVoid.Value; }
            ComptimeMiss ??= $"the end of '{cs.Name}' (no return value)";
            return null;   // fell off the end with no `return` value — treat as non-constant
        }
        catch (ComptimeReturn r)
        {
            if (r.Value is null && c.Type.Unqualified is CType.VoidType) { return CtVoid.Value; }   // a bare `return;`
            return r.Value is { } rv ? RetypeTo(rv, c.Type) : null;
        }
        finally
        {
            _comptimeFrame = saved;
            _comptimeCallStack.Pop();
        }
    }

    /// <summary>The functions the interpreter is inside, innermost first, for a diagnostic that names where an
    /// evaluation stopped.</summary>
    private readonly Stack<string> _comptimeCallStack = new();

    /// <summary>Symbol → <see cref="FuncDef"/> index over <see cref="Functions"/>, keyed by
    /// reference identity (<see cref="Symbol"/> is a plain class — the same instance is shared by
    /// the declaration and every call site, so the default comparer is exactly right). Built
    /// lazily and re-synced by count — <see cref="Functions"/> is append-only while comptime folds can
    /// run (both front-ends only <c>Add</c>; the one removal, of Zig's comptime-only instances, happens
    /// after the last fold resolves), so a count mismatch is the complete invalidation signal. Replaces
    /// a per-call linear scan of the function list.</summary>
    private readonly Dictionary<Symbol, FuncDef> _funcDefIndex = new();

    /// <summary>The lowered <see cref="FuncDef"/> for a callee symbol, by reference identity (the
    /// same <see cref="Symbol"/> instance is shared by the declaration and every call site), or null
    /// if the function has no lowered body (extern / not a user function).</summary>
    private FuncDef? FindFuncDef(Symbol sym)
    {
        if (_funcDefIndex.Count != Functions.Count)
        {
            _funcDefIndex.Clear();
            foreach (var f in Functions) { _funcDefIndex[f.Sym] = f; }
        }
        if (_funcDefIndex.TryGetValue(sym, out var fn)) { return fn; }
        // Not lowered yet: ask the front-end to lower it now (E2), then look again.
        if (DemandFuncBody is { } demand && demand(sym))
        {
            _funcDefIndex.Clear();
            foreach (var f in Functions) { _funcDefIndex[f.Sym] = f; }
            return _funcDefIndex.TryGetValue(sym, out var demanded) ? demanded : null;
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
        if (EvalComptime(a.Value) is not { } rhs) { return null; }

        switch (a.Target)
        {
            // A local / parameter.
            case VarRef vr:
            {
                // A frame local, else a comptime variable that outlives the evaluation (E3).
                var store = _comptimeFrame is { } fr && fr.ContainsKey(vr.Sym) ? fr
                    : ComptimeGlobals.ContainsKey(vr.Sym) ? ComptimeGlobals
                    : _comptimeFrame ?? throw new ComptimeAbort("comptime assignment outside a call frame");
                if (a.CompoundOp is { } vop)
                {
                    if (!store.TryGetValue(vr.Sym, out var vcur) || CombineBin(vop, vcur, rhs) is not { } vcomb)
                    {
                        return null;
                    }
                    rhs = vcomb;
                }
                return store[vr.Sym] = RetypeTo(rhs, vr.Sym.Type);
            }

            // A struct field — `c.field = v`. EvalComptime(m.Base) returns the SAME CtStruct the
            // frame holds (by reference), so mutating its field map writes through to the local; a
            // nested `c.inner.field = v` likewise mutates the nested struct in place.
            case Member m when EvalComptime(m.Base) is CtStruct st:
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
                EvalComptimeList(b.Stmts);
                break;

            case Seq q:
                EvalComptimeList(q.Stmts);
                break;

            case Goto g:
                throw new ComptimeGoto(g.Label);

            // A switch statement: run the matching section (or the default one); a `break` leaves the switch.
            case Switch sw:
            {
                if (EvalComptime(sw.Subject) is not CtInt subject) { throw new ComptimeAbort("non-constant comptime switch subject"); }
                var section = sw.Sections.FirstOrDefault(sec => sec.Labels.Any(l => l.CaseExpr is not null && LabelMatches(l, subject.Value)))
                    ?? sw.Sections.FirstOrDefault(sec => sec.Labels.Any(l => l.CaseExpr is null));
                if (section is null) { break; }
                try { EvalComptimeList(section.Body); }
                catch (ComptimeBreak) { }
                break;
            }

            case Labeled l:
                EvalComptimeStmt(l.Body);
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

            // `return error.X;` through an errdefer boundary.
            case ZigErrorThrow zet:
                throw new ComptimeReturn { Value = EvalComptime(zet.Code) is CtInt thrown ? new CtError(thrown.Value) : null };

            case Break:
                throw new ComptimeBreak();

            case Continue:
                throw new ComptimeContinue();

            default:
                throw new ComptimeAbort("comptime: unsupported statement " + s.GetType().Name);
        }
    }

    /// <summary>True when a switch label (a value or an inclusive range) matches <paramref name="value"/>.</summary>
    private bool LabelMatches(SwitchLabel label, System.Int128 value)
    {
        if (label.CaseExpr is not { } lo || EvalComptime(lo) is not CtInt low) { return false; }
        if (label.HiExpr is null) { return low.Value == value; }
        return EvalComptime(label.HiExpr) is CtInt high && value >= low.Value && value <= high.Value;
    }

    /// <summary>Run a statement list, resuming at a label later in THIS list when a <c>goto</c> from
    /// inside it names one (a labeled value block's exit). Any other jump propagates.</summary>
    private void EvalComptimeList(IReadOnlyList<CStmt> stmts)
    {
        for (var k = 0; k < stmts.Count; k++)
        {
            try
            {
                EvalComptimeStmt(stmts[k]);
            }
            catch (ComptimeGoto g)
            {
                var at = -1;
                for (var j = k + 1; j < stmts.Count && at < 0; j++)
                {
                    if (stmts[j] is Labeled { Name: var name } && name == g.Label) { at = j; }
                }
                if (at < 0) { throw; }
                k = at - 1;
            }
        }
    }
}
