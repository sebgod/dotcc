#nullable enable

using System;
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
/// an expression pushes its operands then its operator, statements lower to wasm's
/// structured control flow, and lvalues live either in fast wasm locals or, when
/// their address is taken (or they're arrays), in a linear-memory shadow stack.
/// <para>Milestones: 1 = the freestanding integer slice. 2 = the runtime track —
/// linear memory, string-literal data segments, pointer load/store/arithmetic, the
/// shadow stack for address-taken locals and arrays (this commit). I/O via WASI and
/// malloc follow. Anything still outside the slice (floats, structs, goto/switch,
/// varargs, library calls) raises <see cref="IrUnsupportedException"/>.</para>
/// </summary>
internal sealed class WatBackend
{
    // Top of the shadow stack — it grows DOWN from the end of the first page; string
    // data grows UP from a 1 KiB null guard. Small programs never collide.
    private const int StackTop = 65536;
    private const int DataBase = 1024;

    private readonly ITarget _wat = new WatTarget();
    private readonly StringBuilder _sb = new();
    // The current output target. EmitFunc redirects it to a per-function body buffer
    // so the (local …) declarations (incl. lazily-needed scratch) can be written
    // ahead of a body already emitted.
    private StringBuilder _out;
    private int _indent;

    private readonly List<(string Brk, string Cont)> _loops = new();
    private int _labelSeq;
    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
    private CType _currentRet = CType.Int;

    // Per-function shadow-stack frame: symbol → byte offset within the frame, the
    // frame's total size, and whether the function has one at all. Memory-resident
    // symbols are the address-taken ones (Symbol.AddressTaken) plus all arrays.
    private readonly Dictionary<Symbol, int> _frame = new();
    private int _frameSize;
    private bool _hasFrame;
    // Scratch locals, declared only when used (the body buffer makes that possible):
    // a saved store value (i32/i64) and a saved store address (i32) for the
    // read-modify-write of a compound assignment through memory.
    private bool _scratch32, _scratch64, _scratchAddr;

    // Interned string literals → linear-memory data segments.
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private readonly List<(int Offset, string Hex)> _strData = new();
    private int _dataEnd = DataBase;

    private WatBackend() { _out = _sb; }

    public static string Run(IrBuilder unit) => new WatBackend().Module(unit);

    /// <summary>Assemble the module: emit the function bodies first (interning string
    /// literals into data segments), then wrap them with the linear memory, the stack
    /// pointer global, the data segments, and the <c>main</c> export.</summary>
    private string Module(IrBuilder unit)
    {
        foreach (var fn in unit.Functions) { _defined.Add(fn.Sym.Name); }

        _indent = 1;
        var hasMain = false;
        foreach (var fn in unit.Functions)
        {
            EmitFunc(fn);
            if (fn.Sym.Name == "main") { hasMain = true; }
        }
        var funcs = _sb.ToString();

        var m = new StringBuilder();
        m.Append("(module\n");
        m.Append("  (memory 1)\n");
        m.Append($"  (global $__sp (mut i32) (i32.const {StackTop}))\n");
        foreach (var (off, hex) in _strData)
        {
            m.Append("  (data (i32.const ").Append(off).Append(") \"").Append(hex).Append("\")\n");
        }
        m.Append(funcs);
        if (hasMain) { m.Append("  (export \"main\" (func $main))\n"); }
        m.Append(")\n");
        return m.ToString();
    }

