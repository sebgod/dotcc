#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// The six outputs <see cref="DotCC.Compiler.BuildShell"/> /
/// <c>SerializeFragment</c> consume — produced by the IR backend in the exact
/// shape the shell expects, so the shell is reused verbatim.
/// </summary>
internal sealed record CSharpBackendResult(
    string Functions,
    string Structs,
    string Aliases,
    string Globals,
    int MainArity,
    IReadOnlyList<DotCC.EmitHelpers.Export> Exports,
    bool MainReturnsVoid = false,
    bool MainReturnsErrUnion = false,
    bool MainErrPayloadIsVoid = false);

/// <summary>
/// Lowers the typed IR to low-level unsafe C# text. Deliberately DUMB: every
/// semantic decision (types, coercions, name resolution, control flow) was made
/// upstream by <see cref="IrBuilder"/> and the IR passes, so this just prints.
/// Phase 0 covers the vertical slice; it grows alongside the builder until the
/// IR path reaches parity with the legacy emitter.
/// </summary>
internal sealed class CSharpBackend
{
    /// <summary>The backend's lexical projection of the neutral IR — currently the
    /// type-spelling map (<see cref="ITarget"/>, the seam a second target slots
    /// into). The statement / expression emitter in this class is still the
    /// C#-specific one.</summary>
    private readonly ITarget _target = new CSharpTarget();

    /// <summary>Project a neutral <see cref="CType"/> onto the target's type
    /// spelling — replaces the type model's old baked-in <c>CsType</c> property.</summary>
    private string Cs(CType t) => _target.RenderType(t);

    public static CSharpBackendResult Run(IrBuilder unit, DotCC.ConversionGate? convGate = null)
    {
        var cg = new CSharpBackend { _convGate = convGate };
        // C tag namespace vs ordinary namespace: collect globals whose name an
        // emitted struct/enum type will shadow, so reads qualify (GlobalName).
        var typeNames = new HashSet<string>(unit.Types.Select(t => t.Name), StringComparer.Ordinal);
        typeNames.UnionWith(unit.Enums.Select(e => e.Name));
        cg._typeShadowedGlobals = new HashSet<string>(
            unit.Globals.Select(g => g.Sym.TargetName).Where(typeNames.Contains), StringComparer.Ordinal);
        var fns = new StringBuilder();
        var exports = new List<DotCC.EmitHelpers.Export>();
        var mainArity = -1;
        var mainReturnsVoid = false;
        var mainReturnsErrUnion = false;
        var mainErrPayloadIsVoid = false;

        foreach (var fn in unit.Functions)
        {
            if (fns.Length > 0) { fns.Append("\n\n"); }
            cg._currentFnName = fn.Sym.Name;
            fns.Append(cg.Func(fn));

            if (fn.Sym.Name == "main")
            {
                mainArity = fn.Params.Count;
                // A `void`-returning main (Zig's `pub fn main() void`, or a non-standard
                // `void main()` in C) can't be `return`ed from the int-typed entry — the
                // shell calls it for effect and returns 0 instead. An error-union main
                // (`pub fn main() !void` / `!u8`, Milestone N part 4) returns an `ErrUnion<…>`:
                // the shell maps an error to a non-zero exit (1) and success to 0 (a void
                // payload) or the payload value (an integer payload). Detect both here so the
                // entry-wiring can choose the right form.
                var mret = fn.Sym.Type is CType.Func mf ? mf.Return.Unqualified : null;
                mainReturnsVoid = mret is CType.VoidType;
                if (mret is CType.ErrorUnion meu)
                {
                    mainReturnsErrUnion = true;
                    mainErrPayloadIsVoid = meu.Payload.Unqualified is CType.VoidType;
                }
            }
            // A variadic function's `params VaArg[]` tail isn't a valid
            // [UnmanagedCallersOnly] signature, so it can't be exported.
            else if (fn.Sym.Storage != Storage.Static && !fn.Variadic)
            {
                var ret = fn.Sym.Type is CType.Func f ? cg.Cs(f.Return) : "int";
                var ps = string.Join(", ", fn.Params.Select(p => $"{cg.Cs(p.Type)} {p.TargetName}"));
                exports.Add(new DotCC.EmitHelpers.Export(fn.Sym.Name, ret, ps));
            }
        }

        // File-scope variables → public static fields of DotCcGlobals (the shell
        // surfaces them by bare name via `using static DotCcGlobals;`).
        var globals = new StringBuilder();
        foreach (var g in unit.Globals)
        {
            // A pointer/fn-ptr global whose address is taken is stored as `nint` so
            // Unsafe.AsPointer / Volatile.* accept it (CS0306) — the init pointer
            // value is cast to nint, reads cast back. (Backend decision from the
            // abstract AddressTaken fact.)
            if (NintStorage(g.Sym))
            {
                var ninit = g.Init is { } i0 ? $" = (nint)({cg.Coerced(i0, g.Sym.Type)})" : "";
                globals.Append($"    public static unsafe nint {g.Sym.TargetName}{ninit};\n");
                continue;
            }
            var init = g.Init is { } i ? " = " + cg.Coerced(i, g.Sym.Type) : "";
            globals.Append($"    public static unsafe {cg.Cs(g.Sym.Type)} {g.Sym.TargetName}{init};\n");
        }

        // struct/union/enum type declarations → the top-level type-decls section.
        var structs = new StringBuilder();
        foreach (var t in unit.Types) { structs.Append(cg.StructText(t)); }
        foreach (var en in unit.Enums) { structs.Append(cg.EnumText(en)); }

        return new CSharpBackendResult(fns.ToString(), structs.ToString(), Aliases: "", globals.ToString(), mainArity, exports, mainReturnsVoid, mainReturnsErrUnion, mainErrPayloadIsVoid);
    }

    // ---- type declarations -----------------------------------------------

    /// <summary>Render a struct/union type. A union uses
    /// <c>[StructLayout(LayoutKind.Explicit)]</c> with every field at
    /// <c>[FieldOffset(0)]</c> (C overlays all members at the same address).</summary>
    private string StructText(StructTypeDef t)
    {
        var wrappers = new StringBuilder();   // [InlineArray] wrapper types for non-primitive array members
        var sb = new StringBuilder();
        if (t.IsUnion)
        {
            sb.Append("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]\n");
        }
        sb.Append("unsafe struct ").Append(t.Name).Append("\n{\n");
        var bitUnitCounter = 0;
        for (var fi = 0; fi < t.Fields.Count; )
        {
            var f = t.Fields[fi];
            // A run of consecutive bit-fields packs into MSVC-style storage units —
            // one shared backing field per unit (so sizeof + member offsets match C)
            // plus a masked/sign-extended accessor per named member (value semantics:
            // overflow wraps, signed fields sign-extend on read). Consume the whole
            // run at once so adjacent same-size fields share a unit.
            if (f.IsBitField)
            {
                var run = new List<StructField>();
                var fj = fi;
                while (fj < t.Fields.Count && t.Fields[fj].IsBitField) { run.Add(t.Fields[fj]); fj++; }
                sb.Append(PackBitFieldRun(run, t.IsUnion, ref bitUnitCounter));
                fi = fj;
                continue;
            }
            if (t.IsUnion) { sb.Append("    [System.Runtime.InteropServices.FieldOffset(0)]\n"); }
            // An array member is C-inline storage, not a pointer field. A primitive
            // element lowers to a C# `fixed` buffer (inline, indexable, decays to a
            // pointer with no bounds check — both for free, matching C). A
            // non-primitive element (struct / pointer) can't be a `fixed` buffer, so
            // it gets a generated [InlineArray] wrapper; access routes through the
            // element pointer (see the Member case) to restore over-indexing + decay.
            if (f.Type.Unqualified is CType.Array arr)
            {
                var flat = arr.FlatElement;
                var count = FlatCount(arr);
                var fid = DotCC.EmitHelpers.Id(f.Name);
                if (IsFixedBufferType(Cs(flat)))
                {
                    sb.Append("    public fixed ").Append(Cs(flat)).Append(' ').Append(fid).Append('[').Append(count).Append("];\n");
                }
                else
                {
                    var wrap = $"__IA_{t.Name}_{fid}";
                    wrappers.Append("[System.Runtime.CompilerServices.InlineArray(").Append(count).Append(")]\nunsafe struct ")
                        .Append(wrap).Append("\n{\n    public ").Append(Cs(flat)).Append(" _e;\n}\n\n");
                    sb.Append("    public ").Append(wrap).Append(' ').Append(fid).Append(";\n");
                }
                fi++;
                continue;
            }
            sb.Append("    public ").Append(Cs(f.Type)).Append(' ').Append(DotCC.EmitHelpers.Id(f.Name)).Append(";\n");
            fi++;
        }
        sb.Append("}\n\n");
        return wrappers.Append(sb).ToString();
    }

    /// <summary>Render a C enum as a real C# <c>enum Name : underlying { … }</c>.
    /// The explicit <c>: underlying</c> matches C's int default (or a C23 fixed
    /// base); each enumerator carries its (auto-incremented or explicit) value.</summary>
    private string EnumText(EnumTypeDef e)
    {
        var sb = new StringBuilder();
        sb.Append("enum ").Append(e.Name).Append(" : ").Append(Cs(e.Underlying)).Append("\n{\n");
        foreach (var m in e.Members)
        {
            sb.Append("    ").Append(DotCC.EmitHelpers.Id(m.Name)).Append(" = ")
              .Append(m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
        }
        sb.Append("}\n\n");
        return sb.ToString();
    }

    /// <summary>In C an enum IS an integer type — it participates in arithmetic,
    /// bitwise, shift, relational, condition and switch contexts as its underlying
    /// integer. C# allows only a few enum operators, so decay an enum-typed operand
    /// to <see cref="CType.Enum.Underlying"/> before it reaches such a context (a
    /// <c>(int)EnumName.Member</c> cast). Recasting at enum-typed SINKS is the
    /// inverse, handled by <see cref="TryCoerceCast"/>. A non-enum operand passes
    /// through unchanged.</summary>
    private static CExpr DecayEnum(CExpr e) =>
        e.Type.Unqualified is CType.Enum en
            ? new Cast(en.Underlying, e) { Type = en.Underlying, Pos = e.Pos }
            : e;

    /// <summary>Pack a maximal run of consecutive bit-fields into MSVC-style storage
    /// units and emit them. A unit is a single private backing field of the declared
    /// integer type's size (so <c>sizeof</c> + member offsets match C's layout);
    /// consecutive bit-fields of the SAME size share a unit, LSB-first, until it
    /// fills (then a new unit starts), and a differing size or a zero-width member
    /// (<c>int : 0;</c>) forces a fresh unit. Each NAMED member gets a masked /
    /// sign-extended accessor property over its unit's backing field — value
    /// semantics (modular store, signed sign-extension on read). Anonymous members
    /// reserve bits but get no accessor. A union puts every unit at
    /// <c>[FieldOffset(0)]</c> (all members overlay).</summary>
    private string PackBitFieldRun(IReadOnlyList<StructField> run, bool isUnion, ref int unitCounter)
    {
        var units = new List<(int Bytes, List<(StructField F, int Off)> Members)>();
        int curBytes = -1; List<(StructField, int)>? curMembers = null; var used = 0;
        void Close()
        {
            if (curMembers is not null) { units.Add((curBytes, curMembers)); }
            curMembers = null; curBytes = -1; used = 0;
        }
        foreach (var f in run)
        {
            var bytes = BitUnitBytes(f.Type);
            var w = f.BitWidth!.Value;
            if (w == 0) { Close(); continue; }       // zero-width → storage-unit boundary
            if (curMembers is null || curBytes != bytes || used + w > bytes * 8)
            {
                Close();
                curMembers = new List<(StructField, int)>();
                curBytes = bytes;
            }
            curMembers.Add((f, used));
            used += w;
        }
        Close();

        var sb = new StringBuilder();
        foreach (var (bytes, members) in units)
        {
            var id = "__bf" + unitCounter++;
            var (ut, _) = BitStorage(bytes);
            if (isUnion) { sb.Append("    [System.Runtime.InteropServices.FieldOffset(0)]\n"); }
            sb.Append("    private ").Append(ut).Append(' ').Append(id).Append(";\n");
            foreach (var (f, off) in members)
            {
                if (f.Name.Length == 0) { continue; }   // anonymous padding — no accessor
                sb.Append(BitFieldAccessor(f, id, bytes, off));
            }
        }
        return sb.ToString();
    }

    /// <summary>The storage-unit size (bytes) for a bit-field — its declared integer
    /// type's size (MSVC allocates a unit sized to the type); an enum bit-field uses
    /// its underlying size. Falls back to 4 for an unexpected size.</summary>
    private static int BitUnitBytes(CType t)
    {
        var b = t.SizeOf;
        return b is 1 or 2 or 4 or 8 ? b : 4;
    }

    /// <summary>The unsigned C# storage type (and its bit width) for a bit-field unit
    /// of the given byte size. Unsigned storage keeps the write-masking sign-clean;
    /// each member's accessor re-applies its own signedness on read.</summary>
    private static (string Cs, int Bits) BitStorage(int bytes) => bytes switch
    {
        1 => ("byte", 8),
        2 => ("ushort", 16),
        8 => ("ulong", 64),
        _ => ("uint", 32),
    };

    /// <summary>Whether a bit-field's declared type is signed (drives sign-extension
    /// on read). An enum bit-field follows its underlying type.</summary>
    private static bool IsSignedBitField(CType t) => t.Unqualified switch
    {
        CType.Prim p => p.Signed,
        CType.Enum e => IsSignedBitField(e.Underlying),
        _ => false,
    };

    /// <summary>Emit a masked / sign-extended accessor property for one named
    /// bit-field member living at bit <paramref name="off"/> of unit
    /// <paramref name="unit"/> (an unsigned backing field of <paramref name="unitBytes"/>
    /// bytes). The getter extracts the field's bits and (signed) sign-extends through
    /// a same-width-or-wider math type; the setter clears the field's bit window and
    /// ORs in the value truncated to the field width — exactly C's value semantics.</summary>
    private string BitFieldAccessor(StructField f, string unit, int unitBytes, int off)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var bt = Cs(f.Type);                          // declared type spelling ("int" / "uint" / …)
        var fid = DotCC.EmitHelpers.Id(f.Name);
        var (ut, ub) = BitStorage(unitBytes);         // unsigned backing type + its bit width
        var w = f.BitWidth!.Value;
        var signed = IsSignedBitField(f.Type);
        var mw = ub <= 32 ? 32 : 64;                  // shift math is done in int/long (C# promotes < int)
        var mtU = mw == 32 ? "uint" : "ulong";
        var mtS = mw == 32 ? "int" : "long";
        var unitAll = ub >= 64 ? ulong.MaxValue : (1UL << ub) - 1;
        var mask = w >= 64 ? ulong.MaxValue : (1UL << w) - 1;
        var clear = unitAll & ~((mask << off) & unitAll);
        var maskLit = mask.ToString(inv) + (mw == 32 ? "u" : "UL");
        var clearLit = LitForUnit(clear, unitBytes);
        var extract = $"(({mtU})({unit} >> {off}) & {maskLit})";
        var get = signed
            ? $"({bt})((({mtS}){extract} << {mw - w}) >> {mw - w})"   // arithmetic shift sign-extends
            : $"({bt})({extract})";
        var set = $"{unit} = ({ut})(({unit} & {clearLit}) | (({ut})((({mtU})value & {maskLit}) << {off})));";
        return $"    public {bt} {fid} {{ get => {get}; set => {set} }}\n";
    }

