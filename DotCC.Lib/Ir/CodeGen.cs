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
            // An array member is C-inline storage, not a pointer field. A primitive
            // element lowers to a C# `fixed` buffer (inline, indexable); a
            // non-primitive element (a pointer/struct array) needs an [InlineArray]
            // wrapper, deferred.
            if (f.Type.Unqualified is CType.Array { Count: { } n } arr)
            {
                if (!IsFixedBufferType(arr.Element.CsType))
                {
                    throw new IrUnsupportedException($"non-primitive array struct member '{f.Name}'");
                }
                sb.Append("    public fixed ").Append(arr.Element.CsType).Append(' ')
                  .Append(DotCC.CSharpEmitter.Id(f.Name)).Append('[').Append(n).Append("];\n");
                continue;
            }
            sb.Append("    public ").Append(f.Type.CsType).Append(' ').Append(DotCC.CSharpEmitter.Id(f.Name)).Append(";\n");
        }
        sb.Append("}\n\n");
        return sb.ToString();
    }

    /// <summary>C# permits a <c>fixed</c> buffer only of these primitive element
    /// types — every other array member must go through an <c>[InlineArray]</c>
    /// wrapper instead.</summary>
    internal static bool IsFixedBufferType(string cs) => cs is
        "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint"
        or "long" or "ulong" or "char" or "float" or "double";

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
            case ExprStmt { Expr: CommaOp co }:
                // A statement-level comma discards every operand's value — emit one
                // statement per operand, braced so a braceless nested body (the body
                // of `while (…) (a, b);`) stays a single statement.
                sb.Append(pad).Append("{\n");
                foreach (var item in co.Items) { sb.Append(Pad(ind + 1)).Append(RenderStmtExpr(item)).Append(";\n"); }
                sb.Append(pad).Append("}\n");
                break;
            case ExprStmt e:
                sb.Append(pad).Append(RenderStmtExpr(e.Expr)).Append(";\n");
                break;
            case Return { Value: CommaOp co } when co.Items.Count > 1:
                // Hoist the leading (side-effect) operands as statements, then return
                // the last — keeps a void leading operand out of any value form.
                sb.Append(pad).Append("{\n");
                for (var k = 0; k < co.Items.Count - 1; k++) { sb.Append(Pad(ind + 1)).Append(RenderStmtExpr(co.Items[k])).Append(";\n"); }
                sb.Append(Pad(ind + 1)).Append($"return {Coerced(co.Items[^1], _currentRet)};\n");
                sb.Append(pad).Append("}\n");
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

    // ---- volatile / atomic access ----------------------------------------

    /// <summary>Render a READ of an lvalue, fencing it when the lvalue's type is
    /// qualified: atomic → seq-cst <c>Atomic.Load</c>, volatile →
    /// <c>Volatile.Read</c>, neither → the bare text at its natural precedence.</summary>
    private static (string, int) QualifiedRead(CExpr lv, string bare, int barePrec) =>
        lv.Type.IsAtomic ? ($"Atomic.Load(ref {bare})", PPrimary)
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

    /// <summary>The bare (un-fenced) C# text of an lvalue — for an assignment
    /// target, an <c>&amp;</c> operand, or the <c>ref</c> argument of
    /// Volatile.Read/Write, where the volatile-read wrap must NOT be applied.</summary>
    private string BareLValue(CExpr e) => e switch
    {
        Paren p => BareLValue(p.Inner),
        VarRef v => v.Sym.CsName,
        Member m => $"{Sub(m.Base, PPostfix)}{(m.Arrow ? "->" : ".")}{DotCC.CSharpEmitter.Id(m.Field)}",
        Index ix => $"{Sub(ix.Base, PPostfix)}[{Expr(ix.Idx)}]",
        Unary { Op: UnOp.Deref } u => $"*{Sub(u.Operand, PUnary)}",
        _ => Expr(e),
    };

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
                : QualifiedRead(v, v.Sym.CsName, PPrimary);
            case IndirectCall ic:
                return ($"{Sub(ic.Callee, PPostfix)}({string.Join(", ", ic.Args.Select(a => Sub(a, PAssign)))})", PPostfix);
            case Paren p: return Render(p.Inner); // explicit C parens are redundant; precedence re-adds as needed
            case Cast c: return RenderCast(c);
            case SizeOfExpr so:
                // C's `sizeof` yields `size_t` — unsigned, `ulong` in dotcc's
                // model — but C#'s `sizeof` operator is `int`. Emit the `(ulong)`
                // cast so the usual-arithmetic reconcile treats a sizeof-bearing
                // expression as unsigned (a bare int would be CS0034 against a
                // `ulong`). An array lowered to a pointer, so C# `sizeof` would
                // measure the pointer — emit the true C size `count * sizeof(elem)`.
                return so.Of is CType.Array { Count: { } n } arr
                    ? ($"((ulong)({n} * sizeof({arr.Element.CsType})))", PPrimary)
                    : ($"((ulong)sizeof({so.Of.CsType}))", PPrimary);
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
                var m = DotCC.CSharpEmitter.Id(o.Member);
                var memberAddr = o.MemberDecaysToPointer ? $"(byte*)__t.{m}" : $"(byte*)&__t.{m}";
                return ($"((System.Func<ulong>)(() => {{ {o.StructType.CsType} __t = default; return (ulong)({memberAddr} - (byte*)&__t); }}))()", PPrimary);
            }
            case Index ix:
            {
                var t = $"{Sub(ix.Base, PPostfix)}[{Expr(ix.Idx)}]";
                return QualifiedRead(ix, t, PPostfix);
            }
            case Member m:
            {
                var t = $"{Sub(m.Base, PPostfix)}{(m.Arrow ? "->" : ".")}{DotCC.CSharpEmitter.Id(m.Field)}";
                return QualifiedRead(m, t, PPostfix);
            }
            case StructInit si: return (StructInitText(si), PPrimary);
            case Call c: return (CallText(c), PPostfix);
            case CondExpr t:
                // Wrapped (atomic): C-truthy condition, arms isolated by `?`/`:`.
                return ($"(Cond.B({Expr(t.Cond)}) ? {Expr(t.Then)} : {Expr(t.Else)})", PPrimary);
            case CommaSeq cs:
                return (string.Join(", ", cs.Items.Select(it => Sub(it, PAssign))), PComma);
            case CommaOp co:
                return (CommaValue(co), PPrimary);
            case Assign a when a.Target.Type.IsAtomic:
                {
                    // An atomic lvalue stores seq-cst (Atomic.Store, returns the stored
                    // value); a compound op maps to the *Fetch helper that returns the
                    // NEW value (C's `x op= n` result). The rhs is cast to the lvalue
                    // type so the generic Atomic.* call infers one element type.
                    var lv = BareLValue(a.Target);
                    var cs = a.Target.Type.Unqualified.CsType;
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
                    var stored = a.CompoundOp is { } cop
                        ? $"{VolatileRead(lv)} {BinSym(cop)} {Sub(a.Value, Prec(cop) + 1)}"
                        : Coerced(a.Value, a.Target.Type);
                    return ($"global::System.Threading.Volatile.Write(ref {lv}, {stored})", PPrimary);
                }
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
        // ++/-- of an atomic lvalue is a seq-cst step: prefix yields the NEW value
        // (AddFetch/SubFetch), postfix the OLD (FetchAdd/FetchSub) — matching C.
        if (u.Op is UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec && u.Operand.Type.IsAtomic)
        {
            var lv = BareLValue(u.Operand);
            var cs = u.Operand.Type.Unqualified.CsType;
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
            case UnOp.Plus: return ($"+{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.Neg: return ($"-{Sub(u.Operand, PUnary)}", PUnary);
            case UnOp.BitNot: return ($"~{Sub(u.Operand, PUnary)}", PUnary);
            // &fn where fn is a function already decays to `&fn` in the VarRef
            // case — don't emit a second `&`.
            case UnOp.AddrOf when u.Operand is VarRef { Sym.Kind: SymKind.Func }: return Render(u.Operand);
            // BareLValue so `&` of a volatile lvalue takes the address, not the
            // address of a Volatile.Read(...) call.
            case UnOp.AddrOf: return ($"&{BareLValue(u.Operand)}", PUnary);
            case UnOp.Deref: return QualifiedRead(u, $"*{Sub(u.Operand, PUnary)}", PUnary);
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
                return ($"((CBool)(Cond.B({Expr(b.Left)}) && Cond.B({Expr(b.Right)})))", PPrimary);
            case BinOp.LogOr:
                return ($"((CBool)(Cond.B({Expr(b.Left)}) || Cond.B({Expr(b.Right)})))", PPrimary);
            case BinOp.Shl or BinOp.Shr:
                {
                    // A shift's operands are promoted INDEPENDENTLY (the right
                    // operand doesn't join the left's type), so no reconcile.
                    var p = Prec(b.Op);
                    return ($"{Sub(b.Left, p)} {BinSym(b.Op)} {Sub(b.Right, p + 1)}", p);
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
            return (ReconcileOne(left, lt.CsName, common.CsName, lp),
                    ReconcileOne(right, rt.CsName, common.CsName, rp));
        }
        return (Sub(left, lp), Sub(right, rp));
    }

    /// <summary>True when <paramref name="t"/> is a function pointer — in dotcc's
    /// IR a fn-ptr is a bare <see cref="CType.Func"/> (its <c>CsType</c> is already
    /// <c>delegate*&lt;…&gt;</c>); <c>Pointer(Func)</c> is tolerated for safety.</summary>
    private static bool IsFnPtrType(CType t) =>
        t.Unqualified is CType.Func or CType.Pointer { Pointee: CType.Func };

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
            ? $"({inner.Type.CsType})({Sub(e, PUnary)})"
            : Sub(e, p);
    }

    private string ReconcileOne(CExpr e, string from, string to, int p) =>
        from != to && !CsImplicitInt(from, to)
            ? $"({to})({Sub(e, PUnary)})"   // cast binds at PUnary — no outer parens needed
            : Sub(e, p);

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
        var text = $"({c.Target.CsType}){Sub(c.Operand, PUnary)}";
        if (c.Target.Unqualified is CType.Prim { Integer: true } pt
            && IsConstExpr(c.Operand)
            && !(TryConstInt(c.Operand, out var cv) && ConstFitsTarget(cv, pt.CsName)))
        {
            return ($"unchecked({text})", PPrimary);
        }
        return (text, PUnary);
    }

    /// <summary>True when <paramref name="e"/> is a C constant expression — only
    /// literals, <c>sizeof</c>, and operators over constant operands; no variable
    /// reads or calls. (Enum constants are already lowered to integer literals.)
    /// Gates the <c>unchecked</c> wrapper above for constants the folder can't
    /// reduce to a value.</summary>
    private static bool IsConstExpr(CExpr e) => e switch
    {
        LitInt or LitFloat or SizeOfExpr => true,
        Paren p => IsConstExpr(p.Inner),
        Cast c => IsConstExpr(c.Operand),
        Unary u => u.Op is UnOp.Plus or UnOp.Neg or UnOp.BitNot or UnOp.LogNot && IsConstExpr(u.Operand),
        Binary { Op: not (BinOp.LogAnd or BinOp.LogOr) } b => IsConstExpr(b.Left) && IsConstExpr(b.Right),
        CondExpr t => IsConstExpr(t.Cond) && IsConstExpr(t.Then) && IsConstExpr(t.Else),
        _ => false,
    };

    /// <summary>Render a positional aggregate initializer as a C# object
    /// initializer — <c>new Point { x = 3, y = 4 }</c>. Each value is coerced to
    /// its field type (C's implicit store conversion); unsupplied trailing fields
    /// are omitted, so C# zero-fills them (C's partial-init rule).</summary>
    private string StructInitText(StructInit si)
    {
        var sb = new StringBuilder("new ").Append(si.Type.CsType).Append(" { ");
        for (var i = 0; i < si.Members.Count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            var m = si.Members[i];
            sb.Append(DotCC.CSharpEmitter.Id(m.Name)).Append(" = ").Append(Coerced(m.Value, m.FieldType));
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
        Assign or Call or IndirectCall => true,
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
        var items = co.Items;
        var leadingVoid = false;
        for (var i = 0; i < items.Count - 1; i++)
        {
            if (items[i].Type.Unqualified is CType.VoidType) { leadingVoid = true; break; }
        }
        return !leadingVoid && items.Count <= 7 ? CommaTuple(items) : CommaDelegate(items);
    }

    private string CommaTuple(IReadOnlyList<CExpr> items)
    {
        // A pointer operand can't be a tuple type argument — cast it to nint for the
        // tuple; if the VALUE (last) is a pointer, cast .ItemN back to its type.
        var elems = items.Select(e =>
            IsPointerType(e.Type) ? $"(nint)({Sub(e, PUnary)})" : Sub(e, PAssign));
        var pick = $"({string.Join(", ", elems)}).Item{items.Count}";
        return IsPointerType(items[^1].Type) ? $"(({items[^1].Type.CsType})({pick}))" : $"({pick})";
    }

    private string CommaDelegate(IReadOnlyList<CExpr> items)
    {
        var body = new StringBuilder();
        for (var i = 0; i < items.Count - 1; i++) { body.Append(RenderStmtExpr(items[i])).Append("; "); }
        var last = items[^1];
        // A pointer value can't be a Func<> type argument either — round-trip nint.
        return IsPointerType(last.Type)
            ? $"(({last.Type.CsType})((System.Func<nint>)(() => {{ {body}return (nint)({Expr(last)}); }}))())"
            : $"((System.Func<{last.Type.CsType}>)(() => {{ {body}return {Expr(last)}; }}))()";
    }

    private string CallText(Call c)
    {
        // Coerce each argument to its parameter type (C's implicit conversion at
        // a call C# requires explicit) when the callee's signature is known; the
        // variadic tail (index ≥ fixed-param count) and unknown-signature callees
        // pass through unchanged.
        var a = new List<string>(c.Args.Count);
        for (var i = 0; i < c.Args.Count; i++)
        {
            a.Add(c.ParamTypes is { } pts && i < pts.Count
                ? CoercedArg(c.Args[i], pts[i])
                : Sub(c.Args[i], PAssign));
        }
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