    /// <summary>Emit one function: lay out its shadow-stack frame, emit the body into
    /// a buffer (so locals — including lazily-needed scratch — can be declared ahead
    /// of it), then compose header + locals + body. The frame is set up on entry and
    /// restored on every exit (so recursion is sound).</summary>
    private void EmitFunc(FuncDef fn)
    {
        if (fn.Variadic)
        {
            throw new IrUnsupportedException("variadic functions are not supported on the wat target");
        }
        _loops.Clear();
        _labelSeq = 0;
        _frame.Clear();
        _frameSize = 0;
        _scratch32 = _scratch64 = _scratchAddr = false;

        var ret = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        _currentRet = ret;

        // Classify storage: address-taken symbols (params or locals) and arrays live
        // in the frame; every other scalar local is a fast wasm value local.
        var valueLocals = new List<Symbol>();
        var spillParams = new List<Symbol>();
        var cursor = 0;
        void Place(Symbol s)
        {
            var align = SlotAlign(s.Type);
            cursor = AlignUp(cursor, align);
            _frame[s] = cursor;
            cursor += Math.Max(1, WasmSizeOf(s.Type));
        }
        // The IR records Symbol.AddressTaken only for globals (the C# backend's nint
        // decision keys on it for any VarRef, so widening it to locals would change
        // C# codegen). The wat backend therefore finds address-taken locals/params
        // itself — a missed case merely gates `&x` loudly, never miscompiles.
        var addrTaken = new HashSet<Symbol>();
        foreach (var s in fn.Body.Stmts) { ScanAddrTaken(s, addrTaken); }
        foreach (var p in fn.Params)
        {
            if (addrTaken.Contains(p)) { Place(p); spillParams.Add(p); }
        }
        var bodyLocals = new List<Symbol>();
        foreach (var s in fn.Body.Stmts) { CollectLocals(s, bodyLocals); }
        foreach (var loc in bodyLocals)
        {
            if (addrTaken.Contains(loc) || loc.Type.Unqualified is CType.Array) { Place(loc); }
            else { valueLocals.Add(loc); }
        }
        _frameSize = AlignUp(cursor, 8);
        _hasFrame = _frameSize > 0;

        var ps = string.Concat(fn.Params.Select(p => $" (param ${p.TargetName} {_wat.RenderType(p.Type)})"));
        var result = ret.Unqualified is CType.VoidType ? "" : $" (result {_wat.RenderType(ret)})";
        Line($"(func ${fn.Sym.TargetName}{ps}{result}");
        _indent++;

        // Emit the body into a buffer first; this discovers which scratch locals it
        // needs and interns strings, so the (local …) block below is complete.
        var body = new StringBuilder();
        var prev = _out;
        _out = body;
        if (_hasFrame)
        {
            Line("global.get $__sp");
            Line("local.tee $__fp");          // save caller's SP
            Line($"i32.const {_frameSize}");
            Line("i32.sub");
            Line("global.set $__sp");         // reserve the frame
            foreach (var p in spillParams)    // address-taken params: spill into the frame
            {
                EmitFrameAddr(_frame[p]);
                Line($"local.get ${p.TargetName}");
                Line(StoreInstr(p.Type));
            }
        }
        foreach (var s in fn.Body.Stmts) { EmitStmt(s); }
        EmitFnEnd(fn, ret);
        _out = prev;

        foreach (var v in valueLocals) { Line($"(local ${v.TargetName} {_wat.RenderType(v.Type)})"); }
        if (_hasFrame) { Line("(local $__fp i32)"); }
        if (_scratch32) { Line("(local $__t32 i32)"); }
        if (_scratch64) { Line("(local $__t64 i64)"); }
        if (_scratchAddr) { Line("(local $__taddr i32)"); }
        _out.Append(body);

        _indent--;
        Line(")");
    }

    /// <summary>The function's fall-off terminator: restore SP, then leave a result.
    /// main returns 0 (C99); any other non-void function falling off is UB → trap.
    /// (Explicit returns restore SP themselves — see the Return case.)</summary>
    private void EmitFnEnd(FuncDef fn, CType ret)
    {
        if (ret.Unqualified is CType.VoidType)
        {
            RestoreSp();
            return;
        }
        if (fn.Sym.Name == "main")
        {
            RestoreSp();
            Line($"{_wat.RenderType(ret)}.const 0");
            Line("return");
        }
        else
        {
            Line("unreachable");
        }
    }

