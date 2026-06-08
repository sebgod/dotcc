#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DotCC.Ir;

/// <summary>
/// Lowers the typed IR to WebAssembly text (<c>.wat</c>) — the second backend
/// behind <see cref="ITarget"/>, a peer of <see cref="CodeGen"/> rather than a
/// rewrite of the pipeline (both consume the same <see cref="IrBuilder"/> via
/// <see cref="DotCC.Compiler.BuildIr"/>). Where CodeGen prints precedence-driven
/// infix C#, this emits a post-order instruction stream for wasm's stack machine:
/// an expression pushes its operands then its operator, and statements lower to
/// wasm's structured control flow (<c>block</c>/<c>loop</c>/<c>if</c>/<c>br</c>).
/// <para>Milestone 1 is the freestanding integer slice — no linear memory, no
/// imports, no libc. Anything outside it (floats, pointers, strings, calls,
/// <c>goto</c>/<c>switch</c>, varargs) raises <see cref="IrUnsupportedException"/>
/// rather than emitting wrong code, and is lifted in later milestones.</para>
/// </summary>
internal sealed class WatBackend
{
    private readonly ITarget _wat = new WatTarget();
    private readonly StringBuilder _sb = new();
    private int _indent;

    // The enclosing loops' branch targets, innermost last: `break` branches to the
    // current loop's Brk block, `continue` to its Cont block (which falls through to
    // the for-post / while-recheck). Reset per function.
    private readonly List<(string Brk, string Cont)> _loops = new();
    private int _labelSeq;

    // Functions defined in this module (by raw C name) — a call to anything else is
    // a library/undefined call, gated until linear memory + host imports land.
    private readonly HashSet<string> _defined = new(System.StringComparer.Ordinal);

    // The current function's return type, so `return E` coerces E to it — like every
    // store position, the IR leaves this implicit conversion for the backend.
    private CType _currentRet = CType.Int;

    public static string Run(IrBuilder unit) => new WatBackend().Module(unit);