    /// <summary>Format an unsigned bit-mask literal in the right C# form for a unit's
    /// storage type — 1/2-byte units take a bare (in-range, positive) <c>int</c>
    /// literal, 4-byte a <c>u</c> suffix, 8-byte a <c>UL</c> suffix.</summary>
    private static string LitForUnit(ulong v, int bytes)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            1 or 2 => v.ToString(inv),
            4 => v.ToString(inv) + "u",
            _ => v.ToString(inv) + "UL",
        };
    }

    /// <summary>C# permits a <c>fixed</c> buffer only of these primitive element
    /// types — every other array member must go through an <c>[InlineArray]</c>
    /// wrapper instead.</summary>
    private static bool IsFixedBufferType(string cs) => cs is
        "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint"
        or "long" or "ulong" or "char" or "float" or "double";

    // ---- functions -------------------------------------------------------

    private string Func(FuncDef fn)
    {
        var retTy = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        _currentRet = retTy;
        var ps = string.Join(", ", fn.Params.Select(p => $"{Cs(p.Type)} {p.TargetName}"));
        // A variadic C function gets a trailing `params VaArg[] _va`; C# converts
        // each variadic actual to a VaArg at the call site (carries pointers too).
        if (fn.Variadic) { ps = ps.Length == 0 ? "params VaArg[] _va" : ps + ", params VaArg[] _va"; }
        var sb = new StringBuilder();
        sb.Append($"static unsafe {Cs(retTy)} {fn.Sym.TargetName}({ps})\n");
        // C lets a goto jump INTO a nested block; C# scopes labels to their
        // block. Hoist labeled tails until every goto is legal (no-op for the
        // overwhelming majority of functions — see GotoScopeNormalizer).
        var body = GotoScopeNormalizer.Normalize(fn.Body);
        // A Zig `!T` function (error-union return) wraps its body so a propagated
        // `try` (ZigErrorReturn) converts back to an `Err` return — the exception-based
        // early-return-out-of-an-expression, modeled on the setjmp lowering. A `!void`
        // body that falls off the end is a Zig success, so it returns Ok(default).
        if (retTy is CType.ErrorUnion eu)
        {
            var ts = eu.Payload is CType.VoidType ? "Unit" : Cs(eu.Payload);
            sb.Append("{\n");
            sb.Append(Pad(1)).Append("try\n");
            Stmt(sb, body, 1);
            sb.Append(Pad(1)).Append($"catch (ZigErrorReturn __e) {{ return ErrUnion<{ts}>.Err(__e.Code); }}\n");
            if (eu.Payload is CType.VoidType && !Terminates(body))
            {
                sb.Append(Pad(1)).Append("return ErrUnion<Unit>.Ok(default);\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }
        Stmt(sb, body, 0);
        return sb.ToString();
    }

    // ---- comma-operator statement hoisting -------------------------------
    // A value-context comma `(e1, …, eN)` evaluates e1..e(N-1) for side effects and
    // yields eN. Rather than an IIFE delegate (which can't take the address of a
    // captured local — CS1686 — and can't carry a void operand), at a once-evaluated
    // statement position the leading operands are HOISTED as statements emitted
    // before the statement, and only eN stays in place. Loop conditions/posts
    // (re-evaluated each iteration) keep the closure form, where hoisting is wrong.

    /// <summary>Leading comma operands awaiting emission before the current statement
    /// (populated while <see cref="_canHoist"/> is set).</summary>
    private readonly List<string> _pending = new();
    /// <summary>True while rendering a once-evaluated statement-level expression, so a
    /// value comma hoists its leading operands instead of forming a closure.</summary>
    private bool _canHoist;

    /// <summary>Bumped each time hoisted statements are actually emitted — lets
    /// <see cref="Nested"/> tell whether a braceless body hoisted (and so must be
    /// braced to keep the hoisted statements inside the controller).</summary>
    private int _hoistedCount;

    /// <summary>Monotonic counter naming the block-local temps an array compound
    /// literal hoists to (<c>__cl0</c>, <c>__cl1</c>, …) when it appears outside
    /// initializer position. Unique within any function scope; never reset.</summary>
    private int _clCounter;

    private void FlushPending(StringBuilder sb, string pad)
    {
        if (_pending.Count == 0) { return; }
        foreach (var p in _pending) { sb.Append(pad).Append(p).Append(";\n"); }
        _pending.Clear();
        _hoistedCount++;
    }

    /// <summary>Render a statement-level expression with comma-hoisting enabled, emit
    /// any hoisted leading statements, and return the (value) text for the statement.</summary>
    private string Hoist(StringBuilder sb, string pad, System.Func<string> render)
    {
        var prev = _canHoist;
        _canHoist = true;
        var text = render();
        _canHoist = prev;
        FlushPending(sb, pad);
        return text;
    }

    /// <summary>Render a sub-expression that is NOT always evaluated (the right
    /// operand of <c>&amp;&amp;</c>/<c>||</c>, a ternary arm) with hoisting disabled —
    /// a comma there must keep its side effect conditional (the inline closure form),
    /// not lift it out where it would run unconditionally.</summary>
    private string NoHoist(System.Func<string> render)
    {
        var prev = _canHoist;
        _canHoist = false;
        var text = render();
        _canHoist = prev;
        return text;
    }

    private void Stmt(StringBuilder sb, CStmt s, int ind)
    {
        var pad = Pad(ind);
        switch (s)
        {
            case Block b:
                sb.Append(pad).Append("{\n");
                foreach (var st in b.Stmts) { Stmt(sb, st, ind + 1); }
                sb.Append(pad).Append("}\n");
                break;
            // Brace-less sequence — children share the ENCLOSING scope (a
            // multi-declarator decl that split into DeclStmt + ArrayDecl).
            case Seq q:
                foreach (var st in q.Stmts) { Stmt(sb, st, ind); }
                break;
            case DeclStmt d:
            {
                // Render the declaration with hoisting enabled (a value-comma
                // initializer hoists its side effects), then flush before the decl.
                var tmp = new StringBuilder();
                var prev = _canHoist;
                _canHoist = true;
                EmitDeclStmt(tmp, d, pad);
                _canHoist = prev;
                FlushPending(sb, pad);
                sb.Append(tmp);
                break;
            }
            case ArrayDecl a:
                {
                    var elemCs = Cs(a.Element);
                    if (a.Inits is { } inits)
                    {
                        // Coerce each element to the array's element type — a no-op
                        // for byte/int arrays (constant fits), but a char16_t array's
                        // `char` elements need the explicit (char) cast C# requires.
                        sb.Append(pad).Append($"{elemCs}* {a.Sym.TargetName} = stackalloc {elemCs}[]{{ {string.Join(", ", inits.Select(e => Coerced(e, a.Element)))} }};\n");
                    }
                    else
                    {
                        // No initializer → zeroed stackalloc of the given extent.
                        var count = a.CountExpr is { } ce ? Expr(ce) : "0";
                        sb.Append(pad).Append($"{elemCs}* {a.Sym.TargetName} = stackalloc {elemCs}[{count}];\n");
                    }
                }
                break;
            case ExprStmt es:
            {
                // A statement-level comma discards every operand's value — emit one
                // statement per operand, BRACED so a braceless nested body (`if (c)
                // (a, b); else …`, `while (…) (a, b);`) stays a single statement. Peel
                // parens / a void cast (`(void)(a, b)`, `api_check`) to find the comma.
                var inner = es.Expr;
                while (inner is Paren pp) { inner = pp.Inner; }
                if (inner is CondExpr { Type.Unqualified: CType.VoidType } ct)
                {
                    // A void-typed ternary in statement position is a real if/else
                    // (CS0173 forbids a void `?:` value) — synthesize an If and recurse
                    // so it nests/braces correctly and takes no trailing `;`.
                    Stmt(sb, new If(ct.Cond, new ExprStmt(ct.Then), new ExprStmt(ct.Else)) { Pos = es.Pos }, ind);
                }
                else if (inner is CommaOp co)
                {
                    // The comma's value is discarded, so each operand is a statement;
                    // pure operands are dropped (no effect). All pure → an empty stmt.
                    var effects = co.Items.Where(it => !IsPure(it)).ToList();
                    if (effects.Count == 0) { sb.Append(pad).Append(";\n"); }
                    else
                    {
                        sb.Append(pad).Append("{\n");
                        foreach (var item in effects) { sb.Append(Pad(ind + 1)).Append(RenderStmtExpr(item)).Append(";\n"); }
                        sb.Append(pad).Append("}\n");
                    }
                }
                else if (IsPure(inner))
                {
                    // A pure expression statement is a no-op in C — e.g.
                    // `(void)luaP_isOT;` suppressing an unused-function warning. Emit an
                    // empty statement: there's nothing to evaluate, and a discard of
                    // `&fn` can't even infer a type (CS8183).
                    sb.Append(pad).Append(";\n");
                }
                else
                {
                    var t = Hoist(sb, pad, () => RenderStmtExpr(es.Expr));
                    sb.Append(pad).Append(t).Append(";\n");
                }
                break;
            }
            case Return r:
                if (r.Value is null) { sb.Append(pad).Append("return;\n"); }
                else
                {
                    var rv = Hoist(sb, pad, () => Coerced(r.Value, _currentRet));
                    sb.Append(pad).Append($"return {rv};\n");
                }
                break;
            case Break:
                // Inside a switch tail HOISTED out of its switch (see RenderSwitch), a
                // `break` that meant "leave the switch" must instead skip to the point
                // just past the switch — otherwise it would escape the enclosing loop.
                sb.Append(pad).Append(_breakAsGoto is { } bt ? $"goto {bt};\n" : "break;\n");
                break;
            case Continue: sb.Append(pad).Append("continue;\n"); break;
            case If f:
                // The condition is evaluated once, so a value comma in it can hoist.
                var ifc = Hoist(sb, pad, () => Expr(DecayEnum(f.Cond)));
                sb.Append(pad).Append($"if (Cond.B({ifc}))\n");
                Nested(sb, f.Then, ind);
                if (f.Else is { } els)
                {
                    sb.Append(pad).Append("else\n");
                    Nested(sb, els, ind);
                }
                break;
            case While w:
                sb.Append(pad).Append($"while (Cond.B({Expr(DecayEnum(w.Cond))}))\n");
                WithNormalBreak(() => Nested(sb, w.Body, ind));
                break;
            case DoWhile dw:
                sb.Append(pad).Append("do\n");
                WithNormalBreak(() => Nested(sb, dw.Body, ind));
                sb.Append(pad).Append($"while (Cond.B({Expr(DecayEnum(dw.Cond))}));\n");
                break;
            case Goto g:
                // A cross-section goto to a label that starts another case section
                // can't be a plain label-goto (C# can't jump into a sibling case —
                // CS0159); it renders as `goto case <V>` via the active switch's map.
                sb.Append(pad).Append(
                    _gotoCaseMap is { } gm && gm.TryGetValue(g.Label, out var jc) ? jc : $"goto {DotCC.EmitHelpers.Id(g.Label)};")
                  .Append('\n');
                break;
            case Labeled lb:
                sb.Append(pad).Append(DotCC.EmitHelpers.Id(lb.Name)).Append(":\n");
                Stmt(sb, lb.Body, ind);
                break;
            case CaseLabelStmt cls:
                // A case/default label nested in a switch-body statement (Duff's
                // device) — printed verbatim (structurally faithful; C# rejects it).
                sb.Append(pad).Append(cls.CaseExpr is { } cse ? $"case {Expr(DecayEnum(cse))}:\n" : "default:\n");
                Stmt(sb, cls.Body, ind);
                break;
            case Switch sw:
                RenderSwitch(sb, sw, ind, pad);
                break;
            case SetjmpGuard sj:
            {
                // Arm this site with a FRESH token identity so the `when` filter
                // disambiguates nested setjmps: a longjmp reads the SAME env (matches
                // here), while one aimed at a different env carries a different token
                // and propagates past this catch.
                var env = Expr(sj.Env);
                sb.Append(pad).Append($"{env} = new Libc.LongJmpToken();\n");
                sb.Append(pad).Append("try\n");
                GuardBody(sb, sj.TryBody, ind);
                sb.Append(pad).Append($"catch (Libc.LongJmpException __jmp) when (__jmp.Token == {env})\n");
                GuardBody(sb, sj.CatchBody, ind);
                break;
            }
            case DeferGuard dg:
            {
                // `defer` → try/finally (cleanup on every exit); `errdefer` → try/catch that
                // runs the cleanup only on a propagating error, then re-throws. Mirrors the
                // SetjmpGuard try precedent; GuardBody braces a braceless body.
                sb.Append(pad).Append("try\n");
                GuardBody(sb, dg.Body, ind);
                if (dg.OnErrorOnly)
                {
                    sb.Append(pad).Append("catch (ZigErrorReturn)\n").Append(pad).Append("{\n");
                    Stmt(sb, dg.Cleanup, ind + 1);
                    sb.Append(Pad(ind + 1)).Append("throw;\n");
                    sb.Append(pad).Append("}\n");
                }
                else
                {
                    sb.Append(pad).Append("finally\n");
                    GuardBody(sb, dg.Cleanup, ind);
                }
                break;
            }
            case ZigErrorThrow zt:
                sb.Append(pad).Append($"throw new ZigErrorReturn((ushort){zt.Code});\n");
                break;
            case For fr:
                var init = fr.Init switch
                {
                    DeclStmt d => DeclInline(d),
                    ExprStmt e => Expr(e.Expr),
                    _ => "",
                };
                var cond = fr.Cond is null ? "" : $"Cond.B({Expr(DecayEnum(fr.Cond))})";
                var post = fr.Post is null ? "" : Expr(fr.Post);
                sb.Append(pad).Append($"for ({init}; {cond}; {post})\n");
                WithNormalBreak(() => Nested(sb, fr.Body, ind));
                break;
            default:
                throw new IrUnsupportedException("codegen stmt " + s.GetType().Name);
        }
    }

    // ---- switch + cross-case label control flow --------------------------

    /// <summary>The active switch's section-start labels, mapped to the
    /// <c>goto case &lt;V&gt;;</c> a cross-section goto to that label renders as
    /// (C# can't jump into a sibling case — CS0159). Replaced (not merged) around a
    /// nested switch so an inner goto to an OUTER label stays a plain label-goto,
    /// which is valid because the outer label is in an enclosing block.</summary>
    private Dictionary<string, string>? _gotoCaseMap;

    /// <summary>When set, a <c>break</c> renders as <c>goto &lt;this&gt;</c> — used
    /// while rendering a switch tail hoisted OUT of its switch, where a `break` that
    /// meant "leave the switch" must skip to just past the switch rather than escape
    /// the enclosing loop. Cleared inside a nested loop/switch (break resumes its
    /// normal target there).</summary>
    private string? _breakAsGoto;

    /// <summary>Unique-suffix counter for the synthetic skip-over labels emitted
    /// after a switch with hoisted shared-handler tails.</summary>
    private int _switchPastSeq;

    /// <summary>Render with <c>break</c> bound to its normal target (loop/switch
    /// exit) — used around a loop or switch body nested inside a hoisted switch tail,
    /// where the surrounding <see cref="_breakAsGoto"/> redirection must not leak in.</summary>
    private void WithNormalBreak(System.Action render)
    {
        var saved = _breakAsGoto;
        _breakAsGoto = null;
        render();
        _breakAsGoto = saved;
    }

    /// <summary>Render a C <c>switch</c> to C#, reconciling two label-scope
    /// mismatches the Lua VM dispatch loop relies on:
    /// <list type="bullet">
    /// <item>A label at a section START (<c>l_tforcall:</c>/<c>l_tforloop:</c>):
    /// a cross-section <c>goto</c> to it becomes <c>goto case &lt;V&gt;</c>.</item>
    /// <item>A label NOT at a section start that a sibling case jumps to (a shared
    /// handler like <c>ret:</c>): C# can't jump into the middle of another case, so
    /// the labeled tail is HOISTED out of the switch into the enclosing block — a
    /// plain <c>goto L</c> then reaches it from anywhere — guarded by a skip-goto so
    /// normal switch exit doesn't fall into it.</item>
    /// </list></summary>
    private void RenderSwitch(StringBuilder sb, Switch sw, int ind, string pad)
    {
        // C's switch is int-semantic. An enum subject / enumerator case label decays
        // to its underlying int so the governing type is uniform — a plain int switch
        // may carry enumerator labels and vice versa (C# rejects the mixed forms).
        var subj = Hoist(sb, pad, () => Expr(DecayEnum(sw.Subject)));

        // A C case section `case X: { … }` parses as one wrapping Block; the labels
        // we reconcile (ret/l_tforcall/…) live INSIDE it. Work on each section's
        // EFFECTIVE statement list (the block's contents when it's a lone block, else
        // the body itself) — see-through unwrap for analysis + hoisting, while the
        // braces are restored in rendering to keep the case's block scope.
        IReadOnlyList<CStmt> Eff(SwitchSection s) =>
            s.Body is [Block b] ? b.Stmts : s.Body;

        // Per-section TOP-LEVEL labels (the granularity we can hoist / `goto case`),
        // plus the case-value a section-start label maps to.
        var labelSection = new Dictionary<string, int>(StringComparer.Ordinal);
        var startLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var si = 0; si < sw.Sections.Count; si++)
        {
            var eff = Eff(sw.Sections[si]);
            for (var p = 0; p < eff.Count; p++)
            {
                if (eff[p] is Labeled l) { labelSection[l.Name] = si; }
            }
            if (eff.Count > 0 && eff[0] is Labeled lb0)
            {
                var first = sw.Sections[si].Labels[0];
                startLabel[lb0.Name] = first.CaseExpr is { } ce ? $"goto case {CaseValue(ce, sw.Subject.Type)};" : "goto default;";
            }
        }

        // A non-start label targeted from a DIFFERENT section must be hoisted.
        var hoist = new HashSet<string>(StringComparer.Ordinal);
        for (var si = 0; si < sw.Sections.Count; si++)
        {
            foreach (var tgt in GotoTargets(sw.Sections[si].Body))
            {
                if (labelSection.TryGetValue(tgt, out var ds) && ds != si && !startLabel.ContainsKey(tgt))
                {
                    hoist.Add(tgt);
                }
            }
        }

        var saved = _gotoCaseMap;
        _gotoCaseMap = startLabel.Count > 0 ? startLabel : null;
        // Inside the sections, a `break` leaves THIS switch normally (clear any
        // outer hoisted-tail break redirection — break is re-bound per construct).
        var savedBreak = _breakAsGoto;
        _breakAsGoto = null;

        // First index of a hoisted shared-handler label in a section's body (or -1).
        int FirstHoistCut(IReadOnlyList<CStmt> e)
        {
            for (var p = 0; p < e.Count; p++)
            {
                if (e[p] is Labeled lp && hoist.Contains(lp.Name)) { return p; }
            }
            return -1;
        }

        // A hoisted tail that doesn't END control flow falls THROUGH (in C) into the
        // next case. C# forbids falling from the out-of-switch hoisted region back
        // into a case, so the successor section(s) — up to and including the first
        // that ends control flow — are relocated WHOLESALE into the post-switch region
        // right after the tail, their in-switch case redirected to a `goto`. (Lua's
        // shared handlers all terminate, so this is inert there; chibi's
        // `call_error_handler:` falls through into `case SEXP_OP_RAISE` — without this
        // the raise/error-handler logic is skipped and the VM runs on corrupt.)
        var relocWhole = new HashSet<int>();
        for (var si = 0; si < sw.Sections.Count; si++)
        {
            var cut0 = FirstHoistCut(Eff(sw.Sections[si]));
            if (cut0 < 0) { continue; }
            var tail0 = Eff(sw.Sections[si]).Skip(cut0).ToList();
            if (tail0.Count > 0 && Terminates(tail0[^1])) { continue; }
            for (var t = si + 1; t < sw.Sections.Count; t++)
            {
                if (!relocWhole.Add(t)) { break; }
                var te = Eff(sw.Sections[t]);
                if (te.Count > 0 && Terminates(te[^1])) { break; }
            }
        }

        // The post-switch region is non-empty exactly when some label is hoisted (a
        // hoisted label lives in some section, so that section gets a cut). Allocate
        // the skip-target id only then, so non-hoisting switches keep stable output.
        var pastId = hoist.Count > 0 ? _switchPastSeq++ : -1;
        var past = $"__sw_past_{pastId}";
        string RelocLabel(int s) => $"__sw_reloc_{pastId}_{s}";

        // Ordered post-switch segments: hoisted tails and relocated sections. Each
        // carries an optional entry label (relocated sections, reached by the in-switch
        // `case …: goto <label>`) and whether it FALLS THROUGH into the next segment (a
        // tail flowing into its relocated successor) rather than skipping past.
        var postSegments = new List<(string? Label, IReadOnlyList<CStmt> Body, bool Continue)>();
        sb.Append(pad).Append($"switch ({subj})\n").Append(pad).Append("{\n");
        var inner = ind + 1;
        var ipad = Pad(inner);
        for (var si = 0; si < sw.Sections.Count; si++)
        {
            var sec = sw.Sections[si];
            foreach (var lab in sec.Labels)
            {
                // A range label (`lo...hi`) → a relational pattern `case >= lo and <= hi:`; a
                // single value → `case v:`; a null `CaseExpr` → `default:`.
                sb.Append(ipad).Append(
                    lab.CaseExpr is not { } ce ? "default:\n"
                    : lab.HiExpr is { } hi ? $"case >= {CaseValue(ce, sw.Subject.Type)} and <= {CaseValue(hi, sw.Subject.Type)}:\n"
                    : $"case {CaseValue(ce, sw.Subject.Type)}:\n");
            }
            var eff = Eff(sec);
            // Relocated wholesale (a fall-through chain flows in): the in-switch case
            // just jumps to its out-of-switch body; the body emits in the post-region.
            if (relocWhole.Contains(si))
            {
                var contNext = !(eff.Count > 0 && Terminates(eff[^1])) && relocWhole.Contains(si + 1);
                postSegments.Add((RelocLabel(si), eff, contNext));
                sb.Append(ipad).Append($"goto {DotCC.EmitHelpers.Id(RelocLabel(si))};\n");
                continue;
            }
            var wrapped = sec.Body is [Block];
            // Split the section at the first hoisted shared-handler label: the head
            // renders in place + a `goto L` to reach the handler; the tail (label +
            // the rest of the section) moves out after the switch.
            var cut = FirstHoistCut(eff);
            var head = cut < 0 ? eff : eff.Take(cut).ToList();
            // Body indent: one deeper inside the restored braces.
            var bodyInd = wrapped ? inner + 2 : inner + 1;
            if (wrapped) { sb.Append(Pad(inner + 1)).Append("{\n"); }
            foreach (var st in head) { Stmt(sb, st, bodyInd); }
            if (cut >= 0)
            {
                var tail = eff.Skip(cut).ToList();
                // A tail that doesn't end control flow falls through to si+1, which we
                // relocated just above — flow into that segment instead of `goto past`.
                var contNext = !(tail.Count > 0 && Terminates(tail[^1])) && relocWhole.Contains(si + 1);
                postSegments.Add((null, tail, contNext));
                sb.Append(Pad(bodyInd)).Append($"goto {DotCC.EmitHelpers.Id(((Labeled)eff[cut]).Name)};\n");
            }
            if (wrapped) { sb.Append(Pad(inner + 1)).Append("}\n"); }
            // Synthesize C's fall-through jump when the section doesn't end control
            // flow (goto case / goto default / a trailing break C falls out of).
            // A wrapped block whose contents terminate also terminates.
            var terminates = cut >= 0 || (head.Count > 0 && Terminates(head[^1]));
            if (!terminates)
            {
                string jump;
                if (si + 1 < sw.Sections.Count)
                {
                    var next = sw.Sections[si + 1].Labels[0];
                    jump = next.CaseExpr is { } nce ? $"goto case {CaseValue(nce, sw.Subject.Type)};\n" : "goto default;\n";
                }
                else { jump = "break;\n"; }
                sb.Append(Pad(inner + 1)).Append(jump);
            }
        }
        sb.Append(pad).Append("}\n");
        _gotoCaseMap = saved;

        // Hoisted shared handlers + relocated sections: placed after the switch (so a
        // plain `goto L` reaches them from any case), skipped on normal switch exit. A
        // `break` here meant "leave the switch" — now it must skip past the switch (the
        // handler runs OUTSIDE it), so it renders as `goto __sw_past`. A segment marked
        // Continue falls straight into the next (a tail into its relocated successor).
        if (postSegments.Count > 0)
        {
            sb.Append(pad).Append($"goto {past};\n");
            _breakAsGoto = past;
            foreach (var (label, body, cont) in postSegments)
            {
                if (label != null) { sb.Append(pad).Append($"{DotCC.EmitHelpers.Id(label)}:\n"); }
                foreach (var st in body) { Stmt(sb, st, ind); }
                if (!cont && (body.Count == 0 || !Terminates(body[^1]))) { sb.Append(pad).Append($"goto {past};\n"); }
            }
            sb.Append(pad).Append($"{past}: ;\n");
        }
        _breakAsGoto = savedBreak;
    }

    /// <summary>All <c>goto</c> targets reachable within a section body, descending
    /// through ordinary nested statements but NOT into a nested <c>switch</c> (whose
    /// labels belong to its own scope).</summary>
    private static IEnumerable<string> GotoTargets(IReadOnlyList<CStmt> body)
    {
        var acc = new List<string>();
        foreach (var s in body) { CollectGotos(s, acc); }
        return acc;
    }

    private static void CollectGotos(CStmt s, List<string> acc)
    {
        switch (s)
        {
            case Goto g: acc.Add(g.Label); break;
            case Labeled l: CollectGotos(l.Body, acc); break;
            case Block b: foreach (var x in b.Stmts) { CollectGotos(x, acc); } break;
            case If f: CollectGotos(f.Then, acc); if (f.Else is { } e) { CollectGotos(e, acc); } break;
            case While w: CollectGotos(w.Body, acc); break;
            case DoWhile d: CollectGotos(d.Body, acc); break;
            case For fr: CollectGotos(fr.Body, acc); break;
            default: break;   // Switch: a goto inside it targets its own labels
        }
    }

    // Render a sub-statement of if/while/for: a block keeps the parent indent; a
    // single statement indents one level. If that statement HOISTED comma side
    // effects (emitting preceding statements), it's wrapped in a block so they don't
    // leak out of the controller's single-statement body — but ONLY then, so a plain
    // body (or a label, which a `goto` must still reach) keeps its bare form.
    private void Nested(StringBuilder sb, CStmt s, int ind)
    {
        if (s is Block) { Stmt(sb, s, ind); return; }
        var before = _hoistedCount;
        var tmp = new StringBuilder();
        Stmt(tmp, s, ind + 1);
        if (_hoistedCount != before)
        {
            sb.Append(Pad(ind)).Append("{\n").Append(tmp).Append(Pad(ind)).Append("}\n");
        }
        else
        {
            sb.Append(tmp);
        }
    }

    /// <summary>True when a statement provably ends control flow at its end, so a
    /// switch section ending in it needs no synthetic fall-through jump.</summary>
    private static bool Terminates(CStmt s) => s switch
    {
        Break or Continue or Return or Goto or ZigErrorThrow => true,
        Block b => b.Stmts.Count > 0 && Terminates(b.Stmts[^1]),
        If f => f.Else is { } e && Terminates(f.Then) && Terminates(e),
        Labeled l => Terminates(l.Body),
        // A defer/errdefer guard adds no fall-through path of its own: the try/finally
        // (and the errdefer catch, which re-throws) terminate exactly when the body does.
        DeferGuard g => Terminates(g.Body),
        _ => false,
    };

    // Render a try/catch body for a SetjmpGuard. C# requires a braced block here
    // (`try stmt;` is invalid), so a single (braceless) C statement is wrapped, and
    // a null body (the no-recovery swallow shape) becomes an empty `{ }`.
    private void GuardBody(StringBuilder sb, CStmt? body, int ind)
    {
        var pad = Pad(ind);
        if (body is null) { sb.Append(pad).Append("{ }\n"); return; }
        if (body is Block) { Stmt(sb, body, ind); return; }
        sb.Append(pad).Append("{\n");
        Stmt(sb, body, ind + 1);
        sb.Append(pad).Append("}\n");
    }

    // ---- store coercions (decl init / assignment / return / global) -------
    // C performs an implicit integer/pointer conversion at a store even when it
    // narrows or changes signedness; C# requires the cast explicitly (CS0266).
    // Mirrors the legacy CoerceStore (validated against the MSVC/gcc oracles):
    // insert `(target)(value)` exactly when C# would NOT convert implicitly.

    /// <summary>The lowered C# return type of the function currently being
    /// rendered — the sink type for <c>return</c> coercion. Set per function in
    /// <see cref="Func"/> (the backend renders functions one at a time).</summary>
    private CType _currentRet = CType.Int;
    // -Wconversion sink (off unless -Wconversion). The narrowing decision is made
    // here in TryCoerceCast, so this is the natural place to record the warning.
    private DotCC.ConversionGate? _convGate;
    private string? _currentFnName;

    // ---- volatile / atomic access ----------------------------------------

    /// <summary>Render a READ of an lvalue, fencing it when the lvalue's type is
    /// qualified: atomic → seq-cst <c>Atomic.Load</c>, volatile →
    /// <c>Volatile.Read</c>, neither → the bare text at its natural precedence.</summary>
    private (string, int) QualifiedRead(CExpr lv, string bare, int barePrec) =>
        lv.Type.IsAtomic ? ($"Atomic.Load(ref {bare})", PPrimary)
        // A pointer/fn-ptr volatile lvalue can't be a Volatile.Read<T> type arg
        // (CS0306): reinterpret its slot as `nint` through its address, cast back.
        : lv.Type.IsVolatile && lv.Type.IsPointerLowered
            ? ($"({Cs(lv.Type.Unqualified)}){VolatileRead($"*(nint*)&{bare}")}", PUnary)
        : lv.Type.IsVolatile ? (VolatileRead(bare), PPrimary)
        : (bare, barePrec);

    /// <summary>The <c>Atomic.*Fetch</c> helper for a compound assignment / step
    /// that returns the NEW value (C's <c>x op= n</c> result). Throws for an
    /// operator with no lock-free primitive.</summary>
    private static string AtomicFetchHelper(BinOp op, string csType) => op switch
    {
        BinOp.Add => "AddFetch",
        BinOp.Sub => "SubFetch",
        BinOp.BitAnd => "AndFetch",
        BinOp.BitOr => "OrFetch",
        BinOp.BitXor => "XorFetch",
        _ => throw new IrUnsupportedException(
            $"atomic compound assignment `{BinSym(op)}=` on `{csType}` (no lock-free primitive)"),
    };

    /// <summary>The fenced read of a volatile lvalue text — C's volatile read.</summary>
    private static string VolatileRead(string lv) => $"global::System.Threading.Volatile.Read(ref {lv})";

    /// <summary>True when an lvalue's storage roots at a file-scope global / static
    /// local — a C# static field, hence a moveable variable whose address must go
    /// through <c>Unsafe.AsPointer</c> rather than a bare <c>&amp;</c> (CS0212). A
    /// member through a pointer (<c>p-&gt;f</c>) or a pointer deref roots at the
    /// pointee, not at the field, so those stay a plain <c>&amp;</c>.</summary>
    private static bool RootsAtGlobal(CExpr e) => e switch
    {
        VarRef v => v.Sym.IsGlobal,
        Paren p => RootsAtGlobal(p.Inner),
        Member { Arrow: false } m => RootsAtGlobal(m.Base),
        _ => false,
    };

    /// <summary>The C# backend's decision to store a pointer/fn-ptr-typed
    /// <em>global</em> as an <c>nint</c> field: when its address is taken (the abstract
    /// <see cref="Symbol.AddressTaken"/> fact), a pointer T can't be the type arg of
    /// Unsafe.AsPointer / Volatile.* (CS0306) nor have a bare <c>&amp;</c> of a moveable
    /// static field (CS0212), so the slot is an <c>nint</c>. The <see cref="Symbol.IsGlobal"/>
    /// guard scopes this to file-scope/function-static fields: locals are emitted as real
    /// pointers (no moveable-field/Unsafe constraints apply), so an address-taken local —
    /// which now also carries the neutral fact — must NOT be reinterpreted as <c>nint</c>.</summary>
    private static bool NintStorage(Symbol s) => s.AddressTaken && s.IsGlobal && s.Type.IsPointerLowered;

    /// <summary>True when an lvalue is a pointer global whose backing field codegen
    /// declared as <c>nint</c>; such a slot is reinterpreted directly, not through
    /// its address.</summary>
    private static bool StoredAsNint(CExpr e) => e switch
    {
        VarRef v => NintStorage(v.Sym),
        Paren p => StoredAsNint(p.Inner),
        _ => false,
    };

    /// <summary>The bare (un-fenced) C# text of an lvalue — for an assignment
    /// target, an <c>&amp;</c> operand, or the <c>ref</c> argument of
    /// Volatile.Read/Write, where the volatile-read wrap must NOT be applied.</summary>
    private string BareLValue(CExpr e) => e switch
    {
        Paren p => BareLValue(p.Inner),
        VarRef v => GlobalName(v.Sym),
        Member m => $"{Sub(m.Base, PPostfix)}{(m.Arrow ? "->" : ".")}{DotCC.EmitHelpers.Id(m.Field)}",
        Index ix => $"{Sub(ix.Base, PPostfix)}[{Expr(DecayEnum(ix.Idx))}]",
        Unary { Op: UnOp.Deref } u => $"*{Sub(u.Operand, PUnary)}",
        _ => Expr(e),
    };

    // Global field names shadowed by an emitted TYPE name — C keeps tags and
    // ordinary identifiers in separate namespaces (`enum sexp_number_types` +
    // `static int sexp_number_types[]` coexist; chibi bignum.c), but in C# the
    // unqualified name resolves to the type (CS0119). Qualifying the field
    // restores the ordinary-namespace reading.
    private HashSet<string> _typeShadowedGlobals = new(StringComparer.Ordinal);

    /// <summary>The spelling of a variable reference: the bare TargetName, or
    /// <c>DotCcGlobals.</c>-qualified when an emitted type name shadows it.</summary>
    private string GlobalName(Symbol s) =>
        s.IsGlobal && _typeShadowedGlobals.Contains(s.TargetName)
            ? "DotCcGlobals." + s.TargetName
            : s.TargetName;

    /// <summary>Render <paramref name="value"/> for storage into a
    /// <paramref name="target"/>-typed sink, inserting any cast C# needs.</summary>
    private string Coerced(CExpr value, CType target) =>
        TryCoerceCast(value, target, out var t) ? t : Expr(value);

    /// <summary>Coerce a call argument to its parameter type, falling back to the
    /// argument rendered at assignment precedence (so a bare comma operator can't
    /// be misread as an argument separator).</summary>
    private string CoercedArg(CExpr value, CType target) =>
        TryCoerceCast(value, target, out var t) ? t : Sub(value, PAssign);

    /// <summary>True (with the coerced text) when storing <paramref name="value"/>
    /// into <paramref name="target"/> needs an explicit C# cast C performs
    /// implicitly; false when the value stores as-is.</summary>
    private bool TryCoerceCast(CExpr value, CType target, out string text)
    {
        text = "";
        var tgt = target.Unqualified;
        var src = value.Type.Unqualified;

        // C's null pointer constant — an integer constant 0 — becomes C# `null`
        // where a POINTER is expected (C# won't convert int 0 to a pointer).
        // A function pointer (bare CType.Func — lowered delegate*) is a pointer
        // for this purpose too: chibi's opcode tables store NULL handlers.
        if (tgt is CType.Pointer or CType.Func && TryConstInt(value, out var z) && z == 0)
        {
            text = "null";
            return true;
        }
        // Enum sink: C lets an integer (or a different enum) store into an enum
        // freely; C# needs the cast explicit — `(EnumName)(value)`. A value already
        // of the same enum stores as-is.
        if (tgt is CType.Enum te && Cs(src) != Cs(te) && (src.IsArithmetic || src is CType.Enum))
        {
            text = $"({Cs(te)})({Sub(value, PUnary)})";
            return true;
        }
        // Enum source into an integer sink: C# requires the explicit `(int)` decay.
        if (tgt is CType.Prim { Integer: true } && src is CType.Enum)
        {
            text = $"({Cs(tgt)})({Sub(value, PUnary)})";
            return true;
        }
        // void* → T* (e.g. malloc's result): C# makes T*→void* implicit but requires
        // the reverse cast explicitly. Skip void*→void* (same type).
        if (tgt is CType.Pointer && src is CType.Pointer { Pointee: CType.VoidType }
            && Cs(tgt) != Cs(src))
        {
            text = $"({Cs(tgt)})({Expr(value)})";
            return true;
        }
        // Float-involved narrowing C# won't do implicitly but C converts at any
        // store: double→float, floating→integer. Widenings (anything→double,
        // integer→float) stay implicit in C# and pass through.
        if (src is CType.Prim sp && tgt is CType.Prim tp && (!sp.Integer || !tp.Integer))
        {
            var s = Cs(src);
            var t2 = Cs(tgt);
            if (s == t2) { return false; }
            var implicitOk = (t2 == "double" && (s == "float" || IsIntegerCs(s)))
                          || (t2 == "float" && IsIntegerCs(s));
            if (implicitOk) { return false; }
            text = $"({t2})({Expr(value)})";
            return true;
        }
        // Integer narrowing / sign change C# won't do implicitly.
        if (src is CType.Prim { Integer: true } && tgt is CType.Prim { Integer: true })
        {
            var s = Cs(src);
            var t2 = Cs(tgt);
            if (s == t2 || !IsIntegerCs(s) || !IsIntegerCs(t2)) { return false; }
            // A bare int constant that fits the target needs no cast (C#'s implicit
            // constant-expression conversion applies, but only from a literal int).
            if (value is LitInt { Value: { } cv } && s == "int" && ConstFitsTarget(cv, t2))
            {
                return false;
            }
            if (CsImplicitInt(s, t2)) { return false; }   // C# widens for free
            // -Wconversion: a genuine width narrowing (wider→narrower) may lose data.
            // A same-width sign change is -Wsign-conversion (out of scope), not flagged.
            if (_convGate != null && IntWidth(t2) is int tw && IntWidth(s) is int sw && tw < sw)
            {
                _convGate.Narrowing(s, t2, _currentFnName, value.Pos.Line);
            }
            var cast = $"({t2})({Expr(value)})";
            // An out-of-range CONSTANT cast is CS0221 unless wrapped in unchecked.
            if (TryConstInt(value, out var k) && !ConstFitsTarget(k, t2))
            {
                cast = $"unchecked({cast})";
            }
            text = cast;
            return true;
        }
        return false;
    }

    private static bool IsIntegerCs(string t) => IntWidth(t) is not null;

    // Byte width of a lowered C# integer type (LP64 — long/pointer = 8). C# `char`
    // (the lowering of C11 char16_t) is a 16-bit numeric type — without it here the
    // coercion machinery treats a char-typed sink as non-integer and SILENTLY DROPS
    // the narrowing cast (CS0266 at C# compile time).
    private static int? IntWidth(string t) => t switch
    {
        "byte" or "sbyte" => 1,
        "short" or "ushort" or "char" => 2,
        "int" or "uint" => 4,
        "long" or "ulong" or "nint" or "nuint" => 8,
        _ => null,
    };

    // Whether an integer CONSTANT fits the target type's range (C#'s implicit
    // constant-expression conversion accepts these with no cast).
    private static bool ConstFitsTarget(long v, string tgt) => tgt switch
    {
        "byte" => v >= 0 && v <= 255,
        "sbyte" => v >= -128 && v <= 127,
        "short" => v >= -32768 && v <= 32767,
        "ushort" => v >= 0 && v <= 65535,
        // NOTE: C# `char` is intentionally ABSENT — unlike byte/short/ushort, C#
        // has NO implicit int-constant→char conversion (§10.2.11 omits char), so a
        // constant stored into a char16_t sink still needs an explicit (char) cast.
        "int" or "nint" => v >= int.MinValue && v <= int.MaxValue,
        "long" => true,
        "uint" => v >= 0 && v <= uint.MaxValue,
        "ulong" or "nuint" => v >= 0,
        _ => false,
    };

    // True when C# IMPLICITLY converts integer `src` to `tgt`: an unsigned source
    // fits any strictly-wider type; a signed source fits only a strictly-wider
    // SIGNED type. Equal types are handled by the caller. Conservative — never
    // claims a conversion that doesn't exist (a false "no" is a harmless cast).
    private static bool CsImplicitInt(string src, string tgt)
    {
        if (IntWidth(src) is not int sw || IntWidth(tgt) is not int tw) { return false; }
        // C# never implicitly converts a (non-constant) integer TO `char`, so a store
        // into a char16_t sink always needs an explicit cast. (Treating `char` as a
        // plain unsigned source below may emit a harmless redundant `(ushort)` for the
        // genuinely-implicit `char → ushort` — accepted, see the plan's honest limits.)
        if (tgt is "char") { return false; }
        var srcUnsigned = src is "byte" or "ushort" or "uint" or "ulong" or "nuint" or "char";
        var tgtUnsigned = tgt is "byte" or "ushort" or "uint" or "ulong" or "nuint";
        return srcUnsigned ? tw > sw : (!tgtUnsigned && tw > sw);
    }

    // Fold a simple integer-constant expression (literal, parens, unary +/-/~) so a
    // store coercion can range-check it for the unchecked / null-pointer rules.
    private static bool TryConstInt(CExpr e, out long v)
    {
        switch (e)
        {
            case LitInt { Value: { } lv }: v = lv; return true;
            case EnumConstRef ec: v = ec.Sym.ConstValue; return true;
            case Paren p: return TryConstInt(p.Inner, out v);
            case Unary u when TryConstInt(u.Operand, out var ov):
                switch (u.Op)
                {
                    case UnOp.Neg: v = -ov; return true;
                    case UnOp.Plus: v = ov; return true;
                    case UnOp.BitNot: v = ~ov; return true;
                }
                break;
        }
        v = 0;
        return false;
    }

    /// <summary>Render one <c>case</c> value. C requires an integer constant
    /// expression, but chibi spells immediates through pointer-cast macros
    /// (<c>case (sexp_uint_t)SEXP_VOID:</c> with <c>SEXP_VOID =
    /// ((sexp)((2&lt;&lt;8)+62))</c>) — rendered literally, the pointer cast is
    /// non-constant C# (CS9135). When the expression contains a pointer cast,
    /// fold it to its value (a pointer cast is value-preserving; an integer
    /// cast truncates to its width) and emit the literal instead.</summary>
    private string CaseValue(CExpr ce, CType subject)
    {
        if (ContainsPointerCast(ce) && FoldConst(ce, out var v))
        {
            var st = subject.Unqualified is CType.Prim { Integer: true } sp ? Cs(sp) : "long";
            return $"unchecked(({st})({v.ToString(System.Globalization.CultureInfo.InvariantCulture)}))";
        }
        return Expr(DecayEnum(ce));
    }

    private static bool ContainsPointerCast(CExpr e) => e switch
    {
        Cast c => c.Target.Unqualified is CType.Pointer || ContainsPointerCast(c.Operand),
        Paren p => ContainsPointerCast(p.Inner),
        Unary u => ContainsPointerCast(u.Operand),
        Binary b => ContainsPointerCast(b.Left) || ContainsPointerCast(b.Right),
        _ => false,
    };

    /// <summary>Full constant fold over the case-label grammar (literals, enum
    /// constants, unary <c>+ - ~</c>, the binary integer operators, casts) —
    /// wider than <see cref="TryConstInt"/>, which deliberately stays cast-opaque
    /// for the range-check rules.</summary>
    private static bool FoldConst(CExpr e, out long v)
    {
        switch (e)
        {
            case LitInt { Value: { } lv }: v = lv; return true;
            case EnumConstRef ec: v = ec.Sym.ConstValue; return true;
            case Paren p: return FoldConst(p.Inner, out v);
            case Cast c when FoldConst(c.Operand, out v):
                if (c.Target.Unqualified is CType.Prim { Integer: true } tp) { v = TruncateTo(v, tp); }
                return true; // pointer (and other reinterpret) casts preserve the value
            case Unary u when FoldConst(u.Operand, out v):
                switch (u.Op)
                {
                    case UnOp.Neg: v = -v; return true;
                    case UnOp.Plus: return true;
                    case UnOp.BitNot: v = ~v; return true;
                }
                break;
            case Binary b when FoldConst(b.Left, out var l) && FoldConst(b.Right, out var r):
                switch (b.Op)
                {
                    case BinOp.Add: v = l + r; return true;
                    case BinOp.Sub: v = l - r; return true;
                    case BinOp.Mul: v = l * r; return true;
                    case BinOp.Div when r != 0: v = l / r; return true;
                    case BinOp.Mod when r != 0: v = l % r; return true;
                    case BinOp.Shl: v = l << (int)r; return true;
                    case BinOp.Shr: v = l >> (int)r; return true;
                    case BinOp.BitAnd: v = l & r; return true;
                    case BinOp.BitOr: v = l | r; return true;
                    case BinOp.BitXor: v = l ^ r; return true;
                }
                break;
        }
        v = 0;
        return false;
    }

    private static long TruncateTo(long v, CType.Prim p) => p.Bytes switch
    {
        1 => p.Signed ? (sbyte)v : (byte)v,
        2 => p.Signed ? (short)v : (ushort)v,
        4 => p.Signed ? (int)v : (uint)v,
        _ => v,
    };

    /// <summary>Render a declaration in statement position. When every declarator
    /// shares a C# type it's one C# declaration (<c>int a = 0, b = 1;</c>); when
    /// the per-declarator types differ (C's <c>int *a, b;</c> — a is a pointer, b
    /// is not — which C# can't express in one statement since <c>int* a, b</c>
    /// makes both pointers) it splits into one statement per declarator.</summary>
    private void EmitDeclStmt(StringBuilder sb, DeclStmt d, string pad)
    {
        if (d.Decls.Count == 0) { return; }
        var firstCs = Cs(d.Decls[0].Sym.Type);
        if (d.Decls.All(e => Cs(e.Sym.Type) == firstCs))
        {
            sb.Append(pad).Append(DeclInline(d)).Append(";\n");
            return;
        }
        foreach (var e in d.Decls)
        {
            var init = e.Init is { } i ? Coerced(i, e.Sym.Type) : "default";
            sb.Append(pad).Append($"{Cs(e.Sym.Type)} {e.Sym.TargetName} = {init};\n");
        }
    }

    // A single C# declaration (shared element type), used in statement position
    // when all declarators agree and in `for`-initializer position.
    private string DeclInline(DeclStmt d)
    {
        var type = d.Decls.Count > 0 ? Cs(d.Decls[0].Sym.Type) : "int";
        var parts = d.Decls.Select(e => e.Init is { } init
            ? $"{e.Sym.TargetName} = {Coerced(init, e.Sym.Type)}"
            : $"{e.Sym.TargetName} = default");
        return $"{type} {string.Join(", ", parts)}";
    }

    // ---- expressions -----------------------------------------------------
    //
    // Precedence-driven printing (NOT paren-wrap-then-strip — that was string
    // surgery on emitted text, the very coupling the IR exists to remove). Each
    // node reports the precedence of its result via Render(); a child is
    // parenthesized only when the surrounding context binds tighter than the
    // child's own operator. So a statement-position expression renders with no
    // spurious outer parens (`i++`, not `(i++)`) and nothing ever needs stripping.

    // C operator precedences, higher = binds tighter. Match C's table so the
    // emitted C# groups exactly as the C source did.
    private const int PComma = 1, PAssign = 2, PCond = 3, PLogOr = 4, PLogAnd = 5,
        PBitOr = 6, PBitXor = 7, PBitAnd = 8, PEq = 9, PRel = 10, PShift = 11,
        PAdd = 12, PMul = 13, PUnary = 14, PPostfix = 15, PPrimary = 16;

    /// <summary>Render <paramref name="e"/> for top-level (statement) position —
    /// no outer parens.</summary>
    private string Expr(CExpr e) => Render(e).Text;

    /// <summary>Render <paramref name="e"/> as a sub-expression that must bind at
    /// least as tightly as <paramref name="minPrec"/>; wrap it in parens only when
    /// it doesn't.</summary>
    private string Sub(CExpr e, int minPrec)
    {
        var (text, prec) = Render(e);
        return prec < minPrec ? $"({text})" : text;
    }

    /// <summary>Render an expression to (text, precedence-of-result). The text
    /// carries NO redundant outer parens; callers add them via <see cref="Sub"/>
    /// when their context needs them.</summary>
    private (string Text, int Prec) Render(CExpr e)
    {
        switch (e)
        {
            case LitInt i: return (_target.RenderIntLit(i), PPrimary);
            case LitBool b: return (b.Value ? "true" : "false", PPrimary);
            case LitFloat f: return (_target.RenderFloatLit(f), PPrimary);
            case LitStr s: return (DotCC.EmitHelpers.EncodeStringLiteral(s.Segments), PPrimary);
            case LitU16Str s: return (DotCC.EmitHelpers.EncodeU16StringLiteral(s.Segments), PPrimary);
            case NullPtr: return ("null", PPrimary);
            // Zig `a orelse b` over a value optional (`T?`) → C#'s `??` (single-eval left,
            // lazy right). The right is coerced to the payload type so `maybe_u8 orelse 0`
            // is `a ?? (byte)0`, not a `byte?`/`int` mismatch. Parenthesized → safe anywhere.
            case NullCoalesce nc:
                return ($"({Sub(nc.Left, PUnary)} ?? {Coerced(nc.Right, nc.Type)})", PPrimary);
            // Zig error unions (Milestone B2). `return e;` in a `!T` fn → ErrUnion<P>.Ok(e);
            // a `!void` success carries no payload (`return;` / fall-off) → Ok(default). The
            // payload type rides on the node's CType.ErrorUnion, so the backend reads it back.
            case ErrUnionOk ok:
            {
                var eu = (CType.ErrorUnion)ok.Type;
                var ts = eu.Payload is CType.VoidType ? "Unit" : Cs(eu.Payload);
                var arg = ok.Payload is null ? "default"
                        : eu.Payload is CType.VoidType ? Expr(ok.Payload)  // a Unit-valued `try`-of-!void
                        : Coerced(ok.Payload, eu.Payload);
                return ($"ErrUnion<{ts}>.Ok({arg})", PPrimary);
            }
            // `return error.Foo;` → ErrUnion<P>.Err(code) (code from the flat global set).
            case ErrUnionErr err:
            {
                var eu = (CType.ErrorUnion)err.Type;
                var ts = eu.Payload is CType.VoidType ? "Unit" : Cs(eu.Payload);
                return ($"ErrUnion<{ts}>.Err({err.Code})", PPrimary);
            }
            // `try e` → ErrUnion.Try(e): the payload on success, else throw ZigErrorReturn
            // (caught at the enclosing `!T` function's emitted try/catch boundary — see Func).
            case ZigTry t:
                return ($"ErrUnion.Try({Expr(t.Inner)})", PPrimary);
            // `u catch fallback` → ErrUnion.Catch<P>(u, fallback): the payload on success,
            // else the (side-effect-free) fallback. The type argument is explicit so a fitting
            // constant fallback (`f() catch 0`) converts to the payload implicitly — otherwise
            // C# infers T from both args and a bare `0` (int) clashes with the union's payload.
            case ZigCatch c:
            {
                var cts = c.Type is CType.VoidType ? "Unit" : Cs(c.Type);
                return ($"ErrUnion.Catch<{cts}>({Expr(c.Union)}, {Coerced(c.Fallback, c.Type)})", PPrimary);
            }
            // A Zig slice fat pointer `{ ptr, len }` (Milestone E): array→slice coercion or
            // `a[lo..hi]` → new Slice<T>(ptr, len) (ConstSlice<T> for a `[]const T`). Len is
            // coerced to the ctor's `ulong`.
            case SliceNew sn:
            {
                var name = sn.Const ? "ConstSlice" : "Slice";
                return ($"new {name}<{Cs(sn.Element.Unqualified)}>({Expr(sn.Ptr)}, {Coerced(sn.Len, CType.ULong)})", PPrimary);
            }
            // A Zig allocator `a.alloc(T, n)` (Milestone F). Receiver null → the DEVIRTUALIZED
            // C-heap default: a direct `ZigAlloc.AllocCHeap<T>(n, oom)` (→ Libc.malloc, no vtable).
            // Non-null → the indirect vtable dispatch via the runtime Allocator's `Alloc<T>`. The
            // count coerces to the helper's `ulong`.
            case AllocCall ac:
            {
                var elem = Cs(ac.Element.Unqualified);
                var n = Coerced(ac.Count, CType.ULong);
                return ac.Receiver is null
                    ? ($"ZigAlloc.AllocCHeap<{elem}>({n}, {ac.OomCode})", PPostfix)
                    : ($"{Sub(ac.Receiver, PPostfix)}.Alloc<{elem}>({n}, {ac.OomCode})", PPostfix);
            }
            // A Zig allocator `a.free(slice)` (Milestone F). Receiver null → devirtualized direct
            // `ZigAlloc.FreeCHeap<T>(slice)` (→ Libc.free); non-null → indirect `recv.Free<T>(slice)`.
            case FreeCall fc:
            {
                var elem = Cs(fc.Element.Unqualified);
                return fc.Receiver is null
                    ? ($"ZigAlloc.FreeCHeap<{elem}>({Expr(fc.SliceExpr)})", PPostfix)
                    : ($"{Sub(fc.Receiver, PPostfix)}.Free<{elem}>({Expr(fc.SliceExpr)})", PPostfix);
            }
            // A Zig tuple literal `.{ a, b }` (Milestone G) → `new System.ValueTuple<T1, …>(e1, …)`,
            // each element coerced to its declared element type. Arity-uniform (incl. 1) — the
            // `(a, b)` shorthand has no 1-tuple form, so the explicit `ValueTuple` ctor is used.
            case TupleNew tn:
            {
                var tt = (CType.Tuple)tn.TupleType.Unqualified;
                var typeArgs = string.Join(", ", tt.Elements.Select(e => Cs(e.Unqualified)));
                var args = string.Join(", ", tn.Elements.Select((e, i) => Coerced(e, tt.Elements[i])));
                return ($"new System.ValueTuple<{typeArgs}>({args})", PPrimary);
            }
            // A Zig tuple index `t[N]` (Milestone G) → `<tuple>.Item{N+1}` (ValueTuple's 1-based
            // fields). Also the per-binder read a destructure desugars into.
            case TupleIndex ti:
                return ($"{Sub(ti.Tuple, PPostfix)}.Item{ti.Index + 1}", PPostfix);
            // A bare unresolved identifier: the backend escapes the raw name.
            case NameRef nr: return (DotCC.EmitHelpers.Id(nr.RawName), PPrimary);
            // An enumerator of a real enum: EnumName.Member (member access). If a
            // global variable shadows the enum TYPE name (C keeps `enum E` in the
            // tag namespace and a variable `E` in the ordinary namespace; `using
            // static DotCcGlobals` then binds the bare name to the variable —
            // chibi's `enum sexp_opcode_names` vs its `const char** sexp_opcode_names`
            // table), force the top-level type with `global::` so the enumerator
            // resolves against the enum, not the field.
            case EnumConstRef ec:
            {
                var enumTy = Cs(ec.Sym.Type.Unqualified);
                if (_typeShadowedGlobals.Contains(enumTy)) { enumTy = "global::" + enumTy; }
                return ($"{enumTy}.{DotCC.EmitHelpers.Id(ec.Sym.Name)}", PPostfix);
            }
            // A bare function name used as a value decays to its address — C#
            // needs the explicit `&` to form a delegate* (C allows the bare name).
            // A pointer global stored as `nint` (its address was taken): a value read
            // fences through Volatile/Atomic on the nint field, then casts back to the
            // pointer type. Assignment targets / `&` / ref-args use BareLValue (raw
            // field), so they never hit this read-cast.
            case VarRef v when NintStorage(v.Sym):
                return v.Type.IsAtomic ? ($"({Cs(v.Type)})Atomic.Load(ref {v.Sym.TargetName})", PUnary)
                     : v.Type.IsVolatile ? ($"({Cs(v.Type)}){VolatileRead(v.Sym.TargetName)}", PUnary)
                     : ($"({Cs(v.Type)}){v.Sym.TargetName}", PUnary);
            case VarRef v: return v.Sym.Kind == SymKind.Func
                ? ($"&{v.Sym.TargetName}", PUnary)
                : QualifiedRead(v, GlobalName(v.Sym), PPrimary);
            case IndirectCall ic:
                return ($"{Sub(ic.Callee, PPostfix)}({string.Join(", ", ic.Args.Select(a => Sub(a, PAssign)))})", PPostfix);
            case Paren p: return Render(p.Inner); // explicit C parens are redundant; precedence re-adds as needed
            case Cast c: return RenderCast(c);
            case BitCast bc:
                // Zig `@bitCast` — same-size bit reinterpret. `Unsafe.BitCast<TFrom, TTo>` is the
                // AOT-clean primitive (it static-asserts the size match); the source type is the
                // operand's lowered type, the destination the result-location sink.
                return ($"System.Runtime.CompilerServices.Unsafe.BitCast<{Cs(bc.Operand.Type)}, {Cs(bc.Target)}>({Sub(bc.Operand, PAssign)})", PPrimary);
            case SizeOfExpr so:
                // C's `sizeof` yields `size_t` — unsigned, `ulong` in dotcc's
                // model — but C#'s `sizeof` operator is `int`. Emit the `(ulong)`
                // cast so the usual-arithmetic reconcile treats a sizeof-bearing
                // expression as unsigned (a bare int would be CS0034 against a
                // `ulong`). An array lowered to a pointer, so C# `sizeof` would
                // measure the pointer — emit the true C size (recursing through the
                // dimensions of a multi-dim array).
                return ($"((ulong)({SizeofText(so.Of)}))", PPrimary);
            case OffsetOf o:
            {
                // .NET has no offsetof operator, and the address-through-a-null
                // idiom (`&((T*)null)->m`) FAULTS — C#'s `->` null-checks the base.
                // Compute it from a stack `default` instance instead: the member's
                // address minus the base address, honoring the real .NET blittable
                // layout (alignment included). `__t` lives inside the lambda (not
                // captured), so `&__t` needs no `fixed`. A primitive `fixed`-buffer
                // member's access already yields its address (no `&`); a scalar
                // member uses `&`.
                var m = string.Join(".", o.Path.Select(DotCC.EmitHelpers.Id));
                // A member that lowers to a C# `fixed` buffer (primitive-element
                // array) already yields its own address — no `&` (would be CS0211).
                var decays = o.MemberType?.Unqualified is CType.Array fa && IsFixedBufferType(Cs(fa.Element));
                var memberAddr = decays ? $"(byte*)__t.{m}" : $"(byte*)&__t.{m}";
                return ($"((System.Func<ulong>)(() => {{ {Cs(o.StructType)} __t = default; return (ulong)({memberAddr} - (byte*)&__t); }}))()", PPrimary);
            }
            case Index ix:
            {
                // A PARTIAL subscript of a multi-dimensional array — the result is
                // still an array — decays to a flat row pointer: `base + idx*stride`,
                // where stride is the number of flat scalar elements per row. A FULL
                // subscript (scalar/struct result) indexes the flat pointer directly.
                if (ix.Type.Unqualified is CType.Array)
                {
                    return ($"{Sub(ix.Base, PAdd)} + {Sub(DecayEnum(ix.Idx), PMul)} * {FlatCount(ix.Type)}", PAdd);
                }
                var t = $"{Sub(ix.Base, PPostfix)}[{Expr(DecayEnum(ix.Idx))}]";
                return QualifiedRead(ix, t, PPostfix);
            }
            case Member m:
            {
                var dot = $"{Sub(m.Base, PPostfix)}{(m.Arrow ? "->" : ".")}{DotCC.EmitHelpers.Id(m.Field)}";
                // A non-primitive array member is stored as an [InlineArray]; its
                // access decays to the element pointer `(T*)&field` (C#'s InlineArray
                // indexer bounds-checks, but a C array over-indexes into the tail),
                // restoring both over-indexing and array→pointer decay.
                if (m.Type.Unqualified is CType.Array arr && !IsFixedBufferType(Cs(arr.FlatElement)))
                {
                    return ($"({Cs(m.Type)})&{dot}", PUnary);
                }
                return QualifiedRead(m, dot, PPostfix);
            }
            case StructInit si: return (StructInitText(si), PPrimary);
            case StackArray sa:
            {
                // An array compound literal `(T[]){…}` is a `stackalloc` — directly
                // assignable to a `T*` only in initializer position. To make it work
                // in ANY expression position (call argument, return, …), hoist it to
                // a block-local pointer temp — C's block-scoped automatic storage —
                // and reference that temp. (Nested literals hoist first: their Expr
                // runs while building this element list, so their temps precede this
                // one in `_pending`.) Outside a hoistable context (e.g. a loop
                // condition, re-evaluated each iteration) fall back to the inline
                // stackalloc, which still binds in a pointer-initializer.
                var lit = $"stackalloc {Cs(sa.Element)}[]{{ {string.Join(", ", sa.Elems.Select(Expr))} }}";
                if (!_canHoist) { return (lit, PPrimary); }
                var name = $"__cl{_clCounter++}";
                _pending.Add($"{Cs(sa.Element)}* {name} = {lit}");
                return (name, PPrimary);
            }
            case DefaultLit: return ($"default({Cs(e.Type)})", PPrimary);
            // A promoted malloc → a zero-initialized stack struct value.
            case StackNew sn: return ($"new {Cs(sn.StructType)}()", PPrimary);
            case PinnedArray pa: return (PinnedArrayText(pa), PPrimary);
            case VaArgGet va:
                // va_arg(ap, T): a scalar reads via (T)ap.Next(); a pointer via
                // (T)ap.NextPtr() (NextPtr returns void*, then a standard cast to T*).
                return va.Target.Unqualified is CType.Pointer or CType.Func or CType.Array
                    ? ($"({Cs(va.Target)})({Sub(va.Ap, PPostfix)}.NextPtr())", PUnary)
                    : ($"({Cs(va.Target)})({Sub(va.Ap, PPostfix)}.Next())", PUnary);
            case Call c: return (CallText(c), PPostfix);
            case CondExpr t:
                // C-truthy condition, arms isolated by `?`/`:`. The arms are
                // conditional, so they render without hoisting (the condition always
                // evaluates and may hoist). C reconciles arithmetic arms to a common
                // type (the usual conversions — CondExpr.Type); C# requires both arms
                // to already share a type, so an arm that differs is cast to it (e.g.
                // `cond ? sizeT : intExpr` → the int arm casts to the ulong result).
                {
                    // A comparison / logical arm is C-typed `int` but RENDERS as the
                    // CBool value type (CBool→int carries it elsewhere). A plain-int
                    // sibling then gives the two arms different C# types though their
                    // CType agrees, so the arithmetic-mismatch coercion below (keyed on
                    // CType) doesn't fire — and a target-typed `Cond.B(cond ? … : …)`
                    // finds both Cond.B(int) and Cond.B(CBool) viable (CS0121). When the
                    // arms differ in CBool-rendering, decay the CBool one to the int
                    // result type so both arms share a C# type. (chibi srfi/69 hash.c:
                    // `sexp_pointerp(o) ? (tag == SYMBOL) : !sexp_fixnump(o)`.)
                    static bool CBoolRendered(CExpr e)
                    {
                        while (e is Paren p) { e = p.Inner; } // parens don't change the rendered type
                        return e is Binary { Op: BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt
                            or BinOp.Le or BinOp.Ge or BinOp.LogAnd or BinOp.LogOr };
                    }
                    var cboolMismatch = t.Type.IsArithmetic && CBoolRendered(t.Then) != CBoolRendered(t.Else);
                    string Arm(CExpr a) => NoHoist(() =>
                        cboolMismatch && CBoolRendered(a)
                            ? CoercionCast(a, Cs(t.Type))
                            : t.Type.IsArithmetic && a.Type.IsArithmetic && Cs(a.Type.Unqualified) != Cs(t.Type)
                            ? CoercionCast(a, Cs(t.Type))
                            // A function-designator arm is an untyped method group
                            // (`cond ? &strcasecmp : &strcmp` — chibi's fold-case
                            // reader); C# needs both arms pinned to the common
                            // fn-ptr type before the ternary (or a call) can bind.
                            : IsFnPtrType(t.Type) && UnparenIsFunc(a)
                            ? $"({Cs(t.Type)})({Expr(a)})"
                            : Expr(a));
                    return ($"(Cond.B({Expr(DecayEnum(t.Cond))}) ? {Arm(t.Then)} : {Arm(t.Else)})", PPrimary);
                }
            case SwitchExpr sw:
                {
                    // C#'s native switch expression. Each arm's labels are constant patterns
                    // (joined with `or` for a multi-value Zig prong); a null-label arm is the `_`
                    // default (Zig's `else`). Each arm value is coerced to the result type so the
                    // arms share a C# type (C# requires it). The subject is decayed (an enum subject
                    // matches its `EnumType.member` constant-pattern labels). Self-parenthesized and
                    // returned as a primary (like the ternary), so it never needs outer parens.
                    // One label → a constant pattern, or `>= lo and <= hi` for an inclusive range
                    // (Zig `lo...hi`); a multi-value prong joins them with `or`.
                    string LabelPat(SwitchLabel l) =>
                        l.HiExpr is { } hi
                            ? $">= {Sub(DecayEnum(l.CaseExpr!), PCond)} and <= {Sub(DecayEnum(hi), PCond)}"
                            : Sub(DecayEnum(l.CaseExpr!), PCond);
                    string ArmText(SwitchExprArm a) => NoHoist(() =>
                    {
                        var val = Coerced(a.Value, sw.Type);
                        return a.Labels is null
                            ? $"_ => {val}"
                            : $"{string.Join(" or ", a.Labels.Select(LabelPat))} => {val}";
                    });
                    return ($"({Sub(DecayEnum(sw.Subject), PPostfix)} switch {{ {string.Join(", ", sw.Arms.Select(ArmText))} }})", PPrimary);
                }
            case CommaSeq cs:
                return (string.Join(", ", cs.Items.Select(it => Sub(it, PAssign))), PComma);
            case CommaOp co:
                // At a hoistable statement position, push the leading (side-effect)
                // operands as statements and become just the value operand (closure-
                // free — no CS1686 on a captured local's address). Otherwise fall back
                // to the inline tuple / IIFE value form.
                if (_canHoist)
                {
                    for (var i = 0; i < co.Items.Count - 1; i++) { _pending.Add(RenderStmtExpr(co.Items[i])); }
                    return Render(co.Items[^1]);
                }
                return (CommaValue(co), PPrimary);
            case Assign a when a.Target.Type.IsAtomic:
                {
                    // An atomic lvalue stores seq-cst (Atomic.Store, returns the stored
                    // value); a compound op maps to the *Fetch helper that returns the
                    // NEW value (C's `x op= n` result). The rhs is cast to the lvalue
                    // type so the generic Atomic.* call infers one element type.
                    var lv = BareLValue(a.Target);
                    var cs = Cs(a.Target.Type.Unqualified);
                    var text = a.CompoundOp is { } cop
                        ? $"Atomic.{AtomicFetchHelper(cop, cs)}(ref {lv}, ({cs})({Sub(a.Value, PAssign)}))"
                        : $"Atomic.Store(ref {lv}, ({cs})({Sub(a.Value, PAssign)}))";
                    return (text, PPrimary);
                }
            case Assign a when a.Target.Type.IsVolatile:
                {
                    // A volatile lvalue stores through Volatile.Write(ref lv, …); a
                    // compound op is a fenced read-modify-write. (Returns void, so —
                    // like the legacy — only valid in statement position.)
                    var lv = BareLValue(a.Target);
                    // A pointer/fn-ptr volatile target can't be a Volatile.Write<T>
                    // type arg (CS0306): reinterpret the slot as `nint` (a global
                    // stored-as-nint already IS a nint field; otherwise via address)
                    // and cast the stored value to nint.
                    if (a.Target.Type.IsPointerLowered)
                    {
                        var pty = Cs(a.Target.Type.Unqualified);
                        var slot = StoredAsNint(a.Target) ? lv : $"*(nint*)&{lv}";
                        var pstored = a.CompoundOp is { } pcop
                            ? $"({pty}){VolatileRead(slot)} {BinSym(pcop)} {Sub(a.Value, Prec(pcop) + 1)}"
                            : Coerced(a.Value, a.Target.Type);
                        return ($"global::System.Threading.Volatile.Write(ref {slot}, (nint)({pstored}))", PPrimary);
                    }
                    var stored = a.CompoundOp is { } cop
                        ? $"{VolatileRead(lv)} {BinSym(cop)} {Sub(a.Value, Prec(cop) + 1)}"
                        : Coerced(a.Value, a.Target.Type);
                    return ($"global::System.Threading.Volatile.Write(ref {lv}, {stored})", PPrimary);
                }
            case Assign a when StoredAsNint(a.Target):
                {
                    // A pointer global stored as `nint`: write the raw field and cast
                    // the value to nint (a value read of the field would re-add a `(T)`
                    // cast, which isn't assignable). A compound op reads back as `(T)`.
                    var lv = BareLValue(a.Target);
                    var pty = Cs(a.Target.Type.Unqualified);
                    var val = a.CompoundOp is { } cop
                        ? $"({pty}){lv} {BinSym(cop)} {Sub(a.Value, Prec(cop) + 1)}"
                        : Coerced(a.Value, a.Target.Type);
                    return ($"{lv} = (nint)({val})", PAssign);
                }
            case Assign { CompoundOp: { } cop } a:
                {
                    // C's `x op= y` evaluates `x op y` under the usual arithmetic
                    // conversions, then stores back into x. C# applies the store-back
                    // narrowing implicitly, but rejects the *operation* when the operand
                    // types don't share one (e.g. `ulong op= int` — CS0034, since dotcc
                    // types strlen et al. as int). Cast the RHS to the common type — but
                    // only when that common type isn't already the RHS's (so `short +=
                    // int` keeps the int and lets C# narrow), and never for a shift
                    // (whose count is independent) or pointer/non-arithmetic arithmetic.
                    var rhs = Sub(a.Value, PAssign);
                    if (cop is not (BinOp.Shl or BinOp.Shr)
                        && a.Target.Type.IsArithmetic && a.Value.Type.IsArithmetic)
                    {
                        // C#'s compound assignment only narrows the RESULT back when the
                        // RHS is implicitly convertible to the target (12.21.4) — so
                        // `int += long`, `long |= ulong`, `byte |= intExpr` are all
                        // CS0266/CS0019 where C converts freely. Cast the RHS to the
                        // target type: for the modular ops that land here (the shifts are
                        // excluded above) truncate-first equals truncate-after, so C's
                        // result is preserved. (Known hole: a compound `/=` or `%=` whose
                        // RHS is wider than the target divides in the narrower type where
                        // C divides wide-then-truncates — not yet observed in practice.)
                        var tt = Cs(a.Target.Type.Unqualified);
                        var vt = Cs(a.Value.Type.Unqualified);
                        var fits = CsImplicitInt(vt, tt)
                            || (tt == "double" && (vt == "float" || IsIntegerCs(vt)))
                            || (tt == "float" && IsIntegerCs(vt));
                        if (tt != vt && !fits)
                        {
                            rhs = CoercionCast(a.Value, tt);
                        }
                    }
                    return ($"{Sub(a.Target, PUnary)} {BinSym(cop)}= {rhs}", PAssign);
                }
            case Assign a:
                {
                    // Plain `=` coerces the value to the target type (C narrowing /
                    // sign / pointer conversions C# requires explicitly).
                    var rhs = TryCoerceCast(a.Value, a.Target.Type, out var ct) ? ct : Sub(a.Value, PAssign);
                    return ($"{Sub(a.Target, PUnary)} = {rhs}", PAssign);
                }
            case Unary u: return RenderUnary(u);
            case Binary b: return RenderBinary(b);
            default: throw new IrUnsupportedException("codegen expr " + e.GetType().Name);
        }
    }

    private (string, int) RenderUnary(Unary u)
    {
        // ++/-- of an atomic lvalue is a seq-cst step: prefix yields the NEW value
        // (AddFetch/SubFetch), postfix the OLD (FetchAdd/FetchSub) — matching C.
        if (u.Op is UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec && u.Operand.Type.IsAtomic)
        {
            var lv = BareLValue(u.Operand);
            var cs = Cs(u.Operand.Type.Unqualified);
            var helper = u.Op switch
            {
                UnOp.PreInc => "AddFetch", UnOp.PreDec => "SubFetch",
                UnOp.PostInc => "FetchAdd", _ => "FetchSub",
            };
            return ($"Atomic.{helper}(ref {lv}, ({cs})1)", PPrimary);
        }
        // ++/-- of a volatile lvalue is a fenced read-modify-write (returns void —
        // statement position only, like the legacy). Handled before the plain forms.
        if (u.Op is UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec && u.Operand.Type.IsVolatile)
        {
            var lv = BareLValue(u.Operand);
            var step = u.Op is UnOp.PreInc or UnOp.PostInc ? "+" : "-";
            return ($"global::System.Threading.Volatile.Write(ref {lv}, {VolatileRead(lv)} {step} 1)", PPrimary);
        }
        switch (u.Op)
        {
            // C's unary arithmetic treats an enum as its underlying int — decay it
            // (`-EnumName.Member` is invalid C#).
            case UnOp.Plus: return ($"+{Sub(DecayEnum(u.Operand), PUnary)}", PUnary);
            // Unary minus on an UNSIGNED operand is modular in C (2^N − x); C#
            // rejects -ulong (CS0023) and silently widens -uint to long. Spell
            // the modular subtraction out.
            case UnOp.Neg when Cs(u.Operand.Type.Unqualified) is "ulong" or "nuint":
                return ($"unchecked(0UL - {Sub(DecayEnum(u.Operand), PUnary)})", PPrimary);
            case UnOp.Neg when Cs(u.Operand.Type.Unqualified) is "uint":
                return ($"unchecked(0u - {Sub(DecayEnum(u.Operand), PUnary)})", PPrimary);
            case UnOp.Neg: return ($"-{Sub(DecayEnum(u.Operand), PUnary)}", PUnary);
            case UnOp.BitNot: return ($"~{Sub(DecayEnum(u.Operand), PUnary)}", PUnary);
            // &fn where fn is a function already decays to `&fn` in the VarRef
            // case — don't emit a second `&`.
            case UnOp.AddrOf when u.Operand is VarRef { Sym.Kind: SymKind.Func }: return Render(u.Operand);
            // &global — a file-scope global / static local lowers to a C# static
            // field, which is a MOVEABLE variable (`&field` is CS0212). Take its
            // address via Unsafe.AsPointer: dotcc's globals are unmanaged value
            // types in non-moving static storage, so the pointer is stable. (Lua
            // leans on this: &absentkey, &dummynode_.)
            case UnOp.AddrOf when RootsAtGlobal(u.Operand):
                return ($"({Cs(u.Type)})System.Runtime.CompilerServices.Unsafe.AsPointer(ref {BareLValue(u.Operand)})", PUnary);
            // &<rvalue> — the address of a materialized temporary: a C compound literal
            // `&(T){…}` or a Zig typed struct literal `&T{…}` (both lower to a StructInit
            // rvalue). C# forbids `&new T{…}` (CS0211), so bind the literal to a block-local
            // temp — C's automatic storage for the compound literal's unnamed object — and
            // take ITS address (a stack local is non-moveable, so `&local` needs no `fixed`).
            // Hoistable contexts only; elsewhere fall through (a non-hoistable `&literal` is
            // rare and unsupported, same leniency as the StackArray case).
            case UnOp.AddrOf when !u.Operand.IsLValue && _canHoist:
            {
                var name = $"__cl{_clCounter++}";
                _pending.Add($"{Cs(u.Operand.Type)} {name} = {Expr(u.Operand)}");
                return ($"&{name}", PUnary);
            }
            // BareLValue so `&` of a volatile lvalue takes the address, not the
            // address of a Volatile.Read(...) call.
            case UnOp.AddrOf: return ($"&{BareLValue(u.Operand)}", PUnary);
            // *p where p is a pointer-TO-array: the pointed-to array decays right
            // back to the (same) flat row pointer, so the deref is a no-op here.
            case UnOp.Deref when u.Type.Unqualified is CType.Array: return (Sub(u.Operand, PUnary), PUnary);
            case UnOp.Deref: return QualifiedRead(u, $"*{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PreInc: return ($"++{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PreDec: return ($"--{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PostInc: return ($"{Sub(u.Operand, PPostfix)}++", PPostfix);
            case UnOp.PostDec: return ($"{Sub(u.Operand, PPostfix)}--", PPostfix);
            // C's `!x` yields int 0/1 (never bool); Cond.B picks the truthy overload
            // (no enum overload — decay an enum operand first). Wrapped → atomic.
            case UnOp.LogNot: return ($"(Cond.B({Expr(DecayEnum(u.Operand))}) ? 0 : 1)", PPrimary);
            default: throw new IrUnsupportedException("unary " + u.Op);
        }
    }

    // C relational / equality yields int 0/1, NOT bool — `int x = a < b;` is legal
    // C. Lower the result to CBool (the integer-typed _Bool): CBool→int carries it
    // into arithmetic positions, and Cond.B(CBool) into conditional ones. `&&`/`||`
    // likewise yield int 0/1, with each operand taken through Cond.B for C-truthy.
    // All three forms render fully wrapped, so they're atomic to a parent.
    private (string, int) RenderBinary(Binary b)
    {
        // C treats an enum operand as its underlying integer in every binary
        // context; C# allows few enum operators, so decay each operand first.
        b = b with { Left = DecayEnum(b.Left), Right = DecayEnum(b.Right) };
        switch (b.Op)
        {
            case BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge:
                {
                    var p = Prec(b.Op);
                    // Function-pointer comparison (`fp == fn`): a bare function
                    // designator renders as the untyped method-group address
                    // `&fn`, which C# can't compare without a target type
                    // (CS0019). CmpOperand casts such an operand to its fn-ptr
                    // type; integer operands keep usual-arithmetic reconcile.
                    if (IsFnPtrType(b.Left.Type) || IsFnPtrType(b.Right.Type))
                    {
                        return ($"((CBool)({CmpOperand(b.Left, p)} {BinSym(b.Op)} {CmpOperand(b.Right, p + 1)}))", PPrimary);
                    }
                    var (l, r) = ReconcileOperands(b.Left, b.Right, p, p + 1);
                    return ($"((CBool)({l} {BinSym(b.Op)} {r}))", PPrimary);
                }
            case BinOp.LogAnd:
                // The right operand is short-circuited — render it WITHOUT hoisting so
                // a comma there stays conditional (the left always evaluates). (Operands
                // already enum-decayed at the top, so Cond.B picks an int overload.)
                return ($"((CBool)(Cond.B({Expr(b.Left)}) && Cond.B({NoHoist(() => Expr(b.Right))})))", PPrimary);
            case BinOp.LogOr:
                return ($"((CBool)(Cond.B({Expr(b.Left)}) || Cond.B({NoHoist(() => Expr(b.Right))})))", PPrimary);
            case BinOp.Shl or BinOp.Shr:
                {
                    // A shift's operands are promoted INDEPENDENTLY (the right operand
                    // doesn't join the left's type), so no reconcile. C# requires the
                    // shift count to be `int`, so cast a non-int count (C allows any
                    // integer; the count is always small).
                    var p = Prec(b.Op);
                    var right = b.Right.Type.Unqualified is CType.Prim { Name: "int" }
                        ? Sub(b.Right, p + 1)
                        : $"(int)({Expr(b.Right)})";
                    return ($"{Sub(b.Left, p)} {BinSym(b.Op)} {right}", p);
                }
            default:
                {
                    // Left-associative: right operand at p+1 so same-precedence
                    // right children (`a - (b - c)`) keep their grouping.
                    var p = Prec(b.Op);
                    var (l, r) = ReconcileOperands(b.Left, b.Right, p, p + 1);
                    return ($"{l} {BinSym(b.Op)} {r}", p);
                }
        }
    }

    /// <summary>Render the two operands of an arithmetic / bitwise / relational
    /// binary operator, inserting C's usual-arithmetic conversion cast only where
    /// C# diverges from C. C# performs most of §6.3.1.8 implicitly; it diverges
    /// when the common type is unsigned (C# has no implicit signed→unsigned
    /// conversion — <c>ulong * int</c> is CS0034, <c>uint op int</c> would widen
    /// to <c>long</c> instead of C's <c>uint</c>). The common type comes from the
    /// type system (<see cref="CType.UsualArithmetic"/>); a per-operand cast is
    /// emitted exactly when <see cref="CsImplicitInt"/> says C# wouldn't convert
    /// it for free — so signed-only and float pairs stay untouched.</summary>
    private (string Left, string Right) ReconcileOperands(CExpr left, CExpr right, int lp, int rp)
    {
        if (left.Type.Unqualified is CType.Prim { Integer: true } lt
            && right.Type.Unqualified is CType.Prim { Integer: true } rt
            && CType.UsualArithmetic(lt, rt) is CType.Prim common)
        {
            return (ReconcileOne(left, Cs(lt), Cs(common), lp),
                    ReconcileOne(right, Cs(rt), Cs(common), rp));
        }
        return (Sub(left, lp), Sub(right, rp));
    }

    /// <summary>True when <paramref name="t"/> is a function pointer — in dotcc's
    /// IR a fn-ptr is a bare <see cref="CType.Func"/> (the C# backend renders it as a
    /// <c>delegate*&lt;…&gt;</c>); <c>Pointer(Func)</c> is tolerated for safety.</summary>
    private static bool IsFnPtrType(CType t) =>
        t.Unqualified is CType.Func or CType.Pointer { Pointee: CType.Func };

    /// <summary>True when the expression (under parens / <c>&amp;</c>) is a bare
    /// function designator — i.e. it renders as an untyped method group.</summary>
    private static bool UnparenIsFunc(CExpr e)
    {
        while (true)
        {
            switch (e)
            {
                case Paren p: e = p.Inner; continue;
                case Unary { Op: UnOp.AddrOf, Operand: { } o }: e = o; continue;
                case VarRef { Sym.Kind: SymKind.Func }: return true;
                default: return false;
            }
        }
    }

    /// <summary>Render a comparison operand. A bare function designator
    /// (<see cref="VarRef"/> of a function, possibly parenthesised) renders as the
    /// method-group address <c>&amp;fn</c>, which C# leaves untyped until a target
    /// type is supplied — a comparison has none, so cast it to its own function-
    /// pointer type. Every other operand passes through unchanged.</summary>
    private string CmpOperand(CExpr e, int p)
    {
        var inner = e;
        while (inner is Paren pp) { inner = pp.Inner; }
        return inner is VarRef { Sym.Kind: SymKind.Func }
            ? $"({Cs(inner.Type)})({Sub(e, PUnary)})"
            : Sub(e, p);
    }

    private string ReconcileOne(CExpr e, string from, string to, int p) =>
        from == to || CsImplicitInt(from, to) ? Sub(e, p) : CoercionCast(e, to);

    /// <summary>An emitter-INSERTED conversion cast <c>(to)(e)</c> — usual-arithmetic
    /// operand reconciliation, ternary-arm reconciliation. Applies the same CS0221
    /// rule as <see cref="RenderCast"/>: a constant out of an integer target's range
    /// (<c>x &amp; (ulong)(~1)</c>, <c>cond ? (ulong)(-1) : …</c> — chibi's tag masks)
    /// must be wrapped in <c>unchecked</c>. Cast binds at PUnary — no outer parens.</summary>
    private string CoercionCast(CExpr e, string to)
    {
        var text = $"({to})({Sub(e, PUnary)})";
        return IsIntegerCs(to) && IsConstExpr(e) && !(TryConstInt(e, out var v) && ConstFitsTarget(v, to))
            ? $"unchecked({text})"
            : text;
    }

    /// <summary>Render a cast. A constant-expression cast to a narrower / unsigned
    /// integer type whose value C# can't prove in range is CS0221 unless wrapped
    /// in <c>unchecked</c> (C# rejects only CONSTANT out-of-range casts; a runtime
    /// cast truncates silently — Lua's <c>cast_byte(~mask)</c>, <c>(size_t)-1</c>,
    /// <c>cast_int(MAX_SIZET / sizeof(t))</c>). A fitting folded constant stays
    /// bare so common output keeps clean; an unfoldable constant expression (a
    /// uint-modular shift, a ulong-wide divide) is wrapped on the
    /// <see cref="IsConstExpr"/> flag alone.</summary>
    private (string, int) RenderCast(Cast c)
    {
        // A C cast of a function designator — `(sexp_proc3)fn`, `(sexp)&fn`,
        // chibi's opcode tables — reaches C# as a METHOD-GROUP cast, which only
        // converts to the function's exactly-matching delegate* type (CS8757 on
        // a different shape, CS8812 on an object pointer). Pin the group to its
        // own type first; delegate*→delegate* and delegate*→T* are both legal
        // explicit pointer conversions from there.
        var des = c.Operand;
        while (des is Paren dp) { des = dp.Inner; }
        if (des is Unary { Op: UnOp.AddrOf, Operand: VarRef { Sym.Kind: SymKind.Func } } da) { des = da.Operand; }
        if (des is VarRef { Sym.Kind: SymKind.Func } fv && Cs(c.Target) != Cs(fv.Type))
        {
            return ($"({Cs(c.Target)})({Cs(fv.Type)})&{fv.Sym.TargetName}", PUnary);
        }
        var text = $"({Cs(c.Target)}){Sub(c.Operand, PUnary)}";
        if (c.Target.Unqualified is CType.Prim { Integer: true } pt
            && IsConstExpr(c.Operand)
            && !(TryConstInt(c.Operand, out var cv) && ConstFitsTarget(cv, Cs(pt))))
        {
            return ($"unchecked({text})", PPrimary);
        }
        return (text, PUnary);
    }

    /// <summary>True when <paramref name="e"/> is a C constant expression — only
    /// literals, enum constants, <c>sizeof</c>, and operators over constant
    /// operands; no variable reads or calls. Gates the <c>unchecked</c> wrapper
    /// above for constants the folder can't reduce to a value.</summary>
    private static bool IsConstExpr(CExpr e) => e switch
    {
        LitInt or LitFloat or SizeOfExpr or EnumConstRef => true,
        Paren p => IsConstExpr(p.Inner),
        Cast c => IsConstExpr(c.Operand),
        Unary u => u.Op is UnOp.Plus or UnOp.Neg or UnOp.BitNot or UnOp.LogNot && IsConstExpr(u.Operand),
        Binary { Op: not (BinOp.LogAnd or BinOp.LogOr) } b => IsConstExpr(b.Left) && IsConstExpr(b.Right),
        CondExpr t => IsConstExpr(t.Cond) && IsConstExpr(t.Then) && IsConstExpr(t.Else),
        _ => false,
    };

    /// <summary>Render a pinned global/static array's backing store. A scalar /
    /// struct element uses the generic <c>GlobalArrayFrom&lt;T&gt;</c> /
    /// <c>GlobalArrayZeroed&lt;T&gt;</c>; a pointer element (a C# generic type
    /// argument can't be a pointer — CS0306) round-trips through a pinned
    /// <c>nint[]</c> reinterpreted as <c>T**</c>; a function-pointer element uses
    /// the non-generic <c>PinFnPtrArray</c> (delegate* can't be a type argument
    /// either).</summary>
    private string PinnedArrayText(PinnedArray pa)
    {
        var elemCs = Cs(pa.Element);
        if (pa.Elems is null)
        {
            var count = pa.Count is { } c ? Expr(c) : "0";
            return pa.Element.Unqualified is CType.Pointer
                ? $"({elemCs}*)Libc.GlobalArrayZeroed<nint>({count})"
                : $"Libc.GlobalArrayZeroed<{elemCs}>({count})";
        }
        if (pa.Element.Unqualified is CType.Func)
        {
            return $"({elemCs}*)Libc.PinFnPtrArray(new {elemCs}[]{{ {string.Join(", ", pa.Elems.Select(Expr))} }})";
        }
        if (pa.Element.Unqualified is CType.Pointer)
        {
            var ptr = string.Join(", ", pa.Elems.Select(e => $"(nint)({Expr(e)})"));
            return $"({elemCs}*)Libc.GlobalArrayFrom<nint>(new nint[]{{ {ptr} }})";
        }
        var vals = string.Join(", ", pa.Elems.Select(e => Coerced(e, pa.Element)));
        // const-driven RVA: a const primitive array (incl. a const #embed blob) is
        // read-only, so point straight at the PE .rodata blob via Libc.L — the
        // zero-copy string-literal path (Roslyn RVA-folds an all-constant
        // `new T[]{…}` in a ReadOnlySpan<T> position: no alloc, no GC root, no
        // startup copy) — instead of the writable GlobalArrayFrom POH copy. Both
        // forms return T*, so the global's type and every use are unchanged. Sound
        // because writing to a const object is UB and the const-correctness check
        // rejects in-source writes; reads / sizeof / address-of are unaffected.
        // GUARD: only when every element is a compile-time constant — otherwise
        // Roslyn allocates a transient heap array and L() would return a pointer
        // into it (dangling). `byte` keeps the non-generic L (the string-literal
        // form); other primitives use the generic L<T> (LP64 little-endian).
        if (pa.Element.IsConst && pa.Element.Unqualified is CType.Prim && pa.Elems.All(IsRvaConstant))
        {
            return elemCs == "byte"
                ? $"Libc.L(new byte[]{{ {vals} }})"
                : $"Libc.L<{elemCs}>(new {elemCs}[]{{ {vals} }})";
        }
        return $"Libc.GlobalArrayFrom<{elemCs}>(new {elemCs}[]{{ {vals} }})";
    }

    /// <summary>An array element that lowers to a C# compile-time constant — the
    /// precondition for Roslyn to RVA-fold the backing <c>new T[]{…}</c> into the
    /// PE data section (so <see cref="PinnedArrayText"/> can point at it via
    /// <c>Libc.L</c>). Numeric literals only; anything else keeps the writable copy.</summary>
    private static bool IsRvaConstant(CExpr e) => e is LitInt or LitFloat;

    /// <summary>Render a positional aggregate initializer as a C# object
    /// initializer — <c>new Point { x = 3, y = 4 }</c>. Each value is coerced to
    /// its field type (C's implicit store conversion); unsupplied trailing fields
    /// are omitted, so C# zero-fills them (C's partial-init rule).</summary>
    private string StructInitText(StructInit si)
    {
        var sb = new StringBuilder("new ").Append(Cs(si.Type)).Append(" { ");
        for (var i = 0; i < si.Members.Count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            var m = si.Members[i];
            sb.Append(DotCC.EmitHelpers.Id(m.Name)).Append(" = ").Append(Coerced(m.Value, m.FieldType));
        }
        return sb.Append(" }").ToString();
    }

    // ---- comma operator --------------------------------------------------

    /// <summary>True when a pointer-shaped type can't be a ValueTuple element or a
    /// <c>Func&lt;&gt;</c> type argument (CS0306) — it must round-trip through
    /// <c>nint</c> in those forms. Covers raw pointers, function pointers, and a
    /// decayed array.</summary>
    private static bool IsPointerType(CType t) => t.Unqualified is CType.Pointer or CType.Func or CType.Array;

    /// <summary>Render a comma operand standing in statement position — for the
    /// hoisted statement-comma and the leading operands of the delegate form. A
    /// call / assignment / <c>++</c>/<c>--</c> is already a valid C#
    /// statement-expression; any other (a discarded value, incl. a <c>(void)x</c>
    /// the binder re-typed to void) is consumed by a discard so C# accepts it.</summary>
    private string RenderStmtExpr(CExpr e)
    {
        // Outer parens never matter for a statement-expression (a macro body like
        // `((c) ? a() : b())` arrives parenthesized) — strip them to see the shape.
        while (e is Paren p) { e = p.Inner; }
        // A comma in statement position discards every operand's value (the whole
        // comma's value is unused here), so emit each operand as its own statement
        // rather than a value tuple/delegate. This is also the ONLY correct lowering
        // when the comma's value is void (`api_check` = `((void)l, lua_assert(...))`,
        // `lua_lock` = `((void)0)`), where a Func<void>/tuple element is illegal C#.
        if (e is CommaOp co) { return string.Join("; ", co.Items.Select(RenderStmtExpr)); }
        // A void-typed conditional (`c ? voidA() : voidB()`) has no C# value form
        // (CS0173) — in statement/discard position it IS an if/else. Arms recurse
        // (a nested void `?:`); each arm renders in statement position too.
        if (e is CondExpr ct && ct.Type.Unqualified is CType.VoidType)
        {
            return $"if (Cond.B({Expr(ct.Cond)})) {{ {RenderStmtExpr(ct.Then)}; }} else {{ {RenderStmtExpr(ct.Else)}; }}";
        }
        return IsStmtExpr(e) ? Expr(e) : $"_ = {Sub(e, PAssign)}";
    }

    private static bool IsStmtExpr(CExpr e) => e switch
    {
        // A Zig allocator alloc/free (Milestone F) renders as a method call — a valid statement
        // expression. FreeCall is void, so it MUST be recognized here (a `_ = <void>` discard is
        // a C# error); a discarded AllocCall is a normal `_ = recv.Alloc(...)`, also fine as a stmt.
        Assign or Call or IndirectCall or AllocCall or FreeCall => true,
        Unary u => u.Op is UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec,
        Paren p => IsStmtExpr(p.Inner),
        _ => false,
    };

    /// <summary>Render a value-context comma. With no <c>void</c> leading operand
    /// (and ≤7 operands) a ValueTuple is enough — C# evaluates its elements
    /// left-to-right and <c>.ItemN</c> picks the last (the comma's value). A
    /// <c>void</c> leading operand (or an over-long chain) needs the
    /// immediately-invoked delegate, the only form that both swallows a void
    /// side effect and stays lazy inside a short-circuit.</summary>
    private string CommaValue(CommaOp co)
    {
        // Shed leading operands with no side effects (e.g. the `(void)0` an
        // asserts-off `lua_assert`/`check_exp` leaves behind): they don't change the
        // comma's value, and dropping them often collapses the chain to its value
        // operand alone — sidestepping the tuple/IIFE entirely (and the CS1686 the
        // IIFE hits when the value operand takes a local's address).
        var items = co.Items;
        if (items.Count > 1)
        {
            var kept = new List<CExpr>();
            for (var i = 0; i < items.Count - 1; i++) { if (!IsPure(items[i])) { kept.Add(items[i]); } }
            kept.Add(items[^1]);
            items = kept;
        }
        if (items.Count == 1) { return $"({Expr(items[0])})"; }
        var leadingVoid = false;
        for (var i = 0; i < items.Count - 1; i++)
        {
            if (items[i].Type.Unqualified is CType.VoidType) { leadingVoid = true; break; }
        }
        return !leadingVoid && items.Count <= 7 ? CommaTuple(items) : CommaDelegate(items);
    }

    /// <summary>True when an expression has no side effects — so a non-final comma
    /// operand can be dropped without changing observable behavior. Conservative:
    /// reads (literals, variables, member/index/deref), pure operators, sizeof and
    /// address-of are pure; calls, assignments, and ++/-- are not.</summary>
    private static bool IsPure(CExpr e) => e switch
    {
        // A volatile/atomic read is itself an observable access — never drop it.
        _ when e.Type.IsVolatile || e.Type.IsAtomic => false,
        LitInt or LitFloat or LitStr or NullPtr or NameRef or DefaultLit or VarRef or SizeOfExpr or OffsetOf => true,
        Paren p => IsPure(p.Inner),
        Cast c => IsPure(c.Operand),
        BitCast bc => IsPure(bc.Operand),
        Member m => IsPure(m.Base),
        Index ix => IsPure(ix.Base) && IsPure(ix.Idx),
        Unary u => u.Op is not (UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec)
                   && IsPure(u.Operand),
        Binary b => IsPure(b.Left) && IsPure(b.Right),
        CondExpr t => IsPure(t.Cond) && IsPure(t.Then) && IsPure(t.Else),
        SwitchExpr sw => IsPure(sw.Subject) && sw.Arms.All(a => IsPure(a.Value)),
        CommaOp co => co.Items.All(IsPure),
        _ => false,   // Call / IndirectCall / Assign / StructInit / VaArgGet / …
    };

    private string CommaTuple(IReadOnlyList<CExpr> items)
    {
        // A pointer operand can't be a tuple type argument — cast it to nint for the
        // tuple; if the VALUE (last) is a pointer, cast .ItemN back to its type.
        var elems = items.Select(e =>
            IsPointerType(e.Type) ? $"(nint)({Sub(e, PUnary)})" : Sub(e, PAssign));
        var pick = $"({string.Join(", ", elems)}).Item{items.Count}";
        return IsPointerType(items[^1].Type) ? $"(({Cs(items[^1].Type)})({pick}))" : $"({pick})";
    }

    private string CommaDelegate(IReadOnlyList<CExpr> items)
    {
        var body = new StringBuilder();
        for (var i = 0; i < items.Count - 1; i++) { body.Append(RenderStmtExpr(items[i])).Append("; "); }
        var last = items[^1];
        // A pointer value can't be a Func<> type argument either — round-trip nint.
        return IsPointerType(last.Type)
            ? $"(({Cs(last.Type)})((System.Func<nint>)(() => {{ {body}return (nint)({Expr(last)}); }}))())"
            : $"((System.Func<{Cs(last.Type)}>)(() => {{ {body}return {Expr(last)}; }}))()";
    }

    // ---- <stdatomic.h> generic functions ---------------------------------
    // C11's atomic_* "generic functions" are type-generic in a real header; dotcc
    // intercepts them by NAME and lowers onto the seq-cst Atomic.* helpers
    // (DotCC.Libc/AtomicLib.cs). arg0 is always a pointer to the atomic object —
    // `*(arg0)` is the location, and its pointee type casts the value args (C's
    // int→uint/float conversions aren't implicit in C#). The `_explicit` variants'
    // trailing memory_order args are ignored (every order maps to a full barrier —
    // the safe over-approximation, same as volatile).

    /// <summary>C# types Atomic.* covers (4-/8-byte unmanaged INumber scalars).</summary>
    private static readonly HashSet<string> _atomicEligible = new(StringComparer.Ordinal)
    {
        "int", "uint", "long", "ulong", "nint", "nuint", "float", "double",
    };

    /// <summary>Lower a recognised <c>atomic_*</c> generic function to its
    /// <c>Atomic.*</c> form, or null when <paramref name="c"/> isn't one (so the
    /// normal call path runs).</summary>
    private string? LowerAtomicCall(Call c)
    {
        if (!c.Callee.StartsWith("atomic_", StringComparison.Ordinal)) { return null; }
        var args = c.Args;
        var name = c.Callee.EndsWith("_explicit", StringComparison.Ordinal)
            ? c.Callee[..^"_explicit".Length] : c.Callee;

        // Pointee C# type of arg0 (a `T*`), or null if undeterminable.
        string? Pointee() => args.Count > 0 && args[0].Type.Unqualified is CType.Pointer p
            ? Cs(p.Pointee.Unqualified) : null;
        // The atomic object as a ref-able location: `*(arg0)`.
        string Obj() => $"*({Expr(args[0])})";
        bool Eligible() => Pointee() is string p && _atomicEligible.Contains(p);
        // Cast a value arg to the element type so the generic Atomic.* call infers T.
        string Cast(int i) => Pointee() is string p ? $"({p})({Expr(args[i])})" : Expr(args[i]);

        switch (name)
        {
            case "atomic_load":
                return Eligible() ? $"Atomic.Load(ref {Obj()})" : $"({Obj()})";
            case "atomic_store":
            case "atomic_init":   // init is a (non-atomic) plain store in C anyway
                // The plain-store form is a bare assignment (void in C, so only ever
                // in statement position) — no outer parens (CS0201 wraps `(x = y);`).
                return name == "atomic_init" || !Eligible()
                    ? $"{Obj()} = {Cast(1)}"
                    : $"Atomic.Store(ref {Obj()}, {Cast(1)})";
            case "atomic_exchange":
                return $"Atomic.Exchange(ref {Obj()}, {Cast(1)})";
            case "atomic_compare_exchange_strong":
            case "atomic_compare_exchange_weak":
                return $"((CBool)Atomic.CompareExchange(ref {Obj()}, ref *({Expr(args[1])}), {Cast(2)}))";
            case "atomic_fetch_add": return $"Atomic.FetchAdd(ref {Obj()}, {Cast(1)})";
            case "atomic_fetch_sub": return $"Atomic.FetchSub(ref {Obj()}, {Cast(1)})";
            case "atomic_fetch_or":  return $"Atomic.FetchOr(ref {Obj()}, {Cast(1)})";
            case "atomic_fetch_and": return $"Atomic.FetchAnd(ref {Obj()}, {Cast(1)})";
            case "atomic_fetch_xor": return $"Atomic.FetchXor(ref {Obj()}, {Cast(1)})";
            // atomic_flag — dotcc models the opaque flag as an int (see <stdatomic.h>).
            case "atomic_flag_test_and_set": return $"((CBool)(Atomic.Exchange(ref {Obj()}, 1) != 0))";
            case "atomic_flag_clear": return $"Atomic.Store(ref {Obj()}, 0)";
            case "atomic_thread_fence": return "Atomic.ThreadFence()";
            case "atomic_signal_fence": return "Atomic.SignalFence()";
            // Our eligible atomics are always lock-free; report 1, else 0.
            case "atomic_is_lock_free": return Eligible() ? "1" : "0";
            default: return null;
        }
    }

    /// <summary>Lower the <c>&lt;stdarg.h&gt;</c> control macros onto the
    /// <c>VaList</c> runtime: <c>va_start(ap, last)</c> → <c>ap = new VaList(_va)</c>
    /// (the synthesized params array; <c>last</c> is ignored), <c>va_end(ap)</c> →
    /// <c>ap.End()</c>, <c>va_copy(d, s)</c> → <c>d = s</c>. <c>va_arg</c> is a
    /// dedicated node (its 2nd operand is a type). Null when not a va_* call.</summary>
    private string? LowerVaCall(Call c) => c.Callee switch
    {
        "va_start" => $"{Expr(c.Args[0])} = new VaList(_va)",
        "va_end" => $"{Sub(c.Args[0], PPostfix)}.End()",
        "va_copy" => $"{Expr(c.Args[0])} = {Sub(c.Args[1], PAssign)}",
        _ => null,
    };

    private string CallText(Call c)
    {
        if (LowerAtomicCall(c) is { } atomic) { return atomic; }
        if (LowerVaCall(c) is { } va) { return va; }
        // Coerce each argument to its parameter type (C's implicit conversion at
        // a call C# requires explicit) when the callee's signature is known; the
        // variadic tail (index ≥ fixed-param count) and unknown-signature callees
        // pass through unchanged.
        var a = new List<string>(c.Args.Count);
        for (var i = 0; i < c.Args.Count; i++)
        {
            // malloc's size arg: a bare `sizeof(T)` is already a C# int, so emit it
            // directly — `malloc(sizeof(T))` — not the size_t-wrapped form
            // `malloc((int)((ulong)(sizeof(T))))` the generic int-param coercion gives.
            if (c.Callee == "malloc" && c.Args[i] is SizeOfExpr so)
            {
                a.Add(SizeofText(so.Of));
                continue;
            }
            // A known parameter coerces the arg to its type; a variadic-tail or
            // unknown-signature arg takes C's default argument promotions — notably
            // an enum decays to its underlying int (C# has no enum→int for `.Arg`).
            a.Add(c.ParamTypes is { } pts && i < pts.Count
                ? CoercedArg(c.Args[i], pts[i])
                : Sub(DecayEnum(c.Args[i]), PAssign));
        }
        if (IsPrintfFamily(c.Callee) || IsScanfFamily(c.Callee))
        {
            // Format-family fluent lowering — matches the runtime contract:
            // printf(fmt).Arg(x).Arg(y).Done()  /  fprintf(stream, fmt)…  /
            // sscanf(src, fmt).Read(p).Read(q).Done()  etc.
            var (fixedCount, head) = c.Callee switch
            {
                "printf" => (1, $"printf({Arg(a, 0)})"),
                "fprintf" => (2, $"fprintf({Arg(a, 0)}, {Arg(a, 1)})"),
                "sprintf" => (2, $"sprintf({Arg(a, 0)}, {Arg(a, 1)})"),
                "snprintf" => (3, $"snprintf({Arg(a, 0)}, {Arg(a, 1)}, {Arg(a, 2)})"),
                "scanf" => (1, $"scanf({Arg(a, 0)})"),
                "fscanf" => (2, $"fscanf({Arg(a, 0)}, {Arg(a, 1)})"),
                "sscanf" => (2, $"sscanf({Arg(a, 0)}, {Arg(a, 1)})"),
                // Wide formatted I/O — the same fluent lowering; the runtime
                // transcodes the wide format to UTF-8 and reuses the byte
                // PrintfBuilder/ScanfReader (a wide %s/%c arg is a char*).
                "wprintf" => (1, $"wprintf({Arg(a, 0)})"),
                "fwprintf" => (2, $"fwprintf({Arg(a, 0)}, {Arg(a, 1)})"),
                "swprintf" => (3, $"swprintf({Arg(a, 0)}, {Arg(a, 1)}, {Arg(a, 2)})"),
                "wscanf" => (1, $"wscanf({Arg(a, 0)})"),
                "fwscanf" => (2, $"fwscanf({Arg(a, 0)}, {Arg(a, 1)})"),
                "swscanf" => (2, $"swscanf({Arg(a, 0)}, {Arg(a, 1)})"),
                _ => (a.Count, $"{c.Callee}({string.Join(", ", a)})"),
            };
            var chain = IsScanfFamily(c.Callee) ? ".Read(" : ".Arg(";
            var sb = new StringBuilder(head);
            for (var i = fixedCount; i < a.Count; i++) { sb.Append(chain).Append(a[i]).Append(')'); }
            sb.Append(".Done()");
            return sb.ToString();
        }
        // Emit the resolved symbol's TargetName when known — identical to
        // Id(Callee) for every normal function (the C# legalizer's Escape IS
        // EmitHelpers.Id), differing only for a static renamed out of the way of a
        // same-named external (BuildFuncDef). Falls back to the escaped raw name
        // for libc builtins / fn-ptr-variable / unresolved callees.
        var target = c.CalleeSym?.TargetName ?? DotCC.EmitHelpers.Id(c.Callee);
        return $"{target}({string.Join(", ", a)})";
    }

    /// <summary>The libc names the C# backend lowers to the fluent
    /// <c>printf(fmt).Arg(x).Done()</c> form (variadic format functions) — narrow
    /// plus the wide <c>w*printf</c> family (same lowering; the wide format is
    /// transcoded to UTF-8 at runtime).</summary>
    private static bool IsPrintfFamily(string callee) =>
        callee is "printf" or "fprintf" or "sprintf" or "snprintf"
               or "wprintf" or "fwprintf" or "swprintf";

    /// <summary>The scanf side of the same lowering — fluent
    /// <c>.Read(ptr)</c> chain into <c>ScanfReader</c> — narrow plus the wide
    /// <c>w*scanf</c> family.</summary>
    private static bool IsScanfFamily(string callee) =>
        callee is "scanf" or "fscanf" or "sscanf"
               or "wscanf" or "fwscanf" or "swscanf";

    private static string Arg(List<string> a, int i) => i < a.Count ? a[i] : "";

    /// <summary>The C precedence of a binary operator (see the P* constants).</summary>
    private static int Prec(BinOp op) => op switch
    {
        BinOp.Mul or BinOp.Div or BinOp.Mod => PMul,
        BinOp.Add or BinOp.Sub => PAdd,
        BinOp.Shl or BinOp.Shr => PShift,
        BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge => PRel,
        BinOp.Eq or BinOp.Ne => PEq,
        BinOp.BitAnd => PBitAnd,
        BinOp.BitXor => PBitXor,
        BinOp.BitOr => PBitOr,
        BinOp.LogAnd => PLogAnd,
        BinOp.LogOr => PLogOr,
        _ => PPrimary,
    };

    private static string BinSym(BinOp op) => op switch
    {
        BinOp.Add => "+", BinOp.Sub => "-", BinOp.Mul => "*", BinOp.Div => "/", BinOp.Mod => "%",
        BinOp.Shl => "<<", BinOp.Shr => ">>", BinOp.BitAnd => "&", BinOp.BitOr => "|", BinOp.BitXor => "^",
        BinOp.Lt => "<", BinOp.Gt => ">", BinOp.Le => "<=", BinOp.Ge => ">=", BinOp.Eq => "==", BinOp.Ne => "!=",
        BinOp.LogAnd => "&&", BinOp.LogOr => "||",
        _ => throw new IrUnsupportedException("binop " + op),
    };

    /// <summary>The C# <c>sizeof</c> text for a type, recursing through array
    /// dimensions (<c>int[2][3]</c> → <c>(2 * (3 * sizeof(int)))</c>). A struct
    /// element bottoms out at C#'s <c>sizeof(T)</c> so the real aligned layout is
    /// used; a scalar/pointer likewise.</summary>
    private string SizeofText(CType t) => t.Unqualified is CType.Array { Count: { } n } a
        ? $"({n} * {SizeofText(a.Element)})"
        : $"sizeof({Cs(t)})";

    /// <summary>The number of flat (innermost-scalar) elements an array type holds —
    /// the stride, in elements, of one row when a multi-dim array is flattened.</summary>
    private static int FlatCount(CType t) => t.Unqualified is CType.Array a ? (a.Count ?? 0) * FlatCount(a.Element) : 1;

    private static string Pad(int ind) => new string(' ', ind * 4);
}
