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

        return new CodeGenResult(fns.ToString(), Structs: "", Aliases: "", Globals: "", mainArity, exports);
    }

    // ---- functions -------------------------------------------------------

    private string Func(FuncDef fn)
    {
        var ret = fn.Sym.Type is CType.Func f ? f.Return.CsType : "int";
        var ps = string.Join(", ", fn.Params.Select(p => $"{p.Type.CsType} {p.CsName}"));
        var sb = new StringBuilder();
        sb.Append($"static unsafe {ret} {fn.Sym.CsName}({ps})\n");
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
                sb.Append(pad).Append(DeclInline(d)).Append(";\n");
                break;
            case ExprStmt e:
                sb.Append(pad).Append(Expr(e.Expr)).Append(";\n");
                break;
            case Return r:
                sb.Append(pad).Append(r.Value is null ? "return;" : $"return {Expr(r.Value)};").Append('\n');
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

    private string DeclInline(DeclStmt d)
    {
        var type = d.Decls.Count > 0 ? d.Decls[0].Sym.Type.CsType : "int";
        var parts = d.Decls.Select(e => e.Init is { } init
            ? $"{e.Sym.CsName} = {Expr(init)}"
            : $"{e.Sym.CsName} = default");
        return $"{type} {string.Join(", ", parts)}";
    }

    // ---- expressions -----------------------------------------------------

    private string Expr(CExpr e) => e switch
    {
        LitInt i => i.CsText,
        LitFloat f => f.CsText,
        LitStr s => s.CsExpr,
        Raw r => r.CsText,
        VarRef v => v.Sym.CsName,
        Paren p => $"({Expr(p.Inner)})",
        Unary u => UnaryText(u),
        Binary b => $"{Expr(b.Left)} {BinSym(b.Op)} {Expr(b.Right)}",
        Assign a => $"{Expr(a.Target)} {(a.CompoundOp is { } op ? BinSym(op) : "")}= {Expr(a.Value)}",
        Call c => CallText(c),
        Cast c => $"({c.Target.CsType}){Expr(c.Operand)}",
        _ => throw new IrUnsupportedException("codegen expr " + e.GetType().Name),
    };

    private string UnaryText(Unary u)
    {
        var o = Expr(u.Operand);
        return u.Op switch
        {
            UnOp.Plus => "+" + o,
            UnOp.Neg => "-" + o,
            UnOp.BitNot => "~" + o,
            UnOp.LogNot => "!" + o,
            UnOp.AddrOf => "&" + o,
            UnOp.Deref => "*" + o,
            UnOp.PreInc => "++" + o,
            UnOp.PreDec => "--" + o,
            UnOp.PostInc => o + "++",
            UnOp.PostDec => o + "--",
            _ => throw new IrUnsupportedException("unary " + u.Op),
        };
    }

    private string CallText(Call c)
    {
        var a = c.Args.Select(Expr).ToList();
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