    /// <summary>Walk a statement tree collecting block-local declaration symbols
    /// (both scalar <see cref="DeclStmt"/> and <see cref="ArrayDecl"/>), so the
    /// function declares/places them all up front.</summary>
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
            case ArrayDecl ad:
                acc.Add(ad.Sym);
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
                break;
        }
    }

    /// <summary>Collect the symbols whose address is taken (<c>&amp;x</c>) anywhere in
    /// a statement, so they get a linear-memory frame slot. Conservative by
    /// construction: an un-scanned expression position simply doesn't mark its
    /// symbols, and a later <c>&amp;</c> on one of them then fails loudly.</summary>
    private static void ScanAddrTaken(CStmt s, HashSet<Symbol> taken)
    {
        switch (s)
        {
            case Block b:
                foreach (var x in b.Stmts) { ScanAddrTaken(x, taken); }
                break;
            case DeclStmt d:
                foreach (var ld in d.Decls) { if (ld.Init is { } init) { ScanAddrExpr(init, taken); } }
                break;
            case ArrayDecl ad:
                if (ad.Inits is { } inits) { foreach (var el in inits) { ScanAddrExpr(el, taken); } }
                break;
            case ExprStmt es:
                ScanAddrExpr(es.Expr, taken);
                break;
            case Return r:
                if (r.Value is { } rv) { ScanAddrExpr(rv, taken); }
                break;
            case If iff:
                ScanAddrExpr(iff.Cond, taken);
                ScanAddrTaken(iff.Then, taken);
                if (iff.Else is { } els) { ScanAddrTaken(els, taken); }
                break;
            case While w:
                ScanAddrExpr(w.Cond, taken);
                ScanAddrTaken(w.Body, taken);
                break;
            case DoWhile dw:
                ScanAddrExpr(dw.Cond, taken);
                ScanAddrTaken(dw.Body, taken);
                break;
            case For f:
                if (f.Init is { } fi) { ScanAddrTaken(fi, taken); }
                if (f.Cond is { } fc) { ScanAddrExpr(fc, taken); }
                if (f.Post is { } fp) { ScanAddrExpr(fp, taken); }
                ScanAddrTaken(f.Body, taken);
                break;
            default:
                break;
        }
    }

    private static void ScanAddrExpr(CExpr e, HashSet<Symbol> taken)
    {
        switch (e)
        {
            case Unary { Op: UnOp.AddrOf } ua:
                if (Unparen(ua.Operand) is VarRef v && !v.Sym.IsGlobal) { taken.Add(v.Sym); }
                ScanAddrExpr(ua.Operand, taken);
                break;
            case Unary u:
                ScanAddrExpr(u.Operand, taken);
                break;
            case Binary b:
                ScanAddrExpr(b.Left, taken);
                ScanAddrExpr(b.Right, taken);
                break;
            case Assign a:
                ScanAddrExpr(a.Target, taken);
                ScanAddrExpr(a.Value, taken);
                break;
            case Index ix:
                ScanAddrExpr(ix.Base, taken);
                ScanAddrExpr(ix.Idx, taken);
                break;
            case Cast c:
                ScanAddrExpr(c.Operand, taken);
                break;
            case Call call:
                foreach (var arg in call.Args) { ScanAddrExpr(arg, taken); }
                break;
            case CondExpr ce:
                ScanAddrExpr(ce.Cond, taken);
                ScanAddrExpr(ce.Then, taken);
                ScanAddrExpr(ce.Else, taken);
                break;
            case Paren p:
                ScanAddrExpr(p.Inner, taken);
                break;
            default:
                break;
        }
    }

    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    // ---- statements ------------------------------------------------------

    private void EmitStmt(CStmt s)
    {
        switch (s)
        {
            case Block b:
                foreach (var inner in b.Stmts) { EmitStmt(inner); }
                break;

            case DeclStmt d:
                foreach (var ld in d.Decls)
                {
                    if (ld.Init is not { } init) { continue; }
                    if (_frame.TryGetValue(ld.Sym, out var off))
                    {
                        // Address-taken scalar initialised in its frame slot.
                        EmitFrameAddr(off);
                        EmitExpr(init);
                        EmitConvert(init.Type, ld.Sym.Type);
                        Line(StoreInstr(ld.Sym.Type));
                    }
                    else
                    {
                        EmitExpr(init);
                        EmitConvert(init.Type, ld.Sym.Type);
                        Line($"local.set ${ld.Sym.TargetName}");
                    }
                }
                break;

            case ArrayDecl ad:
                EmitArrayDecl(ad);
                break;

            case ExprStmt es:
                EmitExpr(es.Expr);
                if (es.Expr.Type.Unqualified is not CType.VoidType) { Line("drop"); }
                break;

            case Return r:
                if (r.Value is { } v)
                {
                    EmitExpr(v);
                    EmitConvert(v.Type, _currentRet);
                }
                RestoreSp();   // stack-neutral: leaves any return value in place
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
                EmitLoop(cond: w.Cond, body: w.Body, post: null, testAtTop: true);
                break;

            case DoWhile dw:
                EmitLoop(cond: dw.Cond, body: dw.Body, post: null, testAtTop: false);
                break;

            case For f:
                if (f.Init is { } fi) { EmitStmt(fi); }
                EmitLoop(cond: f.Cond, body: f.Body, post: f.Post, testAtTop: true);
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

    /// <summary>A local array lives in the shadow-stack frame. A brace initializer
    /// stores each element into its slot; an uninitialised array is left as-is
    /// (reading it before assignment is UB in C, and the frame is reused memory).</summary>
    private void EmitArrayDecl(ArrayDecl ad)
    {
        if (!_frame.TryGetValue(ad.Sym, out var baseOff))
        {
            throw new IrUnsupportedException("the wat target could not place array local in the frame");
        }
        if (ad.Inits is not { } inits) { return; }
        var elemSize = WasmSizeOf(ad.Element);
        for (var i = 0; i < inits.Count; i++)
        {
            EmitFrameAddr(baseOff + i * elemSize);
            EmitExpr(inits[i]);
            EmitConvert(inits[i].Type, ad.Element);
            Line(StoreInstr(ad.Element));
        }
    }

    private void EmitLoop(CExpr? cond, CStmt body, CExpr? post, bool testAtTop)
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
            case LitStr ls:
                Line($"i32.const {InternString(ls.Segments)}");
                break;
            case NullPtr:
                Line("i32.const 0");
                break;
            case EnumConstRef ec:
                Line($"{ValType(e.Type)}.const {ec.Sym.ConstValue}");
                break;
            case VarRef v:
                EmitVarRead(v);
                break;
            case Index ix:
                if (e.Type.Unqualified is CType.Array) { EmitAddress(ix); break; }  // nested-array decay
                EmitAddress(ix);
                Line(LoadInstr(e.Type));
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

    /// <summary>Read a variable: a frame-resident array decays to its address, a
    /// frame-resident scalar loads from its slot, a fast local is a <c>local.get</c>.</summary>
    private void EmitVarRead(VarRef v)
    {
        if (v.Sym.IsGlobal)
        {
            throw new IrUnsupportedException("global variables are not yet supported on the wat target");
        }
        if (_frame.TryGetValue(v.Sym, out var off))
        {
            EmitFrameAddr(off);
            if (v.Sym.Type.Unqualified is not CType.Array) { Line(LoadInstr(v.Sym.Type)); }
            return;
        }
        Line($"local.get ${v.Sym.TargetName}");
    }

    private void EmitUnary(Unary u)
    {
        switch (u.Op)
        {
            case UnOp.Plus:
                EmitExpr(u.Operand);
                break;
            case UnOp.Neg:
                Line($"{ValType(u.Operand.Type)}.const 0");
                EmitExpr(u.Operand);
                Line($"{ValType(u.Operand.Type)}.sub");
                break;
            case UnOp.BitNot:
                EmitExpr(u.Operand);
                Line($"{ValType(u.Operand.Type)}.const -1");
                Line($"{ValType(u.Operand.Type)}.xor");
                break;
            case UnOp.LogNot:
                EmitExpr(u.Operand);
                Line($"{ValType(u.Operand.Type)}.eqz");
                break;
            case UnOp.AddrOf:
                // &lvalue — the address machinery (frame slot, *p, a[i]).
                EmitAddress(u.Operand);
                break;
            case UnOp.Deref:
                EmitExpr(u.Operand);
                if (u.Type.Unqualified is not CType.Array) { Line(LoadInstr(u.Type)); }
                break;
            case UnOp.PreInc:
            case UnOp.PreDec:
            case UnOp.PostInc:
            case UnOp.PostDec:
                EmitIncDec(u);
                break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support unary operator {u.Op}");
        }
    }

    /// <summary><c>++</c>/<c>--</c>. On a fast local it read-modify-writes the local;
    /// on a memory lvalue (frame scalar, <c>*p</c>, <c>a[i]</c>) it goes through the
    /// address. A pointer steps by its pointee size.</summary>
    private void EmitIncDec(Unary u)
    {
        var t = u.Operand.Type.Unqualified;
        var vt = ValType(u.Operand.Type);
        var op = u.Op is UnOp.PreInc or UnOp.PostInc ? "add" : "sub";
        var step = t is CType.Pointer or CType.Array ? WasmSizeOf(ElementType(t)) : 1;
        var post = u.Op is UnOp.PostInc or UnOp.PostDec;

        if (u.Operand is VarRef vr && !vr.Sym.IsGlobal && !_frame.ContainsKey(vr.Sym))
        {
            var name = vr.Sym.TargetName;
            if (post)
            {
                Line($"local.get ${name}");
                Line($"local.get ${name}");
                Line($"{vt}.const {step}");
                Line($"{vt}.{op}");
                Line($"local.set ${name}");
            }
            else
            {
                Line($"local.get ${name}");
                Line($"{vt}.const {step}");
                Line($"{vt}.{op}");
                Line($"local.tee ${name}");
            }
            return;
        }

        // Memory lvalue: addr (saved), load old, compute new, store, leave old|new.
        var valScratch = ScratchFor(u.Operand.Type);
        _scratchAddr = true;
        EmitAddress(u.Operand);
        Line("local.set $__taddr");
        Line("local.get $__taddr");
        Line(LoadInstr(u.Operand.Type));   // old value
        Line($"local.set {valScratch}");   // keep it
        Line("local.get $__taddr");
        Line($"local.get {valScratch}");
        Line($"{vt}.const {step}");
        Line($"{vt}.{op}");                 // new value
        if (!post) { Line($"local.tee {valScratch}"); }   // pre: result is the new value
        Line(StoreInstr(u.Operand.Type));
        Line($"local.get {valScratch}");    // post: old; pre: new (tee'd above)
    }

    private void EmitBinary(Binary b)
    {
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

        var lptr = b.Left.Type.Unqualified is CType.Pointer or CType.Array;
        var rptr = b.Right.Type.Unqualified is CType.Pointer or CType.Array;
        if (lptr || rptr)
        {
            EmitPtrBinary(b, lptr, rptr);
            return;
        }

        EmitExpr(b.Left);
        EmitExpr(b.Right);
        Line(IntBinOp(b.Op, CType.UsualArithmetic(b.Left.Type, b.Right.Type)));
    }

    /// <summary>Pointer +/- integer (scaled by the pointee size), pointer - pointer
    /// (element distance, widened to ptrdiff_t), and pointer comparisons.</summary>
    private void EmitPtrBinary(Binary b, bool lptr, bool rptr)
    {
        switch (b.Op)
        {
            case BinOp.Add:
            {
                var ptr = lptr ? b.Left : b.Right;
                var idx = lptr ? b.Right : b.Left;
                EmitExpr(ptr);
                EmitScaledIndex(idx, ElementType(ptr.Type));
                Line("i32.add");
                return;
            }
            case BinOp.Sub when lptr && rptr:
            {
                var size = WasmSizeOf(ElementType(b.Left.Type));
                EmitExpr(b.Left);
                EmitExpr(b.Right);
                Line("i32.sub");
                if (size != 1) { Line($"i32.const {size}"); Line("i32.div_s"); }
                if (ValType(b.Type) == "i64") { Line("i64.extend_i32_s"); }
                return;
            }
            case BinOp.Sub when lptr:
                EmitExpr(b.Left);
                EmitScaledIndex(b.Right, ElementType(b.Left.Type));
                Line("i32.sub");
                return;
            case BinOp.Eq: case BinOp.Ne:
            case BinOp.Lt: case BinOp.Gt: case BinOp.Le: case BinOp.Ge:
                EmitExpr(b.Left);
                EmitExpr(b.Right);
                Line(PtrCmp(b.Op));
                return;
            default:
                throw new IrUnsupportedException($"the wat target does not support pointer operator {b.Op}");
        }
    }

    private void EmitAssign(Assign a)
    {
        // Fast path: a plain wasm value local — store-and-keep via local.tee.
        if (a.Target is VarRef vr && !vr.Sym.IsGlobal && !_frame.ContainsKey(vr.Sym))
        {
            CType produced;
            if (a.CompoundOp is { } cop)
            {
                var common = CType.UsualArithmetic(vr.Type, a.Value.Type);
                Line($"local.get ${vr.Sym.TargetName}");
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
            EmitConvert(produced, vr.Type);
            Line($"local.tee ${vr.Sym.TargetName}");
            return;
        }

        // Memory lvalue: a frame-resident variable, *p, or a[i].
        if (a.Target is not (VarRef or Index or Unary { Op: UnOp.Deref }))
        {
            throw new IrUnsupportedException($"the wat target cannot assign to {a.Target.GetType().Name}");
        }
        var tt = a.Target.Type;
        var scratch = ScratchFor(tt);

        if (a.CompoundOp is { } mop)
        {
            // *lv OP= v  — compute the address once, read-modify-write through it.
            _scratchAddr = true;
            var common = CType.UsualArithmetic(tt, a.Value.Type);
            EmitAddress(a.Target);
            Line("local.set $__taddr");
            Line("local.get $__taddr");
            Line(LoadInstr(tt));
            EmitConvert(tt, common);
            EmitExpr(a.Value);
            EmitConvert(a.Value.Type, common);
            Line(IntBinOp(mop, common));
            EmitConvert(common, tt);
            Line($"local.set {scratch}");
            Line("local.get $__taddr");
            Line($"local.get {scratch}");
            Line(StoreInstr(tt));
            Line($"local.get {scratch}");   // assignment is an expression
        }
        else
        {
            EmitExpr(a.Value);
            EmitConvert(a.Value.Type, tt);
            Line($"local.set {scratch}");
            EmitAddress(a.Target);
            Line($"local.get {scratch}");
            Line(StoreInstr(tt));
            Line($"local.get {scratch}");   // leave the stored value
        }
    }

    private void EmitCast(Cast c)
    {
        if (c.Target.Unqualified is CType.VoidType)
        {
            EmitExpr(c.Operand);
            Line("drop");
            return;
        }
        EmitExpr(c.Operand);
        EmitConvert(c.Operand.Type, c.Target);
    }

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

    private void EmitCall(Call c)
    {
        if (!_defined.Contains(c.Callee))
        {
            throw new IrUnsupportedException($"call to '{c.Callee}': library or undefined functions need host imports (milestone 2 I/O)");
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

    // ---- addresses & memory ----------------------------------------------

    /// <summary>Push the linear-memory address (i32) of an lvalue: a frame-resident
    /// variable (its slot), a dereference (<c>*p</c> — the operand is the address), or
    /// a subscript (<c>a[i]</c> = base + i·sizeof(elem)).</summary>
    private void EmitAddress(CExpr lv)
    {
        switch (lv)
        {
            case Paren p:
                EmitAddress(p.Inner);
                break;
            case VarRef v when _frame.TryGetValue(v.Sym, out var off):
                EmitFrameAddr(off);
                break;
            case Unary { Op: UnOp.Deref } u:   // &*p == p
                EmitExpr(u.Operand);
                break;
            case Index ix:
                EmitExpr(ix.Base);
                EmitScaledIndex(ix.Idx, ElementType(ix.Base.Type));
                Line("i32.add");
                break;
            default:
                throw new IrUnsupportedException($"the wat target cannot take the address of {lv.GetType().Name}");
        }
    }

    /// <summary>Push the address of a frame slot: <c>$__sp + offset</c>.</summary>
    private void EmitFrameAddr(int offset)
    {
        Line("global.get $__sp");
        if (offset != 0) { Line($"i32.const {offset}"); Line("i32.add"); }
    }

    /// <summary>Restore the stack pointer to the caller's (saved in <c>$__fp</c>).
    /// Stack-neutral, so it can precede a <c>return</c> that already left a value.</summary>
    private void RestoreSp()
    {
        if (_hasFrame)
        {
            Line("local.get $__fp");
            Line("global.set $__sp");
        }
    }

    private void EmitScaledIndex(CExpr idx, CType elem)
    {
        EmitExpr(idx);
        if (ValType(idx.Type) == "i64") { Line("i32.wrap_i64"); }
        var size = WasmSizeOf(elem);
        if (size != 1) { Line($"i32.const {size}"); Line("i32.mul"); }
    }

    private int InternString(IReadOnlyList<string> segments)
    {
        var bytes = DotCC.EmitHelpers.StringByteValues(segments);
        var key = string.Join(",", bytes);
        if (_strings.TryGetValue(key, out var existing)) { return existing; }

        var offset = _dataEnd;
        var hex = new StringBuilder();
        foreach (var by in bytes) { hex.Append('\\').Append((by & 0xFF).ToString("x2")); }
        hex.Append("\\00");
        _strData.Add((offset, hex.ToString()));
        _dataEnd += bytes.Count + 1;
        _strings[key] = offset;
        return offset;
    }

    /// <summary>The scratch value-local for a store of type <paramref name="t"/>
    /// (i32/i64), marking it for declaration.</summary>
    private string ScratchFor(CType t)
    {
        if (ValType(t) == "i64") { _scratch64 = true; return "$__t64"; }
        _scratch32 = true;
        return "$__t32";
    }

    // ---- helpers ---------------------------------------------------------

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

    private static string PtrCmp(BinOp op) => op switch
    {
        BinOp.Eq => "i32.eq",
        BinOp.Ne => "i32.ne",
        BinOp.Lt => "i32.lt_u",
        BinOp.Gt => "i32.gt_u",
        BinOp.Le => "i32.le_u",
        BinOp.Ge => "i32.ge_u",
        _ => throw new IrUnsupportedException($"the wat target does not support pointer comparison {op}"),
    };

    private static string LoadInstr(CType pointee)
    {
        var p = pointee.Unqualified;
        if (p is CType.Pointer or CType.Func) { return "i32.load"; }
        if (p is CType.Enum en) { return LoadInstr(en.Underlying); }
        if (p is CType.Prim { Integer: true } prim)
        {
            return prim.Bytes switch
            {
                1 => prim.Signed ? "i32.load8_s" : "i32.load8_u",
                2 => prim.Signed ? "i32.load16_s" : "i32.load16_u",
                4 => "i32.load",
                8 => "i64.load",
                _ => throw new IrUnsupportedException($"the wat target cannot load a {pointee.Describe()}"),
            };
        }
        throw new IrUnsupportedException($"the wat target cannot load through {pointee.Describe()} yet (milestone 2 is integers)");
    }

    private static string StoreInstr(CType pointee)
    {
        var p = pointee.Unqualified;
        if (p is CType.Pointer or CType.Func) { return "i32.store"; }
        if (p is CType.Enum en) { return StoreInstr(en.Underlying); }
        if (p is CType.Prim { Integer: true } prim)
        {
            return prim.Bytes switch
            {
                1 => "i32.store8",
                2 => "i32.store16",
                4 => "i32.store",
                8 => "i64.store",
                _ => throw new IrUnsupportedException($"the wat target cannot store a {pointee.Describe()}"),
            };
        }
        throw new IrUnsupportedException($"the wat target cannot store through {pointee.Describe()} yet (milestone 2 is integers)");
    }

    private static CType ElementType(CType t) => t.Unqualified switch
    {
        CType.Pointer p => p.Pointee,
        CType.Array a => a.Element,
        _ => throw new IrUnsupportedException($"the wat target cannot subscript a {t.Describe()}"),
    };

    private static int WasmSizeOf(CType t) => t.Unqualified switch
    {
        CType.Pointer or CType.Func => 4,
        CType.Array a => (a.Count ?? 0) * WasmSizeOf(a.Element),
        _ => t.SizeOf,
    };

    /// <summary>Natural alignment for a frame slot (a power-of-two ≤ 8): an array
    /// aligns to its element, a scalar to its own size.</summary>
    private static int SlotAlign(CType t)
    {
        var u = t.Unqualified;
        var sz = u is CType.Array a ? WasmSizeOf(a.FlatElement) : WasmSizeOf(u);
        return Math.Min(8, Math.Max(1, sz));
    }

    private static int AlignUp(int x, int a) => (x + a - 1) & ~(a - 1);

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

    private void EmitCond(CExpr c)
    {
        EmitExpr(c);
        if (ValType(c.Type) == "i64") { Line("i64.const 0"); Line("i64.ne"); }
    }

    private void EmitBool(CExpr c)
    {
        EmitExpr(c);
        if (ValType(c.Type) == "i64") { Line("i64.const 0"); Line("i64.ne"); }
        else { Line("i32.const 0"); Line("i32.ne"); }
    }

    private string ValType(CType t) => _wat.RenderType(t);

    private static bool IsSignedInt(CType t) =>
        t.Unqualified is CType.Prim { Integer: true, Signed: true } or CType.Enum;

    private void Line(string text) => _out.Append(' ', _indent * 2).Append(text).Append('\n');
}