    /// <summary>Emit the module shell: one <c>(func …)</c> per definition, plus an
    /// <c>(export "main" …)</c> so a host (wasmtime <c>--invoke</c>, node) can call
    /// the entry point.</summary>
    private string Module(IrBuilder unit)
    {
        Line("(module");
        _indent++;
        foreach (var fn in unit.Functions) { _defined.Add(fn.Sym.Name); }
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

    /// <summary>Emit one function: parameter signature, the result type, every local
    /// declared up front (wasm requires it — see <see cref="CollectLocals"/>), then
    /// the body, then a fall-off terminator so the module validates.</summary>
    private void EmitFunc(FuncDef fn)
    {
        if (fn.Variadic)
        {
            throw new IrUnsupportedException("variadic functions are not supported on the wat target");
        }
        _loops.Clear();
        _labelSeq = 0;

        var ret = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        _currentRet = ret;
        var ps = string.Concat(fn.Params.Select(p => $" (param ${p.TargetName} {_wat.RenderType(p.Type)})"));
        var result = ret.Unqualified is CType.VoidType ? "" : $" (result {_wat.RenderType(ret)})";
        Line($"(func ${fn.Sym.TargetName}{ps}{result}");
        _indent++;

        // wasm locals are function-flat and declared before any instruction.
        var locals = new List<Symbol>();
        foreach (var s in fn.Body.Stmts) { CollectLocals(s, locals); }
        foreach (var loc in locals) { Line($"(local ${loc.TargetName} {_wat.RenderType(loc.Type)})"); }

        foreach (var s in fn.Body.Stmts) { EmitStmt(s); }

        // A function with a result must leave one on the stack at the end. main
        // returns 0 if control reaches its end (C99); any other non-void function
        // falling off the end is UB, so trap. Dead code when the body already
        // returns on all paths — still valid wasm.
        if (ret.Unqualified is not CType.VoidType)
        {
            if (fn.Sym.Name == "main") { Line($"{_wat.RenderType(ret)}.const 0"); Line("return"); }
            else { Line("unreachable"); }
        }

        _indent--;
        Line(")");
    }

    /// <summary>Walk a statement tree collecting every block-local declaration's
    /// symbol, so the function can declare them all up front. (Array locals land
    /// with linear memory; their statement form is gated until then.)</summary>
    private static void CollectLocals(CStmt s, List<Symbol> acc)
    {
        switch (s)
        {
            case Block b:
                foreach (var x in b.Stmts) { CollectLocals(x, acc); }
                break;
            case DeclStmt d:
                foreach (var ld in d.Decls) { acc.Add(ld.Sym); }
                break;
            case If i:
                CollectLocals(i.Then, acc);
                if (i.Else is { } e) { CollectLocals(e, acc); }
                break;
            case While w:
                CollectLocals(w.Body, acc);
                break;
            case DoWhile dw:
                CollectLocals(dw.Body, acc);
                break;
            case For f:
                if (f.Init is { } init) { CollectLocals(init, acc); }
                CollectLocals(f.Body, acc);
                break;
            default:
                // Other statement kinds either declare no locals or aren't in the
                // milestone-1 scope (EmitStmt gates them).
                break;
        }
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

            case DeclStmt d:
                foreach (var ld in d.Decls)
                {
                    if (ld.Init is { } init)
                    {
                        EmitExpr(init);
                        EmitConvert(init.Type, ld.Sym.Type);   // store coercion (the IR leaves it to us)
                        Line($"local.set ${ld.Sym.TargetName}");
                    }
                    // No initializer: wasm zero-inits the local. (Reading an
                    // uninitialised C local is UB anyway.)
                }
                break;

            case ExprStmt es:
                EmitExpr(es.Expr);
                // A statement-expression's value is discarded.
                if (es.Expr.Type.Unqualified is not CType.VoidType) { Line("drop"); }
                break;

            case Return r:
                if (r.Value is { } v)
                {
                    EmitExpr(v);
                    EmitConvert(v.Type, _currentRet);   // coerce to the declared return type
                }
                Line("return");
                break;

            case If i:
                EmitCond(i.Cond);
                Line("if");
                _indent++;
                EmitStmt(i.Then);
                _indent--;
                if (i.Else is { } els)
                {
                    Line("else");
                    _indent++;
                    EmitStmt(els);
                    _indent--;
                }
                Line("end");
                break;

            case While w:
                EmitLoop(cond: w.Cond, body: w.Body, post: null, init: null, testAtTop: true);
                break;

            case DoWhile dw:
                EmitLoop(cond: dw.Cond, body: dw.Body, post: null, init: null, testAtTop: false);
                break;

            case For f:
                if (f.Init is { } fi) { EmitStmt(fi); }
                EmitLoop(cond: f.Cond, body: f.Body, post: f.Post, init: null, testAtTop: true);
                break;

            case Break:
                if (_loops.Count == 0)
                {
                    throw new IrUnsupportedException("`break` outside a loop is not supported on the wat target (switch is a later milestone)");
                }
                Line($"br {_loops[^1].Brk}");
                break;

            case Continue:
                if (_loops.Count == 0)
                {
                    throw new IrUnsupportedException("`continue` outside a loop is not supported on the wat target");
                }
                Line($"br {_loops[^1].Cont}");
                break;

            default:
                throw new IrUnsupportedException($"the wat target does not yet support the statement {s.GetType().Name}");
        }
    }

