#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DotCC.Ir;

/// <summary>
/// The six outputs <see cref="DotCC.Compiler.BuildShell"/> /
/// <c>SerializeFragment</c> consume — produced by the IR backend exactly as the
/// legacy <see cref="DotCC.CSharpEmitter"/> exposes them, so the shell is reused
/// verbatim.
/// </summary>
internal sealed record CodeGenResult(
    string Functions,
    string Structs,
    string Aliases,
    string Globals,
    int MainArity,
    IReadOnlyList<DotCC.CSharpEmitter.Export> Exports);

/// <summary>
/// Lowers the typed IR to low-level unsafe C# text. Deliberately DUMB: every
/// semantic decision (types, coercions, name resolution, control flow) was made
/// upstream by <see cref="IrBuilder"/> and the IR passes, so this just prints.
/// Phase 0 covers the vertical slice; it grows alongside the builder until the
/// IR path reaches parity with the legacy emitter.
/// </summary>
internal sealed class CodeGen
{
    public static CodeGenResult Run(IrBuilder unit)
    {
        var cg = new CodeGen();
        var fns = new StringBuilder();
        var exports = new List<DotCC.CSharpEmitter.Export>();
        var mainArity = -1;

        foreach (var fn in unit.Functions)
        {
            if (fns.Length > 0) { fns.Append("\n\n"); }
            fns.Append(cg.Func(fn));

            if (fn.Sym.Name == "main") { mainArity = fn.Params.Count; }
            else if (fn.Sym.Storage != Storage.Static)
            {
                var ret = fn.Sym.Type is CType.Func f ? f.Return.CsType : "int";
                var ps = string.Join(", ", fn.Params.Select(p => $"{p.Type.CsType} {p.CsName}"));
                exports.Add(new DotCC.CSharpEmitter.Export(fn.Sym.Name, ret, ps));
            }
        }

        // File-scope variables → public static fields of DotCcGlobals (the shell
        // surfaces them by bare name via `using static DotCcGlobals;`).
        var globals = new StringBuilder();
        foreach (var g in unit.Globals)
        {
            var init = g.Init is { } i ? " = " + cg.Coerced(i, g.Sym.Type) : "";
            globals.Append($"    public static unsafe {g.Sym.Type.CsType} {g.Sym.CsName}{init};\n");
        }

        // struct/union type declarations → the top-level type-decls section.
        var structs = new StringBuilder();
        foreach (var t in unit.Types) { structs.Append(StructText(t)); }

        return new CodeGenResult(fns.ToString(), structs.ToString(), Aliases: "", globals.ToString(), mainArity, exports);
    }

    // ---- type declarations -----------------------------------------------

    /// <summary>Render a struct/union type. A union uses
    /// <c>[StructLayout(LayoutKind.Explicit)]</c> with every field at
    /// <c>[FieldOffset(0)]</c> (C overlays all members at the same address).</summary>
    private static string StructText(StructTypeDef t)
    {
        var sb = new StringBuilder();
        if (t.IsUnion)
        {
            sb.Append("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]\n");
        }
        sb.Append("unsafe struct ").Append(t.Name).Append("\n{\n");
        foreach (var f in t.Fields)
        {
            if (t.IsUnion) { sb.Append("    [System.Runtime.InteropServices.FieldOffset(0)]\n"); }
            sb.Append("    public ").Append(f.Type.CsType).Append(' ').Append(DotCC.CSharpEmitter.Id(f.Name)).Append(";\n");
        }
        sb.Append("}\n\n");
        return sb.ToString();
    }

    // ---- functions -------------------------------------------------------

    private string Func(FuncDef fn)
    {
        var retTy = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        _currentRet = retTy;
        var ps = string.Join(", ", fn.Params.Select(p => $"{p.Type.CsType} {p.CsName}"));
        var sb = new StringBuilder();
        sb.Append($"static unsafe {retTy.CsType} {fn.Sym.CsName}({ps})\n");
        Stmt(sb, fn.Body, 0);
        return sb.ToString();
    }

