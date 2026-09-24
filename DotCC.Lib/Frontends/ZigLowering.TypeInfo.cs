#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Comptime reflection — <c>@typeInfo(T)</c> and the <c>switch</c> over it, folded at
/// LOWERING time (road-to-zig-std S5). The <c>std.builtin.Type</c> value is synthesized directly
/// from the resolved <see cref="CType"/>, never by compiling <c>std/lang.zig</c> for its layout:
/// the union is compiler-known, exactly as the real compiler treats it (its own doc comment says
/// so). Nothing here reaches the IR — a folded field is a literal, a folded <c>switch</c> lowers
/// only the taken prong — so the comptime interpreter keeps its value/type firewall (no
/// <c>TypeVal</c>), the W1/W4 lowering-tier tradition the whole wall was built on.
///
/// <para><b>The fidelity rule.</b> A field is answered only when it is EXACTLY recoverable.
/// <c>signedness</c>, <c>child</c>, <c>is_const</c> and <c>len</c> are read straight off the
/// <see cref="CType"/> and are always right. <c>bits</c> is NOT: dotcc widens an arbitrary-width
/// <c>uN</c>/<c>iN</c> to the smallest standard width (<c>u21</c> → a 32-bit <c>uint</c>), so the
/// lowered type no longer knows it was declared with 21. It is therefore answered from the SOURCE
/// SPELLING — the same rule <c>@typeName</c> already follows — which S5b then made travel WITH a
/// type binding (see <see cref="_declaredIntBits"/>), so a <c>comptime T: type</c> param answers it
/// too. Only an <c>anytype</c> param / <c>@TypeOf(expr)</c>, where the type is inferred from a
/// VALUE and no spelling survives, is still refused. A silently-32 answer where zig says 21 is the
/// one outcome worth refusing.</para>
///
/// <para>This file covers the scalar kinds and the tag of every kind, so a <c>switch</c> over
/// <c>@typeInfo(T)</c> dispatches on ANY type. The aggregate kinds' member LISTS
/// (<c>field_names</c> / <c>field_types</c> / <c>field_values</c>) live in
/// <c>ZigLowering.TypeInfo.Lists.cs</c> (S5c).</para>
///
/// <para><b>Version note.</b> dotcc models zig <b>0.17-dev</b>'s <c>std.builtin.Type</c> — the
/// version whose std the campaign compiles from source. 0.16 and earlier expose
/// <c>fields</c>/<c>decls</c> as slices of field STRUCTS instead of the parallel arrays; the CI
/// oracle pins 0.16.0 (the newest DURABLE tag — dev tarballs are GC'd off the download index), so
/// the member-list surface is validated by emit pins plus a by-hand 0.17-dev run rather than a CI
/// differential. See the note in <c>ZigOracleTests</c>.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>A folded <c>@typeInfo(T)</c> value: the active <c>std.builtin.Type</c> union TAG
    /// (<c>int</c>, <c>pointer</c>, <c>@"struct"</c> — the quoted tags arrive here already folded to
    /// their inner text by <see cref="NormalizeIdent"/>), the type it describes, and the declared bit
    /// width when the source spelling was available at the <c>@typeInfo</c> site (see the fidelity
    /// rule on the class). One record serves both the whole union value and the payload of its active
    /// field — <c>@typeInfo(T)</c>, <c>@typeInfo(T).int</c> and the <c>|i|</c> captured by
    /// <c>.int =&gt; |i|</c> are the same folded thing, which is what lets one map bind all three.</summary>
    private sealed record ZigTypeInfo(string Tag, CType Type, int? DeclaredBits);

    /// <summary>Each name bound to a folded <c>@typeInfo</c> value — a <c>const i = @typeInfo(T);</c>
    /// or <c>const i = @typeInfo(T).int;</c> (no runtime decl is emitted: the value is comptime-only),
    /// and the <c>|i|</c> capture of a folded comptime <c>switch</c> prong. Name-keyed and
    /// function-flat like <see cref="_comptimeValues"/> / <see cref="_typeAliases"/> (the W1
    /// leniency); the switch capture shadow-saves through <see cref="_typeInfoShadows"/> so a prong's
    /// binding does not leak past its arm.</summary>
    private readonly Dictionary<string, ZigTypeInfo> _typeInfoBindings = new(System.StringComparer.Ordinal);

    /// <summary>Name → previous <see cref="_typeInfoBindings"/> entry shadowed by a folded comptime
    /// <c>switch</c> prong's <c>|i|</c> capture, restored when the arm is done — the proven W2/W3b
    /// shadow pattern (<see cref="_typeAliasShadows"/>) applied to the reflection bindings.</summary>
    private readonly List<(string Name, ZigTypeInfo? Prev)> _typeInfoShadows = new();

    /// <summary>Each type-binding name → the DECLARED bit width of the zig integer bound to it, when
    /// that width is not recoverable from the lowered type. dotcc widens an arbitrary-width
    /// <c>uN</c>/<c>iN</c> to the smallest standard width (<c>u21</c> → a 32-bit <c>uint</c>), so the
    /// `CType` alone cannot answer <c>@typeInfo(T).int.bits</c>; this rides ALONGSIDE
    /// <see cref="_typeAliases"/> — seeded, shadowed and restored in lockstep with it — so a
    /// <c>comptime T: type</c> param, an alias, and a reified generic all carry the width the source
    /// spelled. Deliberately NOT a property of <see cref="CType"/>: `Prim` has value equality, so a
    /// width-carrying `u21` would stop comparing equal to `u32` and change coercion, peer typing and
    /// every memoization key in the front end — a far larger blast radius than this one question
    /// warrants.</summary>
    private readonly Dictionary<string, int> _declaredIntBits = new(System.StringComparer.Ordinal);

    /// <summary>Set or clear a name's declared integer width — the write half of
    /// <see cref="_declaredIntBits"/>, used both to seed a binding and to restore a shadowed one, so
    /// "no recorded width" and "width W" round-trip through the same call.</summary>
    private void SetDeclaredIntBits(string name, int? bits)
    {
        if (bits is { } b) { _declaredIntBits[name] = b; } else { _declaredIntBits.Remove(name); }
    }

    /// <summary>The declared bit width of a type ARGUMENT's AST — its own spelling
    /// (<see cref="DeclaredBitsFromSpelling"/>) when it is a primitive keyword, else the width already
    /// recorded for the name it references, so a width survives a chain of bindings
    /// (<c>const I = u21; f(I)</c> keys and answers exactly as <c>f(u21)</c> does).</summary>
    private int? DeclaredBitsOfTypeArg(Item typeAst)
    {
        if (DeclaredBitsFromSpelling(typeAst) is { } spelled) { return spelled; }
        var cur = typeAst;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        // A CONSTRUCTED `@Int(.unsigned, 21)` (road-to-zig-std S7) knows its width just as exactly as
        // the spelling `u21` does — it was built from it — so the constructor's own argument is the
        // answer. Read from the site's recorded width rather than re-evaluated (see _reifiedIntBits).
        if (cur.Content is Zig.BuiltinCall { } bc && Tok(bc.Arg0) == "@Int"
            && _reifiedIntBits.TryGetValue(bc.Arg2, out var reified))
        {
            return reified;
        }
        // A call to a type-returning generic (the W4 lift) — the width its body's returned type carried
        // (`fn U(comptime n: u16) type { return @Int(.unsigned, n); }` → `U(21)` is 21 bits).
        if (cur.Content is Zig.CallArgs or Zig.CallNoArgs && _typeCallBits.TryGetValue(cur, out var called))
        {
            return called;
        }
        // A switch over a TYPE (std.math.inf's `const RuntimeType = switch (Type) { else => Type, comptime_float => f128 };`):
        // the selected arm's width, so `@typeInfo(RuntimeType).float.bits` still answers.
        if (cur.Content is Zig.SwitchExpr or Zig.SwitchExprTrailing)
        {
            var (switchSubject, switchProngs) = cur.Content is Zig.SwitchExpr se ? (se.Arg2, se.Arg5) : (((Zig.SwitchExprTrailing)cur.Content).Arg2, ((Zig.SwitchExprTrailing)cur.Content).Arg5);
            return TrySelectTypeProng(switchSubject, switchProngs) is { Expr: { } typeArm, CaptureName: null } ? DeclaredBitsOfTypeArg(typeArm) : null;
        }
        // An error union's width is its payload's (`fn charToDigit(…) (error{InvalidCharacter}!u8)`).
        if (cur.Content is Zig.ErrUnion eu) { return DeclaredBitsOfTypeArg(eu.Arg2); }
        // So is an optional's (`fn cast(comptime T: type, x: anytype) ?T`), so an unwrapped payload keeps it.
        if (cur.Content is Zig.TyOptional opt) { return DeclaredBitsOfTypeArg(opt.Arg1); }
        // `@TypeOf(x)`: the width the VALUE `x` carries (road-to-zig-std G3 — an `anytype` parameter's, a
        // typed local's, a `.len`'s), so `maxInt(@TypeOf(x))` in std.math.cast / sqrt has its answer.
        if (cur.Content is Zig.BuiltinCall { } tof && Tok(tof.Arg0) == "@TypeOf" && Flatten(tof.Arg2) is { Count: 1 } tofArgs)
        {
            if (tofArgs[0].Content is Zig.Ident aid && _anytypeSeedBits.TryGetValue(Tok(aid.Arg0), out var seeded)) { return seeded; }
            return DeclaredBitsOfValue(tofArgs[0]);
        }
        // A module-qualified alias (`std.fmt.ArgSetType`, `pub const ArgSetType = u32;`): the owning
        // module recorded the width its alias spelled (Writer.print asks `@typeInfo(…).int.bits` of it).
        if (cur.Content is Zig.Field qf && ResolveModulePath(qf.Arg0)?.Lowering is { } owner)
        {
            return owner.ExportedDeclaredBits(Tok(qf.Arg2));
        }
        // A container's type const named qualified (`Metadata.FingerPrint`): the width it spelled.
        if (cur.Content is Zig.Field cf && MemberBaseType(cf.Arg0)?.Unqualified is CType.Named { Name: var baseContainer })
        {
            TryContainerTypeConst(baseContainer, Tok(cf.Arg2));   // evaluated (and its width recorded) on first use
            return _typeConstBits.TryGetValue((baseContainer, Tok(cf.Arg2)), out var qualifiedBits) ? qualifiedBits : null;
        }
        if (cur.Content is Zig.Ident id)
        {
            if (_declaredIntBits.TryGetValue(Tok(id.Arg0), out var bound)) { return bound; }
            // A container TYPE const (`pub const Hash = u64;` in hash_map) carries the width it spelled.
            return ContainerTypeConstBits(Tok(id.Arg0));
        }
        return null;
    }

    /// <summary>The declared integer width each VALUE symbol's type carries where the source spelled it (a
    /// typed local, a parameter, a capture over a spelled element type), the value-level counterpart of
    /// <see cref="_declaredIntBits"/>, so <c>@typeInfo(@TypeOf(x)).int.bits</c> is answered exactly rather
    /// than refused. Reference-keyed by symbol; a symbol with no entry has no known spelling.</summary>
    private readonly Dictionary<Symbol, int> _valueBits = new();

    /// <summary>The declared width of the ELEMENT type of each slice / array / many-pointer VALUE symbol
    /// (<c>buf: []const Character</c>), so an index, a slice of it, or a <c>for</c> capture over it carries one.</summary>
    private readonly Dictionary<Symbol, int> _valueElemBits = new();

    /// <summary>Each generic `anytype` parameter's declared width while its instance's SIGNATURE lowers
    /// (the <see cref="_anytypeSeeds"/> counterpart; shadow-restored with it).</summary>
    private readonly Dictionary<string, int> _anytypeSeedBits = new(System.StringComparer.Ordinal);

    /// <summary>Each function's parameter list with raw type ASTs, so its body can give every parameter
    /// symbol the width its type spelled (<see cref="RecordParamBits"/>).</summary>
    private readonly Dictionary<Symbol, IReadOnlyList<ParamInfo>> _fnParamInfos = new();

    /// <summary>Each generic instance's `anytype` parameter widths, read from the call site's arguments.</summary>
    private readonly Dictionary<Symbol, Dictionary<string, int>> _instanceAnytypeBits = new();

    /// <summary>Each instance's tuple-literal `anytype` arguments' per-element declared widths (see
    /// <see cref="TupleLiteralElemBits"/>), by parameter name, recorded on the parameter as <see cref="_valueTupleElemBits"/>.</summary>
    private readonly Dictionary<Symbol, Dictionary<string, int?[]>> _instanceAnytypeTupleBits = new();

    /// <summary>A tuple-valued symbol's per-element declared widths, read through <c>t[i]</c> / <c>@field(t, "i")</c>.</summary>
    private readonly Dictionary<Symbol, int?[]> _valueTupleElemBits = new();

    /// <summary>The declared width of each element of a positional tuple literal (<c>.{ 42, @as(u21, 5) }</c>): an
    /// element's own width, or for an untyped integer literal the width dotcc infers it at (<c>int</c>, 32 bits),
    /// which is exact by construction. Null when the argument is not such a literal.</summary>
    private int?[]? TupleLiteralElemBits(Item arg)
    {
        // A tuple VALUE passed on (`w.print(fmt, args)` inside std.fmt.bufPrint) forwards what its symbol recorded.
        if (arg.Content is Zig.Ident { Arg0: var tupleTok } && _symbols.Resolve(Tok(tupleTok)) is { } tupleSym)
        {
            return _valueTupleElemBits.GetValueOrDefault(tupleSym);
        }
        if (arg.Content is not Zig.AnonStructInit init) { return null; }
        var elems = Flatten(init.Arg2);
        if (elems.Count == 0 || elems.Any(e => e.Content is not Zig.FieldInitPositional)) { return null; }
        var bits = new int?[elems.Count];
        for (var k = 0; k < elems.Count; k++)
        {
            var value = ((Zig.FieldInitPositional)elems[k].Content).Arg0;
            if (value.Content is Zig.IntLit)
            {
                CExpr lit;
                using (EnterThrowawayHoist()) { lit = LowerExpr(value); }
                bits[k] = lit.Type?.Unqualified is CType.Prim { Integer: true, Bytes: var bytes } ? bytes * 8 : null;
            }
            else
            {
                bits[k] = DeclaredBitsOfArgument(value);
            }
        }
        return bits;
    }

    /// <summary>The declared width a VALUE expression's type carries, or null when no spelling is known:
    /// a symbol's recorded width, a slice / array <c>.len</c> (<c>usize</c>), an element of a value whose
    /// element width is known, <c>@as(T, e)</c> / <c>@intCast</c>-free spellings.</summary>
    private int? DeclaredBitsOfValue(Item e) => e.Content switch
    {
        Zig.Grouped g => DeclaredBitsOfValue(g.Arg1),
        Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } s && _valueBits.TryGetValue(s, out var b) ? b : null,
        Zig.Field f when Tok(f.Arg2) == "len" => 64,
        Zig.Index ix => DeclaredElemBitsOfValue(ix.Arg0),
        Zig.BuiltinCall bc when Tok(bc.Arg0) == "@as" && Flatten(bc.Arg2) is { Count: 2 } asArgs => DeclaredBitsOfTypeArg(asArgs[0]),
        // Negation / complement / `try` keep their operand's type (zig has no C integer promotion).
        Zig.PreNeg n => DeclaredBitsOfValue(n.Arg1),
        Zig.PreBitNot n => DeclaredBitsOfValue(n.Arg1),
        Zig.PreTry t => DeclaredBitsOfValue(t.Arg1),
        _ => null,
    };

    /// <summary>The declared width a LOWERED expression carries, for what the AST alone cannot resolve: a
    /// call's callee (its declared return width, <see cref="_fnReturnBits"/>: <c>iterator.length()</c>,
    /// <c>try charToDigit(…)</c>), a variable, a slice length, through <c>try</c> and parentheses.</summary>
    private int? DeclaredBitsOfLowered(CExpr e) => e switch
    {
        Paren p => DeclaredBitsOfLowered(p.Inner),
        ZigTry t => DeclaredBitsOfLowered(t.Inner),
        VarRef v => _valueBits.TryGetValue(v.Sym, out var b) ? b : null,
        Call { CalleeSym: { } s } => _fnReturnBits.TryGetValue(s, out var rb) ? rb : null,
        ComptimeFold f => DeclaredBitsOfLowered(f.Inner),
        TupleIndex { Tuple: VarRef tv, Index: var ti } when _valueTupleElemBits.TryGetValue(tv.Sym, out var tbits) && ti < tbits.Length
            => tbits[ti],
        Member { Field: "Len" } => 64,
        Unary { Op: UnOp.Neg or UnOp.BitNot } u => DeclaredBitsOfLowered(u.Operand),
        _ => null,
    };

    /// <summary>The width a value argument's type carries (<see cref="DeclaredBitsOfValue"/>, then, if the
    /// AST alone does not say, the LOWERED argument's; lowered into a throwaway hoist, since this is a
    /// question about its type and the call lowers the argument itself).</summary>
    private int? DeclaredBitsOfArgument(Item arg)
    {
        if (DeclaredBitsOfValue(arg) is { } bits) { return bits; }
        using var hoist = EnterThrowawayHoist();
        return DeclaredBitsOfLowered(LowerExpr(arg));
    }

    /// <summary>Each function's declared RETURN width, where its return type spelled one (read per instance
    /// with its seeds live), so a call's result carries it (<see cref="DeclaredBitsOfLowered"/>).</summary>
    private Dictionary<Symbol, int> _fnReturnBits => _shared.FnReturnBits;

    /// <summary>The declared width of a slice / array VALUE's ELEMENT type, or null (see
    /// <see cref="_valueElemBits"/>): a symbol's record, carried through <c>&amp;x</c>, slicing and a value
    /// <c>if</c> whose arms agree.</summary>
    private int? DeclaredElemBitsOfValue(Item e) => e.Content switch
    {
        Zig.Grouped g => DeclaredElemBitsOfValue(g.Arg1),
        Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } s && _valueElemBits.TryGetValue(s, out var b) ? b : null,
        Zig.PreAddrOf a => DeclaredElemBitsOfValue(a.Arg1),
        Zig.SliceRange sr => DeclaredElemBitsOfValue(sr.Arg0),
        Zig.SliceOpen so => DeclaredElemBitsOfValue(so.Arg0),
        Zig.SliceRangeSentinel srs => DeclaredElemBitsOfValue(srs.Arg0),
        Zig.SliceOpenSentinel sos => DeclaredElemBitsOfValue(sos.Arg0),
        Zig.IfExpr ie when DeclaredElemBitsOfValue(ie.Arg4) is { } t && DeclaredElemBitsOfValue(ie.Arg6) == t => t,
        _ => null,
    };

    /// <summary>The declared width of the ELEMENT of a slice / array / many-pointer TYPE spelling, or null.</summary>
    private int? ElemBitsOfTypeAst(Item typeAst) => typeAst.Content switch
    {
        Zig.TySlice s => DeclaredBitsOfTypeArg(s.Arg2),
        Zig.TySliceConst s => DeclaredBitsOfTypeArg(s.Arg3),
        Zig.TyArray a => DeclaredBitsOfTypeArg(a.Arg3),
        Zig.TyManyPtr p => DeclaredBitsOfTypeArg(p.Arg1),
        Zig.TyManyPtrConst p => DeclaredBitsOfTypeArg(p.Arg2),
        _ => null,
    };

    /// <summary>Record a value symbol's declared width and element width (see <see cref="_valueBits"/>).</summary>
    private void RecordValueBits(Symbol sym, int? bits, int? elemBits)
    {
        if (bits is { } b) { _valueBits[sym] = b; }
        if (elemBits is { } eb) { _valueElemBits[sym] = eb; }
    }

    /// <summary>Give each parameter symbol of <paramref name="fn"/>'s body the width its type spelled (read
    /// with the body's comptime seeds live, so <c>x: T</c> is <c>T</c>'s width), or, for an `anytype`
    /// parameter of an instance, the width its call-site argument carried.</summary>
    private void RecordParamBits(Symbol fn, IReadOnlyList<Symbol> paramSyms)
    {
        if (!_fnParamInfos.TryGetValue(fn, out var infos)) { return; }
        _instanceAnytypeBits.TryGetValue(fn, out var anyBits);
        foreach (var ps in paramSyms)
        {
            var at = -1;
            for (var i = 0; i < infos.Count; i++) { if (infos[i].Name == ps.Name) { at = i; break; } }
            if (at < 0) { continue; }
            var info = infos[at];
            if (info.Kind == ParamKind.AnyType)
            {
                if (anyBits is not null && anyBits.TryGetValue(ps.Name, out var ab)) { _valueBits[ps] = ab; }
                if (_instanceAnytypeTupleBits.TryGetValue(fn, out var tupleBits) && tupleBits.TryGetValue(ps.Name, out var tb))
                {
                    _valueTupleElemBits[ps] = tb;
                }
                continue;
            }
            if (info.Kind != ParamKind.Runtime) { continue; }
            RecordValueBits(ps, DeclaredBitsOfTypeArg(info.TypeAst), ElemBitsOfTypeAst(info.TypeAst));
        }
    }

    /// <summary>The declared integer width of this module's top-level type alias
    /// <paramref name="name"/> (see <see cref="_declaredIntBits"/>), for an importer naming it through a
    /// module path; null when it recorded none.</summary>
    internal int? ExportedDeclaredBits(string name) => _declaredIntBits.TryGetValue(name, out var b) ? b : null;

    /// <summary>The <c>std.builtin.Type</c> union tag for a resolved type — what a
    /// <c>switch (@typeInfo(T))</c> prong matches. Tags are spelled as
    /// <see cref="NormalizeIdent"/> leaves them, so the quoted source forms <c>.@"struct"</c> /
    /// <c>.@"enum"</c> / <c>.@"union"</c> / <c>.@"fn"</c> compare as their inner text. A slice is
    /// zig's <c>pointer</c> kind (with <c>size</c> <c>.slice</c>), matching how the real
    /// <c>@typeInfo</c> reports one.</summary>
    private string TypeInfoTag(CType type) => type.Unqualified switch
    {
        // `_Bool` is an INTEGER prim in the C type model (CType.Bool == Prim("_Bool", 1, true, false)),
        // so it must be recognized before the integer arm or every `bool` would report `int`.
        CType.Prim { Name: "_Bool" } => "bool",
        CType.Prim { IsComptimeInt: true } => "comptime_int",
        CType.Prim { Integer: true } => "int",
        CType.Prim => "float",
        CType.VoidType => "void",
        CType.Pointer or CType.Slice => "pointer",
        CType.Optional => "optional",
        CType.Array => "array",
        CType.Vector => "vector",
        CType.Enum => "enum",
        CType.Func => "fn",
        CType.ErrorUnion => "error_union",
        CType.ErrorSetType => "error_set",
        // A named aggregate is a struct unless it was registered as a `union(enum)`; the tuple /
        // curated-runtime types (ArrayList, Allocator) are structs in zig too.
        CType.Named n when _unions.ContainsKey(n.Name) => "union",
        CType.Named or CType.Tuple or CType.ZigList or CType.Allocator => "struct",
        _ => throw new IrUnsupportedException(
            $"zig `@typeInfo`: no `std.builtin.Type` tag is modeled for {type.Describe()} (road-to-zig-std S5)"),
    };

    /// <summary>The DECLARED bit width read off a type's SOURCE spelling, or null when the operand is
    /// not a bare primitive keyword (an alias, a comptime type param, <c>@TypeOf(x)</c>, a composite).
    /// This is the only width that is safe to report as <c>@typeInfo(T).int.bits</c> — see the
    /// fidelity rule on the class. The <c>uN</c>/<c>iN</c> rule matches
    /// <see cref="TryArbitraryWidthInt"/>'s (that is where the widening this guards against happens);
    /// the fixed names are dotcc's LP64 target widths, which agree with zig's for this target.</summary>
    private static int? DeclaredBitsFromSpelling(Item typeAst)
    {
        if (typeAst.Content is Zig.Grouped g) { return DeclaredBitsFromSpelling(g.Arg1); }
        if (typeAst.Content is not Zig.Ident id) { return null; }
        var name = Tok(id.Arg0);
        switch (name)
        {
            case "usize" or "isize" or "c_long" or "c_ulong" or "c_longlong" or "c_ulonglong" or "f64": return 64;
            case "c_int" or "c_uint" or "f32": return 32;
            case "c_short" or "c_ushort": return 16;
            case "c_char": return 8;
        }
        // `uN` / `iN` — the declared width verbatim, which is exactly what the lowered type loses.
        if (name.Length < 2 || (name[0] != 'u' && name[0] != 'i')) { return null; }
        var digits = name.AsSpan(1);
        if (digits.Length > 1 && digits[0] == '0') { return null; }
        foreach (var ch in digits) { if (ch is < '0' or > '9') { return null; } }
        return int.TryParse(digits, out var bits) && bits >= 1 && bits <= 128 ? bits : null;
    }

    /// <summary>Evaluate an expression to a folded <c>@typeInfo</c> value. Four forms, all comptime:
    /// the <c>@typeInfo(T)</c> builtin itself, a parenthesized one, a name bound to one
    /// (<see cref="_typeInfoBindings"/> — a <c>const</c> or a folded prong capture), and a payload
    /// access <c>&lt;info&gt;.int</c> that names the ACTIVE tag. Naming an inactive tag
    /// (<c>@typeInfo(u8).float</c>) is a loud error, exactly as zig rejects reading an inactive union
    /// field. Non-throwing for a non-<c>@typeInfo</c> expression (returns false) so every caller can
    /// probe speculatively before its own lowering.</summary>
    private bool TryEvalTypeInfo(Item expr, out ZigTypeInfo info)
    {
        info = null!;
        switch (expr.Content)
        {
            case Zig.Grouped g:
                return TryEvalTypeInfo(g.Arg1, out info);

            // A name bound to a folded value. Guarded on the name NOT resolving to a real symbol: the
            // bindings are function-flat (the W1 leniency), so without this a `const info = @typeInfo(T);`
            // in one function would shadow an ordinary local named `info` in another — and a
            // `switch (info)` there would fold instead of lowering. A dropped comptime binding declares
            // no symbol, so the guard never rejects a genuine one.
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null
                                   && _typeInfoBindings.TryGetValue(Tok(id.Arg0), out var bound):
                info = bound;
                return true;

            case Zig.BuiltinCall b when Tok(b.Arg0) == "@typeInfo":
            {
                var args = Flatten(b.Arg2);
                if (args.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@typeInfo` expects (type); got {args.Count} argument(s)");
                }
                var t = LowerType(args[0]);
                info = new ZigTypeInfo(TypeInfoTag(t), t, DeclaredBitsOfTypeArg(args[0]));
                return true;
            }

            // `<info>.int` — the payload of the ACTIVE union field. Yields the same folded record (the
            // payload and the union value describe one type), after checking the tag really is active.
            case Zig.Field f when TryEvalTypeInfo(f.Arg0, out var baseInfo):
            {
                var tag = Tok(f.Arg2);
                if (tag == baseInfo.Tag) { info = baseInfo; return true; }
                // Not a tag at all → let the caller try a payload FIELD (`.bits`, `.child`, …).
                if (!IsTypeInfoTag(tag)) { return false; }
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({baseInfo.Type.Describe()}).{tag}`: the active `std.builtin.Type` field is "
                    + $"`{baseInfo.Tag}`, not `{tag}`");
            }
        }
        return false;
    }

    /// <summary>True for a name that is a <c>std.builtin.Type</c> union TAG rather than a payload
    /// field — the set <see cref="TypeInfoTag"/> can produce, so <see cref="TryEvalTypeInfo"/> can
    /// tell "you named an inactive tag" (an error) from "you named a payload field" (fall through to
    /// the field fold).</summary>
    private static bool IsTypeInfoTag(string name) => name is
        "int" or "float" or "bool" or "void" or "pointer" or "optional" or "array"
        or "enum" or "struct" or "union" or "fn" or "error_union" or "error_set";

    /// <summary>Fold a <c>@typeInfo</c> payload field that yields a VALUE — <c>bits</c>,
    /// <c>is_const</c>, <c>len</c>. Returns false when the expression is not such an access, so the
    /// ordinary member lowering runs. A field that exists in zig but is not exactly recoverable here
    /// (<c>size</c>) or needs comptime aggregates (<c>fields</c>, <c>decls</c>) is a loud cut naming
    /// the brick that lands it. <c>signedness</c> is not a value: it is an enum literal, folded by
    /// <see cref="TryEvalComptimeTag"/> for the <c>==</c> / <c>switch</c> positions it is actually
    /// used in.</summary>
    private bool TryFoldTypeInfoValue(Item expr, out CExpr value)
    {
        value = null!;
        if (expr.Content is not Zig.Field f || !TryEvalTypeInfo(f.Arg0, out var info)) { return false; }
        var field = Tok(f.Arg2);
        // `@typeInfo(P).pointer` / `info.pointer` is the union's PAYLOAD step, not a value field
        // (std.mem.AsBytesReturnType binds `const pointer = @typeInfo(P).pointer;`): not folded here.
        if (IsTypeInfoTag(field)) { return false; }
        switch (info.Tag, field)
        {
            case ("int", "bits") or ("float", "bits"):
                if (info.DeclaredBits is not { } bits)
                {
                    throw new IrUnsupportedException(
                        $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.bits`: the declared width is not known here"
                        + (_currentFnName.Length > 0 ? $" (in '{_currentFnName}')" : "") + ". "
                        + "It rides a type SPELLING — `@typeInfo(u21)` directly, or a `comptime T: type` / alias bound to "
                        + "one — but an `anytype` param or `@TypeOf(expr)` yields only the lowered type, and dotcc widens "
                        + "`uN`/`iN` to the smallest standard width (`u21` → a 32-bit `uint`), so reporting that width "
                        + "would disagree with zig. Spell the type, or pass it as a `comptime T: type`");
                }
                value = new LitInt(bits.ToString(System.Globalization.CultureInfo.InvariantCulture), bits) { Type = CType.Int };
                return true;

            case ("pointer", "is_const"):
                var pointee = info.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Slice s => s.Element,
                    _ => CType.Void,
                };
                value = new LitBool(pointee.IsConst) { Type = CType.Bool };
                return true;

            case ("vector", "len"):
                var lanes = ((CType.Vector)info.Type.Unqualified).Count;
                value = new LitInt(lanes.ToString(System.Globalization.CultureInfo.InvariantCulture), lanes) { Type = CType.Int };
                return true;

            case ("array", "len"):
                if (info.Type.Unqualified is not CType.Array { Count: { } count })
                {
                    throw new IrUnsupportedException(
                        $"zig `@typeInfo({info.Type.Describe()}).array.len`: the array has no comptime-known length");
                }
                // Typed `int`, not the `usize` zig gives `.len`, for the same reason BindFoldedCapture
                // narrows a folded capture: the literal then renders bare (`4`, not `4UL`) and C#'s
                // implicit CONSTANT conversion lets it land in any integer sink — a `4UL` would not
                // assign to a `u8`/`u16` annotation (CS0266). The value always fits.
                value = new LitInt(count.ToString(System.Globalization.CultureInfo.InvariantCulture), count) { Type = CType.Int };
                return true;

            // The member LISTS (road-to-zig-std S5c) fold in their own path — reaching here means one
            // was used as a bare VALUE, which it cannot be: a comptime list has no runtime form.
            case (_, "field_names") or (_, "field_types") or (_, "field_values"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}` is a comptime member LIST with no runtime "
                    + "representation — read its `.len`, index it at a comptime index, walk it with `inline for` "
                    + "(a plain `for` cannot: there is nothing to iterate at runtime), or bind it to a `const`");

            // `fields` / `decls` are the OLDER `std.builtin.Type` shape (a slice of field STRUCTS). The
            // pinned zig replaced them with the parallel `field_names` / `field_types` / `field_values`
            // arrays, so pointing at those is more useful than implementing a shape zig no longer has.
            case (_, "fields") or (_, "decls"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}`: dotcc models zig 0.17-dev's "
                    + "`std.builtin.Type`, which replaced the `fields`/`decls` slice-of-structs with the parallel "
                    + "arrays `field_names` / `field_types` / `field_values` — use those. (Zig 0.16 and earlier "
                    + "spell it the old way; dotcc tracks 0.17-dev, the version whose std it compiles.)");

            case ("pointer", "size"):
                throw new IrUnsupportedException(
                    "zig `@typeInfo(T).pointer.size`: dotcc lowers `*T`, `[*]T` and `[*c]T` to one C pointer, so the "
                    + "pointer SIZE class is not recoverable from the lowered type (road-to-zig-std S5)");

            case (_, "signedness"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.signedness`: only an `int` carries a signedness");

            case ("int", "child") or ("float", "child") or ("bool", "child") or ("void", "child"):
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child`: a scalar kind has no child type");
        }
        // `.child` on a pointer/optional/array is a TYPE, not a value — handled by
        // TryFoldTypeInfoType from the type positions; reaching here means it was used as a value.
        if (field == "child")
        {
            throw new IrUnsupportedException(
                $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child` is a TYPE — use it in a type position "
                + "(`const C = @typeInfo(T).pointer.child;`), not as a value");
        }
        throw new IrUnsupportedException(
            $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.{field}`: no such `std.builtin.Type` field is modeled "
            + "(road-to-zig-std S5)");
    }

    /// <summary>Fold a <c>@typeInfo</c> payload field that yields a TYPE — <c>child</c> on a pointer,
    /// slice, optional or array. Consulted from the TYPE positions (<see cref="LowerType"/> and the
    /// type-alias RHS), so <c>const C = @typeInfo(T).pointer.child;</c> and a <c>child</c>-typed
    /// annotation resolve. Returns false for any other expression.</summary>
    private bool TryFoldTypeInfoType(Item expr, out CType type)
    {
        type = CType.Void;
        if (expr.Content is Zig.Grouped g) { return TryFoldTypeInfoType(g.Arg1, out type); }
        if (expr.Content is not Zig.Field f || Tok(f.Arg2) != "child" || !TryEvalTypeInfo(f.Arg0, out var info))
        {
            return false;
        }
        var child = info.Type.Unqualified switch
        {
            CType.Pointer p => p.Pointee,
            CType.Slice s => s.Element,
            CType.Optional o => o.Inner,
            CType.Array a => a.Element,
            CType.Vector v => v.Element,
            _ => null,
        };
        if (child is null)
        {
            throw new IrUnsupportedException(
                $"zig `@typeInfo({info.Type.Describe()}).{info.Tag}.child`: only a pointer / slice / optional / array "
                + "kind has a child type");
        }
        type = child;
        return true;
    }

    /// <summary>Evaluate an expression to a comptime enum-literal TAG — the two positions a tag is
    /// legal in: the subject of a <c>switch</c> and an <c>==</c>/<c>!=</c> against a <c>.name</c>
    /// literal. Two producers: a folded <c>@typeInfo</c> value (its union tag, so
    /// <c>switch (@typeInfo(T))</c> dispatches) and an <c>int</c> payload's <c>signedness</c>
    /// (<c>signed</c>/<c>unsigned</c>, which IS exactly recoverable — dotcc's width widening never
    /// changes a type's signedness). Yields the payload alongside, so a prong capture can bind it.</summary>
    private bool TryEvalComptimeTag(Item expr, out string tag, out ZigTypeInfo? payload)
    {
        tag = "";
        payload = null;
        if (expr.Content is Zig.Grouped g) { return TryEvalComptimeTag(g.Arg1, out tag, out payload); }
        // `<info>.signedness` — checked BEFORE the whole-value case so the field wins over the record.
        if (expr.Content is Zig.Field f && Tok(f.Arg2) == "signedness" && TryEvalTypeInfo(f.Arg0, out var sInfo))
        {
            if (sInfo.Tag != "int")
            {
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({sInfo.Type.Describe()}).{sInfo.Tag}.signedness`: only an `int` carries a signedness");
            }
            tag = sInfo.Type.Unqualified is CType.Prim { Signed: true } ? "signed" : "unsigned";
            return true;
        }
        // `<info>.layout` of a struct or union (std.meta.eql's `if (info.layout == .@"packed") return a == b;`):
        // `.auto` / `.@"extern"` / `.@"packed"`, off the layout the aggregate was registered with.
        if (expr.Content is Zig.Field lf && Tok(lf.Arg2) == "layout" && TryEvalTypeInfo(lf.Arg0, out var lInfo))
        {
            if (lInfo.Tag is not ("struct" or "union"))
            {
                throw new IrUnsupportedException(
                    $"zig `@typeInfo({lInfo.Type.Describe()}).{lInfo.Tag}.layout`: only a struct or union carries a layout");
            }
            var layout = lInfo.Type.Unqualified is CType.Named ln
                ? _ir.Types.Find(d => d.Name == ln.Name)?.Layout ?? AggregateLayout.Default
                : AggregateLayout.Default;
            tag = layout switch
            {
                AggregateLayout.Packed => "packed",
                AggregateLayout.Sequential => "extern",
                _ => "auto",
            };
            return true;
        }
        // `<info>.size` on a SLICE — `.slice`, the one pointer size class dotcc's lowering keeps (a slice is
        // its own `CType.Slice`). `*T` / `[*]T` / `[*c]T` share one C pointer, so for those the size stays the
        // loud cut TryFoldTypeInfoValue raises (std.meta.Elem switches on it).
        if (expr.Content is Zig.Field sz && Tok(sz.Arg2) == "size" && TryEvalTypeInfo(sz.Arg0, out var zInfo)
            && zInfo.Tag == "pointer" && zInfo.Type.Unqualified is CType.Slice)
        {
            tag = "slice";
            return true;
        }
        if (TryEvalTypeInfo(expr, out var info))
        {
            tag = info.Tag;
            payload = info;
            return true;
        }
        // A comptime AGGREGATE field holding an enum literal (road-to-zig-std S3a) — `builtin.cpu.arch`,
        // `builtin.os.tag`, `builtin.mode`. This is what makes a platform conditional FOLD instead of
        // lowering both arms, which matters because the untaken arm is the inline asm / syscall /
        // per-arch code the branch exists to avoid. Checked after the `@typeInfo` producers, which are
        // the more specific shapes.
        if (TryReadComptimeAggregateField(expr) is { Tag: { } aggTag })
        {
            tag = aggTag;
            return true;
        }
        // A comptime ENUM variable (`const signedness: Signedness = if (from < 0) .signed else .unsigned;` in
        // std.math.IntFittingRange's type body): its value mapped back to the member's name.
        if (expr.Content is Zig.Ident { Arg0: var enumTok } && _symbols.Resolve(Tok(enumTok)) is { } enumSym
            && _comptimeVars.TryGetValue(enumSym, out var enumVar) && enumVar.Type.Unqualified is CType.Enum varEnum
            && _enumMembers.TryGetValue(varEnum.Name, out var varMembers)
            && varMembers.FirstOrDefault(m => m.Value.ConstValue == enumVar.Value).Key is { } memberName)
        {
            tag = memberName;
            return true;
        }
        // A comptime tagged-UNION value (`comptime switch (placeholder.arg)` in std.Io.Writer.print, the
        // Placeholder a comptime call returned): its active variant is the tag, its payload the capture.
        if (TryEvalComptimeUnion(expr, out var unionTag, out var unionPayload))
        {
            tag = unionTag;
            _comptimeUnionPayload = unionPayload;
            return true;
        }
        return false;
    }

    /// <summary>The payload of the comptime union <see cref="TryEvalComptimeTag"/> last matched, spliced to a
    /// literal, for the selected prong's <c>|v|</c> capture (<see cref="EnterComptimeProng"/>). Null for a void
    /// variant.</summary>
    private CExpr? _comptimeUnionPayload;

    /// <summary>What each <see cref="EnterComptimeProng"/> bound for a comptime union's capture in
    /// <see cref="_comptimeValues"/> (an empty name when nothing), restored by <see cref="ExitComptimeProng"/>.</summary>
    private readonly List<(string Name, CExpr? Prev)> _unionCaptureShadows = new();

    /// <summary>Read a comptime tagged-union value: <paramref name="expr"/> is rooted at a comptime aggregate
    /// (<c>const placeholder = comptime Placeholder.parse(…)</c>, held by the interpreter), and evaluates to a
    /// union. Yields the active variant's name and its payload spliced to a literal (null for a void variant).
    /// Anything else is false, so the switch lowers at runtime as before.</summary>
    private bool TryEvalComptimeUnion(Item expr, out string variant, out CExpr? payload)
    {
        variant = "";
        payload = null;
        var root = expr;
        while (root.Content is Zig.Field or Zig.Grouped)
        {
            root = root.Content is Zig.Field rf ? rf.Arg0 : ((Zig.Grouped)root.Content).Arg1;
        }
        if (root.Content is not Zig.Ident rid || _symbols.Resolve(Tok(rid.Arg0)) is not { } rootSym
            || !_ir.ComptimeGlobals.ContainsKey(rootSym))
        {
            return false;
        }
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(expr); }
        if (_ir.EvalComptimeValue(lowered) is not IrModule.CtStruct value
            || value.Type.Unqualified is not CType.Named { Name: var unionName }
            || !_unions.TryGetValue(unionName, out var info)
            || !value.Fields.TryGetValue(info.TagFieldName, out var tagValue) || tagValue is not IrModule.CtInt tagInt
            || info.TagType.Unqualified is not CType.Enum tagEnum
            || !_enumMembers.TryGetValue(tagEnum.Name, out var members))
        {
            return false;
        }
        var match = members.FirstOrDefault(m => m.Value.ConstValue == (long)tagInt.Value);
        if (match.Key is not { } name) { return false; }
        variant = name;
        if (info.Variants.GetValueOrDefault(name) is not null
            && value.Fields.GetValueOrDefault(info.PayloadFieldName) is IrModule.CtStruct payloadStruct
            && payloadStruct.Fields.TryGetValue(name, out var payloadValue))
        {
            payload = _ir.SpliceComptimeValue(payloadValue);
        }
        return true;
    }

    /// <summary>Fold <c>&lt;comptime tag&gt; == .name</c> / <c>!=</c> to a boolean literal — the
    /// non-<c>switch</c> half of how a tag is consumed (<c>info.signedness == .unsigned</c>). Either
    /// operand may be the tag. Returns false when this is not a tag comparison, so the ordinary
    /// binary lowering runs.</summary>
    private bool TryFoldComptimeTagCompare(Item leftItem, Item rightItem, bool negate, out CExpr value)
    {
        value = null!;
        string? tag = null;
        string? lit = null;
        if (TryEvalComptimeTag(leftItem, out var lt, out _) && rightItem.Content is Zig.EnumLit rel)
        {
            tag = lt;
            lit = Tok(rel.Arg1);
        }
        else if (TryEvalComptimeTag(rightItem, out var rt, out _) && leftItem.Content is Zig.EnumLit lel)
        {
            tag = rt;
            lit = Tok(lel.Arg1);
        }
        if (tag is null || lit is null) { return false; }
        var equal = tag == lit;
        value = new LitBool(negate ? !equal : equal) { Type = CType.Bool };
        return true;
    }

    /// <summary>One decomposed <c>switch</c> prong: its case-value list, an optional payload capture,
    /// and exactly one body form (a braced block, a bare value expression, or a <c>return</c>). The
    /// shape the comptime fold needs in all three switch positions (statement, expression, and the
    /// value-temp filler) without each re-deriving it from the eight capture productions.</summary>
    private sealed record ZigProng(Item CaseVals, string? CaptureName, Item? Block, Item? Expr, Item? Return, bool ReturnsVoid,
        Item? Jump = null, Zig.ProngAssign? Assign = null, string? Cut = null, Zig.ProngIfSwitch? IfSwitch = null,
        Zig.ProngIfCaptureReturn? IfCaptureReturn = null, Item? Loop = null);

    /// <summary>Decompose a prong into <see cref="ZigProng"/>. A by-reference capture
    /// (<c>|*x|</c>) is rejected: a comptime <c>@typeInfo</c> value has no storage to point at.</summary>
    private static ZigProng DecomposeProng(Item prong) => prong.Content switch
    {
        Zig.Prong p                  => new ZigProng(p.Arg0, null,         p.Arg2, null,   null,   false),
        Zig.ProngExpr p              => new ZigProng(p.Arg0, null,         null,   p.Arg2, null,   false),
        Zig.ProngReturn p            => new ZigProng(p.Arg0, null,         null,   null,   p.Arg3, false),
        Zig.ProngReturnVoid p        => new ZigProng(p.Arg0, null,         null,   null,   null,   true),
        Zig.ProngCapture p           => new ZigProng(p.Arg0, Tok(p.Arg3),  p.Arg5, null,   null,   false),
        Zig.ProngCaptureExpr p       => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   p.Arg5, null,   false),
        Zig.ProngCaptureReturn p     => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   p.Arg6, false),
        Zig.ProngCaptureReturnVoid p => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   null,   true),
        Zig.ProngJump p              => new ZigProng(p.Arg0, null,         null,   null,   null,   false, p.Arg2),
        Zig.ProngAssign p            => new ZigProng(p.Arg0, null,         null,   null,   null,   false, Assign: p),
        // A `comptime { … }` body is walked as a block: a comptime-selected prong runs at lowering time anyway.
        Zig.ProngComptimeBlock p     => new ZigProng(p.Arg0, null,         p.Arg3, null,   null,   false),
        Zig.ProngCaptureJump p       => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   null,   false, p.Arg5),
        // `inline` only matters to a RUNTIME switch (one instantiated prong per case); a comptime-selected
        // switch takes one prong anyway.
        Zig.InlineProng p            => DecomposeProng(p.Arg1),
        // `|val, tag|`: the payload capture folds as `|val|`; a use of the tag name stays unresolved (loud).
        Zig.ProngCaptureTag p        => new ZigProng(p.Arg0, Tok(p.Arg3),  p.Arg7, null,   null,   false),
        Zig.ProngCaptureTagExpr p    => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   p.Arg7, null,   false),
        Zig.ProngCaptureTagReturn p  => new ZigProng(p.Arg0, Tok(p.Arg3),  null,   null,   p.Arg8, false),
        // A no-`else` capture `if` body: its case values are readable, so an unselected prong is fine; a
        // comptime-SELECTED one is a loud cut (see SelectComptimeProng).
        Zig.ProngIfSwitch p          => new ZigProng(p.Arg0, null,         null,   null,   null,   false, IfSwitch: p),
        Zig.ProngIfCaptureReturn p   => new ZigProng(p.Arg0, null,         null,   null,   null,   false, IfCaptureReturn: p),
        Zig.ProngLoop p              => new ZigProng(p.Arg0, null,         null,   null,   null,   false, Loop: p.Arg2),
        Zig.ProngIfCapture p         => new ZigProng(p.Arg0, null,         null,   null,   null,   false,
            Cut: "zig switch prong `=> if (x) |v| …` with no `else` is not supported yet as a selected comptime prong"),
        Zig.ProngCaptureRef or Zig.ProngCaptureRefExpr or Zig.ProngCaptureRefReturn or Zig.ProngCaptureRefReturnVoid
            => throw new IrUnsupportedException(
                "zig `switch (@typeInfo(T)) { … => |*x| … }`: a comptime `std.builtin.Type` value has no storage, so it "
                + "cannot be captured by reference"),
        _ => throw new IrUnsupportedException("zig switch prong: " + (prong.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Select the prong a <c>switch</c> over a COMPTIME TAG takes, or null when the subject
    /// is not comptime-known (the caller then lowers an ordinary runtime switch). The tag is matched
    /// against each prong's <c>.name</c> case values, with an <c>else</c> prong as the fallback —
    /// only the selected prong is then lowered, so the arms that do not apply never instantiate a
    /// generic, never demand a field the type does not have, and never fail to compile. That is the
    /// whole point of the fold: <c>switch (@typeInfo(T))</c> is how std asks "which kind is T", and
    /// every non-taken arm is written for a different kind.</summary>
    /// <summary>The tag an enum literal names: <c>.int</c>, and the keyword-named <c>.undefined</c> / <c>.null</c>;
    /// null for any other node.</summary>
    private static string? EnumLitName(Item lit) => lit.Content switch
    {
        Zig.EnumLit el => Tok(el.Arg1),
        Zig.EnumLitUndefined => "undefined",
        Zig.EnumLitNull => "null",
        _ => null,
    };

    private ZigProng? SelectComptimeProng(Item subjectItem, Item prongsItem, out ZigTypeInfo? payload)
    {
        _comptimeUnionPayload = null;
        // A switch over a TYPE (std.Io.Writer.printInt's `switch (@TypeOf(value)) { isize, usize => {}, comptime_int =>
        // …, else => … }`): prongs are types, matched by type equality (declared width included).
        if (TrySelectTypeProng(subjectItem, prongsItem) is { } typeProng) { payload = null; return typeProng; }
        if (!TryEvalComptimeTag(subjectItem, out var tag, out payload)) { return null; }
        ZigProng? elseProng = null;
        foreach (var prongItem in Flatten(prongsItem))
        {
            var prong = DecomposeProng(prongItem);
            if (prong.CaseVals.Content is Zig.CaseElse) { elseProng = prong; continue; }
            foreach (var (lo, hi) in WalkCaseValItems(prong.CaseVals))
            {
                if (hi is not null)
                {
                    throw new IrUnsupportedException(
                        "zig `switch (@typeInfo(T))`: a `lo...hi` range is not a tag — prongs match `.int` / `.pointer` / "
                        + "`.@\"struct\"` literals or `else`");
                }
                if (EnumLitName(lo) is not { } litName)
                {
                    throw new IrUnsupportedException(
                        "zig `switch (@typeInfo(T))`: a prong's case value must be a `.tag` enum literal or `else`");
                }
                if (litName == tag) { return prong.Cut is { } cut ? throw new IrUnsupportedException(cut) : prong; }
            }
        }
        if (elseProng is not null) { return elseProng.Cut is { } elseCut ? throw new IrUnsupportedException(elseCut) : elseProng; }
        throw new IrUnsupportedException(
            $"zig `switch` over a comptime `.{tag}`: no prong matches it and there is no `else` "
            + "(real zig would reject the switch as non-exhaustive)");
    }

    /// <summary>The prong a switch over a TYPE subject selects: <c>@TypeOf(x)</c> or a type binding, each prong's case
    /// values compared as types (<see cref="TryFoldTypeEquality"/>). Null when the subject is not a type, or a case
    /// value does not answer as one.</summary>
    private ZigProng? TrySelectTypeProng(Item subjectItem, Item prongsItem)
    {
        var subject = subjectItem;
        while (subject.Content is Zig.Grouped g) { subject = g.Arg1; }
        var isType = subject.Content is Zig.BuiltinCall { Arg0: var tb } && Tok(tb) == "@TypeOf"
                     || subject.Content is Zig.Ident ti && _typeAliases.ContainsKey(Tok(ti.Arg0)) && _symbols.Resolve(Tok(ti.Arg0)) is null;
        if (!isType || !TryTypeAliasRhs(subject, out _)) { return null; }
        ZigProng? elseProng = null;
        foreach (var prongItem in Flatten(prongsItem))
        {
            var prong = DecomposeProng(prongItem);
            if (prong.CaseVals.Content is Zig.CaseElse) { elseProng = prong; continue; }
            foreach (var (lo, hi) in WalkCaseValItems(prong.CaseVals))
            {
                if (hi is not null) { return null; }
                // A case type dotcc does not lower (std.fmt.parse_float's `f16, f32, f64 => u64, f80, f128 => u128`)
                // is not the subject's, which did lower: it cannot match, so it is passed over.
                if (TryFoldTypeEquality(subject, lo) is true) { return prong.Cut is { } cut ? throw new IrUnsupportedException(cut) : prong; }
            }
        }
        if (elseProng is not null) { return elseProng.Cut is { } elseCut ? throw new IrUnsupportedException(elseCut) : elseProng; }
        throw new IrUnsupportedException("zig `switch` over a comptime TYPE: no prong matches it and there is no `else`");
    }

    /// <summary>Bind a folded prong's <c>|i|</c> capture to the switch subject's payload for the
    /// duration of that arm, shadow-saving any previous binding of the name. <c>_</c> binds nothing;
    /// a capture on a subject with no payload (a bare <c>signedness</c> tag) is a loud cut. Paired
    /// with <see cref="ExitComptimeProng"/>.</summary>
    private void EnterComptimeProng(ZigProng prong, ZigTypeInfo? payload)
    {
        // An empty name is the "bound nothing" marker, so Enter/Exit always pair one-for-one whether or
        // not this prong actually captures.
        _typeInfoShadows.Add(("", null));
        _unionCaptureShadows.Add(("", null));
        if (prong.CaptureName is not { } name || name == "_") { return; }
        // A comptime union's variant payload (TryEvalComptimeUnion): the capture is that literal.
        if (payload is null && _comptimeUnionPayload is { } unionPayload)
        {
            _unionCaptureShadows[^1] = (name, _comptimeValues.GetValueOrDefault(name));
            _comptimeValues[name] = unionPayload;
            return;
        }
        if (payload is null)
        {
            throw new IrUnsupportedException(
                $"zig `switch` prong capture `|{name}|`: this comptime tag carries no payload to bind");
        }
        _typeInfoShadows[^1] = (name, _typeInfoBindings.GetValueOrDefault(name));
        _typeInfoBindings[name] = payload;
    }

    /// <summary>Restore what <see cref="EnterComptimeProng"/> shadowed.</summary>
    private void ExitComptimeProng()
    {
        var (unionName, unionPrev) = _unionCaptureShadows[^1];
        _unionCaptureShadows.RemoveAt(_unionCaptureShadows.Count - 1);
        if (unionName.Length > 0)
        {
            if (unionPrev is { } up) { _comptimeValues[unionName] = up; } else { _comptimeValues.Remove(unionName); }
        }
        var (name, prev) = _typeInfoShadows[^1];
        _typeInfoShadows.RemoveAt(_typeInfoShadows.Count - 1);
        if (name.Length == 0) { return; }
        if (prev is { } p) { _typeInfoBindings[name] = p; } else { _typeInfoBindings.Remove(name); }
    }
}