    /// <summary>The shared loop shape for <c>while</c>/<c>do</c>/<c>for</c>:
    /// <code>
    /// block $brk
    ///   loop $loop
    ///     [testAtTop: cond; i32.eqz; br_if $brk]
    ///     block $cont          ;; continue branches here -> falls through to post
    ///       body
    ///     end
    ///     [post]
    ///     [!testAtTop: cond; br_if $loop]   ;; do-while: test at the bottom
    ///     [testAtTop: br $loop]
    ///   end
    /// end
    /// </code>
    /// The inner <c>$cont</c> block is what makes <c>continue</c> run a
    /// <c>for</c>-loop's post expression (rather than skipping it).</summary>
    private void EmitLoop(CExpr? cond, CStmt body, CExpr? post, CStmt? init, bool testAtTop)
    {
        var n = _labelSeq++;
        string brk = $"$brk{n}", loop = $"$loop{n}", cont = $"$cont{n}";
        Line($"block {brk}");
        _indent++;
        Line($"loop {loop}");
        _indent++;
        if (testAtTop && cond is { } topCond)
        {
            EmitCond(topCond);
            Line("i32.eqz");
            Line($"br_if {brk}");
        }
        Line($"block {cont}");
        _indent++;
        _loops.Add((brk, cont));
        EmitStmt(body);
        _loops.RemoveAt(_loops.Count - 1);
        _indent--;
        Line("end"); // $cont
        if (post is { } p)
        {
            EmitExpr(p);
            if (p.Type.Unqualified is not CType.VoidType) { Line("drop"); }
        }
        if (!testAtTop && cond is { } bottomCond)
        {
            EmitCond(bottomCond);
            Line($"br_if {loop}");
        }
        else
        {
            Line($"br {loop}");
        }
        _indent--;
        Line("end"); // $loop
        _indent--;
        Line("end"); // $brk
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
            case EnumConstRef ec:
                Line($"{ValType(e.Type)}.const {ec.Sym.ConstValue}");
                break;
            case VarRef v:
                if (v.Sym.IsGlobal)
                {
                    throw new IrUnsupportedException("global variables are not yet supported on the wat target");
                }
                Line($"local.get ${v.Sym.TargetName}");
                break;
            case Unary u:
                EmitUnary(u);
                break;
            case Binary b:
                EmitBinary(b);
                break;
            case Assign a:
                EmitAssign(a);
                break;
            case Cast c:
                EmitCast(c);
                break;
            case Call call:
                EmitCall(call);
                break;
            case CondExpr ce:
                EmitCond(ce.Cond);
                Line($"if (result {ValType(ce.Type)})");
                _indent++;
                EmitExpr(ce.Then);
                EmitConvert(ce.Then.Type, ce.Type);
                _indent--;
                Line("else");
                _indent++;
                EmitExpr(ce.Else);
                EmitConvert(ce.Else.Type, ce.Type);
                _indent--;
                Line("end");
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
                // eqz: 0 → 1, non-zero → 0 — yields an i32, exactly C's int result.
                EmitExpr(u.Operand);
                Line($"{vt}.eqz");
                break;
            case UnOp.PreInc:
            case UnOp.PreDec:
            case UnOp.PostInc:
            case UnOp.PostDec:
                EmitIncDec(u);
                break;
            default:
                // & / * (address-of / deref) arrive with the linear-memory milestone.
                throw new IrUnsupportedException($"the wat target does not yet support unary operator {u.Op}");
        }
    }

    /// <summary><c>++</c>/<c>--</c> on a local. Pre-forms leave the new value
    /// (<c>local.tee</c>); post-forms leave the old value then store the updated one.
    /// Pointer arithmetic (scaling by the pointee size) arrives with linear memory.</summary>
    private void EmitIncDec(Unary u)
    {
        if (u.Operand is not VarRef vr || vr.Sym.IsGlobal)
        {
            throw new IrUnsupportedException("the wat target supports ++/-- on local variables only (milestone 1)");
        }
        var name = vr.Sym.TargetName;
        var vt = ValType(u.Operand.Type);
        var op = u.Op is UnOp.PreInc or UnOp.PostInc ? "add" : "sub";
        if (u.Op is UnOp.PostInc or UnOp.PostDec)
        {
            Line($"local.get ${name}");   // old value — the expression's result
            Line($"local.get ${name}");
            Line($"{vt}.const 1");
            Line($"{vt}.{op}");
            Line($"local.set ${name}");
        }
        else
        {
            Line($"local.get ${name}");
            Line($"{vt}.const 1");
            Line($"{vt}.{op}");
            Line($"local.tee ${name}");   // new value — the expression's result
        }
    }

    private void EmitBinary(Binary b)
    {
        // && / || must short-circuit; there is no operator, so they lower to a
        // structured `if` producing a normalised 0/1.
        switch (b.Op)
        {
            case BinOp.LogAnd:
                EmitBool(b.Left);
                Line("if (result i32)");
                _indent++; EmitBool(b.Right); _indent--;
                Line("else");
                _indent++; Line("i32.const 0"); _indent--;
                Line("end");
                return;
            case BinOp.LogOr:
                EmitBool(b.Left);
                Line("if (result i32)");
                _indent++; Line("i32.const 1"); _indent--;
                Line("else");
                _indent++; EmitBool(b.Right); _indent--;
                Line("end");
                return;
        }
        EmitExpr(b.Left);
        EmitExpr(b.Right);
        // The instruction's type prefix is the OPERAND width (a comparison over i64
        // operands is `i64.lt_s` yet still yields an i32); the IR's conversion pass
        // has already coerced both operands to the common type.
        Line(IntBinOp(b.Op, CType.UsualArithmetic(b.Left.Type, b.Right.Type)));
    }

    private void EmitAssign(Assign a)
    {
        if (a.Target is not VarRef vr || vr.Sym.IsGlobal)
        {
            throw new IrUnsupportedException("the wat target supports assignment to local variables only (milestone 1)");
        }
        var name = vr.Sym.TargetName;
        CType produced;
        if (a.CompoundOp is { } cop)
        {
            // a OP= v  ≡  a = a OP v, with both sides promoted to the arithmetic type.
            var common = CType.UsualArithmetic(vr.Type, a.Value.Type);
            Line($"local.get ${name}");
            EmitConvert(vr.Type, common);
            EmitExpr(a.Value);
            EmitConvert(a.Value.Type, common);
            Line(IntBinOp(cop, common));
            produced = common;
        }
        else
        {
            EmitExpr(a.Value);
            produced = a.Value.Type;
        }
        EmitConvert(produced, vr.Type);   // narrow/extend the result to the lvalue type
        // tee stores AND leaves the value — C assignment is an expression.
        Line($"local.tee ${name}");
    }

    private void EmitCast(Cast c)
    {
        if (c.Target.Unqualified is CType.VoidType)
        {
            // (void)expr — evaluate for side effects, discard the value.
            EmitExpr(c.Operand);
            Line("drop");
            return;
        }
        EmitExpr(c.Operand);
        EmitConvert(c.Operand.Type, c.Target);
    }

    /// <summary>Convert the value on top of the stack from <paramref name="from"/> to
    /// <paramref name="to"/> in place — the integer width/sign conversions C performs
    /// implicitly at casts, call arguments and stores: i32↔i64 wrap/extend, then any
    /// sub-word narrowing (see <see cref="NarrowI32"/>).</summary>
    private void EmitConvert(CType from, CType to)
    {
        var toVt = ValType(to);
        var fromVt = ValType(from);
        if (toVt == fromVt)
        {
            NarrowI32(to.Unqualified);
        }
        else if (toVt == "i64" && fromVt == "i32")
        {
            Line(IsSignedInt(from) ? "i64.extend_i32_s" : "i64.extend_i32_u");
        }
        else if (toVt == "i32" && fromVt == "i64")
        {
            Line("i32.wrap_i64");
            NarrowI32(to.Unqualified);
        }
        else
        {
            throw new IrUnsupportedException($"the wat target does not yet support the conversion {from.Describe()} -> {to.Describe()}");
        }
    }

    /// <summary>A direct call. Each argument is coerced to its parameter type (the
    /// implicit conversion C performs at a call); the callee must be a function
    /// defined in this module — library calls (printf, malloc, …) need linear memory
    /// and host imports, a later milestone. (The wat name legalizer escapes
    /// identity, so the raw callee name is its <c>$</c>-name.)</summary>
    private void EmitCall(Call c)
    {
        if (!_defined.Contains(c.Callee))
        {
            throw new IrUnsupportedException($"call to '{c.Callee}': library or undefined functions need linear memory + imports (milestone 2)");
        }
        for (var i = 0; i < c.Args.Count; i++)
        {
            EmitExpr(c.Args[i]);
            if (c.ParamTypes is { } pts && i < pts.Count)
            {
                EmitConvert(c.Args[i].Type, pts[i]);
            }
        }
        Line($"call ${c.Callee}");
    }

    /// <summary>After producing an i32, narrow it to a sub-word target type the way
    /// a store to that C type would: <c>_Bool</c> normalises to 0/1, a signed
    /// <c>char</c>/<c>short</c> sign-extends, an unsigned one masks. (In C# the
    /// narrower storage type did this for free; wasm has only i32.)</summary>
    private void NarrowI32(CType to)
    {
        var u = to.Unqualified;
        if (u is CType.Prim { Name: "_Bool" })
        {
            Line("i32.const 0");
            Line("i32.ne");
            return;
        }
        if (u is CType.Prim { Integer: true, Bytes: var w, Signed: var signed } && w < 4)
        {
            if (signed) { Line(w == 1 ? "i32.extend8_s" : "i32.extend16_s"); }
            else { Line($"i32.const {(w == 1 ? 0xFF : 0xFFFF)}"); Line("i32.and"); }
        }
    }

    // ---- helpers ---------------------------------------------------------

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

    /// <summary>Emit a condition for an <c>if</c>/<c>br_if</c>: leave an i32 where
    /// non-zero means true. An i32 expression is already truthy as-is; an i64 one is
    /// compared to zero.</summary>
    private void EmitCond(CExpr c)
    {
        EmitExpr(c);
        if (ValType(c.Type) == "i64") { Line("i64.const 0"); Line("i64.ne"); }
    }

    /// <summary>Emit a boolean: leave an i32 that is exactly 0 or 1 (for <c>&amp;&amp;</c>
    /// / <c>||</c> results, which C defines as 1/0 rather than any truthy value).</summary>
    private void EmitBool(CExpr c)
    {
        EmitExpr(c);
        if (ValType(c.Type) == "i64") { Line("i64.const 0"); Line("i64.ne"); }
        else { Line("i32.const 0"); Line("i32.ne"); }
    }

    /// <summary>The wasm value type an expression of type <paramref name="t"/> lives
    /// in (<c>i32</c>/<c>i64</c>).</summary>
    private string ValType(CType t) => _wat.RenderType(t);

    private static bool IsSignedInt(CType t) =>
        t.Unqualified is CType.Prim { Integer: true, Signed: true } or CType.Enum;

    private void Line(string text) => _sb.Append(' ', _indent * 2).Append(text).Append('\n');
}
