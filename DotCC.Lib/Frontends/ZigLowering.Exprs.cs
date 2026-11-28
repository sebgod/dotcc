#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions: the <c>LowerExpr</c> dispatcher, <c>LowerExprSink</c> and every
/// value-position lowering helper (calls, concat/repeat, catch/orelse helpers, coercions).
/// <c>LowerExpr</c> routes each node kind to a category method in its own partial file:
/// <c>ZigLowering.Exprs.Literals.cs</c> (literals), <c>.Names.cs</c> (identifiers and field
/// access), <c>.Access.cs</c> (deref, subscript, slices, unwrap), <c>.Operators.cs</c>
/// (operators, comptime, concat/repeat), <c>.ControlFlow.cs</c> (value if/switch, labeled
/// blocks, orelse/catch) and <c>.Builtins.cs</c> (builtins and asm).
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    // ---- expressions -----------------------------------------------------

    /// <summary>Routes an expression node to its category method (see the <c>ZigLowering.Exprs.*.cs</c> files);
    /// calls and struct initializers delegate straight to their lowering.</summary>
    private CExpr LowerExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit or Zig.FloatLit or Zig.TrueLit or Zig.FalseLit or Zig.CharLit or Zig.StrLit or Zig.NullLit or Zig.UndefinedLit or Zig.VoidArg or Zig.VoidValue or Zig.EnumLit or Zig.ErrorLit or Zig.ErrorSet or Zig.ErrSetMerge:
                return LowerLiteralExpr(expr);
            case Zig.Ident id:
                return LowerIdentExpr(id);
            case Zig.Field fld:
                return LowerFieldExpr(expr, fld);
            case Zig.Deref or Zig.Index or Zig.SliceRange or Zig.SliceOpen or Zig.SliceRangeSentinel or Zig.SliceOpenSentinel or Zig.Unwrap:
                return LowerAccessExpr(expr);
            case Zig.Add or Zig.Sub or Zig.Mul or Zig.AddWrap or Zig.SubWrap or Zig.MulWrap or Zig.AddSat or Zig.SubSat or Zig.MulSat or Zig.ShlSat or Zig.DivOp or Zig.ModOp or Zig.CmpEq or Zig.CmpNe or Zig.CmpLt or Zig.CmpGt or Zig.CmpLe or Zig.CmpGe or Zig.BoolOr or Zig.BoolAnd or Zig.BitAnd or Zig.BitXor or Zig.BitOr or Zig.Shl or Zig.Shr or Zig.PreNeg or Zig.PreNegWrap or Zig.PreBitNot or Zig.PreNot or Zig.PreAddrOf or Zig.PreTry or Zig.PreComptime or Zig.Concat or Zig.Repeat:
                return LowerOperatorExpr(expr);
            case Zig.SwitchExprEmpty or Zig.Grouped or Zig.IfElseExpr or Zig.SwitchExpr or Zig.LabeledBlock or Zig.LabeledSwitch or Zig.OrElse or Zig.CatchOp or Zig.CatchCapture or Zig.OrElseArm or Zig.CatchArm or Zig.CatchCaptureArm or Zig.ReturnExpr or Zig.FbBreak or Zig.FbBreakLabel or Zig.FbBreakLabelValue or Zig.FbBreakValue or Zig.FbContinue or Zig.FbContinueLabel or Zig.FbContinueLabelValue:
                return LowerControlFlowExpr(expr);
            case Zig.BuiltinCall or Zig.AsmExpr:
                return LowerBuiltinExpr(expr);
            // A `.{…}` with no sink: a POSITIONAL list is an inferred tuple literal (`const t =
            // .{a, b};`); a NAMED list still needs a struct result type, so LowerStructInit errors
            // there as before (Milestone G routes both through the one method).
            case Zig.AnonStructInit:
            case Zig.AnonStructInitEmpty:
                return LowerStructInit(expr, null);

            // Typed struct literal `Type{ .field = … }` (Zig's CurlySuffixExpr). The struct type
            // is named explicitly, so — unlike `.{…}` — it needs no sink and is valid anywhere.
            case Zig.TypedStructInit t:       // CurlySuffix -> Type '{' FieldInits? '}'
                return LowerTypedStructInit(t.Arg0, Flatten(t.Arg2));
            // `struct { fn f(…) … }.f` in value position: the closure idiom's method as a function value.
            case Zig.StructMemberExpr sme:
            {
                var method = ReifyClosureExpr(expr, sme);
                return new VarRef(method) { Type = method.Type };
            }

            // call of a named function (bare-identifier callee).
            case Zig.CallArgs { Arg2: not null } c:   return LowerCall(c.Arg0, c.Arg2);
            case Zig.CallArgs { Arg2: null } c: return LowerCall(c.Arg0, null);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }

    // The cap on a `**` repeat count / a folded literal element count — a guard so a pathological
    // `"x" ** 1_000_000_000` can't blow up the segment/element list (well beyond any real use).
    private const long MaxRepeat = 1 << 16;

    /// <summary>Lower a Zig comptime concat <c>a ++ b</c>. V1 folds the LITERAL case:
    /// <list type="bullet">
    /// <item>two STRING literals → a single string literal whose raw quoted segments are the operands'
    /// segments concatenated — exactly C's adjacent-string-literal concatenation, reusing the identical
    /// decode/encode path (<see cref="DotCC.EmitHelpers"/>), so escapes / UTF-8 / the trailing NUL are
    /// handled once and the bytes are identical;</item>
    /// <item>two TYPED ARRAY literals of the same element type (<c>[_]u8{1,2} ++ [_]u8{3}</c>) → one array
    /// literal over the merged positional elements, re-fed through the shared <see cref="BuildArrayInit"/>
    /// (so the result is byte-identical to writing the merged literal directly).</item>
    /// </list>
    /// A non-literal operand (a comptime const, <c>@typeName</c>, a sink-needing anon <c>.{…}</c>, or a
    /// mismatched element type) still needs the comptime aggregate engine (road-to-zig-std S5–S6) and is a
    /// loud cut.</summary>
    private CExpr LowerConcat(Item leftItem, Item rightItem)
    {
        // String fold — each operand resolved to a comptime STRING value (a literal, a `const` bound to
        // one, or a nested `++`/`**` fold), then segments concatenated.
        if (TryFoldStringConcat(leftItem, rightItem) is { } str) { return str; }
        // Array-shaped fold — each operand is a typed `[N]T{…}` literal, a `const` bound to one, or an
        // anon `.{…}` (element type BORROWED from the other, typed operand). The merged positional items
        // re-feed the one BuildArrayInit the plain `[_]T{…}` uses.
        var lIsArr = TryArrayOperand(leftItem, out var lElem, out var lItems);
        var rIsArr = TryArrayOperand(rightItem, out var rElem, out var rItems);
        if (lIsArr && rIsArr)
        {
            var elem = lElem ?? rElem;
            if (elem is null)
            {
                throw new IrUnsupportedException(
                    "zig `a ++ b`: both operands are untyped anon `.{…}` — the common element/tuple type needs the "
                    + "comptime aggregate engine (road-to-zig-std S5); give one operand an explicit `[_]T{…}` type");
            }
            if (lElem is not null && rElem is not null && !lElem.Equals(rElem))
            {
                throw new IrUnsupportedException(
                    $"zig `a ++ b`: array element types differ ({lElem.Describe()} vs {rElem.Describe()})");
            }
            var merged = new List<Item>(lItems.Count + rItems.Count);
            merged.AddRange(lItems);
            merged.AddRange(rItems);
            return BuildArrayInit(merged, new CType.Array(elem, null));   // inferred length = element count
        }
        // Two array VALUES of comptime-known length (std.unicode's `break :first a ++ b ++ c` over `@splat` locals, task
        // #82): a new array of both lengths, element by element. Both operands are read once per element, so each must
        // be an lvalue-like array (a local, a global, a field, or a nested concat), never a call.
        CType? leftProbe = null, rightProbe = null;
        using (EnterThrowawayHoist())
        {
            try { leftProbe = LowerExpr(leftItem).Type; rightProbe = LowerExpr(rightItem).Type; }
            catch (IrUnsupportedException) { leftProbe = null; }
        }
        if (leftProbe?.Unqualified is CType.Array { Count: int ln } la && rightProbe?.Unqualified is CType.Array { Count: int rn } ra
            && la.Element.Equals(ra.Element) && ln + rn <= MaxRepeat)
        {
            var left = LowerExpr(leftItem);
            var right = LowerExpr(rightItem);
            if (left is not (VarRef or Member or StackArray) || right is not (VarRef or Member or StackArray))
            {
                throw new IrUnsupportedException("zig `a ++ b` of array values: each operand must be a named array (bind a call's result to a `const` first)");
            }
            IEnumerable<CExpr> Elems(CExpr arr, int n) => arr is StackArray sa
                ? sa.Elems
                : Enumerable.Range(0, n).Select(k => (CExpr)new DotCC.Ir.Index(arr,
                    new LitInt(k.ToString(System.Globalization.CultureInfo.InvariantCulture), k) { Type = CType.Int }) { Type = la.Element });
            var joined = Elems(left, ln).Concat(Elems(right, rn)).ToList();
            return new StackArray(la.Element, joined) { Type = new CType.Array(la.Element, ln + rn) };
        }
        throw new IrUnsupportedException(
            "zig `a ++ b`: only string / array-literal concatenation (incl. a `const` bound to a comptime string/array, "
            + "and an anon `.{…}` borrowing a typed operand's element type) is lowered (road-to-zig-std S9/S5); an "
            + "`@typeName`-of-user-type or a non-literal operand needs the fuller comptime aggregate engine (S5–S6)");
    }

    /// <summary>Fold <c>a ++ b</c> when both operands are comptime STRING values (a string literal, a
    /// <c>const</c> bound to one, or a nested string <c>++</c>/<c>**</c>): concatenate the raw quoted
    /// segments (C adjacent-string-literal concatenation, reusing the shared encode path so escapes /
    /// UTF-8 / NUL are handled once). Returns null when either operand is not a comptime string — the
    /// caller then tries the array-literal fold or the loud cut.</summary>
    private LitStr? TryFoldStringConcat(Item leftItem, Item rightItem)
    {
        return AsBytes(TryEvalComptime(leftItem)) is { } left && AsBytes(TryEvalComptime(rightItem)) is { } right
            ? AsLitStr(StringValue(left.Concat(right))) : null;
    }

    /// <summary>Lower a Zig comptime repeat <c>a ** n</c> with a comptime-integer count. V1 folds the
    /// LITERAL case, symmetric with <see cref="LowerConcat"/>: a STRING literal repeats its raw segments
    /// (<c>"x" ** 3</c> → the pooled literal <c>"xxx"</c>); a TYPED ARRAY literal repeats its positional
    /// elements through the shared <see cref="BuildArrayInit"/>. <c>n == 0</c> is a loud cut for arrays (an
    /// empty array literal is unsupported) and yields the empty string literal for strings. A non-literal
    /// operand, a non-constant count, or a negative / oversized count is a loud cut (the general case needs
    /// the comptime aggregate engine, S5–S6).</summary>
    private CExpr LowerRepeat(Item leftItem, Item countItem)
    {
        // The count resolves to a comptime integer (a literal or a `const` bound to one).
        if (ComptimeFoldedLong(countItem) is not { } n || n < 0 || n > MaxRepeat)
        {
            throw new IrUnsupportedException(
                $"zig `a ** n`: the repeat count must be a comptime integer in [0, {MaxRepeat}]"
                + " (a non-constant count needs the comptime aggregate engine, S5–S6)");
        }
        // String fold — the operand resolved to a comptime string value, segments repeated.
        if (AsLitStr(TryEvalComptime(leftItem)) is LitStr ls)
        {
            var segs = new List<string>(ls.Segments.Count * (int)n);
            for (var i = 0; i < n; i++) { segs.AddRange(ls.Segments); }
            DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
            return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
        }
        // Array-literal fold — a typed `[N]T{…}` or a `const` bound to one (an anon `.{…} ** n` has no
        // element type to borrow, so it stays a loud cut below).
        if (TryArrayOperand(leftItem, out var elem, out var items) && elem is not null)
        {
            var merged = new List<Item>(items.Count * (int)n);
            for (var i = 0; i < n; i++) { merged.AddRange(items); }
            return BuildArrayInit(merged, new CType.Array(elem, null));   // n == 0 → empty → BuildArrayInit loud-cuts
        }
        throw new IrUnsupportedException(
            "zig `a ** n`: only string / typed-array-literal repeat (incl. a `const` bound to a comptime string/array) "
            + "is lowered (road-to-zig-std S9); an untyped anon `.{…}` operand or a non-literal operand needs the fuller "
            + "comptime aggregate engine (S5–S6)");
    }

    /// <summary><c>a or b</c> / <c>a and b</c> whose LEFT side is a settled comptime question: zig never analyses the
    /// right side when the left decides the result (std.math.cast's <c>(is_comptime or maxInt(@TypeOf(x)) &gt; …)</c>,
    /// where <c>maxInt(comptime_int)</c> would not compile), so it is not lowered at all. Otherwise the ordinary
    /// runtime operator.</summary>
    private CExpr ShortCircuit(BinOp op, Item left, Item right)
    {
        if (TryFoldComptimeCondition(left) is { } settled && settled == (op == BinOp.LogOr))
        {
            return new LitBool(settled) { Type = CType.Bool };
        }
        // A left side that is always false (`and`) or always true (`or`) though not wholly comptime-known, because a
        // conjunct of it settles the value while another is a runtime call (std.mem's `use_vectors and !inValgrind() and …
        // and (@typeInfo(T) == .int or …) and isPowerOfTwo(@bitSizeOf(T))` over a struct T, task #108): the right side
        // never runs, so the value is the left side's, its runtime parts still evaluated.
        if (SettlesShortCircuit(left, op == BinOp.LogOr))
        {
            return LowerExpr(left);
        }
        return Bin(op, left, right);
    }

    /// <summary>True when <paramref name="e"/> always evaluates to <paramref name="value"/>: it folds to it at compile time,
    /// or it is an <c>and</c> (for false) / <c>or</c> (for true) chain one of whose operands does.</summary>
    private bool SettlesShortCircuit(Item e, bool value)
    {
        while (e.Content is Zig.Grouped g) { e = g.Arg1; }
        if (TryFoldComptimeCondition(e) is { } folded) { return folded == value; }
        // A comptime call (`std.math.isPowerOfTwo(@bitSizeOf(T))`) settles through the interpreter; its lowering is discarded.
        if (e.Content is Zig.CallArgs)
        {
            CExpr call;
            using (EnterThrowawayHoist()) { call = LowerExpr(e); }
            if (_ir.EvalComptimeValue(call) is IrModule.CtBool { Value: var called }) { return called == value; }
            return false;
        }
        return (value ? e.Content as Zig.BoolOr is { } o ? (o.Arg0, o.Arg2) : ((Item, Item)?)null
                      : e.Content as Zig.BoolAnd is { } a ? (a.Arg0, a.Arg2) : null) is var (l, r)
            && (SettlesShortCircuit(l, value) || SettlesShortCircuit(r, value));
    }

    /// <summary>The comptime string a field path rooted at a comptime aggregate holds, spliced to a string literal;
    /// null when the root is not one (checked before anything lowers, so this stays cheap and side-effect free
    /// for every other field) or the value is not a byte slice.</summary>
    private LitStr? TryComptimeAggregateString(Item expr)
    {
        var root = expr;
        while (root.Content is Zig.Field or Zig.Grouped)
        {
            root = root.Content is Zig.Field rf ? rf.Arg0 : ((Zig.Grouped)root.Content).Arg1;
        }
        if (root.Content is not Zig.Ident rid || _symbols.Resolve(Tok(rid.Arg0)) is not { } rootSym
            || !_ir.ComptimeGlobals.ContainsKey(rootSym))
        {
            return null;
        }
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(expr); }
        return _ir.EvalComptimeValue(lowered) is IrModule.CtSlice slice && _ir.SpliceComptimeValue(slice) is SliceNew { Ptr: LitStr str }
            ? str : null;
    }

    /// <summary>Each <c>comptime var</c> holding a comptime STRING (std.Io.Writer.print's
    /// <c>comptime var literal: []const u8 = "";</c>), by symbol → its current value, updated by assignments
    /// at lowering time (<see cref="TryAssignComptimeVar"/>) and substituted where it is read.</summary>
    private readonly Dictionary<Symbol, LitStr> _comptimeStringVars = new();

    private LitStr? TryComptimeByteList(Item fieldInits)
    {
        var bytes = new List<int>();
        foreach (var element in Flatten(fieldInits))
        {
            if (element.Content is not Zig.FieldInitPositional pos) { return null; }
            CExpr lowered;
            using (EnterThrowawayHoist())
            {
                try { lowered = LowerExpr(pos.Arg0); }
                catch (IrUnsupportedException) { return null; }
            }
            var value = ComptimeLong(lowered);
            if (value is not { } b || b is < 0 or > 255) { return null; }
            bytes.Add((int)b);
        }
        return bytes.Count > 0 ? ComptimeStringFromBytes(bytes) : null;
    }

    private static LitStr ComptimeStringFromBytes(IEnumerable<int> bytes)
    {
        // Printable ASCII is spelled as itself (an emitted program reads "foobar"); any other byte is a hex escape, and a
        // raw character after one starts a new quoted segment, since a hex escape would swallow a following hex digit.
        var segs = new List<string>();
        var sb = new System.Text.StringBuilder("\"");
        var afterEscape = false;
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7E && b != '"' && b != '\\')
            {
                if (afterEscape)
                {
                    segs.Add(sb.Append('"').ToString());
                    sb.Clear().Append('"');
                    afterEscape = false;
                }
                sb.Append((char)b);
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                afterEscape = true;
            }
        }
        segs.Add(sb.Append('"').ToString());
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
    }

    /// <summary>Evaluate <c>@typeName(T)</c> to a comptime string value, or null if it is not that
    /// builtin or the type is not spellable (see <see cref="ZigTypeSpelling"/>). Non-throwing — the
    /// comptime-value path (<see cref="EvalComptimeValue"/>) needs a null-return, not the loud cut
    /// <see cref="LowerBuiltinCall"/> raises, so an unspellable operand falls through to the loud cut
    /// there or the caller's own.</summary>
    private LitStr? TryEvalTypeNameBuiltin(Zig.BuiltinCall b)
    {
        if (Tok(b.Arg0) != "@typeName") { return null; }
        var args = Flatten(b.Arg2);
        return args.Count == 1 && ZigTypeSpelling(args[0]) is { } spelling ? ZigStringLiteral(spelling) : null;
    }

    /// <summary>Recognize an ARRAY-SHAPED operand of a comptime <c>++</c>/<c>**</c> fold and yield its
    /// <paramref name="element"/> type + RAW positional element items (re-lowered by
    /// <see cref="BuildArrayInit"/> at the merged extent). Three forms:
    /// <list type="bullet">
    /// <item>a DIRECT typed array literal <c>[N]T{…}</c> / <c>[_]T{…}</c> — <paramref name="element"/> is
    /// its element type;</item>
    /// <item>a bare name bound to an array-literal <c>const</c> (<see cref="_comptimeArrayConsts"/>) —
    /// same, lowering the recorded element-type item now (body-lowering time, safe);</item>
    /// <item>an anon <c>.{…}</c> — items only, with <paramref name="element"/> <c>null</c> (the type is
    /// BORROWED from the other, typed operand by the caller; two untyped anons are a loud cut).</item>
    /// </list>
    /// Also unwraps a <c>(grouped)</c> operand. Returns false for any non-array operand (a string
    /// literal, a non-literal expression) — those take the string path or the loud cut.</summary>
    private bool TryArrayOperand(Item item, out CType? element, out IReadOnlyList<Item> posItems)
    {
        // `(expr)` — unwrap so a parenthesized array operand composes.
        if (item.Content is Zig.Grouped g) { return TryArrayOperand(g.Arg1, out element, out posItems); }
        // A DIRECT typed array literal `[N]T{…}` / `[_]T{…}`.
        if (item.Content is Zig.TypedStructInit { Arg0.Content: Zig.TyArray ta } tsi)
        {
            element = LowerDataType(ta.Arg3);
            posItems = Flatten(tsi.Arg2);
            return true;
        }
        // A `const` bound to an array literal (road-to-zig-std S5) — the raw items were recorded WITHOUT
        // lowering; lower the element TYPE now (body-lowering time, safe) and re-feed BuildArrayInit.
        if (item.Content is Zig.Ident id && _comptimeArrayConsts.TryGetValue(Tok(id.Arg0), out var rec))
        {
            element = LowerDataType(rec.ElemTypeItem);
            posItems = rec.Items;
            return true;
        }
        // An anon `.{…}` — the element type is inferred from the OTHER operand (null here); the items
        // lower at that borrowed element type via BuildArrayInit. (A named `.field =` element is rejected
        // there — an array literal is all-positional.)
        if (item.Content is Zig.AnonStructInit anon)
        {
            element = null;
            posItems = Flatten(anon.Arg2);
            return true;
        }
        element = null;
        posItems = [];
        return false;
    }

    /// <summary>Lower a call. Two callee shapes: a bare identifier bound to a named function
    /// (free function or <c>extern</c>/libc) → <see cref="BuildCall"/>; a <c>base.name(args)</c>
    /// field callee → <see cref="LowerMethodCall"/> (a UFCS instance method or a static/associated
    /// function). An indirect / function-pointer callee is still deferred.</summary>
    private CExpr LowerCall(Item calleeItem, Item? argListItem)
    {
        var result = LowerCallInner(calleeItem, argListItem);
        // A call is a side effect, evaluated AFTER its arguments. Mark the ANF impurity watermark so a
        // LATER sibling `catch`/`orelse` in the same statement is NOT hoisted past this call (which
        // would reorder it). An argument's own `catch`/`orelse` was already lowered (and checked)
        // inside LowerCallInner, BEFORE this set — so `f(a catch b)` still hoists cleanly.
        if (_hoist is not null) { _hoistImpureSeen = true; }
        return result;
    }

    /// <summary>A bare name inside a container (a const's RHS re-lower or a method body) that names a SIBLING const or
    /// container <c>var</c>, of this container or an enclosing one (zig's lexical scoping), or null. An untyped const takes
    /// <paramref name="useSink"/>, its reader's result type (std.bit_set's <c>masks: [*]MaskInt = empty_masks_ptr</c>
    /// with <c>const empty_masks_ptr = empty_masks_data[1..2];</c>, task #130); a container var is its mangled global.</summary>
    private CExpr? TryLowerSiblingMember(string name, CType? useSink)
    {
        for (var cc = _currentConstContainer ?? _currentContainer; cc is not null; cc = _containerParents.GetValueOrDefault(cc))
        {
            if (_containerConsts.TryGetValue(cc, out var sibs) && sibs.TryGetValue(name, out var sib))
            {
                return LowerContainerConst(cc, name, sib.typeItem, sib.rhs, useSink);
            }
            if (EnsureContainerVar(cc, name) is { } sibVar)
            {
                return new VarRef(sibVar) { Type = sibVar.Type, IsLValue = true };
            }
        }
        return null;
    }

    /// <summary>True for <c>void</c> and for void as data (the runtime <c>Unit</c>, task #114).</summary>
    private static bool IsVoidValueType(CType? t) => t?.Unqualified is CType.VoidType or CType.Named { Name: "Unit" };

    /// <summary><c>try e</c> over the already-lowered operand <paramref name="inner"/>: unwrap the error union's payload, or
    /// propagate its error by throwing ZigErrorReturn (caught at the enclosing <c>!T</c> function's emitted try/catch, the
    /// backend's Func wrap). Shared by the sink-free path and a <c>try</c> whose operand needs the result type (task #130).</summary>
    private static CExpr LowerTry(CExpr inner)
    {
        if (inner.Type.Unqualified is not CType.ErrorUnion eu)
        {
            throw new IrUnsupportedException("zig `try` requires an error-union operand");
        }
        var unwrapped = new ZigTry(inner) { Type = eu.Payload };
        // A `create`-style error-union-over-pointer (`Error!*T`, Milestone U) carries its
        // payload as a `nuint` (a pointer can't be an `ErrUnion<T>` generic arg), so
        // `ErrUnion.Try(...)` yields a `nuint`; cast it back to the `T*` the payload names.
        // `create` is the only producer of a pointer-payload union, so the cast is
        // exactly-and-only correct here.
        if (eu.Payload.Unqualified is CType.Pointer)
        {
            return new Cast(eu.Payload, unwrapped) { Type = eu.Payload };
        }
        return unwrapped;
    }

    private CExpr LowerCallInner(Item calleeItem, Item? argListItem)
        => LowerCallItems(calleeItem, argListItem is null ? new List<Item>() : Flatten(argListItem));

    /// <summary>The body of <see cref="LowerCallInner"/> over already-split argument items, so a call spelled
    /// another way (<c>@call(modifier, f, .{ a, b })</c>) lowers exactly as <c>f(a, b)</c>.</summary>
    private CExpr LowerCallItems(Item calleeItem, List<Item> argItems)
    {

        // `base.method(args)` — a method (UFCS) or associated-function call.
        if (calleeItem.Content is Zig.Field fld)
        {
            return LowerMethodCall(fld, argItems);
        }

        // `.name(args)` reaching here had no container-typed result location (LowerExprSink takes the
        // decl literal when it does) — zig rejects it the same way, for want of a type to look `name` up in.
        if (calleeItem.Content is Zig.EnumLit declLit)
        {
            throw new IrUnsupportedException(
                $"decl literal `.{Tok(declLit.Arg1)}(…)` needs a struct/union result type to resolve against "
                + "(a typed `const`/`var`, a typed parameter, a `return` of a typed function)");
        }
        // Inside std's own mem.zig a bare `asBytes(...)` / `sliceAsBytes(...)` (std.mem.indexOf's `sliceAsBytes(haystack)`)
        // is the curated lowering too, as `std.mem.asBytes(...)` from any other module is: the rows flagged BareInStdMem.
        if (calleeItem.Content is Zig.Ident { Arg0: var curatedTok }
            && ZigCuratedStd.FindFunction("std.mem", Tok(curatedTok)) is { BareInStdMem: true } bareRow
            && IsStdModule("mem.zig"))
        {
            return LowerCuratedStdFn(bareRow, argItems);
        }
        if (calleeItem.Content is not Zig.Ident id)
        {
            throw new IrUnsupportedException("zig call: only a bare-identifier or `base.method` callee is lowered yet (got "
                + (calleeItem.Content?.GetType().Name ?? "null") + ")");
        }
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name);
        // In a lazy module, a bare call to a not-yet-lowered SIBLING declares it on demand (its body is
        // enqueued for the top-level drain), so `isPrint` can call `isAscii`/`isControl` (road-to-zig-std S2).
        if (sym is null && _lazy) { sym = EnsureDeclLowered(name); }
        // The root unit's own function named while its containers register (task #123: `b = maxOf(u16)`).
        if (sym is null && _registeringRootContainers) { sym = DeclareRootFnEarly(name); }
        // A re-export alias (`pub const indexOfScalar = findScalar;`, `const f = util.f;`): call the
        // declaration it names, in its own module when that is another one.
        if (sym is null && ResolveExportedDecl(name, raiseIfSkipped: true) is { } aliased)
        {
            if (aliased.Owner != this) { return CallExportedDecl(aliased.Owner, aliased.Sym, argItems); }
            sym = aliased.Sym;
        }
        // A local comptime alias of a function (`const add = switch (sign) { .pos => math.add, … };`).
        if (sym is null && _fnAliases.TryGetValue(name, out var fnAlias))
        {
            return CallExportedDecl(fnAlias.Owner, fnAlias.Sym, argItems);
        }
        // A bare call to a function of the container in scope or one enclosing it (`self.* = init(allocator);`
        // inside AlignedManaged): zig resolves a container's declarations lexically, as `Self.init(…)`.
        if (sym is null)
        {
            for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
            {
                if (EnsureMethodDeclared(c, name) is { } sibling) { return CallStaticMethod(sibling, argItems); }
            }
        }
        // A top-level const naming a container's function (`pub const featureSet =
        // CpuFeature.FeatureSetFns(Feature).featureSet;` in std/Target/x86.zig): the call is that method call.
        if (sym is null && ResolveMethodAlias(name) is { } aliasedMethod)
        {
            return CallStaticMethod(aliasedMethod, argItems);
        }
        if (sym is null) { throw new IrUnsupportedException($"call to unresolved name '{name}'"); }
        // A type-returning generic (wall-plan W4) is a COMPTIME type constructor — calling it in value
        // position is meaningless; it must appear in a TYPE position (a type annotation / alias / typed
        // literal), where LowerType reifies it. Reject a value-position call clearly.
        if (_typeReturningGenerics.ContainsKey(sym))
        {
            throw new IrUnsupportedException(
                $"'{name}' is a type-returning generic — use it in a TYPE position (`const x: {name}(…) = …` / "
                + $"`{name}(…){{…}}`), not as a value");
        }
        // A real (named) function → a direct, by-name call. A GENERIC (comptime-param template,
        // wall-plan W3a) instead instantiates a specialized body per resolved comptime-argument tuple
        // and calls the mangled instance.
        if (sym.Kind is SymKind.Func && sym.Type.Unqualified is CType.Func)
        {
            if (_genericFns.TryGetValue(sym, out var generic)) { return InstantiateGeneric(sym, generic, argItems); }
            return BuildCall(sym, argItems, receiver: null);
        }
        // A fn-pointer VALUE — a `delegate*` local / parameter typed `CType.Func` (Milestone W,
        // part 1a) → an INDIRECT call through the variable, each argument result-located against
        // the fn-pointer's parameter type (Zig result-locates call arguments). Renders as
        // `op(args)` over the (renamed-safe) VarRef.
        if (sym.Type.Unqualified is CType.Func fnptr)
        {
            var callee = new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            if (argItems.Count != fnptr.Params.Count)
            {
                throw new IrUnsupportedException(
                    $"call through fn-pointer '{name}': expected {fnptr.Params.Count} argument(s), got {argItems.Count}");
            }
            var args = new List<CExpr>(argItems.Count);
            for (var i = 0; i < argItems.Count; i++)
            {
                args.Add(LowerExprSink(argItems[i], fnptr.Params[i]));
            }
            return new IndirectCall(callee, args) { Type = fnptr.Return };
        }
        throw new IrUnsupportedException($"'{name}' is not callable (expected a function or a fn-pointer value)");
    }

    /// <summary>Build an IR <see cref="Call"/> to a resolved function symbol, optionally with a
    /// synthesized leading <paramref name="receiver"/> argument (an instance method's <c>self</c>).
    /// Carries the callee's parameter types (so the backend coerces each argument as C does at a
    /// call) and the symbol (so it emits the legalized target name); an <c>extern</c>/libc symbol
    /// (<see cref="Symbol.FromSystemHeader"/>) drops the symbol so the call binds to dotcc's
    /// <c>Libc</c> runtime by bare name. Each fixed argument's parameter type is its sink (Zig
    /// result-locates call arguments), accounting for the receiver's parameter slot.</summary>
    /// <summary>True for a void-typed argument the C# backend may erase without losing anything: the
    /// void value itself, or a read of a void symbol. zig's <c>void</c> has no runtime representation, so
    /// the backend drops void parameters and their arguments; a SIDE-EFFECTING void argument
    /// (<c>f(g())</c> with <c>g</c> returning void) would lose its call that way, so it is rejected.</summary>
    /// <summary>Is every operand of an <c>asm</c> input list a plain name (<c>[x] "r" (x)</c>)? Such an input has nothing to
    /// evaluate, so an empty barrier over it lowers to nothing (task #174).</summary>
    private static bool AsmInputsArePlainNames(Item items) => items.Content switch
    {
        Zig.AsmItemsNone => true,
        Zig.AsmItemsOne one => one.Arg0.Content is Zig.AsmItemExpr { Arg5.Content: Zig.Ident },
        Zig.AsmItemsCons cons => cons.Arg0.Content is Zig.AsmItemExpr { Arg5.Content: Zig.Ident } && AsmInputsArePlainNames(cons.Arg2),
        _ => false,
    };

    private static bool IsErasableVoid(CExpr e) => e is DefaultLit or VarRef || e is Paren p && IsErasableVoid(p.Inner)
        // A `void` FIELD read (std.sort's `lessThanFn(ctx.sub_ctx, …)` with `context: void`) has no effect either.
        || e is Member { Base: var fieldBase } && IsPurePath(fieldBase);

    /// <summary>An open-ended slice <c>a[lo..]</c>. Over a slice or an array (or a pointer to one) it is the sub-slice to the
    /// end. Over a MANY-item pointer (<c>[*]T</c> / <c>[*c]T</c>), which has no length, zig's result is the pointer advanced
    /// by <c>lo</c>, still a many-item pointer (std.crypto.blake3's <c>block[j * 4 ..][0..4]</c> over <c>inputs[i] + offset</c>,
    /// task #140). A single-item <c>*T</c> is refused, as zig refuses it.</summary>
    private CExpr LowerOpenSlice(Item baseItem, Item loItem)
    {
        var baseExpr = LowerExpr(baseItem);
        var lo = LowerUsizeOperand(loItem);
        if (baseExpr.Type.Unqualified is CType.Pointer { Pointee.Unqualified: not CType.Array } manyPtr)
        {
            if (PointerSizeOfValue(baseItem) == "one")
            {
                throw new IrUnsupportedException("zig: slice of single-item pointer must be bounded (`p[lo..]` needs a many-item pointer, a slice, or an array)");
            }
            return new Binary(BinOp.Add, baseExpr, lo) { Type = manyPtr };
        }
        return BuildSlice(baseExpr, lo, null);
    }

    /// <summary>An index or slice bound (<c>a[i]</c>, <c>a[lo..hi]</c>): zig gives it a <c>usize</c> result location, so a
    /// result-located cast (<c>inp[0..@intCast(subtree_len)]</c> in std.crypto.blake3, task #140; std.base64's
    /// <c>alphabet_chars[@truncate(bits &gt;&gt; 18 &amp; 0x3f)]</c>) lowers at that sink. Any other operand lowers as before, so
    /// its emitted shape does not change.</summary>
    private CExpr LowerUsizeOperand(Item operand) =>
        operand.Content is Zig.BuiltinCall { Arg2: not null, Arg0: var bt } && Tok(bt) is "@intCast" or "@truncate" or "@bitCast" or "@enumFromInt" or "@intFromFloat"
            ? LowerExprSink(operand, CType.ULong)
            : LowerExpr(operand);

    /// <summary>A variable, or a field path off one: reading it has no side effect.</summary>
    private static bool IsPurePath(CExpr e) => e switch
    {
        VarRef => true,
        Paren p => IsPurePath(p.Inner),
        Member m => IsPurePath(m.Base),
        _ => false,
    };

    /// <summary>The width runtime parameter <paramref name="index"/> of <paramref name="fn"/> spells (<c>u11</c>, <c>usize</c>),
    /// or null where it names a type (a generic's <c>T</c>), whose lowered carrier then stands in, never narrower.</summary>
    private int? SpelledParamBits(Symbol fn, int index)
    {
        if (!_fnParamInfos.TryGetValue(fn, out var infos)) { return null; }
        var runtime = infos.Where(p => !p.IsComptime).ToList();
        return index < runtime.Count ? DeclaredBitsFromSpelling(runtime[index].TypeAst) : null;
    }

    private CExpr BuildCall(Symbol sym, IReadOnlyList<Item> argItems, CExpr? receiver)
    {
        RecordRuntimeCall(sym);
        var fn = (CType.Func)sym.Type.Unqualified;
        var args = new List<CExpr>(argItems.Count + 1);
        var paramOffset = 0;
        if (receiver is not null) { args.Add(receiver); paramOffset = 1; }

        // Each fixed argument's parameter type is its sink (Zig result-locates a call argument),
        // so `f(.member)` / `f(.{…})` resolve against the parameter. The receiver, if any, has
        // already consumed parameter slot 0. A variadic tail argument has no fixed parameter
        // type → no sink (plain LowerExpr).
        for (var i = 0; i < argItems.Count; i++)
        {
            var pIndex = i + paramOffset;
            var paramSink = pIndex < fn.Params.Count ? fn.Params[pIndex] : null;
            // A runtime integer wider than the parameter is zig's "expected type" error (task #167).
            RejectIntegerNarrowing(argItems[i], paramSink, SpelledParamBits(sym, pIndex));
            var arg = LowerExprSink(argItems[i], paramSink);
            // `utf8Decode2(bytes[0..2].*)` (std.unicode): the comptime-length slice stands for its array copy (see the
            // `.*` lowering), and an array parameter is its element pointer, so the slice passes its `.Ptr`. A
            // parameter is immutable in zig, so no copy is observable.
            if (paramSink?.Unqualified is CType.Array { Element: var arrayParamElem } && arg.Type.Unqualified is CType.Slice
                && argItems[i].Content is Zig.Deref)
            {
                arg = new Member(arg, "Ptr", false) { Type = new CType.Pointer(arrayParamElem) };
            }
            // An aggregate VALUE (a slice, an array, a tuple) never coerces to a scalar parameter in zig ("expected type
            // 'u8', found '[]const [:0]const u8'"); passing one on would only make C# reject the emitted call.
            if (paramSink?.Unqualified is CType.Prim && arg.Type.Unqualified is CType.Slice or CType.Array or CType.Tuple)
            {
                throw new IrUnsupportedException(
                    $"call to '{sym.Name}': expected type {paramSink.Describe()}, found {arg.Type.Describe()}");
            }
            if (paramSink?.Unqualified is CType.VoidType && !IsErasableVoid(arg))
            {
                throw new IrUnsupportedException(
                    $"call to '{sym.Name}': a side-effecting argument to a `void` parameter is not supported yet "
                    + "(void has no runtime value, so the argument is erased); evaluate it as its own statement first");
            }
            args.Add(arg);
        }

        // A variadic callee (printf) needs AT LEAST the fixed params; the rest are the variadic
        // tail. A fixed-arity callee needs an exact match.
        var arityOk = fn.Variadic ? args.Count >= fn.Params.Count : args.Count == fn.Params.Count;
        if (!arityOk)
        {
            throw new IrUnsupportedException(
                $"call to '{sym.Name}': expected {(fn.Variadic ? "at least " : "")}{fn.Params.Count} argument(s), got {args.Count}");
        }
        // Zig parity (the differential oracle caught dotcc being too lenient here): an untyped
        // comptime numeric literal has no fixed-size ABI type, so Zig forbids passing it to a
        // C-variadic — `printf("%d", 42)` is an error, `@as(c_int, 42)` is required. The variadic
        // tail begins at argItems index `fn.Params.Count - paramOffset`. (Methods are never
        // variadic, so paramOffset is 0 whenever this branch runs.)
        if (fn.Variadic)
        {
            for (var k = fn.Params.Count - paramOffset; k < argItems.Count; k++)
            {
                if (IsComptimeUntypedNumeric(argItems[k]))
                {
                    _ir.Diagnostics.Add(new Diagnostic(Severity.Error,
                        "integer and float literals passed to variadic function must be casted to a fixed-size number type",
                        SrcPos.From(argItems[k])));
                }
            }
        }

        // An extern/libc function (FromSystemHeader) renders by its bare name — no CalleeSym —
        // so it binds to dotcc's Libc runtime (and printf/scanf hit the fluent builder), exactly
        // as a C program's libc call does. A user Zig function (or method) carries its symbol so
        // the (possibly legalized / mangled) target name is used.
        var calleeSym = sym.FromSystemHeader ? null : sym;
        return new Call(sym.Name, args, fn.Params, calleeSym) { Type = fn.Return };
    }

    /// <summary>Lower a <c>base.name(args)</c> call. Two shapes: (A) a STATIC / associated call
    /// <c>Type.func(args)</c> — the base is a bare identifier naming a registered struct (and is
    /// NOT a variable in scope) — every argument is explicit, no receiver; (B) an INSTANCE call
    /// <c>expr.method(args)</c> — the base value is the receiver, adjusted (Zig UFCS auto-ref/
    /// deref) to the method's declared first-parameter form. Both rewrite to the mangled free
    /// function <c>TypeName_method</c> recorded in <see cref="_methods"/>.</summary>
    /// <summary>Call a function that <paramref name="owner"/> declares, from this module: a GENERIC
    /// export (a <c>comptime</c> / <c>anytype</c> parameter, <c>std.fmt.bufPrint</c>) instantiates in its
    /// OWN module with the arguments read here (road-to-zig-std G3), since calling the template symbol
    /// directly would bind its empty placeholder signature; anything else is a direct call.</summary>
    private CExpr CallExportedDecl(ZigLowering owner, Symbol sym, IReadOnlyList<Item> argItems)
        => owner.TryResolveExportedGenericInstance(sym, argItems, caller: this) is { } inst
            ? FoldIfComptimeOnly(owner, inst.Instance, BuildCall(inst.Instance, inst.RuntimeArgs, receiver: null))
            : BuildCall(sym, argItems, receiver: null);

    /// <summary>The function a container CONST names (<c>pub const hash = getAutoHashFn(K, @This());</c> in
    /// hash_map's AutoContext, the closure idiom's function value), or null when <paramref name="container"/>
    /// declares no such const or it is not a comptime function value. Resolved by the module holding the
    /// const, in the container's scope with its comptime seeds live, and memoized.</summary>
    private Symbol? ContainerFnConst(string container, string name)
    {
        var owner = _shared.ContainerConstOwners.TryGetValue(container, out var o) ? o : this;
        return owner.OwnContainerFnConst(container, name);
    }

    /// <summary>The owner-side half of <see cref="ContainerFnConst"/>.</summary>
    private Symbol? OwnContainerFnConst(string container, string name)
    {
        if (_containerFnConsts.TryGetValue((container, name), out var known)) { return known; }
        if (!_containerConsts.TryGetValue(container, out var consts) || !consts.TryGetValue(name, out var entry)
            || entry.typeItem is not null)
        {
            return null;
        }
        using var seeds = EnterReifiedSeeds(container);
        using var scope = EnterContainer(container);
        var fn = TryResolveComptimeFnValue(entry.rhs, this)?.Fn;
        _containerFnConsts[(container, name)] = fn;
        return fn;
    }

    /// <summary>Memo of <see cref="OwnContainerFnConst"/>: (container, const) → the function it names, or null.</summary>
    private readonly Dictionary<(string Container, string Name), Symbol?> _containerFnConsts = new();

    /// <summary>Call a container function through its type (<c>Self.init(…)</c>, <c>Map(K, V).init(…)</c>): a
    /// direct call, or, for a GENERIC method, an instantiation in the module that owns it.</summary>
    /// <summary>The function a top-level const aliases, when it names a container's function through a type
    /// (<c>pub const featureSet = CpuFeature.FeatureSetFns(Feature).featureSet;</c> in std/Target/x86.zig), resolved
    /// in this module (the type call reifies here); null for any other const.</summary>
    internal Symbol? ResolveMethodAlias(string name)
    {
        if (!_topLevelConstRhs.TryGetValue(name, out var rhs) || rhs.Content is not Zig.Field f) { return null; }
        CType? owner = f.Arg0.Content switch
        {
            Zig.CallArgs => TryEvalTypeReturningCall(f.Arg0, out var called) ? called : null,
            Zig.Ident id => TryLookupContainerType(Tok(id.Arg0), out var named) ? named : null,
            Zig.Field => TryResolveQualifiedNestedType(f.Arg0),
            _ => null,
        };
        return owner is not null && ContainerTypeName(owner) is { } container
            ? EnsureMethodDeclared(container, Tok(f.Arg2)) ?? ContainerFnConst(container, Tok(f.Arg2))
            : null;
    }

    private CExpr CallStaticMethod(Symbol method, IReadOnlyList<Item> argItems)
    {
        if (_shared.GenericMethodOwners.TryGetValue(method, out var owner) && owner._genericFns.TryGetValue(method, out var g))
        {
            var (instance, runtimeArgs) = owner.ResolveGenericInstance(method, g, argItems, argScope: this);
            return FoldIfComptimeOnly(owner, instance, BuildCall(instance, runtimeArgs, receiver: null));
        }
        return BuildCall(method, argItems, receiver: null);
    }

    private CExpr LowerMethodCall(Zig.Field fld, IReadOnlyList<Item> argItems)
    {
        var methodName = Tok(fld.Arg2);

        // --- curated std namespace functions (std.mem.*, std.fmt.comptimePrint, std.debug.print, std.testing.*) ---
        // Routed before the generic dispatch through the ZigCuratedStd registry, which models no `std` in general, only
        // the rows. An unmodeled member is a clear, specific error without a std tree; with one (DOTCC_ZIG_LIB_DIR) it
        // navigates to real source. `comptimePrint` is formatted while lowering (task #128), as zig types its result by
        // running the formatter at compile time.
        if (TryLowerCuratedStdCall(fld.Arg0, methodName, argItems) is { } curatedCall)
        {
            return curatedCall;
        }

        // --- a call navigated to an @import'ed module: `util.func(args)` / `std.ascii.isDigit(args)` ---
        // AFTER the curated std.* fast-paths (so std.mem/debug/testing keep their curated lowering),
        // resolve the receiver as a module path — a relative import, or the real `std` root and its
        // re-export chain (std → ascii = @import("ascii.zig")) — and lower the referenced function on
        // demand: its signature lowers now (so the call binds) and its body is enqueued for the
        // top-level drain (road-to-zig-std S1/S2).
        //
        // A path the CURATED model owns is never navigated. Upstream std re-exports its allocators as
        // whole FILES (`pub const FixedBufferAllocator = @import("heap/FixedBufferAllocator.zig");`), so
        // with a real std tree configured (DOTCC_ZIG_LIB_DIR) `std.heap.FixedBufferAllocator` IS a
        // navigable module and navigation would beat the curated allocator below — then die lowering
        // upstream's own `init` signature. That made real-std navigation and the curated allocators
        // mutually exclusive; the guard restores S1's stated rule that the curated set is checked first
        // in EVERY position. (The complementary direction — a NON-curated std TYPE falling back to
        // navigation — is the separate S4d lift.)
        if (!IsCuratedStdPath(fld.Arg0) && ResolveModulePath(fld.Arg0) is { } navMod)
        {
            // `std.Target.x86.featureSet(…)`: the module exports the name as a const aliasing a container's
            // function, which the module resolves; the call and its arguments stay here.
            if (navMod.Lowering?.ResolveMethodAlias(methodName) is { } navAliased)
            {
                return CallStaticMethod(navAliased, argItems);
            }
            // Through any re-export (`pub const indexOfScalar = findScalar;`), to the module that owns it.
            if (navMod.Lowering?.ResolveExportedDecl(methodName, raiseIfSkipped: true) is not { } nav)
            {
                // A deprecation tombstone (`pub const fields = @compileError("Deprecated; use 'fieldNames' …");` in
                // std.meta, task #186) names its own diagnostic, as zig reports it.
                navMod.Lowering?.RaiseIfPoisoned(methodName);
                throw new IrUnsupportedException(
                    $"zig module '{System.IO.Path.GetFileName(navMod.Path)}' has no exported function '{methodName}'");
            }
            return CallExportedDecl(nav.Owner, nav.Sym, argItems);
        }

        // A member call on the `std.ArrayList(T)` TYPE (`std.ArrayList(i32).init(alloc)`) is
        // the pre-0.15 MANAGED API, which no longer exists in the pinned zig — reject it by
        // name with the migration path (the generic std-path error would only say `std`).
        if (fld.Arg0.Content is Zig.CallArgs { Arg2: not null } mca && TryResolveStdPath(mca.Arg0, out var mlPath) && mlPath == "std.ArrayList")
        {
            // Only `init` is the removed managed constructor. The unmanaged type has static functions of its own
            // (`std.ArrayList(u8).growCapacity(n)` in std.Io.Writer.Allocating, task #63): with a real std tree those are
            // real std's, called on the type its `ArrayList(T)` returns (`array_list.Aligned(T, null)`), as (A2) calls
            // a static function of any reified type.
            // Spelled `std.ArrayList(u8)` or through an alias (`const ArrayList = std.ArrayList;` in Writer.zig): either way
            // the std root module this unit imports.
            if (methodName != "init"
                && _imports.FirstOrDefault(kv => kv.Value == "std").Key is { } stdName
                && ResolveImport(stdName) is { Lowering: { } listNav }
                && listNav.TryEvalExportedTypeReturningCall("ArrayList", Flatten(mca.Arg2), caller: this) is { Type: var realList }
                && ContainerTypeName(realList) is { } realListName)
            {
                if ((EnsureMethodDeclared(realListName, methodName) ?? ContainerFnConst(realListName, methodName)) is not { } listStatic)
                {
                    throw new IrUnsupportedException($"'{realListName}' has no function '{methodName}'");
                }
                return CallStaticMethod(listStatic, argItems);
            }
            throw new IrUnsupportedException(
                "zig std.ArrayList's managed API (`std.ArrayList(T).init(alloc)` + allocator-less calls) was removed in zig 0.15 — "
                + "use the unmanaged API: `var list: std.ArrayList(T) = .empty;` with a per-call allocator "
                + "(`try list.append(alloc, v)`, `list.deinit(alloc)`)");
        }

        // --- Zig allocators (Milestone F), before the generic method dispatch ---
        // `std.heap.FixedBufferAllocator.init(buf)` — a static call on the std FBA type.
        // A static function row of a curated type: `FixedBufferAllocator.init(buf)`, `ArenaAllocator.init(backing)`
        // (Milestone U), and the Alignment carrier's `fromByteUnits(n)` / `of(T)` spelled through the type
        // (std.MultiArrayList's `.big_align = mem.Alignment.fromByteUnits(big_align)`, task #108).
        if (TryResolveStdPath(fld.Arg0, out var curatedBase))
        {
            if (ZigCuratedStd.FindMember(curatedBase, methodName, CuratedShape.StaticFn) is { } staticRow)
            {
                return LowerCuratedMember(staticRow, new CuratedMemberCall(methodName, argItems, Base: fld.Arg0));
            }
            // Any OTHER member call on a curated std TYPE: the model owns the path (so it was not
            // navigated above) but doesn't provide this function: say so precisely, instead of falling
            // through to the instance path and reporting the type as "a type, not a value".
            if (ZigCuratedStd.Find(curatedBase, CuratedShape.Type) is not null)
            {
                throw new IrUnsupportedException(
                    $"zig `{curatedBase}` has no modeled function '{methodName}'; dotcc curates these std "
                    + $"types, and models `init` on {string.Join(" / ", ZigCuratedStd.TypesWithStatic("init"))}");
            }
        }
        // `a.alloc(T, n)` / `a.free(s)` (and the deferred `create`/`destroy`) on a known-default
        // (→ devirt) or an Allocator-typed receiver (→ indirect). A same-named method on a
        // non-allocator receiver falls through to the generic dispatch below.
        if (ZigCuratedStd.AllocatorPath is { } allocatorPath && ZigCuratedStd.FindMember(allocatorPath, methodName, CuratedShape.Method) is not null
            // A container TYPE's own function of that name (std.array_hash_map's `IndexHeader.alloc(gpa, bit_index)`,
            // task #135) is a static call, not an allocator method: it takes the `Type.func(args)` path below.
            && !(fld.Arg0.Content is Zig.Ident typeRecv && _symbols.Resolve(Tok(typeRecv.Arg0)) is null
                 && TryLookupContainerType(Tok(typeRecv.Arg0), out var recvType) && ContainerTypeName(recvType) is not null)
            // So is one of a type a type-returning call builds (std.crypto.auth.siphash's `SipHash64(2, 4).create(&out,
            // msg, &key)`, task #159); the reification is memoized, and any other call base answers no.
            && !(fld.Arg0.Content is Zig.CallArgs && TryEvalTypeReturningCall(fld.Arg0, out _))
            // And so is one of a type a dotted path names: a container's type const (std.crypto.auth.hmac's
            // `sha2.HmacSha256.create(&out, msg, key)`, with `pub const HmacSha256 = Hmac(…);`, task #168), a nested
            // container, or a type another module declares. Any other dotted base answers no.
            && !(fld.Arg0.Content is Zig.Field && !IsCuratedStdPath(fld.Arg0)
                 && (TryResolveQualifiedNestedType(fld.Arg0) is not null || TryResolveModuleNestedType(fld.Arg0) is not null))
            && TryLowerAllocatorMethod(fld, methodName, argItems, out var allocExpr))
        {
            return allocExpr;
        }

        // (A) `Type.func(args)` — a bare-identifier base naming a registered container type (a
        // struct/union `Named`, an enum `Enum`, or the self alias `Self`), and NOT a variable →
        // the associated/static function; all arguments are explicit (no receiver). A self alias
        // maps to the real container name so the method lookup and the mangled target match the
        // explicit-name form. (An `EnumName.member` non-call resolves to a tag constant earlier, in
        // the Zig.Field case; this branch only fires for a CALL whose method is a function.)
        if (fld.Arg0.Content is Zig.Ident bid
            && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
            && ContainerTypeName(baseTy) is { } typeName
            && _symbols.Resolve(Tok(bid.Arg0)) is null)
        {
            // A GENERIC top-level function of a file-as-struct module, called through the type (inside
            // the module, `const Writer = @This(); Writer.print(w, …)`): an ordinary exported generic call.
            if (_shared.FileStructOwners.TryGetValue(typeName, out var staticOwner)
                && staticOwner.FileStructGenericTemplate(methodName) is { } template)
            {
                return CallExportedDecl(staticOwner, template, argItems);
            }
            if ((EnsureMethodDeclared(typeName, methodName) ?? ContainerFnConst(typeName, methodName)) is not { } staticSym)
            {
                throw new IrUnsupportedException($"'{typeName}' has no function '{methodName}'");
            }
            return CallStaticMethod(staticSym, argItems);
        }
        // (A1) `Outer.Inner.func(args)` — the same static call through a QUALIFIED nested container.
        if (fld.Arg0.Content is Zig.Field
            && TryResolveQualifiedNestedType(fld.Arg0) is { } qualifiedTy
            && ContainerTypeName(qualifiedTy) is { } qualifiedName)
        {
            if (EnsureMethodDeclared(qualifiedName, methodName) is not { } qStaticSym)
            {
                throw new IrUnsupportedException($"'{qualifiedName}' has no function '{methodName}'");
            }
            return CallStaticMethod(qStaticSym, argItems);
        }
        // (A1b) `std.fmt.Placeholder.parse(…)` — a static call through a type ANOTHER module declares, named by
        // its module path. Also from a lazy module: this is body lowering, never a lazy module's pass 0, so
        // resolving the path lowers only what the call needs.
        if (fld.Arg0.Content is Zig.Field && !IsCuratedStdPath(fld.Arg0)
            && TryResolveModuleNestedType(fld.Arg0) is { Type: var moduleTy, Owner: var tyOwner } && ContainerTypeName(moduleTy) is { } moduleTyName)
        {
            // Declared in the module that owns the type, so its body (and a generic's instances) resolve there.
            if ((tyOwner.EnsureMethodDeclared(moduleTyName, methodName) ?? tyOwner.ContainerFnConst(moduleTyName, methodName)) is not { } mStaticSym)
            {
                throw new IrUnsupportedException($"'{moduleTyName}' has no function '{methodName}'");
            }
            return CallStaticMethod(mStaticSym, argItems);
        }
        // A static call through a module type whose declaration did not PARSE (`std.Io.Writer.Allocating.initCapacity(…)`
        // while Allocating had a parse gap): say so, rather than fall through to a "path not modeled" read of the base.
        if (fld.Arg0.Content is Zig.Field { Arg0: var skippedBase, Arg2: var skippedName } && !IsCuratedStdPath(fld.Arg0)
            && ResolveModulePath(skippedBase)?.Lowering is { } skippedOwner)
        {
            skippedOwner.RaiseIfSkippedDecl(Tok(skippedName));
        }

        // (A2) `Generic(args).func(…)` — the base is a call to a type-returning generic (wall-plan W4),
        // naming the REIFIED container directly instead of through an alias. This is the idiomatic
        // spelling (`std.ArrayList(u8).init(…)`), so a reified struct's static methods must be reachable
        // this way and not only via `const L = ArrayList(u8);` (road-to-zig-std G4). Evaluating the base
        // reifies it — memoized, so a repeat call reuses the one instance — and then the method resolves
        // exactly like (A). `TryEvalTypeReturningCall` returns false (no side effect) for any other call
        // base, so an ordinary `getBox().get()` still falls through to (B).
        if (fld.Arg0.Content is Zig.CallArgs
            && TryEvalTypeReturningCall(fld.Arg0, out var reifiedBase)
            && ContainerTypeName(reifiedBase) is { } reifiedName)
        {
            if ((EnsureMethodDeclared(reifiedName, methodName) ?? ContainerFnConst(reifiedName, methodName)) is not { } reifiedSym)
            {
                throw new IrUnsupportedException($"'{reifiedName}' has no function '{methodName}'");
            }
            return CallStaticMethod(reifiedSym, argItems);
        }

        // (B) `expr.method(args)` — the base is an instance of a container type.
        // A method on a TYPED comptime tag of a module (`builtin.cpu.arch.endian()` in std.mem, with a real std):
        // the receiver is that enum's constant, and the method is the enum's own, from its source.
        if (IsModuleRooted(fld.Arg0) && TryReadComptimeAggregateField(fld.Arg0) is { Tag: { } typedTag, TagType: { } tagType, TagTypeScope: { } tagScope }
            && tagScope.LowerType(tagType).Unqualified is CType.Enum tagEnum)
        {
            var tagRecv = tagScope.ResolveEnumLit(typedTag, tagEnum);
            if (EnsureMethodDeclared(tagEnum.Name, methodName) is not { } tagMethod)
            {
                throw new IrUnsupportedException($"enum '{tagEnum.Name}' has no method '{methodName}'");
            }
            var tagFn = (CType.Func)tagMethod.Type.Unqualified;
            return BuildCall(tagMethod, argItems, tagFn.Params.Count > 0 ? AdjustReceiver(tagRecv, tagFn.Params[0]) : null);
        }
        var recv = LowerExpr(fld.Arg0);
        // A member row of the receiver's curated runtime type (a registry lookup on the type path): `fba.allocator()`,
        // `arena.allocator()` / `arena.deinit()` (Milestones F and U; both are runtime types, not Zig-declared
        // containers), the Alignment carrier's methods (hash_map's `max_align.forward(total)`) and the unmanaged
        // `std.ArrayList(T)` members (wall-plan W0; the allocator is an explicit per-call argument, zig 0.15+).
        // Mutating methods are INSTANCE methods on the runtime struct, so calling on an lvalue receiver mutates in place.
        if (ZigCuratedStd.PathOfType(recv.Type) is { } recvPath)
        {
            if (ZigCuratedStd.FindMember(recvPath, methodName, CuratedShape.Method) is { } methodRow)
            {
                return LowerCuratedMember(methodRow, new CuratedMemberCall(methodName, argItems, recv, fld.Arg0));
            }
            if (ZigCuratedStd.ReportsUnmodeledMembers(recvPath))
            {
                throw ZigCuratedStd.UnmodeledMember(recvPath, methodName, argItems.Count);
            }
        }
        if (!TryContainerName(recv.Type, out var container))
        {
            throw new IrUnsupportedException(
                $"zig method call `.{methodName}()` needs a struct (or pointer-to-struct) receiver, got {recv.Type.Describe()}");
        }
        // A GENERIC top-level function of a file-as-struct module called on an instance
        // (`w.print(fmt, args)`, road-to-zig-std G3): instantiate it in its own module with the receiver as
        // its first argument, then call the instance as a method.
        if (_shared.FileStructOwners.TryGetValue(container, out var fileOwner)
            && fileOwner.TryResolveFileStructGenericMethod(methodName, fld.Arg0, argItems, caller: this) is { } generic)
        {
            var gfn = (CType.Func)generic.Instance.Type.Unqualified;
            var gcall = BuildCall(generic.Instance, generic.RuntimeArgs, AdjustReceiver(recv, gfn.Params[0]));
            return FoldIfComptimeOnly(fileOwner, generic.Instance, gcall);
        }
        if (EnsureMethodDeclared(container, methodName) is not { } msym)
        {
            // A container CONST bound to a function value (hash_map's AutoContext: `pub const hash =
            // getAutoHashFn(K, @This());`) called on an instance: the function, with the receiver first.
            if (ContainerFnConst(container, methodName) is { } constFn)
            {
                var cfnType = (CType.Func)constFn.Type.Unqualified;
                if (cfnType.Params.Count == 0)
                {
                    throw new IrUnsupportedException(
                        $"'{container}.{methodName}' takes no parameters — call it as `{container}.{methodName}(…)`, not on an instance");
                }
                return BuildCall(constFn, argItems, AdjustReceiver(recv, cfnType.Params[0]));
            }
            // A FIELD holding a function pointer (`w.vtable.drain(w, data, n)`, std.Io.Writer's dispatch):
            // zig calls the field's value, with no receiver, each argument result-located against the
            // pointer's parameter type, like a call through a fn-pointer local.
            if (_ir.StructFieldType(recv.Type, methodName)?.Unqualified is CType.Func fieldFn)
            {
                if (argItems.Count != fieldFn.Params.Count)
                {
                    throw new IrUnsupportedException(
                        $"call through fn-pointer field '{container}.{methodName}': expected {fieldFn.Params.Count} argument(s), got {argItems.Count}");
                }
                var callee = new Member(recv, methodName, recv.Type.Unqualified is CType.Pointer) { Type = fieldFn, IsLValue = true };
                var fieldArgs = new List<CExpr>(argItems.Count);
                for (var i = 0; i < argItems.Count; i++)
                {
                    fieldArgs.Add(LowerExprSink(argItems[i], fieldFn.Params[i]));
                }
                return new IndirectCall(callee, fieldArgs) { Type = fieldFn.Return };
            }
            throw new IrUnsupportedException($"struct '{container}' has no method '{methodName}'");
        }
        // A GENERIC method (a `comptime` / `anytype` parameter): instantiate it in the module that owns it,
        // the receiver filling parameter 0 as it would in `Type.method(recv, …)`.
        if (_shared.GenericMethodOwners.TryGetValue(msym, out var genericOwner)
            && genericOwner._genericFns.TryGetValue(msym, out var genericMethod))
        {
            if (genericMethod.Params.Count == 0 || genericMethod.Params[0].Kind is not (ParamKind.Runtime or ParamKind.AnyType))
            {
                throw new IrUnsupportedException(
                    $"'{container}.{methodName}' called on an instance needs a runtime first parameter to take the receiver");
            }
            var withReceiver = new List<Item>(argItems.Count + 1) { fld.Arg0 };
            withReceiver.AddRange(argItems);
            var (instance, runtimeArgs) = genericOwner.ResolveGenericInstance(msym, genericMethod, withReceiver, argScope: this);
            var ifn = (CType.Func)instance.Type.Unqualified;
            var icall = BuildCall(instance, runtimeArgs.Skip(1).ToList(), AdjustReceiver(recv, ifn.Params[0]));
            return FoldIfComptimeOnly(genericOwner, instance, icall);
        }
        var mfn = (CType.Func)msym.Type.Unqualified;
        if (mfn.Params.Count == 0)
        {
            throw new IrUnsupportedException(
                $"'{container}.{methodName}' takes no parameters — call it as `{container}.{methodName}(…)`, not on an instance");
        }
        var receiver = AdjustReceiver(recv, mfn.Params[0]);
        return BuildCall(msym, argItems, receiver);
    }

    /// <summary>Arity check for a curated list member — a clear error naming the expected shape.</summary>
    private static void RequireListArgs(string method, IReadOnlyList<Item> argItems, int count, string shape)
    {
        if (argItems.Count != count)
        {
            throw new IrUnsupportedException(
                $"zig std.ArrayList `.{method}{shape}` takes {count} argument(s); got {argItems.Count}");
        }
    }

    /// <summary>Lower the explicit allocator argument of an unmanaged-API list call. A std
    /// default path (<c>std.heap.c_allocator</c>) materializes a runtime <c>Allocator</c> via
    /// the ordinary expression lowering; anything not Allocator-typed is a clear error (the
    /// managed pre-0.15 API — <c>init(alloc)</c> + allocator-less calls — is NOT modeled,
    /// matching the pinned zig, which no longer has it).</summary>
    private CExpr LowerListAllocatorArg(string method, Item arg)
    {
        var a = LowerExpr(arg);
        if (a.Type.Unqualified is not CType.Allocator)
        {
            throw new IrUnsupportedException(
                $"zig std.ArrayList `.{method}` expects a std.mem.Allocator as its first argument, got {a.Type.Describe()}");
        }
        return a;
    }

    /// <summary>The <c>error.OutOfMemory</c> code as a literal argument — the same flat-set code
    /// <see cref="AllocCall"/> carries in <c>OomCode</c>. Typed <c>int</c> (NOT ushort): a plain
    /// in-range constant literal converts implicitly to the runtime's <c>ushort oom</c>
    /// parameter, whereas a UShort-typed literal renders with a <c>u</c> suffix (uint), which
    /// C# refuses to narrow (CS1503).</summary>
    private CExpr OomLit()
    {
        var code = ErrorCode("OutOfMemory");
        return new LitInt(code.ToString(System.Globalization.CultureInfo.InvariantCulture), code) { Type = CType.Int };
    }

    /// <summary>Try to lower an allocator method call <c>a.alloc(T, n)</c> / <c>a.free(s)</c> /
    /// <c>a.create(T)</c> / <c>a.destroy(p)</c> (Milestone F/U) through the <c>std.mem.Allocator</c> member rows of the
    /// curated registry. The receiver is DEVIRTUALIZED to a direct call when provable: the statically-known C-heap
    /// default (the row sees no receiver and no FBA context) or a provable <c>fba.allocator()</c> site (the row sees the
    /// <c>&amp;fba</c> context). Otherwise the receiver is lowered and, if it is an <see cref="CType.Allocator"/>,
    /// dispatched indirectly through its vtable. Returns <c>false</c> (so the caller falls through to the generic method
    /// dispatch) when the receiver is neither, i.e. a same-named method on a non-allocator type, or when the registry
    /// has no such row.</summary>
    private bool TryLowerAllocatorMethod(Zig.Field fld, string methodName, IReadOnlyList<Item> argItems, [NotNullWhen(true)] out CExpr? result)
    {
        result = null;
        if (ZigCuratedStd.AllocatorPath is not { } allocatorPath
            || ZigCuratedStd.FindMember(allocatorPath, methodName, CuratedShape.Method) is not { } row)
        {
            return false;
        }
        bool devirt = TryKnownAllocatorKind(fld.Arg0, out var kind);
        CExpr? recv = null;
        // For an FBA-site devirt the `&fba` context rides on the IR node's FbaCtx (takes precedence
        // over a null Receiver, which alone would mean the C-heap default). The C-heap devirt leaves
        // both null; a non-devirt receiver is lowered and dispatched indirectly.
        CExpr? fbaCtx = devirt && kind == AllocKind.Fba ? FbaCtxFor(fld.Arg0) : null;
        if (!devirt)
        {
            recv = LowerExpr(fld.Arg0);
            if (recv.Type.Unqualified is not CType.Allocator) { return false; }   // not an allocator, generic dispatch
        }
        result = LowerCuratedMember(row, new CuratedMemberCall(methodName, argItems, recv, fld.Arg0, devirt ? kind : null, fbaCtx));
        return true;
    }

    /// <summary>Build the <c>&amp;fba</c> context for a devirtualized FBA-site allocator call
    /// (Milestone U). <paramref name="allocExpr"/> is the <c>a</c> identifier bound to an
    /// <c>fba.allocator()</c> site in <see cref="_fbaAllocatorSites"/> (the kind is
    /// <see cref="AllocKind.Fba"/>, so it is necessarily a <see cref="Zig.Ident"/>).</summary>
    private CExpr FbaCtxFor(Item allocExpr)
    {
        var fbaSym = _fbaAllocatorSites[Tok(((Zig.Ident)allocExpr.Content!).Arg0)];
        fbaSym.AddressTaken = true;
        // IsLValue=true so the backend takes `&fba` directly — without it, `&<rvalue>` would
        // materialize a COPY of `fba` per call (`__clN = fba; &__clN`), so the bump cursor would
        // not be shared across allocations.
        var fbaRef = new VarRef(fbaSym) { Type = fbaSym.Type, IsLValue = true };
        return new Unary(UnOp.AddrOf, fbaRef) { Type = new CType.Pointer(fbaSym.Type) };
    }

    /// <summary>Lower <c>std.heap.FixedBufferAllocator.init(&amp;buf)</c> (Milestone F) to
    /// <c>FixedBufferAllocator.Init(bytePtr, capacity)</c>. <paramref name="argItems"/> is the
    /// single <c>&amp;buf</c> argument, where <c>buf</c> is a <c>[N]T</c> array local — which
    /// already lowers to a stackalloc'd pointer, so the buffer pointer is the array value itself
    /// (cast to <c>byte*</c>) and the capacity is <c>N * sizeof(T)</c> bytes.</summary>
    internal CExpr LowerFbaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `FixedBufferAllocator.init` expects (buffer); got {argItems.Count} argument(s)");
        }
        // Accept `&buf` (the idiom) or a bare `buf`; either way the array local is the byte run.
        var inner = argItems[0].Content is Zig.PreAddrOf ad ? ad.Arg1 : argItems[0];
        var buf = LowerExpr(inner);
        if (buf.Type.Unqualified is not CType.Array a || a.Count is not int n)
        {
            throw new IrUnsupportedException(
                "zig `FixedBufferAllocator.init` expects `&buf` where buf is a fixed-size `[N]T` array local");
        }
        var bytePtr = new Cast(new CType.Pointer(CType.UChar), buf) { Type = new CType.Pointer(CType.UChar) };
        long bytes = (long)n * a.Element.Unqualified.SizeOf;
        var cap = new LitInt(bytes.ToString(CultureInfo.InvariantCulture), bytes) { Type = CType.ULong };
        return new Call("FixedBufferAllocator.Init", new List<CExpr> { bytePtr, cap },
            new List<CType> { new CType.Pointer(CType.UChar), CType.ULong }, null) { Type = new CType.Named(FbaTypeName) };
    }

    /// <summary>Lower <c>std.heap.ArenaAllocator.init(backing)</c> (Milestone U) to
    /// <c>ArenaAllocator.Init(backing)</c>. The single argument is the backing
    /// <c>std.mem.Allocator</c> — the statically-known default materializes a runtime C-heap
    /// <see cref="CType.Allocator"/> through the ordinary value path (so the arena draws its chunks
    /// from a real allocator); an opaque allocator value is taken as-is.</summary>
    internal CExpr LowerArenaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `ArenaAllocator.init` expects (backing allocator); got {argItems.Count} argument(s)");
        }
        var backing = LowerExpr(argItems[0]);
        if (backing.Type.Unqualified is not CType.Allocator)
        {
            throw new IrUnsupportedException(
                $"zig `ArenaAllocator.init` expects a `std.mem.Allocator` backing, got {backing.Type.Describe()}");
        }
        return new Call("ArenaAllocator.Init", new List<CExpr> { backing },
            new List<CType> { new CType.Allocator() }, null) { Type = new CType.Named(ArenaTypeName) };
    }

    /// <summary>Resolve the container name a receiver expression's type names — a
    /// <see cref="CType.Named"/> struct/union or a <see cref="CType.Enum"/>, as a value or a pointer
    /// to one (<c>Point</c> / <c>*Point</c> / <c>Color</c> / <c>*Color</c>) — for instance-method
    /// dispatch.</summary>
    private static bool TryContainerName(CType t, out string name)
    {
        var u = t.Unqualified;
        if (u is CType.Pointer p) { u = p.Pointee.Unqualified; }
        switch (u)
        {
            case CType.Named n: name = n.Name; return true;
            case CType.Enum e:  name = e.Name; return true;
            default: name = ""; return false;
        }
    }

    /// <summary>The container name a type names for static-call (<c>Type.func(…)</c>) dispatch — a
    /// struct/union (<see cref="CType.Named"/>) or an enum (<see cref="CType.Enum"/>); null for any
    /// other type.</summary>
    private static string? ContainerTypeName(CType t) => t.Unqualified switch
    {
        CType.Named n => n.Name,
        CType.Enum e  => e.Name,
        _ => null,
    };

    /// <summary>Adjust an instance-method receiver to the method's declared first-parameter form
    /// (Zig UFCS auto-ref/deref): a value receiver to a <c>*Self</c> method takes its address (a
    /// var/param operand is marked address-taken; a non-lvalue is materialized to a temp by the
    /// backend's <c>&amp;rvalue</c> rule); a pointer receiver to a value-<c>Self</c> method is
    /// dereferenced; matching forms (both pointer or both value) pass through unchanged.</summary>
    private static CExpr AdjustReceiver(CExpr recv, CType paramType)
    {
        var paramIsPtr = paramType.Unqualified is CType.Pointer;
        var recvIsPtr = recv.Type.Unqualified is CType.Pointer;
        if (paramIsPtr && !recvIsPtr)
        {
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            return new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
        }
        if (!paramIsPtr && recvIsPtr)
        {
            var pointee = ((CType.Pointer)recv.Type.Unqualified).Pointee;
            return new Unary(UnOp.Deref, recv) { Type = pointee, IsLValue = true };
        }
        return recv;
    }

    /// <summary>zig's rule for <c>/</c> and <c>%</c> (task #98): on a SIGNED integer (and, for <c>%</c>, a float) whose
    /// operands are not both comptime-known the operator is ambiguous about rounding, so zig rejects it ("signed integers
    /// must use @divTrunc, @divFloor, or @divExact"; "... must use @rem or @mod"). An unsigned operand, a comptime_int, a
    /// comptime context and two comptime-known operands are all fine. Returns the division unchanged when legal.</summary>
    /// <remarks><paramref name="zigPeer"/> is the operands' ZIG peer type when the source spells it (task #103): the lowered
    /// operands carry C#'s integer promotion, so <c>(i * 37) % 101</c> over a <c>u16</c> would read as a signed <c>int</c>.</remarks>
    private CExpr RejectRuntimeSignedDivision(CExpr division, CType? zigPeer)
    {
        if (_comptimeDepth > 0 || division is not Binary { Op: BinOp.Div or BinOp.Mod } b) { return division; }
        CheckZigDivision(b.Op, b.Left, b.Right, zigPeer);
        return division;
    }

    /// <summary>zig's peer type resolution for an arithmetic or bitwise operator (task #158): a signed and an unsigned
    /// RUNTIME integer combine only when the signed type holds every value of the unsigned one, so <c>i32 + u8</c> is an
    /// <c>i32</c> but <c>i16 + usize</c> and <c>i16 - u16</c> are "incompatible types". A comparison is not resolved
    /// that way (zig compares mixed signedness exactly), nor is a shift; a comptime-known operand coerces to its peer.
    /// Declared widths are read where the source spells them (a <c>u3</c> beside an <c>i8</c> is fine).</summary>
    private void RejectIncompatiblePeerSignedness(BinOp op, Item l, Item r, CExpr left, CExpr right)
    {
        if (_comptimeDepth > 0 || op is not (BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Mod
                or BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor)) { return; }
        bool Known(CExpr e) => e is ComptimeFold || _ir.ConstEval(e) is not null;
        if (Known(left) || Known(right)) { return; }
        // Only operands whose zig integer type is CERTAIN count: a false rejection would break valid zig (std mixes
        // signedness freely through casts), so anything this cannot pin down is let through, as before.
        if (CertainZigInt(l) is not { } lt || CertainZigInt(r) is not { } rt || lt.Signed == rt.Signed) { return; }
        var (signedBits, unsignedBits) = lt.Signed ? (lt.Bits, rt.Bits) : (rt.Bits, lt.Bits);
        if (signedBits > unsignedBits) { return; }
        static string Spell((bool Signed, int Bits) t) => (t.Signed ? "i" : "u") + t.Bits.ToString(CultureInfo.InvariantCulture);
        throw new CompileException(
            $"zig: incompatible types: '{Spell(lt)}' and '{Spell(rt)}' "
            + "(a signed and an unsigned integer combine only when the signed type holds every unsigned value; cast one with @intCast / @as)");
    }

    /// <summary>The zig integer type (signedness and declared width) of a runtime operand when it is certain, else null
    /// (<see cref="RejectIncompatiblePeerSignedness"/>): a parameter (its recorded declared width), an
    /// array or slice element, an <c>@as(T, …)</c>, a 64-bit field read, or an operator over two operands of one such
    /// type. A comptime-known value, a call and anything narrower whose declared width is not recorded give null.</summary>
    private (bool Signed, int Bits)? CertainZigInt(Item it)
    {
        // Plain C `char` is no zig type (zig's `u8` / `i8` lower to `unsigned char` / `signed char`): it is a string literal's
        // element, a zig `u8` whose C signedness would mislead (task #165).
        static CType.Prim? RuntimeInt(CType? t) =>
            t?.Unqualified is CType.Prim { Integer: true, IsComptimeInt: false, Name: not ("_Bool" or "char") } prim ? prim : null;
        // zig's peer type (task #165): an integer literal adopts the other operand's type; two operands of one signedness
        // meet at the wider; a signed and an unsigned meet at the signed one when it holds every unsigned value (anything
        // else is the "incompatible types" RejectIncompatiblePeerSignedness reports).
        (bool, int)? Peer(Item a, Item b)
        {
            if (IsIntegerLiteral(b)) { return CertainZigInt(a); }
            if (IsIntegerLiteral(a)) { return CertainZigInt(b); }
            if (CertainZigInt(a) is not var (aSigned, aBits) || CertainZigInt(b) is not var (bSigned, bBits)) { return null; }
            if (aSigned == bSigned) { return (aSigned, System.Math.Max(aBits, bBits)); }
            var (signedBits, unsignedBits) = aSigned ? (aBits, bBits) : (bBits, aBits);
            return signedBits > unsignedBits ? (true, signedBits) : null;
        }
        switch (it.Content)
        {
            case Zig.Grouped g: return CertainZigInt(g.Arg1);
            case Zig.Add a: return Peer(a.Arg0, a.Arg2);
            case Zig.Sub a: return Peer(a.Arg0, a.Arg2);
            case Zig.Mul a: return Peer(a.Arg0, a.Arg2);
            case Zig.AddWrap a: return Peer(a.Arg0, a.Arg2);
            case Zig.SubWrap a: return Peer(a.Arg0, a.Arg2);
            case Zig.MulWrap a: return Peer(a.Arg0, a.Arg2);
            case Zig.BitAnd a: return Peer(a.Arg0, a.Arg2);
            case Zig.BitOr a: return Peer(a.Arg0, a.Arg2);
            case Zig.BitXor a: return Peer(a.Arg0, a.Arg2);
            case Zig.Ident id:
            {
                // A PARAMETER's type is spelled; a local's may be inferred from an expression whose lowered type is C's
                // (`const tz = @ctz(x);` is a zig `u4` but a C# `int`), so a local is not certain.
                if (_symbols.Resolve(Tok(id.Arg0)) is not { Kind: SymKind.Param } s || s.IsConstexpr
                    || _comptimeVars.ContainsKey(s) || RuntimeInt(s.Type) is not { } p) { return null; }
                return (p.Signed, _valueBits.TryGetValue(s, out var bits) ? bits : p.Bytes * 8);
            }
            case Zig.Index ix:
            {
                CExpr read;
                using (EnterThrowawayHoist()) { read = LowerExpr(it); }
                if (read is ComptimeFold || _ir.ConstEval(read) is not null || RuntimeInt(read.Type) is not { } p) { return null; }
                return (p.Signed, DeclaredElemBitsOfValue(ix.Arg0) ?? p.Bytes * 8);
            }
            case Zig.Field:
            {
                CExpr read;
                using (EnterThrowawayHoist()) { read = LowerExpr(it); }
                if (read is ComptimeFold || _ir.ConstEval(read) is not null || RuntimeInt(read.Type) is not { Bytes: 8 } p)
                {
                    return null;
                }
                return (p.Signed, 64);
            }
            // A call whose return type spells its width (`std.mem.indexOfMax` returns a `usize`, task #167). A comptime-only
            // call folds to its value, which zig coerces by value, so it is not certain.
            case Zig.CallArgs:
            {
                CExpr called;
                using (EnterThrowawayHoist()) { called = LowerExpr(it); }
                return called is Call { CalleeSym: { } callee } && _fnReturnBits.TryGetValue(callee, out var returnBits)
                       && RuntimeInt(called.Type) is { } rp
                    ? (rp.Signed, returnBits)
                    : null;
            }
            case Zig.BuiltinCall { Arg2: not null } b when Tok(b.Arg0) == "@as" && Flatten(b.Arg2) is [var asType, _]:
                return RuntimeInt(LowerType(asType)) is { } asPrim ? (asPrim.Signed, DeclaredBitsOfTypeArg(asType) ?? asPrim.Bytes * 8) : null;
            default:
                return null;
        }
    }

    /// <summary>The check behind <see cref="RejectRuntimeSignedDivision"/>, for a binary or a compound <c>/=</c> /
    /// <c>%=</c>: the peer type is the typed operand's (a literal yields to its peer, as in zig).</summary>
    private void CheckZigDivision(BinOp op, CExpr left, CExpr right, CType? zigPeer = null)
    {
        if (_comptimeDepth > 0) { return; }
        var peer = zigPeer?.Unqualified ?? (left is LitInt && right is not LitInt ? right.Type : left.Type).Unqualified;
        var signedInt = peer is CType.Prim { Integer: true, Signed: true, IsComptimeInt: false };
        var isFloat = (peer is CType.Prim { Integer: false } && peer != CType.Bool) || peer is CType.Float128Type;
        if (!signedInt && !(op == BinOp.Mod && isFloat)) { return; }
        bool Known(CExpr e) => e is ComptimeFold || _ir.ConstEval(e) is not null;
        if (Known(left) && Known(right)) { return; }
        throw new CompileException(op == BinOp.Div
            ? $"zig: division with '{peer.Describe()}' operands: signed integers must use @divTrunc, @divFloor, or @divExact"
            : $"zig: remainder division with '{peer.Describe()}' operands: signed integers and floats must use @rem or @mod");
    }

    /// <summary>Lower a binary op, synthesizing the result type the way the C# backend
    /// will treat it: usual-arithmetic for arithmetic/bitwise, the promoted left type
    /// for a shift (operands promote independently), and <c>int</c> for a relational /
    /// boolean (the backend renders those as an integer-valued <c>(CBool)(…)</c>).</summary>
    /// <summary>The operands of <c>a and b</c> / <c>a or b</c>: the left as usual, the right under its own hoist buffer
    /// (<see cref="LowerArmIsolated"/>), since it runs only when the left does not decide the result.
    /// <paramref name="rightHoisted"/> is what the right side hoisted.</summary>
    private (CExpr Left, CExpr Right) LowerShortCircuitOperands(Item l, Item r, out List<CStmt> rightHoisted)
    {
        var left = LowerExpr(l);
        var (right, hoisted) = LowerArmIsolated(() => LowerExpr(r));
        rightHoisted = hoisted;
        return (left, right);
    }

    private CExpr Bin(BinOp op, Item l, Item r)
    {
        // `<comptime tag> == .member` (road-to-zig-std S5) — `@typeInfo(T).int.signedness == .unsigned`
        // and friends fold to a boolean literal here, before either side is lowered: the tag has no
        // runtime value to compare, and folding is what lets the surrounding comptime `if` prune.
        if (op is BinOp.Eq or BinOp.Ne && TryFoldComptimeTagCompare(l, r, op == BinOp.Ne, out var tagCmp))
        {
            return tagCmp;
        }
        // A TYPE comparison anywhere a value is wanted, not only as an `if` condition
        // (`std.debug.assert(T == f16 or T == f32 or T == f64)` in std.fmt.parse_float): types have no runtime value.
        if (op is BinOp.Eq or BinOp.Ne && TryFoldTypeEquality(l, r) is { } typesEqual)
        {
            return new LitBool(op == BinOp.Eq ? typesEqual : !typesEqual) { Type = CType.Bool };
        }
        // `==` / `!=` may compare an enum value against a bare `.member` literal (`self == .red`),
        // which Zig result-locates against the other operand's enum type — so those two operands
        // get the enum-aware lowering; everything else lowers both sides plainly.
        // A vector shifted by a `@splat(n)` amount (std.math.rotr's `(x >> @splat(ar)) | (x << @splat(1 +% ~ar))`, task #148)
        // shifts every lane by the one scalar count; a `@splat` operand of any other operator has no result type.
        if (TrySplatBesideVector(op, l, r) is { } splatPair) { return TryVectorBinary(op, splatPair.Left, splatPair.Right) ?? splatPair.Left; }
        // The right side of `and` / `or` runs only when the left does not decide the result, so what it hoists (the early
        // return of `a and (opt orelse return false)`) stays with it (task #203).
        var impureBeforeLeft = _hoistImpureSeen;
        List<CStmt>? rightHoisted = null;
        var (left, right) = op is BinOp.LogAnd or BinOp.LogOr
            ? LowerShortCircuitOperands(l, r, out rightHoisted)
            : op is BinOp.Eq or BinOp.Ne
            ? LowerComparisonOperands(l, r)
            // A shift amount is a result location (zig types it `Log2Int(T)`): `1 << @intCast(i)` infers a cast there.
            : op is BinOp.Shl or BinOp.Shr && r.Content is Zig.BuiltinCall { Arg2: not null, Arg0: var shiftCast }
                && Tok(shiftCast) is "@intCast" or "@truncate"
                ? (LowerExpr(l), LowerExprSink(r, CType.Int))
                : (LowerExpr(l), LowerExpr(r));
        // `void == void` is true, as zig has it (std.array_hash_map's `hashes_array[i] == h` where both the element and
        // `h` are `void` when `store_hash` is false, task #135); C# cannot compare the runtime `Unit`.
        if (op is BinOp.Eq or BinOp.Ne && IsVoidValueType(left.Type) && IsVoidValueType(right.Type))
        {
            return new LitBool(op == BinOp.Eq) { Type = CType.Bool };
        }
        if (rightHoisted is { Count: > 0 })
        {
            // `a and b` is `if (a) b else false`; `a or b` is `if (a) true else b`.
            var decided = new LitBool(op == BinOp.LogOr) { Type = CType.Bool };
            return op == BinOp.LogAnd
                ? HoistedValueIf(left, impureBeforeLeft, right, rightHoisted, decided, new List<CStmt>(), CType.Bool)
                : HoistedValueIf(left, impureBeforeLeft, decided, new List<CStmt>(), right, rightHoisted, CType.Bool);
        }
        // An operator over a SIMD vector is element-wise, a comparison a lane mask (T5).
        if (TryVectorBinary(op, left, right) is { } vectorOp) { return vectorOp; }
        // An untyped float literal beside an `f128` operand is a comptime_float coerced to it, so it is the binary128
        // nearest its spelling (`q > 0.1`), not the double nearest it (task #214).
        if (left.Type.Unqualified is CType.Float128Type && RetypeFloatLiteralTree(right, CType.Float128, _comptimeFloatConsts) is { } quadRight)
        {
            right = quadRight;
        }
        else if (right.Type.Unqualified is CType.Float128Type && RetypeFloatLiteralTree(left, CType.Float128, _comptimeFloatConsts) is { } quadLeft)
        {
            left = quadLeft;
        }
        // `1 << 52` (std.fmt.parse_float's `1 << (1 + fractional_bits)`, FloatInfo's `2 << 52`): an untyped literal is a
        // comptime_int, which is unbounded, but its C# `int` would shift by the count MOD 32 (`1 << 52` == `1 << 20`, a
        // silent wrong answer). It widens to the comptime_int carrier, unless the count is a constant below 31 (the result
        // then fits a positive `int` exactly).
        if (op is BinOp.Shl && left is LitInt && left.Type.Unqualified == CType.Int
            && !(_ir.ConstEval(right) is { } shiftCount && shiftCount is >= 0 and < 31))
        {
            left = left with { Type = CType.ComptimeInt };
        }
        // Pointer arithmetic on a Zig many-item pointer (`[*]T` / `[*c]T`, both lowered to
        // `CType.Pointer`): `p + i` / `p - i` yields the pointer type, and `p - q` yields a
        // signed offset (`long`). `UsualArithmetic` only knows `Prim`s — it returns `int` for a
        // pointer operand — so handle the pointer cases here, mirroring the C frontend's
        // `IrBuilder.BinaryType`. (Zig fixed arrays are values and don't decay in arithmetic, so
        // only `CType.Pointer` participates; you slice an array before pointer-walking it.)
        RejectUnrepresentableComptimeOperand(op, l, r, left, right);
        RejectIncompatiblePeerSignedness(op, l, r, left, right);
        if (TryFoldWideComptimeInt(op, l, r, left, right) is { } wide) { return wide; }
        var lPtr = left.Type.Unqualified is CType.Pointer;
        var rPtr = right.Type.Unqualified is CType.Pointer;
        var type = op switch
        {
            BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
                or BinOp.LogAnd or BinOp.LogOr => CType.Int,
            BinOp.Shl or BinOp.Shr => CType.IntegerPromote(left.Type),
            BinOp.Add or BinOp.Sub when lPtr || rPtr
                => lPtr && rPtr ? CType.Long : (lPtr ? left.Type.Unqualified : right.Type.Unqualified),
            _ => CType.UsualArithmetic(left.Type, right.Type),
        };
        return new Binary(op, left, right) { Type = type };
    }

    /// <summary>comptime_int arithmetic that leaves the range its lowered carrier holds (task #83): <c>std.math.maxInt(usize) + 1</c>
    /// is 2^64 in zig, but its folded operand is a <c>ulong</c> literal, so the C# sum wrapped (or C# rejected the constant,
    /// CS0220). When both operands are comptime_int values (an untyped literal, an untyped comptime local, a comptime-only
    /// call's fold, or such an expression) and the exact 128-bit result does not fit the ordinary result type, the
    /// expression is that result as a comptime_int literal. An in-range result is left to the ordinary lowering.</summary>
    private CExpr? TryFoldWideComptimeInt(BinOp op, Item l, Item r, CExpr left, CExpr right)
    {
        if (op is not (BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Shl or BinOp.BitOr or BinOp.BitXor or BinOp.BitAnd))
        {
            return null;
        }
        if (!IsComptimeIntOperand(l, left) || !IsComptimeIntOperand(r, right)) { return null; }
        if (_ir.ConstEval128(left) is not { } lhs || _ir.ConstEval128(right) is not { } rhs) { return null; }
        System.Int128 value;
        try
        {
            value = op switch
            {
                BinOp.Add => checked(lhs + rhs),
                BinOp.Sub => checked(lhs - rhs),
                BinOp.Mul => checked(lhs * rhs),
                BinOp.Shl => rhs >= 0 && rhs < 127 ? checked(lhs * (System.Int128.One << (int)rhs)) : throw new System.OverflowException(),
                BinOp.BitOr => lhs | rhs,
                BinOp.BitXor => lhs ^ rhs,
                _ => lhs & rhs,
            };
        }
        catch (System.OverflowException) { return null; }   // beyond 128 bits: left to the ordinary lowering (and its loud cuts)
        var ordinary = CType.UsualArithmetic(left.Type, right.Type);
        var fits = ordinary.Unqualified switch
        {
            CType.Prim { Integer: true, IsComptimeInt: true } => true,
            CType.Prim { Integer: true, Bytes: var b and < 16, Signed: var sgn } => sgn
                ? value >= -(System.Int128.One << (b * 8 - 1)) && value < (System.Int128.One << (b * 8 - 1))
                : value >= 0 && value < (System.Int128.One << (b * 8)),
            _ => true,
        };
        if (fits) { return null; }
        var magnitude = System.Int128.Abs(value).ToString(CultureInfo.InvariantCulture);
        CExpr lit = new LitInt(magnitude, value >= long.MinValue && value <= long.MaxValue ? (long)value : null) { Type = CType.ComptimeInt };
        return value < 0 ? new Unary(UnOp.Neg, lit) { Type = CType.ComptimeInt } : lit;
    }

    /// <summary>True for an operand that is a comptime_int VALUE rather than a typed one: an untyped integer literal, an
    /// untyped comptime local, a comptime-only call's fold (a call that lowered to a literal), or anything already typed
    /// comptime_int.</summary>
    private bool IsComptimeIntOperand(Item item, CExpr lowered)
    {
        while (item.Content is Zig.Grouped g) { item = g.Arg1; }
        if (lowered.Type?.Unqualified is CType.Prim { IsComptimeInt: true }) { return true; }
        return item.Content switch
        {
            Zig.IntLit => true,
            Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } sym && _comptimeIntLocals.Contains(sym),
            Zig.CallArgs => lowered is LitInt or ComptimeFold,
            _ => false,
        };
    }

    /// <summary>Reject, as zig does, a comptime-known operand its operator's type cannot hold (task #91): a literal shift
    /// amount that does not fit <c>Log2Int</c> of the shifted operand (<c>~@as(u64, 0) &gt;&gt; 88</c>: "type 'u6' cannot represent
    /// integer value '88'"), and an integer literal outside the range of its typed peer (<c>@intFromBool(b) * 10</c>: "type
    /// 'u1' cannot represent integer value '10'"). Only a width dotcc knows from the source is checked, so a comptime_int
    /// operand (unbounded) is never rejected.</summary>
    private void RejectUnrepresentableComptimeOperand(BinOp op, Item l, Item r, CExpr left, CExpr right)
    {
        if (op is BinOp.Shl or BinOp.Shr)
        {
            // Only a LITERAL amount is checked: a typed one (std.math.rotl's `1 +% ~ar`, `ar: Log2Int(T)`) fits by
            // construction, and dotcc folds its constant at the carrier's width rather than the declared one.
            var amountItem = r;
            while (amountItem.Content is Zig.Grouped ga) { amountItem = ga.Arg1; }
            if (left.Type.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false } shifted
                || amountItem.Content is not Zig.IntLit
                || _ir.ConstEval(right) is not { } amount)
            {
                return;
            }
            // A literal / comptime-known left side is comptime_int unless the source spells its width (`@as(u64, 0)`).
            var bits = DeclaredBitsOfValue(l) ?? (_ir.ConstEval(left) is null ? DeclaredBitsOfLowered(left) ?? shifted.Bytes * 8 : null);
            if (bits is not { } width || width <= 0) { return; }
            var log2 = width <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(width - 1));
            if (amount < 0 || amount >= width)
            {
                throw new CompileException(
                    $"zig: type 'u{log2}' cannot represent integer value '{amount}' (a shift of a {width}-bit operand takes an amount below {width})");
            }
            return;
        }
        if (op is not (BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Mod or BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor))
        {
            return;
        }
        foreach (var (literalItem, literal, peerItem, peer) in new[] { (r, right, l, left), (l, left, r, right) })
        {
            if (literalItem.Content is not Zig.IntLit || _ir.ConstEval128(literal) is not { } value
                || peer.Type.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false } peerPrim
                || DeclaredBitsOfValue(peerItem) is not { } peerBits || peerBits is <= 0 or > 64)
            {
                continue;
            }
            // `@intFromBool` is a `u1` and a `@clz` / `@ctz` / `@popCount` count a `Log2IntCeil(T)`, whatever carrier they lower to.
            var signed = peerPrim.Signed && !(peerItem.Content is Zig.BuiltinCall { Arg2: not null, Arg0: var peerTok }
                                              && Tok(peerTok) is "@intFromBool" or "@clz" or "@ctz" or "@popCount");
            var (min, max) = signed
                ? (-(System.Int128.One << (peerBits - 1)), (System.Int128.One << (peerBits - 1)) - 1)
                : (System.Int128.Zero, (System.Int128.One << peerBits) - 1);
            if (value < min || value > max)
            {
                throw new CompileException(
                    $"zig: type '{(signed ? "i" : "u")}{peerBits}' cannot represent integer value '{value}'");
            }
        }
    }

    /// <summary>Lower a Zig WRAPPING arithmetic operator (<c>+%</c>/<c>-%</c>/<c>*%</c>) —
    /// two's-complement arithmetic that wraps at the OPERAND width. Zig has no integer promotion,
    /// so the result type is the peer-resolved operand type (<see cref="PeerIntType"/>), and the
    /// wrap happens at that width. The emitted C# runs in the project's default <c>unchecked</c>
    /// context, where a narrowing cast truncates rather than throwing. For a sub-<c>int</c> peer
    /// width (<c>byte</c>/<c>short</c>/…) C# would promote the operands to <c>int</c> and so NOT
    /// wrap at the operand width — so a truncating <see cref="Cast"/> back to the peer type is
    /// inserted (correct even when the result is then widened: <c>u8 +% u8</c> wraps at 8 bits
    /// BEFORE any widening). At <c>int</c> and wider, native C# arithmetic already wraps at the
    /// right width, so no cast is needed.</summary>
    private CExpr WrapBin(BinOp op, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        CExpr inner = new Binary(op, left, right) { Type = t };
        // An arbitrary-width unsigned operand (`u3`, std.math.rotl's `1 +% ~ar` with `ar: Log2Int(u8)`) wraps at ITS width,
        // not its carrier's: 1 +% 6 is 7 in a u3 (task #97; it had been computed in C#'s int).
        if ((DeclaredBitsOfValue(l) ?? DeclaredBitsOfValue(r)) is { } bits) { inner = MaskToBits(inner, t, bits); }
        CExpr wrapped = t.SizeOf < 4 ? new Cast(t, inner) { Type = t } : inner;
        // Both operands LITERALS (std.hash.XxHash3's `input.len *% XxHash64.prime_1` over a comptime length, task #179): the
        // interpreter wraps at the type's width, so the result is a literal. Left as C# arithmetic, a constant product that
        // overflows is CS0220 even in an unchecked program (C# checks constant expressions at compile time). Only a literal
        // tree: a variable is no C# constant, and a call must still run (the interpreter would run it now, dropping its effects).
        if (IsLiteralTree(wrapped) && _ir.EvalComptimeValue(wrapped) is IrModule.CtInt { Value: var folded } && folded >= 0 && folded <= ulong.MaxValue)
        {
            var foldedLit = new LitInt(folded.ToString(CultureInfo.InvariantCulture), folded <= long.MaxValue ? (long)folded : null)
            {
                Type = t.SizeOf < 4 ? CType.Int : t,
            };
            // A sub-`int` carrier (`@as(u3, 5) *% 3` in a byte) takes an `int` literal under a cast: a `7u` would not narrow.
            return t.SizeOf < 4 ? new Cast(t, foldedLit) { Type = t } : foldedLit;
        }
        return wrapped;
    }

    /// <summary>Is <paramref name="e"/> built only from literals, casts and arithmetic over them, a C# constant expression?</summary>
    private static bool IsLiteralTree(CExpr e) => e switch
    {
        LitInt => true,
        Paren p => IsLiteralTree(p.Inner),
        Cast c => IsLiteralTree(c.Operand),
        Unary u => u.Op is UnOp.Neg or UnOp.BitNot or UnOp.Plus && IsLiteralTree(u.Operand),
        Binary b => IsLiteralTree(b.Left) && IsLiteralTree(b.Right),
        _ => false,
    };

    /// <summary><paramref name="value"/> reduced to its low <paramref name="bits"/> bits, for an unsigned
    /// <paramref name="carrier"/> wider than that declared width (dotcc carries a <c>u3</c> in a byte); unchanged
    /// otherwise.</summary>
    private static CExpr MaskToBits(CExpr value, CType carrier, int bits)
    {
        if (carrier.Unqualified is not CType.Prim { Integer: true, Signed: false, Bytes: var bytes } || bits >= bytes * 8 || bits <= 0)
        {
            return value;
        }
        var mask = (1L << bits) - 1;
        var maskLit = new LitInt(mask.ToString(System.Globalization.CultureInfo.InvariantCulture), mask) { Type = CType.Int };
        return new Binary(BinOp.BitAnd, value, maskLit) { Type = CType.IntegerPromote(carrier) };
    }

    /// <summary>The fixed-width integer type a wrapping/saturating operator wraps (or saturates) at —
    /// Zig's peer-resolved operand type. Valid Zig gives both operands one shared type; a bare integer
    /// literal (a <c>comptime_int</c>, lowered to a <see cref="LitInt"/>) yields to its concrete-typed
    /// peer. With both concrete the wider wins (they are equal in valid Zig; ties resolve to the left).
    /// Two comptime literals have no fixed-width peer — Zig evaluates them at comptime (exact, then
    /// coerced to the result location, erroring if it overflows), so dotcc just picks the wider integer
    /// (a fit-checking comptime engine is out of scope; a non-fitting literal pair is already a Zig
    /// error, never round-trippable code).</summary>
    private static CType PeerIntType(CExpr left, CExpr right)
    {
        var lt = left.Type.Unqualified;
        var rt = right.Type.Unqualified;
        if (left is LitInt && right is not LitInt) return rt;
        if (right is LitInt && left is not LitInt) return lt;
        return lt.SizeOf >= rt.SizeOf ? lt : rt;
    }

    /// <summary>Lower a Zig SATURATING arithmetic operator (<c>+|</c>/<c>-|</c>/<c>*|</c>) to a
    /// <c>ZigMath.Sat{Add,Sub,Mul}&lt;T&gt;</c> call (<see cref="DotCC.Libc.ZigMath"/>) that clamps
    /// the true result to the operand type's range. Zig has no integer promotion, so the result
    /// type is the peer-resolved operand type (<see cref="PeerIntType"/>); both operands are coerced
    /// to it so C# infers the generic <c>T</c> and the runtime clamps at the right width. Unlike
    /// wrapping (a truncating cast in the unchecked context), a clamp has no native C# operator, so
    /// this routes through the spliced runtime.</summary>
    private CExpr SatBin(string helper, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        GuardNo128Saturation(t);
        if (TryFoldSaturating(helper, left, right, t, DeclaredBitsOfValue(l) ?? DeclaredBitsOfValue(r)) is { } folded) { return folded; }
        var args = new List<CExpr> { CoerceToPeer(left, t), CoerceToPeer(right, t) };
        return new Call($"ZigMath.{helper}", args) { Type = t };
    }

    /// <summary>Lower zig's saturating shift left (<c>a &lt;&lt;| n</c>) to <c>ZigMath.SatShl</c>. Unlike the other
    /// saturating operators its operands are not peers: the result has the LEFT operand's type and the shift amount
    /// keeps its own (zig takes any unsigned integer there).</summary>
    private CExpr SatShl(Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = left.Type.Unqualified;
        GuardNo128Saturation(t);
        if (TryFoldSaturating("SatShl", left, right, t, DeclaredBitsOfValue(l)) is { } folded) { return folded; }
        if (left is LitInt)
        {
            throw new IrUnsupportedException(
                "zig: the left operand of `<<|` must have a fixed-width integer type when the shift amount is not known at compile time");
        }
        return new Call("ZigMath.SatShl", new List<CExpr> { left, right }) { Type = t };
    }

    /// <summary>Fold a saturating op over two comptime-known operands (std.meta.FieldEnum's
    /// <c>IntFittingRange(0, field_names.len -| 1)</c>, task #108): the exact result clamped to the peer type's range (its
    /// declared width when the source spells one), or left unclamped when both are untyped literals, a comptime_int being
    /// unbounded. Null when either operand is not comptime-known or the result leaves a <c>long</c>.</summary>
    private CExpr? TryFoldSaturating(string helper, CExpr left, CExpr right, CType t, int? declaredBits)
    {
        if (_ir.ConstEval128(left) is not { } a || _ir.ConstEval128(right) is not { } b) { return null; }
        System.Int128 v = helper switch
        {
            "SatAdd" => a + b,
            "SatSub" => a - b,
            // A value the lowering holds is at most 64 bits wide, so a shift below 60 is exact here; a larger one
            // of a non-zero value is past every integer type's range.
            "SatShl" => a == 0 || (b >= 0 && b < 60) ? a << (int)b : a > 0 ? System.Int128.MaxValue : System.Int128.MinValue,
            _ => a * b,
        };
        if (!(left is LitInt && right is LitInt) && t.Unqualified is CType.Prim { Integer: true, Signed: var signed, Bytes: var bytes })
        {
            var (min, max) = IntRange(signed, declaredBits is { } db and > 0 ? db : bytes * 8);
            v = System.Int128.Clamp(v, min, max);
        }
        if (v < long.MinValue || v > long.MaxValue) { return null; }
        var value = (long)v;
        var literal = new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value)
        {
            Type = value is >= int.MinValue and <= int.MaxValue ? CType.Int : CType.Long,
        };
        // A narrow type's literal renders through a cast (a `u8` result is `(byte)255`, not an unsuffixed-uint `255u`).
        return literal.Type == t.Unqualified ? literal : new Cast(t, literal) { Type = t };
    }

    /// <summary>Reject a saturating op (<c>+|</c>/<c>-|</c>/<c>*|</c>) at a 128-bit operand width.
    /// <see cref="DotCC.Libc.ZigMath"/> clamps via an exact-in-128-bit accumulator, which a 16-byte
    /// operand would itself overflow — so it can't honor the saturation contract there. Wrapping
    /// (<c>+%</c>) and ordinary arithmetic on <c>i128</c>/<c>u128</c> are unaffected (native C#
    /// <c>Int128</c>/<c>UInt128</c>). A documented V1 cut.</summary>
    private static void GuardNo128Saturation(CType t)
    {
        if (t.Unqualified is CType.Prim { Integer: true, Bytes: >= 16 })
        {
            throw new IrUnsupportedException(
                "saturating arithmetic (+|/-|/*|) on a 128-bit integer is not supported — the exact " +
                "128-bit accumulator would itself overflow; use wrapping (+%) or clamp manually");
        }
    }

    /// <summary>Coerce a wrapping/saturating operand to the peer integer type, skipping the cast when
    /// it already has that type — so <c>i32 +| i32</c> emits <c>ZigMath.SatAdd(a, b)</c> with no
    /// redundant casts, while <c>u8 +| 5</c> casts the literal so C# infers <c>byte</c>.</summary>
    private static CExpr CoerceToPeer(CExpr e, CType t)
        => e.Type.Unqualified.Equals(t) ? e
            // An untyped float literal at an `f128` peer is rounded at 128 bits, not converted from a double (task #214).
            : t is CType.Float128Type && RetypeFloatLiteralTree(e, t) is { } quad ? quad
            : new Cast(t, e) { Type = t };

    /// <summary>Lower a Zig SATURATING compound assignment (<c>x op|= y</c>). There is no native C#
    /// saturating compound operator, so it desugars to <c>target = ZigMath.Sat…(target, y)</c> at the
    /// LHS width. The lvalue is read on both sides; that is sound only when re-evaluating it has no
    /// side effects, so a non-repeatable target (an index/deref reached through a call) is a clear
    /// deferred error rather than a silent double-eval.</summary>
    private CStmt SatCompoundAssign(Item targetItem, string helper, Item valueItem)
        => RejectConstStore(targetItem) ?? new ExprStmt(SatCompoundAssignExpr(targetItem, helper, valueItem));

    /// <summary>The <c>Assign</c> CExpr for a saturating compound assignment <c>x op|= y</c>
    /// (<c>x = ZigMath.Sat…(x, y)</c>) — the core shared by the statement form (wrapped in an
    /// <see cref="ExprStmt"/>) and the <c>while (…) : (i +|= 1)</c> continue-expression.</summary>
    private CExpr SatCompoundAssignExpr(Item targetItem, string helper, Item valueItem)
    {
        var target = LowerExpr(targetItem);
        if (!IsRepeatableLValue(target))
        {
            throw new IrUnsupportedException(
                "a saturating compound assignment (`x op|= y`) to a target with side effects is not " +
                "supported yet — assign in two steps (`x = x op| y;`) with a simpler target");
        }
        GuardNo128Saturation(target.Type);
        var value = LowerExprSink(valueItem, target.Type);
        var call = new Call($"ZigMath.{helper}", new List<CExpr> { target, CoerceToPeer(value, target.Type) })
            { Type = target.Type };
        return new Assign(null, target, call) { Type = target.Type };
    }

    /// <summary>True when <paramref name="e"/> is an lvalue that can be re-evaluated without side
    /// effects — a variable / parameter, or a field / element / deref reached only through other
    /// repeatable sub-expressions and constants. A call anywhere makes it non-repeatable. Gates the
    /// double-read in <see cref="SatCompoundAssign"/>, whose desugar has no single-eval form.</summary>
    private static bool IsRepeatableLValue(CExpr e) => e switch
    {
        VarRef => true,
        LitInt or LitBool or LitFloat => true,
        Paren p => IsRepeatableLValue(p.Inner),
        Cast c => IsRepeatableLValue(c.Operand),
        Member m => IsRepeatableLValue(m.Base),
        Unary { Op: UnOp.Deref or UnOp.AddrOf } u => IsRepeatableLValue(u.Operand),
        DotCC.Ir.Index ix => IsRepeatableLValue(ix.Base) && IsRepeatableLValue(ix.Idx),
        _ => false,
    };

    /// <summary>Lower the two operands of an <c>==</c> / <c>!=</c> comparison, result-locating a
    /// bare enum literal <c>.member</c> against the OTHER operand's enum type — Zig's
    /// <c>self == .red</c> (the idiomatic enum-method test). The concrete side is lowered first; if
    /// it is enum-typed, the <c>.member</c> resolves to that enum's tag constant. When neither side
    /// is a bare <c>.member</c> (or the concrete side isn't an enum), both lower normally — so a
    /// bare literal with no enum partner still hits <see cref="LowerExpr"/>'s loud rejection.</summary>
    private (CExpr left, CExpr right) LowerComparisonOperands(Item l, Item r)
    {
        if (r.Content is Zig.EnumLit rel && l.Content is not Zig.EnumLit)
        {
            var left = UnionTagOrSelf(LowerExpr(l));
            return left.Type.Unqualified is CType.Enum en
                ? (left, ResolveEnumLit(Tok(rel.Arg1), en))
                : (left, LowerExpr(r));
        }
        if (l.Content is Zig.EnumLit lel && r.Content is not Zig.EnumLit)
        {
            var right = UnionTagOrSelf(LowerExpr(r));
            return right.Type.Unqualified is CType.Enum en
                ? (ResolveEnumLit(Tok(lel.Arg1), en), right)
                : (LowerExpr(l), right);
        }
        return (LowerExpr(l), LowerExpr(r));
    }

    /// <summary>A tagged union compared against a tag literal (<c>u == .off</c>, task #109) compares its TAG, as zig
    /// coerces the union to its tag enum there: the union's tag field, typed as the tag enum. Any other operand is
    /// returned unchanged.</summary>
    private CExpr UnionTagOrSelf(CExpr operand)
        => operand.Type.Unqualified is CType.Named named && _unions.TryGetValue(named.Name, out var info)
            ? new Member(operand, info.TagFieldName, false) { Type = info.TagType, IsLValue = operand.IsLValue }
            : operand;

    /// <summary>Lower a value-prefix unary op. <c>!x</c> yields an int (the backend
    /// renders it 0/1); <c>-x</c>/<c>~x</c> take the integer-promoted operand type.</summary>
    private CExpr Pre(UnOp op, Item operandItem)
    {
        var operand = LowerExpr(operandItem);
        // `~x` of an unsigned integer keeps x's type, as zig has no integer promotion: `~@as(u8, 1)` is 254, not C#'s
        // `int` -2, and a `u3` complement is masked to its three bits (std.math.rotl's `~ar`, task #97).
        if (op == UnOp.BitNot && operand.Type.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: < 4 } narrow)
        {
            CExpr complement = new Unary(op, operand) { Type = CType.IntegerPromote(narrow) };
            if ((DeclaredBitsOfValue(operandItem) ?? DeclaredBitsOfLowered(operand)) is { } bits) { complement = MaskToBits(complement, narrow, bits); }
            return new Cast(narrow, complement) { Type = narrow };
        }
        var type = op == UnOp.LogNot ? CType.Int : CType.IntegerPromote(operand.Type);
        return new Unary(op, operand) { Type = type };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr
    /// (so `&(x)` still marks `x` AddressTaken). Mirrors <c>IrBuilder.Unparen</c>.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    /// <summary>True for an expression with no side effects, safe to render more than once
    /// — the pointer <c>orelse</c> lowers to <c>a != null ? a : b</c>, naming <c>a</c>
    /// twice. Conservative: a var/param read, a literal, <c>null</c>, or a parenthesized
    /// such; anything else (a call, an assignment) is rejected to avoid double evaluation.</summary>
    private static bool IsSimpleReeval(CExpr e) => e switch
    {
        VarRef or NullPtr or LitInt or LitFloat => true,
        Paren p => IsSimpleReeval(p.Inner),
        _ => false,
    };

    /// <summary>True for an expression that yields the same value when read a second time straight after the first,
    /// with no write between: <see cref="IsSimpleReeval"/>'s leaves, and a field, element or pointee read or an
    /// arithmetic, comparison or cast over such (std.Treap's <c>node.children[0] orelse node.children[1]</c> and
    /// <c>node.children[@intFromBool(!right)] orelse unreachable</c>, task #193). Wider than IsSimpleReeval, and only for
    /// a REREAD (<c>a != null ? a : b</c>, where <c>a</c> has already been read once): a read through a pointer may trap,
    /// so it is not safe to evaluate eagerly where zig would not have evaluated it at all.</summary>
    private static bool IsPureReread(CExpr e) => e switch
    {
        VarRef or NullPtr or LitInt or LitFloat or LitBool or EnumConstRef => true,
        Paren p => IsPureReread(p.Inner),
        Member m => IsPureReread(m.Base),
        DotCC.Ir.Index ix => IsPureReread(ix.Base) && IsPureReread(ix.Idx),
        Cast c => IsPureReread(c.Operand),
        Unary { Op: UnOp.Plus or UnOp.Neg or UnOp.BitNot or UnOp.LogNot or UnOp.Deref or UnOp.AddrOf } u => IsPureReread(u.Operand),
        Binary b => IsPureReread(b.Left) && IsPureReread(b.Right),
        _ => false,
    };

    /// <summary>True if the expression is a comptime-only numeric value — an int/float
    /// literal, or arithmetic over such (Zig's <c>comptime_int</c>/<c>comptime_float</c>).
    /// These have no fixed-size ABI type, so Zig forbids passing them to a C-variadic.
    /// The moment a concrete-typed leaf appears (identifier, call, <c>@as</c>, deref,
    /// index) the expression is typed and allowed across the variadic boundary.</summary>
    private static bool IsComptimeUntypedNumeric(Item it) => it.Content switch
    {
        Zig.IntLit or Zig.FloatLit => true,
        Zig.Grouped g   => IsComptimeUntypedNumeric(g.Arg1),
        Zig.PreNeg p    => IsComptimeUntypedNumeric(p.Arg1),
        Zig.PreBitNot p => IsComptimeUntypedNumeric(p.Arg1),
        Zig.Add a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Sub a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Mul a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.DivOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.ModOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitAnd a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitXor a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitOr a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shl a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shr a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.AddSat a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.SubSat a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.MulSat a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.ShlSat a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        _ => false,
    };

    /// <summary>The range of a <paramref name="bits"/>-wide integer, as far as an <see cref="System.Int128"/> reaches: a 128-bit
    /// type (or a u127) spans every value an Int128 holds on its side of zero, and a shift by 128 would wrap.</summary>
    private static (System.Int128 Min, System.Int128 Max) IntRange(bool signed, int bits) => signed
        ? bits >= 128 ? (System.Int128.MinValue, System.Int128.MaxValue)
                      : (-(System.Int128.One << (bits - 1)), (System.Int128.One << (bits - 1)) - 1)
        : (System.Int128.Zero, bits >= 127 ? System.Int128.MaxValue : (System.Int128.One << bits) - 1);

    /// <summary>Reject, as zig does, a typed declaration whose untyped comptime integer initializer its type cannot hold
    /// (task #112): <c>const s: u8 = 300;</c>, <c>-2</c>, <c>3 -| 5</c> (comptime_int arithmetic, so -2) are "type 'u8'
    /// cannot represent integer value …". The initializer is evaluated as the comptime_int it is, before any narrowing
    /// to the declared type; the range is the declared width the source spells (a <c>u3</c> holds 0 to 7).</summary>
    private void RejectUnrepresentableInit(Item? typeItem, CType? declared, Item initExpr)
    {
        if (typeItem is null
            || declared?.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false, Name: not "_Bool", Signed: var signed, Bytes: var bytes }
            || !IsComptimeUntypedNumeric(initExpr))
        {
            return;
        }
        System.Int128? value;
        using (EnterThrowawayHoist()) { value = _ir.ConstEval128(LowerExpr(initExpr)); }
        if (value is not { } v) { return; }
        var bits = DeclaredBitsOfTypeArg(typeItem) is { } db and > 0 ? db : bytes * 8;
        var (min, max) = IntRange(signed, bits);
        if (v < min || v > max)
        {
            throw new CompileException($"zig: type '{(signed ? "i" : "u")}{bits}' cannot represent integer value '{v}'");
        }
    }

    /// <summary>Reject, as zig does, a RUNTIME integer coerced into a type that cannot hold all its values (task #165):
    /// <c>fn f(x: u16) u8 { return x; }</c> and <c>const a: u8 = x;</c> are "expected type 'u8', found 'u16'", where dotcc
    /// had truncated silently. zig coerces an integer only into a type whose range holds the source's: the same signedness
    /// at least as wide, or an unsigned source into a strictly wider signed type. Checked only where both zig types are
    /// certain: the value's (<see cref="CertainZigInt"/>, which gives none for a comptime-known value, whose coercion zig
    /// decides by the value) and the sink's, a plain integer whose width the caller passes where it is spelled.</summary>
    private void RejectIntegerNarrowing(Item valueItem, CType? sinkType, int? sinkBits)
    {
        if (sinkType?.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false, Name: not ("_Bool" or "char"), Signed: var dstSigned, Bytes: var dstBytes }
            || CertainZigInt(valueItem) is not var (srcSigned, srcBits))
        {
            return;
        }
        var dstBits = sinkBits is { } sb and > 0 ? sb : dstBytes * 8;
        var fits = srcSigned == dstSigned ? srcBits <= dstBits : !srcSigned && srcBits < dstBits;
        if (!fits)
        {
            throw new CompileException(
                $"zig: expected type '{(dstSigned ? "i" : "u")}{dstBits}', found '{(srcSigned ? "i" : "u")}{srcBits}'");
        }
    }

}
