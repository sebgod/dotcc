#nullable enable

using System.Text;

namespace DotCC.Ir;

/// <summary>
/// Lowers the typed IR to WebAssembly text (<c>.wat</c>) — the second backend
/// behind <see cref="ITarget"/>, a peer of <see cref="CodeGen"/> rather than a
/// rewrite of the pipeline (both consume the same <see cref="IrBuilder"/> via
/// <see cref="DotCC.Compiler.BuildIr"/>). Where CodeGen prints precedence-driven
/// infix C#, this emits a post-order instruction stream for wasm's stack machine:
/// an expression pushes its operands then its operator, and statements lower to
/// wasm's structured control flow.
/// <para>Milestone 1 is the freestanding integer slice — no linear memory, no
/// imports, no libc. Anything outside it (floats, pointers, strings, calls with
/// parameters, <c>goto</c>/<c>switch</c>, varargs) raises
/// <see cref="IrUnsupportedException"/> rather than emitting wrong code, and is
/// lifted in later milestones.</para>
/// </summary>
internal sealed class WatBackend
{
    private readonly ITarget _wat = new WatTarget();
    private readonly StringBuilder _sb = new();
    private int _indent;

    public static string Run(IrBuilder unit) => new WatBackend().Module(unit);

    /// <summary>Emit the module shell: one <c>(func …)</c> per definition, plus an
    /// <c>(export "main" …)</c> so a host (wasmtime <c>--invoke</c>, node) can call
    /// the entry point.</summary>
    private string Module(IrBuilder unit)
    {
        Line("(module");
        _indent++;
        var hasMain = false;
        foreach (var fn in unit.Functions)
        {
            EmitFunc(fn);
            if (fn.Sym.Name == "main") { hasMain = true; }
        }
        if (hasMain) { Line("(export \"main\" (func $main))"); }
        _indent--;
        Line(")");
        return _sb.ToString();
    }

    /// <summary>Emit one function. Milestone 1: no parameters yet (so only
    /// <c>main(void)</c> and other niladic functions), no locals.</summary>
    private void EmitFunc(FuncDef fn)
    {
        if (fn.Variadic)
        {
            throw new IrUnsupportedException("variadic functions are not supported on the wat target");
        }
        if (fn.Params.Count > 0)
        {
            throw new IrUnsupportedException("function parameters are not yet supported on the wat target");
        }
        var ret = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        var result = ret.Unqualified is CType.VoidType ? "" : $" (result {_wat.RenderType(ret)})";
        // The `$`-name is the symbol's TargetName (a wasm-specific legalizer arrives
        // with locals in the naming milestone; simple C names need no escaping).
        Line($"(func ${fn.Sym.TargetName}{result}");
        _indent++;
        foreach (var s in fn.Body.Stmts) { EmitStmt(s); }
        _indent--;
        Line(")");
    }

    // ---- statements ------------------------------------------------------

    private void EmitStmt(CStmt s)
    {
        switch (s)
        {
            case Block b:
                // wasm blocks are branch targets, not lexical scopes (locals are
                // function-flat), so a C block is just its statements inline.
                foreach (var inner in b.Stmts) { EmitStmt(inner); }
                break;
            case Return r:
                if (r.Value is { } v) { EmitExpr(v); }
                Line("return");
                break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support the statement {s.GetType().Name}");
        }
    }

    // ---- expressions (post-order onto the stack) -------------------------

    private void EmitExpr(CExpr e)
    {
        switch (e)
        {
            case Paren p:
                EmitExpr(p.Inner);
                break;
            case LitInt n:
                Line($"{ValType(e.Type)}.const {_wat.RenderIntLit(n)}");
                break;
            case Unary u:
                EmitUnary(u);
                break;
            case Binary b:
                EmitBinary(b);
                break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support the expression {e.GetType().Name}");
        }
    }

    private void EmitUnary(Unary u)
    {
        var vt = ValType(u.Operand.Type);
        switch (u.Op)
        {
            case UnOp.Plus:
                EmitExpr(u.Operand);
                break;
            case UnOp.Neg:
                // wasm has no integer negate: 0 - x.
                Line($"{vt}.const 0");
                EmitExpr(u.Operand);
                Line($"{vt}.sub");
                break;
            case UnOp.BitNot:
                EmitExpr(u.Operand);
                Line($"{vt}.const -1");
                Line($"{vt}.xor");
                break;
            case UnOp.LogNot:
                // eqz: 0 → 1, non-zero → 0 — and yields an i32, exactly C's int result.
                EmitExpr(u.Operand);
                Line($"{vt}.eqz");
                break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support unary operator {u.Op}");
        }
    }

    private void EmitBinary(Binary b)
    {
        if (b.Op is BinOp.LogAnd or BinOp.LogOr)
        {
            // && / || must short-circuit; they lower to a structured `if` (no operator
            // exists), which arrives with the control-flow milestone.
            throw new IrUnsupportedException("short-circuit && / || are not yet supported on the wat target");
        }
        EmitExpr(b.Left);
        EmitExpr(b.Right);
        // The instruction's type prefix is the OPERAND width (a comparison over i64
        // operands is `i64.lt_s` and still yields an i32 result), and the IR's
        // conversion pass has already coerced both operands to the common type.
        Line(IntBinOp(b.Op, CType.UsualArithmetic(b.Left.Type, b.Right.Type)));
    }

    /// <summary>An integer binary operator → its wasm instruction, picking the
    /// signed/unsigned variant (<c>div_s</c>/<c>div_u</c>, <c>shr_s</c>/<c>shr_u</c>,
    /// the relational ops) from the operand's signedness — where the C# backend let
    /// the operand's C# type carry it.</summary>
    private string IntBinOp(BinOp op, CType operand)
    {
        var vt = ValType(operand);
        var x = IsSignedInt(operand) ? "s" : "u";
        return op switch
        {
            BinOp.Add => $"{vt}.add",
            BinOp.Sub => $"{vt}.sub",
            BinOp.Mul => $"{vt}.mul",
            BinOp.Div => $"{vt}.div_{x}",
            BinOp.Mod => $"{vt}.rem_{x}",
            BinOp.BitAnd => $"{vt}.and",
            BinOp.BitOr => $"{vt}.or",
            BinOp.BitXor => $"{vt}.xor",
            BinOp.Shl => $"{vt}.shl",
            BinOp.Shr => $"{vt}.shr_{x}",
            BinOp.Eq => $"{vt}.eq",
            BinOp.Ne => $"{vt}.ne",
            BinOp.Lt => $"{vt}.lt_{x}",
            BinOp.Gt => $"{vt}.gt_{x}",
            BinOp.Le => $"{vt}.le_{x}",
            BinOp.Ge => $"{vt}.ge_{x}",
            _ => throw new IrUnsupportedException($"the wat target does not support binary operator {op}"),
        };
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>The wasm value type an expression of type <paramref name="t"/> lives
    /// in (<c>i32</c>/<c>i64</c>).</summary>
    private string ValType(CType t) => _wat.RenderType(t);

    private static bool IsSignedInt(CType t) =>
        t.Unqualified is CType.Prim { Integer: true, Signed: true } or CType.Enum;

    private void Line(string text) => _sb.Append(' ', _indent * 2).Append(text).Append('\n');
}
