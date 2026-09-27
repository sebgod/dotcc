#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Local declarations: <c>const</c> / <c>var</c> in a function body (<c>DeclOf</c>), the comptime-only
/// bindings that emit nothing (a comptime aggregate, a type-argument call, a selected local struct, a function alias),
/// and destructuring (<c>const a, const b = tuple;</c>). One concern of the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower a local <c>const</c> declaration, intercepting a comptime allocator /
    /// namespace binding first (Milestone F): <c>const std = @import("std");</c> /
    /// <c>const a = std.heap.page_allocator;</c> carry no runtime value, so they register the
    /// alias (<see cref="TryComptimeConstBinding"/>) and emit nothing (an empty <see cref="Seq"/>).
    /// Any other <c>const</c> is an ordinary <see cref="DeclOf"/>.</summary>
    /// <summary>Bind <c>const x = comptime E</c> whose value is a struct or array into the interpreter's
    /// comptime variables (see <see cref="DeclOrComptime"/>). False, with nothing bound, for any other value.</summary>
    private bool TryBindComptimeAggregateConst(Item nameTok, Item? typeItem, Item initExpr)
    {
        var declared = typeItem is { } ti ? LowerType(ti) : null;
        CExpr init;
        using (EnterThrowawayHoist()) { init = declared is { } dt ? LowerExprSink(initExpr, dt) : LowerExpr(initExpr); }
        if (init is not ComptimeFold { Resolved: StructInit or StackArray } || _ir.EvalComptimeValue(init) is not { } value)
        {
            return false;
        }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = declared ?? init.Type });
        _ir.ComptimeGlobals[sym] = value;
        return true;
    }
    /// <summary>Bind <c>const x = f(T, U)</c>, a call whose every argument is a type and whose result struct is
    /// comptime-only (a <c>comptime_int</c> field), as a comptime aggregate (see <see cref="DeclOrComptime"/>). False, with nothing bound, for any
    /// other initializer, or when the call does not evaluate at compile time (then it is an ordinary runtime call).</summary>
    private bool TryBindTypeArgumentCallConst(Item nameTok, Item initExpr)
    {
        if (initExpr.Content is not Zig.CallArgs call || Flatten(call.Arg2) is not { Count: > 0 } args
            || !args.All(a => TryTypeAliasRhs(a, out _)))
        {
            return false;
        }
        CExpr inner;
        using (EnterThrowawayHoist())
        {
            try { inner = LowerExpr(initExpr); }
            catch (IrUnsupportedException) { return false; }
        }
        // Only a COMPTIME-ONLY struct (a `comptime_int` field, as std.fmt.parse_float's FloatInfo has): zig evaluates a call
        // returning one at compile time. Any other call stays a runtime call, side effects included.
        if (inner.Type?.Unqualified is not CType.Named { Name: var resultName }
            || _ir.StructFieldsOf(resultName) is not { } resultFields
            || !resultFields.Any(f => f.Type.Unqualified is CType.Prim { IsComptimeInt: true })
            || _ir.ResolveComptimeFold(inner) is not StructInit resolved)
        {
            return false;
        }
        var fold = new ComptimeFold(inner) { Type = inner.Type, Resolved = resolved };
        if (_ir.EvalComptimeValue(fold) is not { } value) { return false; }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = inner.Type });
        _ir.ComptimeGlobals[sym] = value;
        return true;
    }
    private CStmt DeclOrComptime(Item nameTok, Item? typeItem, Item initExpr)
    {
        // `const add = switch (sign) { .pos => math.add, .neg => math.sub };` (std.fmt.parseIntWithSign):
        // a comptime alias of a FUNCTION, here a generic of another module picked by a comptime switch.
        // It has no runtime value to hold (a generic has no single address); a call through it
        // instantiates the function it names (see the bare-call path).
        if (typeItem is null && TryResolveFnAlias(initExpr) is { } fnAlias)
        {
            _fnAliases[Tok(nameTok)] = fnAlias;
            return new Seq(new List<CStmt>());
        }
        if (TryComptimeConstBinding(Tok(nameTok), initExpr)) { return new Seq(new List<CStmt>()); }
        // `const Scan = if (std.simd.suggestVectorLength(u8)) |vec_size| struct {…} else struct {…};` (std.mem.eqlBytes):
        // the comptime condition picks ONE struct, declared as a local container with the capture as its comptime value.
        if (typeItem is null && TryLowerSelectedLocalStruct(Tok(nameTok), initExpr) is { } selectedStruct) { return selectedStruct; }
        // `const placeholder = comptime std.fmt.Placeholder.parse(…);`: a comptime AGGREGATE lives in the
        // interpreter, like a `comptime var` of one (E3), so a later comptime read (`switch (placeholder.arg)`)
        // folds; a runtime read renders it where it stands.
        if (initExpr.Content is Zig.PreComptime && TryBindComptimeAggregateConst(nameTok, typeItem, initExpr))
        {
            return new Seq(new List<CStmt>());
        }
        // `const float_info = FloatInfo.from(T);` (std.fmt.parse_float): FloatInfo is comptime-only (its fields are
        // `comptime_int`), so zig evaluates the call at compile time without the `comptime` keyword; the struct binds as
        // the interpreter's aggregate, and `float_info.mantissa_explicit_bits + 3` can be a `comptime precision` argument.
        if (typeItem is null && TryBindTypeArgumentCallConst(nameTok, initExpr))
        {
            return new Seq(new List<CStmt>());
        }
        if (typeItem is null && TryBindComptimeOptionalSwitch(nameTok, initExpr)) { return new Seq(new List<CStmt>()); }
        // `const is_comptime = @TypeOf(x) == comptime_int;` (std.math.cast): a TYPE comparison (or a comptime
        // tag test) is a comptime bool with no runtime operands to hold, so it binds the folded literal.
        if (typeItem is null && TryFoldComptimeCondition(initExpr) is { } flag)
        {
            _comptimeValues[Tok(nameTok)] = new LitBool(flag) { Type = CType.Bool };
            return new Seq(new List<CStmt>());
        }
        // `const init_capacity: comptime_int = @max(1, std.atomic.cache_line / @sizeOf(T));` (array_list): a
        // comptime-only integer has no runtime type to hold it, so it folds and binds the literal.
        if (typeItem?.Content is Zig.Ident { Arg0: var ctTok } && Tok(ctTok) == "comptime_int")
        {
            if (ComptimeIntValue(LowerExprSink(initExpr, CType.Long)) is not { } ctValue)
            {
                throw new IrUnsupportedException(
                    $"zig `const {Tok(nameTok)}: comptime_int` must be initialized with a compile-time-known integer");
            }
            _comptimeValues[Tok(nameTok)] = new LitInt(ctValue.ToString(CultureInfo.InvariantCulture), ctValue) { Type = CType.Long };
            return new Seq(new List<CStmt>());
        }
        return DeclOf(nameTok, typeItem, initExpr, isConst: true);
    }
    /// <summary>A local <c>const NAME = if (c) struct {…} else struct {…};</c> or its captured form over a comptime
    /// optional: the condition folds, and the chosen struct is declared as a local container (with methods and
    /// consts) whose comptime value seeds include the capture (<c>vec_size</c>). Null for any other initializer; a
    /// condition that does not fold, or an enum arm, is loud.</summary>
    private CStmt? TryLowerSelectedLocalStruct(string name, Item initExpr)
    {
        Item arm;
        var extraSeeds = new List<ValueSeed>();
        switch (initExpr.Content)
        {
            case Zig.IfExprTypeArms ta:
                arm = (TryFoldComptimeCondition(ta.Arg2) ?? (_ir.ConstEval(LowerExpr(ta.Arg2)) is { } cv ? cv != 0 : (bool?)null))
                      switch
                {
                    true => ta.Arg4,
                    false => ta.Arg6,
                    null => throw new IrUnsupportedException($"zig: `const {name} = if (…) struct {{…}} else …` needs a comptime condition"),
                };
                break;
            case Zig.IfExprCaptureTypeArms ca:
                if (!TryComptimeOptionalCond(ca.Arg2, out var copt))
                {
                    throw new IrUnsupportedException(
                        $"zig: `const {name} = if (x) |v| struct {{…}} else …` needs a comptime-known optional");
                }
                if (copt.HasValue)
                {
                    arm = ca.Arg7;
                    extraSeeds.Add((Tok(ca.Arg5), copt.Value, copt.Inner));
                }
                else
                {
                    arm = ca.Arg9;
                }
                break;
            default:
                return null;
        }
        if (arm.Content is not Zig.TypeArmStruct selected)
        {
            throw new IrUnsupportedException($"zig: `const {name} = if (…) …` selects a non-struct type arm, which is not lowered yet");
        }
        return LowerLocalStruct(name, selected.Arg2, AggregateLayout.Default, extraSeeds);
    }
    /// <summary>Bind <c>const arg_pos = comptime switch (placeholder.arg) { .none =&gt; null, .number =&gt; |pos| pos, … };</c>
    /// (std.Io.Writer.print) as a comptime OPTIONAL: a switch over a comptime subject with a <c>null</c> prong is
    /// zig's <c>?T</c>, and the selected prong is either that <c>null</c> or a compile-time integer. Its later
    /// reads (<c>arg_state.nextArg(arg_pos)</c>, run by the interpreter) then see a constant. False when the
    /// switch has no <c>null</c> prong, its subject is not comptime-known, or the payload does not fold.</summary>
    private bool TryBindComptimeOptionalSwitch(Item nameTok, Item initExpr)
    {
        var rhs = initExpr.Content is Zig.ComptimeSwitchExpr cs ? cs.Arg1 : initExpr;
        var (subject, prongsItem) = rhs.Content switch
        {
            Zig.SwitchExpr s => (s.Arg2, s.Arg5),
            Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
            _ => ((Item?)null, (Item?)null),
        };
        if (subject is null || prongsItem is null) { return false; }
        if (!Flatten(prongsItem).Any(p => DecomposeProng(p).Expr?.Content is Zig.NullLit)) { return false; }
        if (SelectComptimeProng(subject, prongsItem, out var payload) is not { Expr: { } value } prong) { return false; }
        bool hasValue;
        long v = 0;
        CType inner = CType.ULong;
        if (value.Content is Zig.NullLit)
        {
            hasValue = false;
        }
        else
        {
            EnterComptimeProng(prong, payload);
            try
            {
                CExpr lowered;
                using (EnterThrowawayHoist()) { lowered = LowerExpr(value); }
                if (_ir.ConstEval(lowered) is not { } folded) { return false; }
                hasValue = true;
                v = folded;
                if (lowered.Type?.Unqualified is CType.Prim { Integer: true, IsComptimeInt: false, Name: not "_Bool" } t) { inner = t; }
            }
            finally { ExitComptimeProng(); }
        }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = new CType.Optional(inner) });
        _comptimeOptionalVars[sym] = (hasValue, v, inner);
        return true;
    }
    /// <summary>Local comptime aliases of a function (see <see cref="DeclOrComptime"/>): name → the
    /// module that owns the function and its symbol. Function-flat, like the other comptime bindings.</summary>
    private readonly Dictionary<string, (ZigLowering Owner, Symbol Sym)> _fnAliases = new(System.StringComparer.Ordinal);
    /// <summary>The function a comptime <c>const</c> initializer names, or null: a module-qualified
    /// GENERIC function (<c>math.add</c>), or a <c>switch</c> / <c>if</c> whose comptime-known subject
    /// selects an arm that names one. A non-generic function is left to the ordinary path, where it is a
    /// fn-pointer value.</summary>
    private (ZigLowering Owner, Symbol Sym)? TryResolveFnAlias(Item rhs)
    {
        switch (rhs.Content)
        {
            case Zig.Grouped g:
                return TryResolveFnAlias(g.Arg1);
            case Zig.Field f when ResolveModulePath(f.Arg0)?.Lowering is { } owner
                               && owner.ResolveExportedDecl(Tok(f.Arg2)) is { } decl
                               && decl.Sym.Kind == SymKind.Func && decl.Owner.IsGenericTemplate(decl.Sym):
                return decl;
            case Zig.SwitchExpr or Zig.SwitchExprTrailing:
            {
                var (subjectItem, prongsItem) = rhs.Content switch
                {
                    Zig.SwitchExpr s => (s.Arg2, s.Arg5),
                    Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
                    _ => throw new System.InvalidOperationException(),
                };
                if (!LooksLikeFnAliasArms(prongsItem)) { return null; }
                CExpr subject;
                using (EnterThrowawayHoist()) { subject = LowerExpr(subjectItem); }
                if (_ir.ConstEval(subject) is not { } v) { return null; }
                Item? elseArm = null;
                foreach (var prong in Flatten(prongsItem))
                {
                    if (prong.Content is not Zig.ProngExpr pe) { return null; }
                    if (pe.Arg0.Content is Zig.CaseElse) { elseArm = pe.Arg2; continue; }
                    foreach (var label in LowerCaseVals(pe.Arg0, subject.Type))
                    {
                        if (label.HiExpr is null && label.CaseExpr is { } ce && _ir.ConstEval(ce) == v)
                        {
                            return TryResolveFnAlias(pe.Arg2);
                        }
                    }
                }
                return elseArm is { } ea ? TryResolveFnAlias(ea) : null;
            }
            case Zig.IfExpr e:
            {
                // std/sort/block.zig: `if (builtin.mode == .Debug) struct { … }.lessThan else lessThanFn`.
                if (TryFoldComptimeCondition(e.Arg2) is { } taken) { return TryResolveFnAlias(taken ? e.Arg4 : e.Arg6); }
                CExpr cond;
                using (EnterThrowawayHoist()) { cond = LowerExpr(e.Arg2); }
                return _ir.ConstEval(cond) is { } c ? TryResolveFnAlias(c != 0 ? e.Arg4 : e.Arg6) : null;
            }
            // An arm naming a comptime FUNCTION parameter (`else lessThanFn`), or a closure-idiom method.
            case Zig.Ident fid when _fnAliases.TryGetValue(Tok(fid.Arg0), out var passed):
                return passed;
            case Zig.StructMemberExpr sme:
                return (this, ReifyClosureExpr(rhs, sme));
            default:
                return null;
        }
    }
    /// <summary>Reify a closure-idiom struct in EXPRESSION position (<c>struct { fn f … }.f</c>) and return
    /// the method, memoized per source site. Its method bodies drain without the enclosing instance's
    /// comptime seeds (a V1 cut the only std site, block.zig's Debug-mode arm, never reaches in the
    /// ReleaseFast mode dotcc reports).</summary>
    private Symbol ReifyClosureExpr(Item site, Zig.StructMemberExpr sme)
    {
        if (_closureSites.TryGetValue(site, out var known)) { return known; }
        var owner = $"{_currentFnName}__L{_closureSites.Count}";
        var sym = ReifyClosureStruct(owner, sme.Arg2, Tok(sme.Arg5),
            System.Array.Empty<TypeSeed>(), System.Array.Empty<ValueSeed>(),
            System.Array.Empty<(string, bool, long, CType)>());
        _closureSites[site] = sym;
        return sym;
    }
    /// <summary>Each closure-idiom expression site → its reified method (<see cref="ReifyClosureExpr"/>).</summary>
    private readonly Dictionary<Item, Symbol> _closureSites = new(ReferenceEqualityComparer.Instance);
    /// <summary>True when every arm of a switch is a bare dotted path (<c>.pos =&gt; math.add</c>): the only
    /// shape <see cref="TryResolveFnAlias"/> evaluates, so an ordinary value switch is never lowered twice.</summary>
    private static bool LooksLikeFnAliasArms(Item prongsItem)
        => Flatten(prongsItem).All(p => p.Content is Zig.ProngExpr { Arg2.Content: Zig.Field });
    // `const`/`var x = init;` — lower under an ANF hoist buffer so a catch/orelse in a SUB-expression
    // of the initializer (`const r = 1 + (a catch b());`) lifts to a temp before the decl. A
    // WHOLE-init catch / control-flow fallback is intercepted at the top of DeclOfInner (its own
    // statement lowering), leaving the buffer empty, so this wrap is a no-op for those.
    private CStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr, bool isConst = false)
    {
        var before = _symbols.Resolve(Tok(nameTok));
        var decl = Hoisted(() => DeclOfInner(nameTok, typeItem, initExpr, isConst));
        // A `const` local is immutable: a later store to it is zig's "cannot assign to constant" (task #95).
        if (isConst && _symbols.Resolve(Tok(nameTok)) is { } declared && !ReferenceEquals(declared, before))
        {
            _zigConstBindings.Add(declared);
        }
        return decl;
    }
    private CStmt DeclOfInner(Item nameTok, Item? typeItem, Item initExpr, bool isConst)
    {
        // Compute the declared type FIRST: a result-located init (`.member` / `.{…}`) needs
        // it as its sink, so resolve the annotation before lowering the initializer.
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        RejectUnrepresentableInit(typeItem, declared, initExpr);
        if (typeItem is not null) { RejectIntegerNarrowing(initExpr, declared, DeclaredBitsOfTypeArg(typeItem)); }
        // `const x = blk: { … break :blk v; };` — a labeled value-block initializer. Temp-fill it
        // (the declared type, if any, is the sink), then bind `x` to the result temp.
        if (IsLabeledValue(initExpr))
        {
            return LowerLabeledValue(initExpr, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const x = switch (y) { … blk: {…} };` / `const x = if (c) blk:{…} else …;` — a value-
        // position if/switch with a labeled-block (statement-producing) branch (Milestone Y, part 1):
        // temp-fill it as a statement (the declared type, if any, is the sink), then bind `x` to the
        // result temp. An all-simple-value if/switch is NOT intercepted here (it stays the clean C#
        // ternary / switch-expression).
        if (IsValueControlFlowStmt(initExpr))
        {
            return LowerValueControlFlowStmt(initExpr, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const v = a catch return [x];` / `const v = a orelse return [x];` (Milestone N, part 6) —
        // a control-flow fallback. On the error/none path the `return` runs (early-out); on success
        // `v` binds the unwrapped payload.
        if (IsControlFlowFallback(initExpr, out var cfLhs, out var cfCatch, out var cfCap, out var cfArm))
        {
            return LowerControlFlowFallback(cfLhs, cfCatch, cfCap, cfArm, payload =>
            {
                var ptype = declared ?? payload.Type ?? CType.Int;
                var psym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ptype });
                // A `const` bound to a compile-time-known payload is itself comptime-known (`const chunk_len =
                // std.simd.suggestVectorLength(u16) orelse break :vectorized;` sizes a `@Vector`, task #144).
                if (isConst && payload is LitInt { Value: { } knownValue })
                {
                    // A comptime_int payload reads back as a plain `long`, not its 128-bit carrier, so `ptr + chunk_len` types.
                    var readType = declared is null && ptype.Unqualified is CType.Prim { Integer: true } payloadPrim
                                   && (payloadPrim.IsComptimeInt || payloadPrim.Bytes == 16)
                        ? CType.Long
                        : ptype;
                    _comptimeVars[psym] = (knownValue, readType);
                    return new Seq(new List<CStmt>());
                }
                return new DeclStmt(new List<LocalDecl> { new(psym, payload) });
            }, resultSink: declared);
        }
        // `const v = a catch |e| b;` / `const v = a catch <side-effecting>;` (Milestone N, part 3) —
        // a capturing or side-effecting catch needs a statement context (the fallback runs only on
        // error; the capture binds `e`). Hoist + (bind) + initialize `v` from the lazy ternary. A
        // simple, side-effect-free `a catch b` (no capture) yields empty pre and falls through to the
        // normal path below — the eager `ErrUnion.Catch`, unchanged.
        if (initExpr.Content is Zig.CatchOp or Zig.CatchCapture)
        {
            string? capName = initExpr.Content is Zig.CatchCapture cc ? Tok(cc.Arg3) : null;
            var unionIt = initExpr.Content switch { Zig.CatchOp co => co.Arg0, Zig.CatchCapture c2 => c2.Arg0, _ => initExpr };
            var fbIt = initExpr.Content switch { Zig.CatchOp co => co.Arg2, Zig.CatchCapture c2 => c2.Arg5, _ => initExpr };
            var (pre, value) = LowerCatchValue(unionIt, capName, fbIt);
            // Capture always lowers structurally; a no-capture catch only when it hoisted (i.e. the
            // fallback was side-effecting). A simple no-capture catch (empty pre) falls through.
            if (capName is not null || pre.Count > 0)
            {
                var ctype = declared ?? value.Type ?? CType.Int;
                var csym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ctype });
                pre.Add(new DeclStmt(new List<LocalDecl> { new(csym, value) }));
                return pre.Count == 1 ? pre[0] : new Seq(pre);
            }
        }
        // `var b: [N]T = …;` → a stackalloc'd C array (ArrayDecl → `T* b = stackalloc T[…]`), so
        // `b[i]` / `b[lo..hi]` reuse the array paths and yield a stack-backed slice. `undefined`
        // gives a zeroed extent; an array literal (`.{…}` / `[N]T{…}`, Milestone K) gives a
        // stackalloc with the element inits. The literal lowers BEFORE the symbol is declared, so
        // the array name isn't visible in its own initializer.
        if (declared is CType.Array arr)
        {
            // `[N:s]T` sentinel array (part 4; non-zero sentinel in Milestone Z): reserve ONE extra
            // trailing slot for the sentinel. The symbol keeps the logical `CType.Array(element, N)`
            // type (so `.len` / slicing exclude the sentinel); only the stackalloc extent (and the
            // literal's element list) grow by one. A ZERO sentinel rides C#'s zero-fill; a NON-ZERO
            // sentinel is written into the trailing slot explicitly.
            var sentinel = IsSentinelArrayType(typeItem);
            var sentVal = sentinel ? SentinelArrayValue(typeItem) : 0;
            if (initExpr.Content is Zig.UndefinedLit)
            {
                // A multi-dimensional array (`var temp: [n][16]u32 = undefined;` in std.crypto.blake3, task #154) is one
                // flat run of the innermost element, as its literal and its subscripts are.
                var n = (arr.Count ?? 0) * RowFlatCount(arr) + (sentinel ? 1 : 0);
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                var count = new LitInt(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.Int };
                var decl = new ArrayDecl(sym, arr.FlatElement, count, null);   // C# zero-fills the stackalloc
                if (sentinel && sentVal != 0)
                {
                    // Zero-fill left the trailing slot at 0; write the actual non-zero sentinel there.
                    var nIdx = arr.Count ?? 0;
                    var slot = new DotCC.Ir.Index(new VarRef(sym) { Type = arr, IsLValue = true },
                        new LitInt(nIdx.ToString(CultureInfo.InvariantCulture), nIdx) { Type = CType.ULong })
                        { Type = arr.Element, IsLValue = true };
                    var write = new ExprStmt(new Assign(null, slot,
                        new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int })
                        { Type = arr.Element });
                    return new Seq(new List<CStmt> { decl, write });
                }
                return decl;
            }
            // `const blk: [4]u64 = @bitCast([_][16]u8{ … });` and `const wide: [2]u64 = @bitCast(@as(u128, a) *% b);`
            // (std.hash.XxHash3, task #176): the local gets its own storage and the operand's bytes are copied in, an
            // array from its storage, a scalar through an addressable temp. Both sizes must be known and equal, as zig requires.
            if (!sentinel && arr.Count is { } bitCount
                && initExpr.Content is Zig.BuiltinCall { Arg0: var bitCastTok } bitCastCall && Tok(bitCastTok) == "@bitCast"
                && Flatten(bitCastCall.Arg2) is [var bitCastArg])
            {
                var source = LowerExpr(bitCastArg);
                long destBytes = (long)bitCount * RowFlatCount(arr) * arr.FlatElement.SizeOf;
                long? sourceBytes = source.Type.Unqualified switch
                {
                    CType.Array { Count: { } sourceCount } sourceArr => (long)sourceCount * RowFlatCount(sourceArr) * sourceArr.FlatElement.SizeOf,
                    CType.Prim { Integer: true, IsComptimeInt: false } or CType.Prim { Integer: false } => source.Type.Unqualified.SizeOf,
                    // `@bitCast(secret[56..72].*)` (XxHash3's `flip`, task #178): a slice with comptime-known bounds, zig's array.
                    CType.Slice { Element: var sliceElem } when source is SliceNew { Len: var sliceLen } && _ir.ConstEval(sliceLen) is { } knownLen
                        => knownLen * sliceElem.Unqualified.SizeOf,
                    CType.Vector { Element: var laneType, Count: var laneCount } => (long)laneCount * laneType.Unqualified.SizeOf,
                    _ => null,
                };
                if (sourceBytes is null)
                {
                    throw new IrUnsupportedException(
                        $"zig `@bitCast` into the array local '{Tok(nameTok)}' from a {source.Type.Describe()} is not supported yet");
                }
                if (sourceBytes is not { } knownSource || knownSource != destBytes || destBytes <= 0)
                {
                    throw new CompileException(
                        $"zig: @bitCast size mismatch: '{Tok(nameTok)}' holds {destBytes} bytes, its operand {(sourceBytes is { } sb ? sb + " bytes" : "an unknown size")}");
                }
                var bitSym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                var flatCount = (arr.Count ?? 0) * RowFlatCount(arr);
                var bitStmts = new List<CStmt>
                {
                    new ArrayDecl(bitSym, arr.FlatElement, new LitInt(flatCount.ToString(CultureInfo.InvariantCulture), flatCount) { Type = CType.Int }, null),
                };
                CExpr sourceBytesPtr = source is SliceNew { Ptr: var slicePtr } ? slicePtr : source;
                if (source.Type.Unqualified is CType.Prim or CType.Vector)
                {
                    var scalarTemp = _symbols.Declare(new Symbol
                    {
                        Name = "__bits" + _anfTempCounter++, Kind = SymKind.Var, Type = source.Type.Unqualified, AddressTaken = true,
                    });
                    bitStmts.Add(new DeclStmt(new List<LocalDecl> { new(scalarTemp, source) }));
                    sourceBytesPtr = new Unary(UnOp.AddrOf, new VarRef(scalarTemp) { Type = scalarTemp.Type, IsLValue = true })
                    { Type = new CType.Pointer(scalarTemp.Type) };
                }
                bitStmts.Add(new ExprStmt(new Call("memcpy", new List<CExpr>
                {
                    new VarRef(bitSym) { Type = arr, IsLValue = true },
                    sourceBytesPtr,
                    new LitInt(destBytes.ToString(CultureInfo.InvariantCulture), destBytes) { Type = CType.Int },
                }) { Type = new CType.Pointer(CType.Void) }));
                return new Seq(bitStmts);
            }
            var arrInit = LowerExprSink(initExpr, arr);
            // A `comptime EXPR` initializer is a ComptimeFold until pass 3 resolves it to a
            // StackArray (e.g. `const t: [N]T = comptime buildTable();`). Route it through the
            // ordinary DeclStmt path — the symbol is array-typed (renders `T*`), and the backend
            // hoists the resolved StackArray into `T* t = stackalloc T[]{…}` exactly as the
            // inferred-type form does. (A sentinel `[N:0]T` would need the +1 stackalloc slot, which
            // this path can't add, so a comptime sentinel array stays a clear error below.)
            // A CALL returning `[N]T` (std.mem.reverse's `const left_shuffled: [simd_size]T = reverseVector(…)`)
            // hands back a fresh copy the caller owns (ZigAlloc.CopyArrayResult), so binding it keeps zig's
            // by-value semantics, as the inferred `const t = f();` form already does.
            // `const c: [3]u8 = a;`: another array's VALUE, so the local gets its own storage and a copy. So is
            // `const t: [n]Vec = vecs.*;` through a pointer to an array (std.crypto.blake3's transposeVecs, task #140).
            if (arrInit is Unary { Op: UnOp.Deref, Operand: var derefd } && PointedArray(derefd) is ({ } pointedInit, _))
            {
                arrInit = pointedInit;
            }
            if (IsArrayLvalue(arrInit) && !sentinel && arr.Count is { } copyCount)
            {
                var csym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                return ArrayValueCopyDecl(csym, arr, copyCount, arrInit);
            }
            if ((arrInit is ComptimeFold || arrInit is Call { Type: CType.Array }) && !sentinel)
            {
                var fsym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                return new DeclStmt(new List<LocalDecl> { new(fsym, arrInit) });
            }
            if (arrInit is not StackArray sa)
            {
                throw new IrUnsupportedException(
                    $"a `[N]T` array local '{Tok(nameTok)}' must be initialized with an array literal (`.{{…}}` / `[N]T{{…}}`) or `undefined`");
            }
            var asym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
            // Append the trailing sentinel → `stackalloc T[]{ e0, …, eN-1, s }` lays down N+1 slots.
            // The sentinel is an `int` literal (NOT element-typed): it renders bare (e.g. `0` / `5`),
            // which C#'s constant conversion accepts into any element type — an element-typed literal
            // on an unsigned/narrow element would render `0u`/`5u` and fail the implicit byte conversion.
            var elems = sentinel
                ? new List<CExpr>(sa.Elems) { new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int } }
                : sa.Elems;
            var countLit = new LitInt(elems.Count.ToString(CultureInfo.InvariantCulture), elems.Count) { Type = CType.Int };
            return new ArrayDecl(asym, sa.Element, countLit, elems);
        }
        var init = LowerExprSink(initExpr, declared);
        // `const blob = b[off..][0..8].*;` (std.crypto.siphash's update, task #159): a slice of comptime-known length
        // deref'd is an ARRAY copy, so the local is a `[8]u8` with its own storage. Standing for the slice, it could not
        // be passed to `round(self, b: [8]u8)`. A length only known at run time keeps the slice (zig rejects such a `.*`).
        if (declared is null && initExpr.Content is Zig.Deref && init is SliceNew derefSlice
            && _ir.ConstEval(derefSlice.Len) is { } derefLen and > 0 and <= 4096)
        {
            var derefArray = new CType.Array(derefSlice.Element.Unqualified, (int)derefLen);
            var dsym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = derefArray });
            return ArrayValueCopyDecl(dsym, derefArray, derefLen, derefSlice.Ptr);
        }
        // `const t = x > 2;` is a zig `bool`, though the IR types a comparison as C's `int` (task #81): `{}` prints it
        // `true`, and `@TypeOf(t)` is `bool`.
        var type = declared ?? (IsZigBoolValue(init) ? CType.Bool : init.Type) ?? CType.Int;
        // `var result = 10;` in a comptime-only function (std.math.log10's `pow10`, task #171) is zig's comptime_int, which
        // only such a body may hold in a `var`: an `int` carrier wrapped `result *= result` at 32 bits (10^16 read back as
        // 1874919424), a silent miscompile of every power the interpreter built past 2^31.
        if (declared is null && !isConst && type.Unqualified is CType.Prim { Integer: true, IsComptimeInt: false }
            && IsComptimeUntypedNumeric(initExpr) && _currentFnSym is { } ctFn && IsComptimeOnlyFn(ctFn))
        {
            type = CType.ComptimeInt;
        }
        // `var b = a;` of an array local: zig arrays are VALUES, so `b` is a copy, not a second name for `a`'s
        // storage (the C# rep of an array local is its element pointer, which a plain decl would share).
        if (declared is null && type.Unqualified is CType.Array { Count: { } untypedCount } untypedArr && IsArrayLvalue(init))
        {
            var usym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = untypedArr });
            return ArrayValueCopyDecl(usym, untypedArr, untypedCount, init);
        }
        // A local `const` whose integer initializer folds IS that value in every comptime question, as a
        // top-level one is (and as zig has it): `const max_format_args = @typeInfo(ArgSetType).int.bits;`
        // makes std.Io.Writer.print's `if (field_names.len > max_format_args) @compileError(…)` fold.
        // An ENUM const folds the same way (std.MultiArrayList's `const field = @as(Field, @enumFromInt(i));` in an unrolled
        // copy, passed on as a `comptime field: Field` argument, task #108).
        var folded = isConst && type.Unqualified is CType.Prim { Integer: true } or CType.Enum ? _ir.ConstEval(init) : null;
        var sym2 = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok), Kind = SymKind.Var, Type = type,
            IsConstexpr = folded is not null, ConstValue = folded ?? 0,
        });
        if (declared is null && init is LitStr) { _stringLiteralSyms.Add(sym2); }
        if (isConst && declared is null && init is LitStr && ComptimeStringArg(initExpr) is { } constText) { _constStringLocals[sym2] = constText; }
        // `const value = 42;` is a comptime_int in zig (the lowered local is an `int` carrier): an `anytype` it is
        // passed to binds it as a comptime value (ComptimeIntArgValue).
        if (declared is null && isConst && folded is not null && initExpr.Content is Zig.IntLit) { _comptimeIntLocals.Add(sym2); }
        // A `comptime_int` const whose initializer is a CALL (std.sort.pdq's `const stack_size = math.log2(math.maxInt(usize) + 1);`)
        // does not fold here; an array extent that names it runs the call then (task #73, see ConstEvalArraySize). Only a
        // comptime_int: zig rejects a runtime-typed call result (`const n = f(3);`) as an extent, and so does dotcc.
        // A comptime-only call's fold (`ComptimeFold`, its result carried as an Int128) is a comptime_int as well.
        if (isConst && folded is null && (init.Type?.Unqualified is CType.Prim { IsComptimeInt: true } || init is ComptimeFold))
        {
            _unfoldedConstInits[sym2] = init;
        }
        // A const bound to a `comptime label: { … }` block (std.meta.stringToEnum's `const kvs = comptime build_kvs: { … };`,
        // task #116) is comptime-known: its evaluated aggregate is what a later comptime use reads (`initComptime(kvs)`).
        if (isConst && initExpr.Content is Zig.ComptimeLabeledBlock && _ir.EvalComptimeValue(init) is { } blockValue)
        {
            _ir.ComptimeGlobals[sym2] = blockValue;
        }
        // A const bound to a `comptime` bool (std.Io.Writer.printValue's `const is_any = comptime std.mem.eql(u8, fmt, ANY);`,
        // task #121): a later `if (!is_any and …) invalidFmtError(…)` settles at compile time, as zig's does.
        if (isConst && init is ComptimeFold { Resolved: LitBool { Value: var comptimeBool } })
        {
            _ir.ComptimeGlobals[sym2] = new IrModule.CtBool(comptimeBool);
        }
        if (typeItem is not null) { _annotatedLocals.Add(sym2); }
        RecordValueBits(sym2,
            typeItem is { } ti ? DeclaredBitsOfTypeArg(ti) : DeclaredBitsOfValue(initExpr) ?? DeclaredBitsOfLowered(init),
            typeItem is { } te ? ElemBitsOfTypeAst(te) : DeclaredElemBitsOfValue(initExpr));
        if (type.Unqualified is CType.Pointer)
        {
            RecordValuePtrSize(sym2, typeItem is { } tp ? PointerSizeOfTypeArg(tp) : PointerSizeOfValue(initExpr));
        }
        // A `void` local (`var unit: void = {};`) has no storage and no C# spelling: the name stays
        // declared, so a use of it is an (erasable) void read, and the declaration emits nothing.
        if (type.Unqualified is CType.VoidType && IsErasableVoid(init)) { return new Seq(new List<CStmt>()); }
        // `const lit = .blue;` (task #113): an enum literal lives only at compile time. Each read carries the member in its
        // type and coerces where it meets an enum, so the declaration emits nothing; a `var` of one is zig's error.
        if (type.Unqualified is CType.EnumLiteral)
        {
            return isConst
                ? new Seq(new List<CStmt>())
                : throw new CompileException(
                    $"zig: variable of type '@EnumLiteral()' must be const or comptime ('{Tok(nameTok)}')");
        }
        return new DeclStmt(new List<LocalDecl> { new(sym2, init) });
    }
    /// <summary>Lower a destructure binding <c>&lt;binder&gt;, &lt;binder&gt;… = e;</c> (Milestone G,
    /// extended in S). A binder is a fresh <c>const</c>/<c>var</c> (optionally typed <c>: T</c>), an
    /// existing lvalue, or a <c>_</c> discard. Two lowerings, picked by the RHS shape:
    /// <list type="bullet">
    /// <item>A tuple-LITERAL RHS (<c>.{e0, e1, …}</c>) is lowered ELEMENT-WISE in source order with NO
    /// snapshot temp — each element <c>e_i</c> is bound/assigned directly. This matches Zig's
    /// sequential destructuring, where an existing-lvalue write is visible to a LATER element's read
    /// (so <c>a, b = .{ b, a }</c> is NOT a swap: <c>a←b</c>, then <c>b←</c> the new <c>a</c>). New
    /// binders can't alias (Zig forbids shadowing), so the order is also faithful for them, and a typed
    /// binder drives its element's result location (sink).</item>
    /// <item>A non-literal tuple-valued RHS (a fn call, a tuple var) is evaluated ONCE into a fresh
    /// <c>__tupN</c> temp, then each binder reads its positional element (<c>__tupN.ItemK</c>) — single
    /// eval, and a value temp can't alias an lvalue being written.</item>
    /// </list>
    /// Emitted as a brace-less <see cref="Seq"/> so any new binders land in the ENCLOSING scope (a
    /// <see cref="Block"/> would wrongly scope them). The arity must match the binder count.</summary>
    private CStmt LowerDestructure(Zig.StmtDestructure d)
    {
        // Binders in source order: the leading one (Arg0) + the rest (the Arg2 list).
        var binders = new List<Item> { d.Arg0 };
        binders.AddRange(Flatten(d.Arg2));
        var stmts = new List<CStmt>();

        // RhsExpr is transparent, so d.Arg4.Content is the underlying literal/expr directly.
        if (IsPositionalTupleLiteral(d.Arg4, out var elemItems))
        {
            if (elemItems.Count != binders.Count)
            {
                throw new IrUnsupportedException(
                    $"zig destructure binds {binders.Count} name(s) but the literal has {elemItems.Count} element(s)");
            }
            // Element-wise, source order: each binder lowers its own element expr (a typed/lvalue
            // binder passes its type as the element's sink). No temp — preserves Zig's aliasing.
            for (int i = 0; i < binders.Count; i++)
            {
                stmts.Add(LowerDestructBinder(binders[i], elemItems[i], snapshotRead: null));
            }
            return new Seq(stmts);
        }

        // `const scheme, const rest = std.mem.cutScalar(u8, text, ':') orelse return error.InvalidFormat;` (std.Uri.parse,
        // task #192): the RHS is a statement point, so a statement-lowering form (an `orelse` / `catch` with a control-flow
        // fallback) hoists ahead of the temp, as it does for a single binder.
        CExpr rhs;
        using (EnterFreshHoist())
        {
            rhs = LowerExpr(d.Arg4);
            if (_hoist is { Count: > 0 } hoisted) { stmts.AddRange(hoisted); }
        }
        if (rhs.Type.Unqualified is not CType.Tuple tup)
        {
            throw new IrUnsupportedException(
                $"zig destructure `…, … = e` needs a tuple value; got {rhs.Type.Describe()}");
        }
        if (tup.Elements.Count != binders.Count)
        {
            throw new IrUnsupportedException(
                $"zig destructure binds {binders.Count} name(s) but the tuple has {tup.Elements.Count} element(s)");
        }
        // The single-eval temp: `var __tupN = e;`, then each binder reads `__tupN.ItemK`.
        var tmp = _symbols.Declare(new Symbol { Name = "__tup" + _tupleTempCounter++, Kind = SymKind.Var, Type = tup });
        stmts.Add(new DeclStmt(new List<LocalDecl> { new(tmp, rhs) }));
        var tmpRef = new VarRef(tmp) { Type = tup, IsLValue = true };
        for (int i = 0; i < binders.Count; i++)
        {
            var et = tup.Elements[i];
            var read = new TupleIndex(tmpRef, i, et) { Type = et };
            stmts.Add(LowerDestructBinder(binders[i], elemItem: null, snapshotRead: read));
        }
        return new Seq(stmts);
    }
    /// <summary>Emit one destructure binder's statement (Milestone G + S). A fresh <c>const</c>/<c>var</c>
    /// binder (optionally typed <c>: T</c>) declares a local; an existing-lvalue binder assigns through
    /// it; <c>_</c> discards. The source value is either the tuple-literal element <paramref name="elemItem"/>
    /// (lowered at the binder's declared/lvalue type as its sink) or the snapshot read
    /// <paramref name="snapshotRead"/> (coerced to the binder's type). Exactly one of the two is non-null.</summary>
    private CStmt LowerDestructBinder(Item binder, Item? elemItem, CExpr? snapshotRead)
    {
        switch (binder.Content)
        {
            case Zig.DestructBindConst c:      return DeclareDestructLocal(Tok(c.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindVar v:        return DeclareDestructLocal(Tok(v.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindConstTyped c: return DeclareDestructLocal(Tok(c.Arg1), LowerType(c.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindVarTyped v:   return DeclareDestructLocal(Tok(v.Arg1), LowerType(v.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindLValue lv:    return AssignDestructTarget(lv.Arg0, elemItem, snapshotRead);
            default:
                throw new IrUnsupportedException(
                    "zig destructure binder: " + (binder.Content?.GetType().Name ?? "null"));
        }
    }
    /// <summary>Declare a fresh destructure local <c>name</c>. With a <paramref name="declType"/> the
    /// element lowers at that type as its sink (literal RHS) or the snapshot read is coerced to it;
    /// without one the type is inferred from the element/read.</summary>
    private CStmt DeclareDestructLocal(string name, CType? declType, Item? elemItem, CExpr? snapshotRead)
    {
        CExpr value = elemItem is not null
            ? (declType is not null ? LowerExprSink(elemItem, declType) : LowerExpr(elemItem))
            : (declType is not null ? CoerceRead(snapshotRead!, declType) : snapshotRead!);
        var symType = declType ?? value.Type;
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = symType });
        return new DeclStmt(new List<LocalDecl> { new(sym, value) });
    }
    /// <summary>Assign a destructure element to an existing lvalue (or discard it for <c>_</c>). The
    /// lvalue is the sink for a literal element; a snapshot read is coerced to the lvalue type. A
    /// <c>_</c> binder just evaluates the element for its side effects (a value-context discard).</summary>
    private CStmt AssignDestructTarget(Item lvalueItem, Item? elemItem, CExpr? snapshotRead)
    {
        if (lvalueItem.Content is Zig.Ident id && Tok(id.Arg0) == "_")
        {
            // `_` — evaluate the element/read; ExprStmt renders a `_ = …` discard when it isn't a call.
            return new ExprStmt(elemItem is not null ? LowerExpr(elemItem) : snapshotRead!);
        }
        var target = LowerExpr(lvalueItem);
        CExpr value = elemItem is not null
            ? LowerExprSink(elemItem, target.Type)
            : CoerceRead(snapshotRead!, target.Type);
        return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
    }
    /// <summary>Coerce a snapshot read (a <c>__tupN.ItemK</c> CExpr) to a binder's declared/lvalue
    /// type, inserting a <see cref="Cast"/> only when the types differ (a no-op when they match).</summary>
    private static CExpr CoerceRead(CExpr read, CType to)
        => read.Type.Unqualified.Equals(to.Unqualified) ? read : new Cast(to, read) { Type = to };
    /// <summary>True when <paramref name="rhsItem"/> is a positional tuple literal (<c>.{e0, e1, …}</c>),
    /// yielding its element expressions in <paramref name="elemItems"/>. A named <c>.{.f = v}</c> (a
    /// struct literal) or the empty <c>.{}</c> is not a positional tuple literal → false (the snapshot
    /// path then handles / rejects it).</summary>
    private static bool IsPositionalTupleLiteral(Item rhsItem, out IReadOnlyList<Item> elemItems)
    {
        elemItems = [];
        if (rhsItem.Content is not Zig.AnonStructInit a) { return false; }
        var fields = Flatten(a.Arg2);
        if (fields.Count == 0) { return false; }
        var items = new List<Item>(fields.Count);
        foreach (var f in fields)
        {
            if (f.Content is not Zig.FieldInitPositional pos) { return false; }   // a named field → struct literal
            items.Add(pos.Arg0);
        }
        elemItems = items;
        return true;
    }
}