    // ---- statements ------------------------------------------------------

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
            case DeclStmt d:
                EmitDeclStmt(sb, d, pad);
                break;
            case ArrayDecl a:
                {
                    var elemCs = a.Element.CsType;
                    if (a.Inits is { } inits)
                    {
                        sb.Append(pad).Append($"{elemCs}* {a.Sym.CsName} = stackalloc {elemCs}[]{{ {string.Join(", ", inits.Select(Expr))} }};\n");
                    }
                    else
                    {
                        // No initializer → zeroed stackalloc of the given extent.
                        var count = a.CountExpr is { } ce ? Expr(ce) : "0";
                        sb.Append(pad).Append($"{elemCs}* {a.Sym.CsName} = stackalloc {elemCs}[{count}];\n");
                    }
                }
                break;
            case ExprStmt e:
                sb.Append(pad).Append(Expr(e.Expr)).Append(";\n");
                break;
            case Return r:
                sb.Append(pad).Append(r.Value is null ? "return;" : $"return {Coerced(r.Value, _currentRet)};").Append('\n');
                break;
            case Break: sb.Append(pad).Append("break;\n"); break;
            case Continue: sb.Append(pad).Append("continue;\n"); break;
            case If f:
                sb.Append(pad).Append($"if (Cond.B({Expr(f.Cond)}))\n");
                Nested(sb, f.Then, ind);
                if (f.Else is { } els)
                {
                    sb.Append(pad).Append("else\n");
                    Nested(sb, els, ind);
                }
                break;
            case While w:
                sb.Append(pad).Append($"while (Cond.B({Expr(w.Cond)}))\n");
                Nested(sb, w.Body, ind);
                break;
            case DoWhile dw:
                sb.Append(pad).Append("do\n");
                Nested(sb, dw.Body, ind);
                sb.Append(pad).Append($"while (Cond.B({Expr(dw.Cond)}));\n");
                break;
            case Goto g:
                sb.Append(pad).Append($"goto {g.Label};\n");
                break;
            case Labeled lb:
                sb.Append(pad).Append(lb.Name).Append(":\n");
                Stmt(sb, lb.Body, ind);
                break;
            case Switch sw:
            {
                sb.Append(pad).Append($"switch ({Expr(sw.Subject)})\n");
                sb.Append(pad).Append("{\n");
                var inner = ind + 1;
                var ipad = Pad(inner);
                for (var si = 0; si < sw.Sections.Count; si++)
                {
                    var sec = sw.Sections[si];
                    foreach (var lab in sec.Labels)
                    {
                        sb.Append(ipad).Append(lab.CaseExpr is { } ce ? $"case {Expr(ce)}:\n" : "default:\n");
                    }
                    foreach (var st in sec.Body) { Stmt(sb, st, inner + 1); }
                    // C fall-through → the explicit C# jump. A section already ending
                    // in break/return/… is left alone; otherwise it jumps to the next
                    // section's first label (goto case / goto default), and the final
                    // section gets a trailing break (C falls out, C# requires it).
                    if (sec.Body.Count == 0 || !Terminates(sec.Body[^1]))
                    {
                        string jump;
                        if (si + 1 < sw.Sections.Count)
                        {
                            var next = sw.Sections[si + 1].Labels[0];
                            jump = next.CaseExpr is { } nce ? $"goto case {Expr(nce)};\n" : "goto default;\n";
                        }
                        else { jump = "break;\n"; }
                        sb.Append(Pad(inner + 1)).Append(jump);
                    }
                }
                sb.Append(pad).Append("}\n");
                break;
            }
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
            case For fr:
                var init = fr.Init switch
                {
                    DeclStmt d => DeclInline(d),
                    ExprStmt e => Expr(e.Expr),
                    _ => "",
                };
                var cond = fr.Cond is null ? "" : $"Cond.B({Expr(fr.Cond)})";
                var post = fr.Post is null ? "" : Expr(fr.Post);
                sb.Append(pad).Append($"for ({init}; {cond}; {post})\n");
                Nested(sb, fr.Body, ind);
                break;
            default:
                throw new IrUnsupportedException("codegen stmt " + s.GetType().Name);
        }
    }

    // Render a sub-statement of if/while/for: a block keeps the parent indent
    // (braces align under the controller); a single statement indents one level.
    private void Nested(StringBuilder sb, CStmt s, int ind) =>
        Stmt(sb, s, s is Block ? ind : ind + 1);

    /// <summary>True when a statement provably ends control flow at its end, so a
    /// switch section ending in it needs no synthetic fall-through jump.</summary>
    private static bool Terminates(CStmt s) => s switch
    {
        Break or Continue or Return or Goto => true,
        Block b => b.Stmts.Count > 0 && Terminates(b.Stmts[^1]),
        If f => f.Else is { } e && Terminates(f.Then) && Terminates(e),
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
    /// <see cref="Func"/> (CodeGen renders functions one at a time).</summary>
    private CType _currentRet = CType.Int;

    /// <summary>Render <paramref name="value"/> for storage into a
    /// <paramref name="target"/>-typed sink, inserting any cast C# needs.</summary>
    private string Coerced(CExpr value, CType target) =>
        TryCoerceCast(value, target, out var t) ? t : Expr(value);

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
        if (tgt is CType.Pointer && TryConstInt(value, out var z) && z == 0)
        {
            text = "null";
            return true;
        }
        // void* → T* (e.g. malloc's result): C# makes T*→void* implicit but requires
        // the reverse cast explicitly. Skip void*→void* (same type).
        if (tgt is CType.Pointer && src is CType.Pointer { Pointee: CType.VoidType }
            && tgt.CsType != src.CsType)
        {
            text = $"({tgt.CsType})({Expr(value)})";
            return true;
        }
        // Integer narrowing / sign change C# won't do implicitly.
        if (src is CType.Prim { Integer: true } && tgt is CType.Prim { Integer: true })
        {
            var s = src.CsType;
            var t2 = tgt.CsType;
            if (s == t2 || !IsIntegerCs(s) || !IsIntegerCs(t2)) { return false; }
            // A bare int constant that fits the target needs no cast (C#'s implicit
            // constant-expression conversion applies, but only from a literal int).
            if (value is LitInt { Value: { } cv } && s == "int" && ConstFitsTarget(cv, t2))
            {
                return false;
            }
            if (CsImplicitInt(s, t2)) { return false; }   // C# widens for free
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

    // Byte width of a lowered C# integer type (LP64 — long/pointer = 8).
    private static int? IntWidth(string t) => t switch
    {
        "byte" or "sbyte" => 1,
        "short" or "ushort" => 2,
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
        var srcUnsigned = src is "byte" or "ushort" or "uint" or "ulong" or "nuint";
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

    /// <summary>Render a declaration in statement position. When every declarator
    /// shares a C# type it's one C# declaration (<c>int a = 0, b = 1;</c>); when
    /// the per-declarator types differ (C's <c>int *a, b;</c> — a is a pointer, b
    /// is not — which C# can't express in one statement since <c>int* a, b</c>
    /// makes both pointers) it splits into one statement per declarator.</summary>
    private void EmitDeclStmt(StringBuilder sb, DeclStmt d, string pad)
    {
        if (d.Decls.Count == 0) { return; }
        var firstCs = d.Decls[0].Sym.Type.CsType;
        if (d.Decls.All(e => e.Sym.Type.CsType == firstCs))
        {
            sb.Append(pad).Append(DeclInline(d)).Append(";\n");
            return;
        }
        foreach (var e in d.Decls)
        {
            var init = e.Init is { } i ? Coerced(i, e.Sym.Type) : "default";
            sb.Append(pad).Append($"{e.Sym.Type.CsType} {e.Sym.CsName} = {init};\n");
        }
    }

    // A single C# declaration (shared element type), used in statement position
    // when all declarators agree and in `for`-initializer position.
    private string DeclInline(DeclStmt d)
    {
        var type = d.Decls.Count > 0 ? d.Decls[0].Sym.Type.CsType : "int";
        var parts = d.Decls.Select(e => e.Init is { } init
            ? $"{e.Sym.CsName} = {Coerced(init, e.Sym.Type)}"
            : $"{e.Sym.CsName} = default");
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
            case LitInt i: return (i.CsText, PPrimary);
            case LitFloat f: return (f.CsText, PPrimary);
            case LitStr s: return (s.CsExpr, PPrimary);
            case Raw r: return (r.CsText, PPrimary);
            // A bare function name used as a value decays to its address — C#
            // needs the explicit `&` to form a delegate* (C allows the bare name).
            case VarRef v: return v.Sym.Kind == SymKind.Func
                ? ($"&{v.Sym.CsName}", PUnary)
                : (v.Sym.CsName, PPrimary);
            case IndirectCall ic:
                return ($"{Sub(ic.Callee, PPostfix)}({string.Join(", ", ic.Args.Select(a => Sub(a, PAssign)))})", PPostfix);
            case Paren p: return Render(p.Inner); // explicit C parens are redundant; precedence re-adds as needed
            case Cast c: return ($"({c.Target.CsType}){Sub(c.Operand, PUnary)}", PUnary);
            case SizeOfExpr so:
                // An array lowered to a pointer, so C#'s sizeof would measure the
                // pointer — emit count * sizeof(element) instead (the true C size).
                return so.Of is CType.Array { Count: { } n } arr
                    ? ($"({n} * sizeof({arr.Element.CsType}))", PPrimary)
                    : ($"sizeof({so.Of.CsType})", PPrimary);
            case Index ix: return ($"{Sub(ix.Base, PPostfix)}[{Expr(ix.Idx)}]", PPostfix);
            case Member m: return ($"{Sub(m.Base, PPostfix)}{(m.Arrow ? "->" : ".")}{DotCC.CSharpEmitter.Id(m.Field)}", PPostfix);
            case Call c: return (CallText(c), PPostfix);
            case CondExpr t:
                // Wrapped (atomic): C-truthy condition, arms isolated by `?`/`:`.
                return ($"(Cond.B({Expr(t.Cond)}) ? {Expr(t.Then)} : {Expr(t.Else)})", PPrimary);
            case CommaSeq cs:
                return (string.Join(", ", cs.Items.Select(it => Sub(it, PAssign))), PComma);
            case Assign a:
                {
                    var op = a.CompoundOp is { } co ? BinSym(co) : "";
                    // Right-associative: value at PAssign keeps `a = b = c` flat. A
                    // plain `=` coerces the value to the target type (C narrowing /
                    // sign / pointer conversions C# requires explicitly); a compound
                    // `+=` already carries C#'s implicit narrowing back to the target.
                    var rhs = a.CompoundOp is null && TryCoerceCast(a.Value, a.Target.Type, out var ct)
                        ? ct
                        : Sub(a.Value, PAssign);
                    return ($"{Sub(a.Target, PUnary)} {op}= {rhs}", PAssign);
                }
            case Unary u: return RenderUnary(u);
            case Binary b: return RenderBinary(b);
            default: throw new IrUnsupportedException("codegen expr " + e.GetType().Name);
        }
    }

    private (string, int) RenderUnary(Unary u)
    {
        switch (u.Op)
        {
            case UnOp.Plus: return ($"+{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.Neg: return ($"-{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.BitNot: return ($"~{Sub(u.Operand, PUnary)}", PUnary);
            // &fn where fn is a function already decays to `&fn` in the VarRef
            // case — don't emit a second `&`.
            case UnOp.AddrOf when u.Operand is VarRef { Sym.Kind: SymKind.Func }: return Render(u.Operand);
            case UnOp.AddrOf: return ($"&{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.Deref: return ($"*{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PreInc: return ($"++{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PreDec: return ($"--{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.PostInc: return ($"{Sub(u.Operand, PPostfix)}++", PPostfix);
            case UnOp.PostDec: return ($"{Sub(u.Operand, PPostfix)}--", PPostfix);
            // C's `!x` yields int 0/1 (never bool); Cond.B picks the truthy overload.
            // Wrapped → atomic.
            case UnOp.LogNot: return ($"(Cond.B({Expr(u.Operand)}) ? 0 : 1)", PPrimary);
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
        switch (b.Op)
        {
            case BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge:
                {
                    var p = Prec(b.Op);
                    return ($"((CBool)({Sub(b.Left, p)} {BinSym(b.Op)} {Sub(b.Right, p + 1)}))", PPrimary);
                }
            case BinOp.LogAnd:
                return ($"((CBool)(Cond.B({Expr(b.Left)}) && Cond.B({Expr(b.Right)})))", PPrimary);
            case BinOp.LogOr:
                return ($"((CBool)(Cond.B({Expr(b.Left)}) || Cond.B({Expr(b.Right)})))", PPrimary);
            default:
                {
                    // Left-associative: right operand at p+1 so same-precedence
                    // right children (`a - (b - c)`) keep their grouping.
                    var p = Prec(b.Op);
                    return ($"{Sub(b.Left, p)} {BinSym(b.Op)} {Sub(b.Right, p + 1)}", p);
                }
        }
    }

    private string CallText(Call c)
    {
        var a = c.Args.Select(x => Sub(x, PAssign)).ToList();
        if (c.Builtin)
        {
            // printf-family fluent lowering — matches the runtime contract:
            // printf(fmt).Arg(x).Arg(y).Done()  /  fprintf(stream, fmt)…  etc.
            var (fixedCount, head) = c.Callee switch
            {
                "printf" => (1, $"printf({Arg(a, 0)})"),
                "fprintf" => (2, $"fprintf({Arg(a, 0)}, {Arg(a, 1)})"),
                "sprintf" => (2, $"sprintf({Arg(a, 0)}, {Arg(a, 1)})"),
                "snprintf" => (3, $"snprintf({Arg(a, 0)}, {Arg(a, 1)}, {Arg(a, 2)})"),
                _ => (a.Count, $"{c.Callee}({string.Join(", ", a)})"),
            };
            var sb = new StringBuilder(head);
            for (var i = fixedCount; i < a.Count; i++) { sb.Append(".Arg(").Append(a[i]).Append(')'); }
            sb.Append(".Done()");
            return sb.ToString();
        }
        return $"{DotCC.CSharpEmitter.Id(c.Callee)}({string.Join(", ", a)})";
    }

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

    private static string Pad(int ind) => new string(' ', ind * 4);
}
