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
/// shadow stack for address-taken locals and arrays, and stdout: <c>putchar</c>/
/// <c>puts</c> plus a string-literal <c>printf</c> expanded inline at the call site
/// (integer/char/string conversions), all over the WASI <c>fd_write</c> import.
/// Wider printf and a <c>malloc</c>/<c>free</c>/<c>calloc</c>/<c>realloc</c> bump
/// allocator over linear memory have since landed, and floating-point arithmetic now
/// lowers (literals, <c>f32</c>/<c>f64</c> operators, int↔float and float↔float
/// conversions, comparisons, loads/stores) — only the printf float CONVERSIONS
/// (<c>%f</c>/<c>%e</c>/<c>%g</c>) still wait on a decimal formatter. Anything still
/// outside the slice (structs, goto/switch, varargs, globals, other library calls)
/// raises <see cref="IrUnsupportedException"/>.</para>
/// </summary>
internal sealed class WatBackend
{
    // Top of the shadow stack — it grows DOWN from the end of the first page; string
    // data grows UP from a 1 KiB null guard. Small programs never collide.
    private const int StackTop = 65536;
    // The heap lives ABOVE the shadow stack: $__hp bumps UP from here (the end of the
    // initial page) and malloc grows linear memory on demand, while the stack grows
    // DOWN from the same point inside page 1 — so the two never collide.
    private const int HeapBase = StackTop;
    private const int DataBase = 1024;
    // A 16-byte I/O scratch block at the top of the null-guard page (just below the
    // string data at DataBase): a single WASI iovec (ptr | len), the fd_write
    // nwritten result slot, and a 1-byte char buffer. No real C object lives in the
    // guard, so reusing its tail is safe.
    //   IoScratch+0  iovec.ptr   IoScratch+8   nwritten
    //   IoScratch+4  iovec.len   IoScratch+12  1-byte char buffer
    private const int IoScratch = DataBase - 16;
    // A 32-byte number-formatting scratch buffer just below the I/O block: the
    // integer→ASCII helpers fill it from the end (an i64 is ≤ 22 octal / 20 decimal
    // / 16 hex digits), then write the slice. Still inside the null guard.
    private const int NumBuf = DataBase - 48;
    private const int NumBufEnd = NumBuf + 32;

    // Scratch for the printf float conversions (only touched when %f/%e/%g is used),
    // carved from the free low part of the null-guard page (below NumBuf at 976). A
    // little-endian u32 big-integer (the exact value × 10^precision is formed here —
    // f64 intermediate math would corrupt the last digit) and a decimal staging buffer
    // the digits are written into right-aligned, ending at FpDigEnd. The widest finite
    // double needs ~39 limbs and ~370 digits, both well within the reserved space.
    private const int FpBig = 16;            // %f bignum limb array base ([16, 16+4*48))
    private const int FpBigLimbs = 48;       // capacity in u32 limbs
    private const int FpDigEnd = 960;        // %f digits staged right-aligned, ending here
    // The maximum printf float precision (fractional digits) the formatter stages.
    // Beyond this the bignum/digit buffers could be overrun, so it fails loud rather
    // than miscompile — like MaxNumDigits for the integer conversions.
    private const int MaxFloatPrec = 60;
    // Scratch for the %e/%g Dragon formatter. Two big-integer registers (numerator R
    // and denominator S, each a length word + limbs) scaled into [1,10), a multiply
    // scratch for the 10·S / 2·R comparisons, the significant-digit buffer, and the
    // assembled output. Each conversion runs to completion before the next, so this
    // may share the page with the %f scratch above (they're never live at once). The
    // widest finite double needs ~40 limbs in R/S.
    private const int FpR = 208;             // numerator region  ([208, 412): len + 50 limbs)
    private const int FpS = 412;             // denominator region ([412, 616))
    private const int FpMul = 616;           // 10·S / 2·R scratch ([616, 824): 51 limbs)
    private const int FpEDig = 824;          // significant digits ([824, 888))
    private const int FpEOut = 888;          // assembled "d.dddde+XX" ([888, 960))

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
    // a saved store value (one per wasm value type) and a saved store address (i32)
    // for the read-modify-write of a compound assignment / ++/-- through memory.
    private bool _scratch32, _scratch64, _scratchF32, _scratchF64, _scratchAddr;

    // Interned string literals → linear-memory data segments.
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private readonly List<(int Offset, string Hex)> _strData = new();
    private int _dataEnd = DataBase;

    // The I/O runtime (milestone 2): byte-level stdout via the WASI fd_write import,
    // emitted lazily — only the primitives a program actually calls. They're spelled
    // as hand-written wat rather than compiled-from-C because each bottoms out at a
    // host syscall (like real libc's putchar); a fuller libc can move to
    // compiled-from-C later. A directly-called name (putchar/puts) is only the wat
    // runtime's if the program didn't define its own (user definitions win, via
    // _defined). printf is not a runtime function: a string-literal printf is
    // expanded inline at the call site (EmitPrintf), calling the integer/string
    // formatting helpers below.
    private static readonly HashSet<string> RuntimeFns =
        new(StringComparer.Ordinal) { "putchar", "puts" };
    // The heap allocators, recognized in EmitCall like the printf family — library
    // names backed by hand-written wat. Unlike the I/O runtime they pull no WASI
    // import: malloc bumps the $__hp global and grows linear memory itself.
    private static readonly HashSet<string> HeapFns =
        new(StringComparer.Ordinal) { "malloc", "calloc", "realloc" };
    // The runtime helpers/functions the module needs, including those reached only
    // through printf expansion. Closed over dependencies by NeedRuntime.
    private readonly HashSet<string> _runtimeUsed = new(StringComparer.Ordinal);

    /// <summary>Mark a runtime helper as needed, pulling in its dependencies. Every
    /// helper ultimately writes through <c>$__write</c> (the WASI sink), so any
    /// non-empty set means the fd_write import + exported memory are emitted.</summary>
    private void NeedRuntime(string name)
    {
        if (!_runtimeUsed.Add(name)) { return; }
        switch (name)
        {
            case "putchar": NeedRuntime("__putb"); break;
            case "puts": NeedRuntime("__emit_str"); NeedRuntime("__putb"); break;
            // $__write and $__putb are mutually recursive (fd vs buffer sink), so each
            // pulls the other — even a literal-only printf (just $__write) needs $__putb.
            case "__write": NeedRuntime("__putb"); break;
            case "__putb": NeedRuntime("__write"); break;
            case "__fill": NeedRuntime("__putb"); break;
            case "__emit_str": NeedRuntime("__fill"); NeedRuntime("__write"); break;
            case "__emit_char": NeedRuntime("__putb"); NeedRuntime("__fill"); break;
            case "__pf_emit": NeedRuntime("__putb"); NeedRuntime("__fill"); NeedRuntime("__write"); break;
            case "__pf_int_s": NeedRuntime("__fmt_radix"); NeedRuntime("__pf_emit"); break;
            case "__pf_int_u": NeedRuntime("__fmt_radix"); NeedRuntime("__pf_emit"); break;
            // The printf float conversions: %f drives the big-integer helper block
            // ("__bn"); %e/%g drive the scaled Dragon digit generator ("__dragon") over
            // the region-based big-integer helpers ("__rbn"). All lay the result out in
            // a field via $__pf_emit.
            case "__pf_f": NeedRuntime("__bn"); NeedRuntime("__pf_emit"); break;
            case "__pf_e": NeedRuntime("__dragon"); NeedRuntime("__pf_emit"); break;
            case "__pf_g": NeedRuntime("__dragon"); NeedRuntime("__pf_emit"); break;
            case "__dragon": NeedRuntime("__rbn"); break;
            // The heap allocators: calloc/realloc are written in terms of malloc.
            // malloc itself has no helper deps (it uses the $__hp global + memory.grow);
            // free isn't a runtime function at all (it lowers to an inline drop).
            case "calloc": NeedRuntime("malloc"); break;
            case "realloc": NeedRuntime("malloc"); break;
        }
    }

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
        // I/O pulls the WASI import + exported memory + sink globals; the heap only
        // needs its bump-pointer global. A program can use either, both, or neither.
        var usesHeap = _runtimeUsed.Contains("malloc");
        var usesIo = _runtimeUsed.Any(n => !HeapFns.Contains(n));
        // The printf float formatter's big-integer needs a limb-count global.
        var usesBn = _runtimeUsed.Contains("__bn");

        var m = new StringBuilder();
        m.Append("(module\n");
        if (usesIo)
        {
            // WASI fd_write — imports must precede every defined function (it takes
            // the lowest function index). Brings in I/O without a bespoke host ABI.
            m.Append("  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))\n");
        }
        // Export the memory only when the WASI shim needs to read iovecs out of it;
        // non-I/O modules keep the byte-identical plain `(memory 1)`.
        m.Append(usesIo ? "  (memory (export \"memory\") 1)\n" : "  (memory 1)\n");
        m.Append($"  (global $__sp (mut i32) (i32.const {StackTop}))\n");
        if (usesHeap)
        {
            // The bump-allocation pointer: next free heap byte, growing UP from the end
            // of the initial page (malloc grows linear memory past it on demand).
            m.Append($"  (global $__hp (mut i32) (i32.const {HeapBase}))\n");
        }
        if (usesIo)
        {
            // The output sink for the byte primitives: $__ob = -1 means fd 1 (printf);
            // otherwise it's a write cursor into linear memory (sprintf), bounded by
            // $__oend, with $__ocount tracking the total chars the format produced.
            m.Append("  (global $__ob (mut i32) (i32.const -1))\n");
            m.Append("  (global $__oend (mut i32) (i32.const 0))\n");
            m.Append("  (global $__ocount (mut i32) (i32.const 0))\n");
        }
        if (usesBn)
        {
            // Active limb count of the float formatter's big-integer at FpBig.
            m.Append("  (global $__bnlen (mut i32) (i32.const 0))\n");
        }
        foreach (var (off, hex) in _strData)
        {
            m.Append("  (data (i32.const ").Append(off).Append(") \"").Append(hex).Append("\")\n");
        }
        m.Append(funcs);
        m.Append(RuntimeFuncDefs());
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
        _scratch32 = _scratch64 = _scratchF32 = _scratchF64 = _scratchAddr = false;

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
        // Symbol.AddressTaken is the IR's target-neutral "&x was taken" fact, set for
        // every var/param at the one site every `&` is built. An address-taken
        // local/param needs a real linear-memory address, so it gets a frame slot;
        // arrays always do (they decay to a pointer). Every other scalar is a fast
        // wasm value local.
        foreach (var p in fn.Params)
        {
            if (p.AddressTaken) { Place(p); spillParams.Add(p); }
        }
        var bodyLocals = new List<Symbol>();
        foreach (var s in fn.Body.Stmts) { CollectLocals(s, bodyLocals); }
        foreach (var loc in bodyLocals)
        {
            if (loc.AddressTaken || loc.Type.Unqualified is CType.Array) { Place(loc); }
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
        if (_scratchF32) { Line("(local $__tf32 f32)"); }
        if (_scratchF64) { Line("(local $__tf64 f64)"); }
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
            case LitFloat lf:
                Line($"{ValType(e.Type)}.const {_wat.RenderFloatLit(lf)}");
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
            {
                var vt = ValType(u.Operand.Type);
                if (vt is "f32" or "f64")
                {
                    EmitExpr(u.Operand);
                    Line($"{vt}.neg");   // correct sign of zero (0 - 0.0 would be +0.0)
                }
                else
                {
                    Line($"{vt}.const 0");
                    EmitExpr(u.Operand);
                    Line($"{vt}.sub");
                }
                break;
            }
            case UnOp.BitNot:
                EmitExpr(u.Operand);
                Line($"{ValType(u.Operand.Type)}.const -1");
                Line($"{ValType(u.Operand.Type)}.xor");
                break;
            case UnOp.LogNot:
            {
                var vt = ValType(u.Operand.Type);
                EmitExpr(u.Operand);
                if (vt is "f32" or "f64") { Line($"{vt}.const 0"); Line($"{vt}.eq"); }
                else if (vt == "i64") { Line("i64.eqz"); }
                else { Line("i32.eqz"); }
                break;
            }
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

        // A shift is the exception to the usual-arithmetic rule: its result type is the
        // PROMOTED LEFT operand's type (the right operand doesn't take part — C99
        // 6.5.7), which the IR already recorded as b.Type. wasm requires both operands
        // of shl/shr to share a type, so the count is coerced to the left's width too
        // (its value is taken mod the width, so the coercion is value-preserving for
        // any in-range count).
        if (b.Op is BinOp.Shl or BinOp.Shr)
        {
            var resT = b.Type.Unqualified;
            EmitExpr(b.Left);
            EmitConvert(b.Left.Type, resT);
            EmitExpr(b.Right);
            EmitConvert(b.Right.Type, resT);
            Line(IntBinOp(b.Op, resT));
            return;
        }

        // Every other arithmetic / relational op: both operands take the usual
        // arithmetic conversions to a common type first (the IR records each operand's
        // own type but does NOT pre-insert the coercion). Converting here is what makes
        // mixed-width integer ops (int + long) and any float op (double + int)
        // type-check on the wasm stack.
        var common = CType.UsualArithmetic(b.Left.Type, b.Right.Type);
        EmitExpr(b.Left);
        EmitConvert(b.Left.Type, common);
        EmitExpr(b.Right);
        EmitConvert(b.Right.Type, common);
        Line(ArithBinOp(b.Op, common));
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
                Line(ArithBinOp(cop, common));
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
            Line(ArithBinOp(mop, common));
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
        var toFloat = toVt is "f32" or "f64";
        var fromFloat = fromVt is "f32" or "f64";

        if (toVt == fromVt)
        {
            // Same wasm value type: only an integer may need a sub-word re-narrow
            // (e.g. int -> char); float-to-same-float is a no-op.
            if (!toFloat) { NarrowI32(to.Unqualified); }
        }
        else if (!toFloat && !fromFloat)
        {
            // Integer width change.
            if (toVt == "i64")
            {
                Line(IsSignedInt(from) ? "i64.extend_i32_s" : "i64.extend_i32_u");
            }
            else
            {
                Line("i32.wrap_i64");
                NarrowI32(to.Unqualified);
            }
        }
        else if (toFloat && fromFloat)
        {
            // float <-> double.
            Line(toVt == "f64" ? "f64.promote_f32" : "f32.demote_f64");
        }
        else if (toFloat)
        {
            // integer -> float: convert by the SOURCE integer's signedness.
            var sx = IsSignedInt(from) ? "s" : "u";
            Line($"{toVt}.convert_{fromVt}_{sx}");
        }
        else if (to.Unqualified is CType.Prim { Name: "_Bool" })
        {
            // float -> _Bool is "x != 0" (any nonzero, including a fraction, is 1),
            // NOT a truncation — `(_Bool)0.5` is 1.
            Line($"{fromVt}.const 0");
            Line($"{fromVt}.ne");
        }
        else
        {
            // float -> integer: truncate toward zero by the TARGET's signedness.
            // The saturating form avoids a trap on NaN / out-of-range (C makes that
            // value undefined; saturating is the benign, deterministic choice).
            var sx = IsSignedInt(to) ? "s" : "u";
            Line($"{toVt}.trunc_sat_{fromVt}_{sx}");
            NarrowI32(to.Unqualified);
        }
    }

    private void EmitCall(Call c)
    {
        // The printf family with a string-literal format is expanded inline (no
        // runtime function); a user-defined one, if any, wins and routes through the
        // generic path below.
        if (c.Callee == "printf" && !_defined.Contains("printf")) { EmitPrintf(c); return; }
        if (c.Callee == "sprintf" && !_defined.Contains("sprintf")) { EmitSprintf(c, bounded: false); return; }
        if (c.Callee == "snprintf" && !_defined.Contains("snprintf")) { EmitSprintf(c, bounded: true); return; }

        // The heap allocators lower to calls into the hand-written bump allocator;
        // free is a no-op drop. A user-defined one wins and routes through below.
        if (c.Callee == "malloc" && !_defined.Contains("malloc")) { EmitHeapAlloc(c, "malloc", 1); return; }
        if (c.Callee == "calloc" && !_defined.Contains("calloc")) { EmitHeapAlloc(c, "calloc", 2); return; }
        if (c.Callee == "realloc" && !_defined.Contains("realloc")) { EmitHeapAlloc(c, "realloc", 2); return; }
        if (c.Callee == "free" && !_defined.Contains("free")) { EmitFree(c); return; }

        if (!_defined.Contains(c.Callee))
        {
            // A handful of libc names are backed by the hand-written wat I/O runtime
            // (emitted on demand from RuntimeFuncDefs); the rest still fail loud.
            if (RuntimeFns.Contains(c.Callee)) { NeedRuntime(c.Callee); }
            else
            {
                throw new IrUnsupportedException($"call to '{c.Callee}': library or undefined functions need host imports (putchar/puts/printf are wired so far)");
            }
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

    /// <summary>Lower a heap allocator call (<c>malloc</c>/<c>calloc</c>/<c>realloc</c>)
    /// to the hand-written bump allocator. Each argument is pushed as an i32: sizes
    /// arrive as the <c>int</c> size_t stand-in, but a <c>sizeof</c> product is i64, so
    /// wrap. The runtime function leaves the (i32) result pointer.</summary>
    private void EmitHeapAlloc(Call c, string name, int argc)
    {
        if (c.Args.Count != argc)
        {
            throw new IrUnsupportedException($"the wat target expects {name} with {argc} argument(s)");
        }
        NeedRuntime(name);
        foreach (var arg in c.Args)
        {
            EmitExpr(arg);
            if (ValType(arg.Type) == "i64") { Line("i32.wrap_i64"); }
        }
        Line($"call ${name}");
    }

    /// <summary><c>free(p)</c> — a no-op for the bump allocator. Evaluate the argument
    /// (for any side effects) and drop it; emit no call, so no <c>$free</c> exists.</summary>
    private void EmitFree(Call c)
    {
        if (c.Args.Count != 1)
        {
            throw new IrUnsupportedException("the wat target expects free with 1 argument");
        }
        EmitExpr(c.Args[0]);
        Line("drop");
        // free returns void; but if it was implicitly declared (no <stdlib.h>) the IR
        // types the call as int and the statement context will drop a "result" — leave
        // one so the stack stays balanced. With the prototype, c.Type is void: no-op.
        if (c.Type.Unqualified is not CType.VoidType) { Line($"{ValType(c.Type)}.const 0"); }
    }

    /// <summary>Expand a <c>printf</c> with a string-literal format at compile time —
    /// the common case. Output goes to fd 1 (the sink's default mode); the result is
    /// C's char count, which we don't track (rarely consulted; the C# backend's
    /// <c>Done()</c> also returns 0), so the expression leaves 0.</summary>
    private void EmitPrintf(Call c)
    {
        var fmt = FormatLiteral(c, 0, "printf");
        EmitFormatExpansion(fmt, c, firstArg: 1);
        Line("i32.const 0");
    }

    /// <summary>Expand <c>sprintf</c>/<c>snprintf</c> with a string-literal format:
    /// point the sink at the destination buffer (bounded by <c>dst + n</c> for
    /// snprintf, effectively unbounded for sprintf), run the shared expansion so every
    /// write lands in the buffer, then NUL-terminate and leave the char count (C's
    /// return). No WASI write happens unless the program also prints elsewhere.</summary>
    private void EmitSprintf(Call c, bool bounded)
    {
        var name = bounded ? "snprintf" : "sprintf";
        var fmtIdx = bounded ? 2 : 1;
        var fmt = FormatLiteral(c, fmtIdx, name);
        NeedRuntime("__sink_end");

        // Aim the sink at the buffer: $__ob = dst, $__oend = dst + n (or "infinite"),
        // $__ocount = 0. Re-reading $__ob avoids a temp for dst in the bound.
        EmitExpr(c.Args[0]);
        if (ValType(c.Args[0].Type) == "i64") { Line("i32.wrap_i64"); }
        Line("global.set $__ob");
        if (bounded)
        {
            Line("global.get $__ob");
            EmitExpr(c.Args[1]);
            if (ValType(c.Args[1].Type) == "i64") { Line("i32.wrap_i64"); }
            Line("i32.add");
            Line("global.set $__oend");
        }
        else
        {
            Line("i32.const 2147483647");
            Line("global.set $__oend");
        }
        Line("i32.const 0");
        Line("global.set $__ocount");

        EmitFormatExpansion(fmt, c, firstArg: fmtIdx + 1);
        Line("call $__sink_end");   // NUL-terminate, restore fd mode, leave the count
    }

    /// <summary>The shared body of the printf-family expansion: parse the literal
    /// format (<see cref="PrintfFormat"/>, the same grammar as the runtime
    /// PrintfBuilder) and lower each segment — a literal run to a direct write, a
    /// conversion to a formatting-helper call consuming the next argument. This
    /// sidesteps a wat varargs ABI entirely (arguments are consumed positionally).
    /// C evaluates every argument even past the last conversion, so the unconsumed
    /// tail is still evaluated (for side effects) and dropped.</summary>
    private void EmitFormatExpansion(LitStr fmt, Call c, int firstArg)
    {
        var bytes = DotCC.EmitHelpers.StringByteValues(fmt.Segments);
        var argIdx = firstArg;
        foreach (var seg in PrintfFormat.Parse(bytes))
        {
            if (seg.Literal is { } literal)
            {
                NeedRuntime("__write");
                Line($"i32.const {InternBytes(literal)}");
                Line($"i32.const {literal.Count}");
                Line("call $__write");
                continue;
            }
            if (argIdx >= c.Args.Count)
            {
                throw new IrUnsupportedException($"'{c.Callee}' has more conversions than arguments");
            }
            EmitConversion(seg.Conversion!.Value, c.Args[argIdx]);
            argIdx++;
        }
        for (; argIdx < c.Args.Count; argIdx++)
        {
            EmitExpr(c.Args[argIdx]);
            if (c.Args[argIdx].Type.Unqualified is not CType.VoidType) { Line("drop"); }
        }
    }

    /// <summary>The format argument of a printf-family call, required to be a string
    /// literal (so it can be expanded at compile time); otherwise fail loud.</summary>
    private static LitStr FormatLiteral(Call c, int index, string name)
    {
        if (c.Args.Count <= index || c.Args[index] is not LitStr fmt)
        {
            throw new IrUnsupportedException($"the wat target only supports {name} with a string-literal format (a runtime format needs a compiled-from-C runtime)");
        }
        return fmt;
    }

    // The widest decimal/precision digit run the integer formatter can stage in
    // NumBuf (an i64 is ≤ 20 digits; the rest is precision headroom).
    private const int MaxNumDigits = 30;

    /// <summary>Lower one printf conversion: push the argument (widened/wrapped as
    /// the conversion needs) plus the field-formatting parameters resolved from the
    /// (compile-time-constant) spec — width, precision, the justification/sign mode —
    /// then call the matching runtime formatter. The runtime side does only what
    /// genuinely depends on the value (digit generation, padding lengths); everything
    /// the literal fixes is an immediate. <c>#</c>, floats and <c>%p</c> aren't
    /// supported yet (fail loud).</summary>
    private void EmitConversion(PrintfFormat.Spec spec, CExpr arg)
    {
        // The '#' alternate form is supported only for the float conversions (force a
        // decimal point; for %g, also keep trailing zeros); for the integer
        // conversions it still fails loud rather than miscompile.
        if (spec.Alt && spec.Conv is not ('f' or 'F' or 'e' or 'E' or 'g' or 'G'))
        {
            throw new IrUnsupportedException($"the wat target does not yet support the printf '#' flag (in '%{spec.Conv}')");
        }
        var width = spec.Width >= 0 ? spec.Width : 0;
        switch (spec.Conv)
        {
            case 'c':
                NeedRuntime("__emit_char");
                EmitExpr(arg);
                if (ValType(arg.Type) == "i64") { Line("i32.wrap_i64"); }
                Line($"i32.const {width}");
                Line($"i32.const {(spec.Left ? 1 : 0)}");
                Line("call $__emit_char");
                break;
            case 's':
                NeedRuntime("__emit_str");
                EmitExpr(arg);
                Line($"i32.const {(spec.Precision >= 0 ? spec.Precision : -1)}");   // max chars
                Line($"i32.const {width}");
                Line($"i32.const {(spec.Left ? 1 : 0)}");
                Line("call $__emit_str");
                break;
            case 'd': case 'i':
                NeedRuntime("__pf_int_s");
                EmitIntArgAsI64(arg, signed: true);
                Line($"i32.const {(spec.Plus ? 43 : spec.Space ? 32 : 0)}");        // sign for non-negative
                Line($"i32.const {IntMinDigits(spec)}");
                Line($"i32.const {width}");
                Line($"i32.const {IntMode(spec)}");
                Line("call $__pf_int_s");
                break;
            case 'u': EmitUnsignedConv(arg, spec, 10, 0); break;
            case 'x': EmitUnsignedConv(arg, spec, 16, 97); break;   // 'a'
            case 'X': EmitUnsignedConv(arg, spec, 16, 65); break;   // 'A'
            case 'o': EmitUnsignedConv(arg, spec, 8, 0); break;
            case 'f': case 'F': EmitFloatConv(arg, spec, spec.Conv); break;
            case 'e': case 'E': EmitFloatConv(arg, spec, spec.Conv); break;
            case 'g': case 'G': EmitFloatConv(arg, spec, spec.Conv); break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support the printf conversion '%{spec.Conv}' (%p comes later)");
        }
    }

    /// <summary>Lower an unsigned integer conversion (<c>%u</c>/<c>%x</c>/<c>%X</c>/
    /// <c>%o</c>): the value is taken as its unsigned bit pattern; <c>+</c>/space don't
    /// apply (C only signs signed conversions).</summary>
    private void EmitUnsignedConv(CExpr arg, PrintfFormat.Spec spec, int radix, int alpha)
    {
        NeedRuntime("__pf_int_u");
        EmitIntArgAsI64(arg, signed: false);
        Line($"i64.const {radix}");
        Line($"i32.const {alpha}");
        Line($"i32.const {IntMinDigits(spec)}");
        Line($"i32.const {(spec.Width >= 0 ? spec.Width : 0)}");
        Line($"i32.const {IntMode(spec)}");
        Line("call $__pf_int_u");
    }

    /// <summary>Lower a floating <c>%f</c>/<c>%e</c>/<c>%g</c> conversion: push the
    /// value as an f64 (a <c>float</c> argument is promoted, matching C's default
    /// argument promotion), then the compile-time-constant field parameters, and call
    /// the matching runtime formatter. Each forms the EXACT value with big-integer
    /// arithmetic and rounds once (round-half-to-even), so the digits match a real
    /// libc: <c>%f</c> via value × 10^precision, <c>%e</c>/<c>%g</c> via a scaled
    /// Dragon digit generator. For <c>%g</c> the "precision" is significant digits
    /// (C99: a precision of 0 is taken as 1).</summary>
    private void EmitFloatConv(CExpr arg, PrintfFormat.Spec spec, char conv)
    {
        // Uppercase conversions (%F/%E/%G) differ only in casing: an 'E' exponent
        // marker and uppercase INF/NAN. The runtime helper is the same one; an extra
        // flag selects the case (it folds to lowercase − (upper << 5) at each letter).
        var upper = conv is 'F' or 'E' or 'G';
        var lc = char.ToLowerInvariant(conv);
        var prec = spec.Precision >= 0 ? spec.Precision : 6;   // C default precision is 6
        if (lc == 'g' && prec == 0) { prec = 1; }              // %g: precision 0 means 1
        if (prec > MaxFloatPrec)
        {
            throw new IrUnsupportedException($"the wat target does not yet support printf float precision > {MaxFloatPrec} (in '%{spec.Conv}')");
        }
        var runtime = lc switch { 'e' => "__pf_e", 'g' => "__pf_g", _ => "__pf_f" };
        NeedRuntime(runtime);
        EmitExpr(arg);
        var vt = ValType(arg.Type);
        if (vt == "f32") { Line("f64.promote_f32"); }
        else if (vt != "f64")
        {
            throw new IrUnsupportedException($"printf '%{spec.Conv}' expects a floating-point argument on the wat target");
        }
        Line($"i32.const {prec}");
        Line($"i32.const {(spec.Plus ? 43 : spec.Space ? 32 : 0)}");   // sign for a non-negative value
        Line($"i32.const {(spec.Width >= 0 ? spec.Width : 0)}");
        Line($"i32.const {FloatMode(spec)}");
        Line($"i32.const {(spec.Alt ? 1 : 0)}");                       // '#': always show a decimal point
        Line($"i32.const {(upper ? 1 : 0)}");                          // uppercase 'E' / INF / NAN
        Line($"call ${runtime}");
    }

    /// <summary>The field-fill mode for a float conversion: 1 = left-justify, 2 =
    /// zero-pad, 0 = space-pad. Unlike the integer conversions, a precision does NOT
    /// disable the <c>0</c> flag for floats (C99 §7.21.6.1); left-justify still wins.</summary>
    private static int FloatMode(PrintfFormat.Spec spec) =>
        spec.Left ? 1 : (spec.Zero ? 2 : 0);

    /// <summary>The minimum digit count for an integer conversion — the precision if
    /// given, else 1. Bounded by the staging buffer.</summary>
    private static int IntMinDigits(PrintfFormat.Spec spec)
    {
        var min = spec.Precision >= 0 ? spec.Precision : 1;
        if (min > MaxNumDigits)
        {
            throw new IrUnsupportedException($"the wat target does not yet support printf precision > {MaxNumDigits} (in '%{spec.Conv}')");
        }
        return min;
    }

    /// <summary>The field-fill mode for an integer conversion: 1 = left-justify, 2 =
    /// zero-pad, 0 = space-pad. The <c>0</c> flag is ignored when a precision is given
    /// or with left-justify (C99 §7.21.6.1).</summary>
    private static int IntMode(PrintfFormat.Spec spec) =>
        spec.Left ? 1 : (spec.Zero && spec.Precision < 0 ? 2 : 0);

    /// <summary>Push a printf integer argument as an i64 for the formatting helpers:
    /// an i32 is widened by the conversion's signedness (sign-extend for <c>%d</c>,
    /// zero-extend for unsigned/hex/octal); an already-64-bit value passes through.</summary>
    private void EmitIntArgAsI64(CExpr arg, bool signed)
    {
        EmitExpr(arg);
        if (ValType(arg.Type) == "i32") { Line(signed ? "i64.extend_i32_s" : "i64.extend_i32_u"); }
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

    private int InternString(IReadOnlyList<string> segments) =>
        InternBytes(DotCC.EmitHelpers.StringByteValues(segments));

    /// <summary>Intern a run of raw bytes into a NUL-terminated data segment and
    /// return its address, deduplicating identical runs. The trailing NUL is
    /// harmless for length-prefixed writes (printf literals) and required for the
    /// string literals a C program treats as <c>char*</c>.</summary>
    private int InternBytes(IReadOnlyList<int> bytes)
    {
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

    /// <summary>Hand-written wat for the I/O runtime helpers the program reached
    /// (directly or through printf expansion). Everything bottoms out at
    /// <c>$__write</c>, which drives the WASI <c>fd_write</c> import to fd 1 (stdout)
    /// through the fixed <see cref="IoScratch"/> iovec; <c>$__putb</c>/<c>$__fill</c>
    /// are the single-byte / padding primitives. Integer conversions stage digits into
    /// <see cref="NumBuf"/> (<c>$__fmt_radix</c>) and lay them out in a field
    /// (<c>$__pf_emit</c>); <c>$__emit_str</c>/<c>$__emit_char</c> do the string/char
    /// fields. Emitted after the user functions, so their indices are stable.
    /// Whitespace is insignificant in wat — the layout here is purely for
    /// readability.</summary>
    private string RuntimeFuncDefs()
    {
        var sb = new StringBuilder();

        // The core sink for a byte run. fd mode ($__ob == -1): one bulk fd_write.
        // Buffer mode (sprintf): copy byte-by-byte through $__putb so the cursor,
        // bound and count logic stays in one place.
        if (_runtimeUsed.Contains("__write"))
        {
            sb.Append($$"""
  (func $__write (param $ptr i32) (param $len i32)
    (local $i i32)
    global.get $__ob
    i32.const -1
    i32.eq
    if                           ;; fd 1 — one iovec, one fd_write
      i32.const {{IoScratch}}
      local.get $ptr
      i32.store
      i32.const {{IoScratch + 4}}
      local.get $len
      i32.store
      i32.const 1
      i32.const {{IoScratch}}
      i32.const 1
      i32.const {{IoScratch + 8}}
      call $fd_write
      drop
    else                         ;; buffer — copy each byte through $__putb
      block $done
        loop $lp
          local.get $i
          local.get $len
          i32.ge_s
          br_if $done
          local.get $ptr
          local.get $i
          i32.add
          i32.load8_u
          call $__putb
          local.get $i
          i32.const 1
          i32.add
          local.set $i
          br $lp
        end
      end
    end
  )

""");
        }

        // Write one byte (the low 8 bits of $ch). fd mode delegates the single byte to
        // $__write; buffer mode stores at the cursor (reserving the final slot for the
        // NUL), advances it within bounds, and always bumps the would-be count.
        if (_runtimeUsed.Contains("__putb"))
        {
            sb.Append($$"""
  (func $__putb (param $ch i32)
    global.get $__ob
    i32.const -1
    i32.eq
    if                           ;; fd mode
      i32.const {{IoScratch + 12}}
      local.get $ch
      i32.store8
      i32.const {{IoScratch + 12}}
      i32.const 1
      call $__write
    else                         ;; buffer mode
      global.get $__ob           ;; store only if ob+1 < oend (keep room for NUL)
      i32.const 1
      i32.add
      global.get $__oend
      i32.lt_s
      if
        global.get $__ob
        local.get $ch
        i32.store8
        global.get $__ob
        i32.const 1
        i32.add
        global.set $__ob
      end
      global.get $__ocount       ;; count every byte (snprintf's would-be length)
      i32.const 1
      i32.add
      global.set $__ocount
    end
  )

""");
        }

        // Write $ch repeated $n times (the field-padding primitive).
        if (_runtimeUsed.Contains("__fill"))
        {
            sb.Append($$"""
  (func $__fill (param $ch i32) (param $n i32)
    block $done
      loop $lp
        local.get $n
        i32.const 0
        i32.le_s
        br_if $done
        local.get $ch
        call $__putb
        local.get $n
        i32.const 1
        i32.sub
        local.set $n
        br $lp
      end
    end
  )

""");
        }

        // Close an sprintf buffer: NUL-terminate at the cursor (skipped when no room,
        // i.e. snprintf size 0), restore fd mode, and return the would-be char count.
        if (_runtimeUsed.Contains("__sink_end"))
        {
            sb.Append("""
  (func $__sink_end (result i32)
    global.get $__ob
    global.get $__oend
    i32.lt_s
    if
      global.get $__ob
      i32.const 0
      i32.store8
    end
    i32.const -1
    global.set $__ob
    global.get $__ocount
  )

""");
        }

        if (_runtimeUsed.Contains("putchar"))
        {
            sb.Append($$"""
  (func $putchar (param $c i32) (result i32)
    local.get $c                 ;; write (unsigned char)c
    call $__putb
    local.get $c                 ;; return (unsigned char)c
    i32.const 255
    i32.and
  )

""");
        }

        if (_runtimeUsed.Contains("puts"))
        {
            sb.Append($$"""
  (func $puts (param $s i32) (result i32)
    local.get $s                 ;; the string (no cap, no field), then a newline
    i32.const -1
    i32.const 0
    i32.const 0
    call $__emit_str
    i32.const 10
    call $__putb
    i32.const 0                  ;; success (non-negative)
  )

""");
        }

        // Stage the digits of $v (radix $base, alpha base for digits ≥ 10) into NumBuf
        // from the end, left-zero-padded to at least $min digits, and return the start
        // pointer (length = NumBufEnd - ptr). A do-while emits ≥ 1 digit so 0 prints
        // "0" — except the C99 corner where value 0 with precision 0 emits nothing.
        if (_runtimeUsed.Contains("__fmt_radix"))
        {
            sb.Append($$"""
  (func $__fmt_radix (param $v i64) (param $base i64) (param $alpha i32) (param $min i32) (result i32)
    (local $p i32) (local $d i32)
    i32.const {{NumBufEnd}}
    local.set $p
    local.get $v                 ;; skip digit gen only when v==0 && min==0
    i64.eqz
    local.get $min
    i32.eqz
    i32.and
    i32.eqz
    if
      loop $lp
        local.get $p
        i32.const 1
        i32.sub
        local.set $p
        local.get $v             ;; d = v % base
        local.get $base
        i64.rem_u
        i32.wrap_i64
        local.set $d
        local.get $p
        local.get $d
        i32.const 10
        i32.lt_u
        if (result i32)
          local.get $d
          i32.const 48           ;; '0' + d
          i32.add
        else
          local.get $d
          i32.const 10
          i32.sub
          local.get $alpha       ;; alpha + (d - 10)
          i32.add
        end
        i32.store8
        local.get $v             ;; v /= base
        local.get $base
        i64.div_u
        local.set $v
        local.get $v
        i64.eqz
        i32.eqz
        br_if $lp
      end
    end
    block $pdone                 ;; left-pad '0' until length ≥ min (precision)
      loop $pad
        i32.const {{NumBufEnd}}
        local.get $p
        i32.sub
        local.get $min
        i32.ge_s
        br_if $pdone
        local.get $p
        i32.const 1
        i32.sub
        local.set $p
        local.get $p
        i32.const 48
        i32.store8
        br $pad
      end
    end
    local.get $p
  )

""");
        }

        // Write a staged number ($ptr,$len) into a field: optional $sign byte, then
        // pad to $width per $mode (0 = space-pad right, 1 = left-justify, 2 = zero-pad).
        if (_runtimeUsed.Contains("__pf_emit"))
        {
            sb.Append($$"""
  (func $__pf_emit (param $ptr i32) (param $len i32) (param $sign i32) (param $width i32) (param $mode i32)
    (local $pad i32)
    local.get $width             ;; pad = max(0, width - (signLen + len))
    local.get $sign
    i32.const 0
    i32.ne
    local.get $len
    i32.add
    i32.sub
    local.set $pad
    local.get $mode
    i32.const 1
    i32.eq
    if                           ;; left: sign, body, spaces
      local.get $sign
      if
        local.get $sign
        call $__putb
      end
      local.get $ptr
      local.get $len
      call $__write
      i32.const 32
      local.get $pad
      call $__fill
    else
      local.get $mode
      i32.const 2
      i32.eq
      if                         ;; zero: sign, '0' pad, body
        local.get $sign
        if
          local.get $sign
          call $__putb
        end
        i32.const 48
        local.get $pad
        call $__fill
        local.get $ptr
        local.get $len
        call $__write
      else                       ;; right: spaces, sign, body
        i32.const 32
        local.get $pad
        call $__fill
        local.get $sign
        if
          local.get $sign
          call $__putb
        end
        local.get $ptr
        local.get $len
        call $__write
      end
    end
  )

""");
        }

        // Signed decimal: resolve the sign ('-' for negative, else $posSign for a
        // non-negative value), stage the magnitude (wrapping negate handles INT64_MIN),
        // emit. base 10.
        if (_runtimeUsed.Contains("__pf_int_s"))
        {
            sb.Append($$"""
  (func $__pf_int_s (param $v i64) (param $posSign i32) (param $min i32) (param $width i32) (param $mode i32)
    (local $sign i32) (local $ptr i32)
    local.get $v
    i64.const 0
    i64.lt_s
    if (result i32)
      i32.const 45               ;; '-'
    else
      local.get $posSign
    end
    local.set $sign
    local.get $v                 ;; mag = v<0 ? -v : v
    i64.const 0
    i64.lt_s
    if (result i64)
      i64.const 0
      local.get $v
      i64.sub
    else
      local.get $v
    end
    i64.const 10
    i32.const 0
    local.get $min
    call $__fmt_radix
    local.set $ptr
    local.get $ptr
    i32.const {{NumBufEnd}}
    local.get $ptr
    i32.sub
    local.get $sign
    local.get $width
    local.get $mode
    call $__pf_emit
  )

""");
        }

        // Unsigned radix (10/16/8): no sign; stage the magnitude and emit.
        if (_runtimeUsed.Contains("__pf_int_u"))
        {
            sb.Append($$"""
  (func $__pf_int_u (param $mag i64) (param $base i64) (param $alpha i32) (param $min i32) (param $width i32) (param $mode i32)
    (local $ptr i32)
    local.get $mag
    local.get $base
    local.get $alpha
    local.get $min
    call $__fmt_radix
    local.set $ptr
    local.get $ptr
    i32.const {{NumBufEnd}}
    local.get $ptr
    i32.sub
    i32.const 0                  ;; no sign
    local.get $width
    local.get $mode
    call $__pf_emit
  )

""");
        }

        // Write a NUL-terminated string into a field: length is strlen capped at $max
        // (-1 = uncapped, i.e. precision); pad to $width ($mode 1 = left, else space).
        if (_runtimeUsed.Contains("__emit_str"))
        {
            sb.Append($$"""
  (func $__emit_str (param $ptr i32) (param $max i32) (param $width i32) (param $mode i32)
    (local $len i32) (local $q i32) (local $pad i32)
    local.get $ptr
    local.set $q
    block $done
      loop $scan
        local.get $max           ;; stop at the precision cap, if any
        i32.const 0
        i32.ge_s
        if
          local.get $len
          local.get $max
          i32.ge_s
          br_if $done
        end
        local.get $q
        i32.load8_u
        i32.eqz
        br_if $done
        local.get $q
        i32.const 1
        i32.add
        local.set $q
        local.get $len
        i32.const 1
        i32.add
        local.set $len
        br $scan
      end
    end
    local.get $width             ;; pad = max(0, width - len)
    local.get $len
    i32.sub
    local.set $pad
    local.get $mode
    i32.const 1
    i32.eq
    if                           ;; left: body then spaces
      local.get $ptr
      local.get $len
      call $__write
      i32.const 32
      local.get $pad
      call $__fill
    else                         ;; right: spaces then body
      i32.const 32
      local.get $pad
      call $__fill
      local.get $ptr
      local.get $len
      call $__write
    end
  )

""");
        }

        // Write one char into a field of $width ($mode 1 = left, else space-pad).
        if (_runtimeUsed.Contains("__emit_char"))
        {
            sb.Append($$"""
  (func $__emit_char (param $ch i32) (param $width i32) (param $mode i32)
    local.get $mode
    i32.const 1
    i32.eq
    if                           ;; left: char then spaces
      local.get $ch
      call $__putb
      i32.const 32
      local.get $width
      i32.const 1
      i32.sub
      call $__fill
    else                         ;; right: spaces then char
      i32.const 32
      local.get $width
      i32.const 1
      i32.sub
      call $__fill
      local.get $ch
      call $__putb
    end
  )

""");
        }

        // ---- big integer for the printf float conversions --------------------------
        // A little-endian u32 big-integer at FpBig with $__bnlen active limbs. The
        // float formatter forms the EXACT value × 10^precision here and reads decimal
        // digits back out — doing the conversion in f64 would lose the last digit, so
        // correctly-rounded output (matching a real libc, round-half-to-even) needs
        // exact integer arithmetic. Every op keeps $__bnlen trimmed of leading zeros.
        if (_runtimeUsed.Contains("__bn"))
        {
            sb.Append($$"""
  (func $__bn_set (param $v i64)         ;; bn = v  (v < 2^53 → 1-2 limbs)
    i32.const {{FpBig}}
    local.get $v
    i32.wrap_i64
    i32.store
    i32.const {{FpBig + 4}}
    local.get $v
    i64.const 32
    i64.shr_u
    i32.wrap_i64
    i32.store
    local.get $v
    i64.const 4294967295
    i64.gt_u
    if (result i32)
      i32.const 2
    else
      local.get $v
      i64.eqz
      if (result i32) i32.const 0 else i32.const 1 end
    end
    global.set $__bnlen
  )

  (func $__bn_mul (param $m i32)         ;; bn *= m  (m a small u32)
    (local $i i32) (local $n i32) (local $carry i64) (local $p i64) (local $addr i32)
    global.get $__bnlen local.set $n
    i32.const 0 local.set $i
    i64.const 0 local.set $carry
    block $done loop $lp
      local.get $i local.get $n i32.ge_s br_if $done
      i32.const {{FpBig}} local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $addr i32.load i64.extend_i32_u
      local.get $m i64.extend_i32_u
      i64.mul
      local.get $carry
      i64.add
      local.set $p                       ;; p = limb[i]*m + carry
      local.get $addr
      local.get $p i32.wrap_i64
      i32.store                          ;; limb[i] = (u32)p
      local.get $p i64.const 32 i64.shr_u local.set $carry
      local.get $i i32.const 1 i32.add local.set $i
      br $lp
    end end
    local.get $carry i64.eqz i32.eqz
    if                                   ;; one more limb for the final carry
      i32.const {{FpBig}} local.get $n i32.const 2 i32.shl i32.add
      local.get $carry i32.wrap_i64
      i32.store
      local.get $n i32.const 1 i32.add local.set $n
    end
    local.get $n global.set $__bnlen
  )

  (func $__bn_add (param $a i32)         ;; bn += a (small) — used for the round-up
    (local $i i32) (local $n i32) (local $carry i64) (local $sum i64) (local $addr i32)
    global.get $__bnlen local.set $n
    local.get $a i64.extend_i32_u local.set $carry
    i32.const 0 local.set $i
    block $done loop $lp
      local.get $carry i64.eqz br_if $done
      local.get $i local.get $n i32.ge_s
      if                                 ;; ran past the top → append the carry limb
        i32.const {{FpBig}} local.get $n i32.const 2 i32.shl i32.add
        local.get $carry i32.wrap_i64
        i32.store
        local.get $n i32.const 1 i32.add local.set $n
        i64.const 0 local.set $carry
        br $done
      end
      i32.const {{FpBig}} local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $addr i32.load i64.extend_i32_u
      local.get $carry
      i64.add
      local.set $sum
      local.get $addr
      local.get $sum i32.wrap_i64
      i32.store
      local.get $sum i64.const 32 i64.shr_u local.set $carry
      local.get $i i32.const 1 i32.add local.set $i
      br $lp
    end end
    local.get $n global.set $__bnlen
  )

  (func $__bn_shr1 (result i32)          ;; bn >>= 1, returning the bit shifted out
    (local $i i32) (local $n i32) (local $carry i32) (local $cur i32) (local $out i32) (local $addr i32)
    global.get $__bnlen local.set $n
    i32.const 0 local.set $out
    local.get $n i32.const 0 i32.gt_s
    if
      i32.const {{FpBig}} i32.load i32.const 1 i32.and local.set $out
    end
    i32.const 0 local.set $carry
    local.get $n i32.const 1 i32.sub local.set $i
    block $done loop $lp
      local.get $i i32.const 0 i32.lt_s br_if $done
      i32.const {{FpBig}} local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $addr i32.load local.set $cur
      local.get $addr
      local.get $cur i32.const 1 i32.shr_u
      local.get $carry i32.const 31 i32.shl
      i32.or
      i32.store                          ;; limb[i] = (cur>>1) | (carry<<31)
      local.get $cur i32.const 1 i32.and local.set $carry
      local.get $i i32.const 1 i32.sub local.set $i
      br $lp
    end end
    local.get $n i32.const 0 i32.gt_s    ;; trim a top limb that became 0
    if
      i32.const {{FpBig}} local.get $n i32.const 1 i32.sub i32.const 2 i32.shl i32.add
      i32.load
      i32.eqz
      if local.get $n i32.const 1 i32.sub local.set $n end
    end
    local.get $n global.set $__bnlen
    local.get $out
  )

  (func $__bn_divmod (param $d i32) (result i32)   ;; bn /= d, returns bn % d
    (local $i i32) (local $n i32) (local $rem i64) (local $cur i64) (local $dd i64) (local $addr i32)
    global.get $__bnlen local.set $n
    local.get $d i64.extend_i32_u local.set $dd
    i64.const 0 local.set $rem
    local.get $n i32.const 1 i32.sub local.set $i
    block $done loop $lp
      local.get $i i32.const 0 i32.lt_s br_if $done
      i32.const {{FpBig}} local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $rem i64.const 32 i64.shl
      local.get $addr i32.load i64.extend_i32_u
      i64.or
      local.set $cur                     ;; cur = (rem<<32) | limb[i]
      local.get $addr
      local.get $cur local.get $dd i64.div_u i32.wrap_i64
      i32.store                          ;; limb[i] = cur / d
      local.get $cur local.get $dd i64.rem_u local.set $rem
      local.get $i i32.const 1 i32.sub local.set $i
      br $lp
    end end
    block $tdone loop $tlp                ;; trim leading zero limbs
      local.get $n i32.const 0 i32.le_s br_if $tdone
      i32.const {{FpBig}} local.get $n i32.const 1 i32.sub i32.const 2 i32.shl i32.add
      i32.load
      i32.eqz i32.eqz
      br_if $tdone
      local.get $n i32.const 1 i32.sub local.set $n
      br $tlp
    end end
    local.get $n global.set $__bnlen
    local.get $rem i32.wrap_i64
  )

""");
        }

        // The %f formatter. Decompose the f64 as M·2^E2 (exact), form D = round(value
        // × 10^prec) as a big integer — N = M·10^prec then either N<<E2 (E2≥0, exact)
        // or round(N>>-E2) with round-half-to-even — and stage D's decimal digits with
        // the point `prec` places from the right. The unsigned magnitude is then laid
        // out in a field by $__pf_emit (sign + width + justify), exactly like the
        // integer conversions. inf/nan are emitted as text (never zero-padded).
        if (_runtimeUsed.Contains("__pf_f"))
        {
            sb.Append($$"""
  (func $__pf_f (param $v f64) (param $prec i32) (param $posSign i32) (param $width i32) (param $mode i32) (param $alt i32) (param $upper i32)
    (local $bits i64) (local $exp i32) (local $mant i64) (local $M i64) (local $E2 i32)
    (local $sign i32) (local $pos i32) (local $end i32) (local $i i32) (local $d i32)
    (local $k i32) (local $bit i32) (local $half i32) (local $sticky i32) (local $uc i32)
    local.get $upper i32.const 5 i32.shl local.set $uc     ;; 0 or 32: lowercase − $uc = uppercase
    local.get $v i64.reinterpret_f64 local.set $bits
    local.get $bits i64.const 0 i64.lt_s         ;; sign byte: '-' for a negative value
    if (result i32) i32.const 45 else local.get $posSign end
    local.set $sign
    local.get $bits i64.const 52 i64.shr_u i64.const 2047 i64.and i32.wrap_i64 local.set $exp
    local.get $bits i64.const 4503599627370495 i64.and local.set $mant   ;; mant = bits & ((1<<52)-1)
    local.get $exp i32.const 2047 i32.eq         ;; inf / nan
    if
      i32.const {{FpDigEnd - 3}} local.set $pos
      local.get $mant i64.eqz
      if                                         ;; "inf" / "INF"
        local.get $pos i32.const 105 local.get $uc i32.sub i32.store8
        local.get $pos i32.const 1 i32.add i32.const 110 local.get $uc i32.sub i32.store8
        local.get $pos i32.const 2 i32.add i32.const 102 local.get $uc i32.sub i32.store8
      else                                       ;; "nan" / "NAN"
        local.get $pos i32.const 110 local.get $uc i32.sub i32.store8
        local.get $pos i32.const 1 i32.add i32.const 97 local.get $uc i32.sub i32.store8
        local.get $pos i32.const 2 i32.add i32.const 110 local.get $uc i32.sub i32.store8
      end
      local.get $pos
      i32.const 3
      local.get $sign
      local.get $width
      local.get $mode i32.const 1 i32.eq if (result i32) i32.const 1 else i32.const 0 end
      call $__pf_emit
      return
    end
    local.get $exp i32.eqz                        ;; M, E2 (subnormal vs normal)
    if
      local.get $mant local.set $M
      i32.const -1074 local.set $E2
    else
      local.get $mant i64.const 4503599627370496 i64.or local.set $M     ;; mant | (1<<52)
      local.get $exp i32.const 1075 i32.sub local.set $E2
    end
    local.get $M call $__bn_set                   ;; N = M * 10^prec
    i32.const 0 local.set $i
    block $md loop $ml
      local.get $i local.get $prec i32.ge_s br_if $md
      i32.const 10 call $__bn_mul
      local.get $i i32.const 1 i32.add local.set $i
      br $ml
    end end
    local.get $E2 i32.const 0 i32.ge_s
    if                                            ;; D = N << E2 (exact): ×2, E2 times
      i32.const 0 local.set $i
      block $sd loop $sl
        local.get $i local.get $E2 i32.ge_s br_if $sd
        i32.const 2 call $__bn_mul
        local.get $i i32.const 1 i32.add local.set $i
        br $sl
      end end
    else                                          ;; D = round(N >> k), round-half-even
      i32.const 0 local.get $E2 i32.sub local.set $k
      i32.const 0 local.set $sticky
      i32.const 0 local.set $half
      i32.const 0 local.set $i
      block $kd loop $kl
        local.get $i local.get $k i32.ge_s br_if $kd
        call $__bn_shr1 local.set $bit
        local.get $i local.get $k i32.const 1 i32.sub i32.eq
        if                                        ;; the top discarded bit is the "half" bit
          local.get $bit local.set $half
        else                                      ;; lower discarded bits feed "sticky"
          local.get $sticky local.get $bit i32.or local.set $sticky
        end
        local.get $i i32.const 1 i32.add local.set $i
        br $kl
      end end
      local.get $half                             ;; round up iff half && (sticky || odd)
      if
        local.get $sticky
        i32.const {{FpBig}} i32.load i32.const 1 i32.and
        i32.or
        if i32.const 1 call $__bn_add end
      end
    end
    i32.const {{FpDigEnd}} local.set $pos          ;; stage digits right-aligned, point at `prec`
    i32.const {{FpDigEnd}} local.set $end
    i32.const 0 local.set $i
    block $dd loop $dl
      local.get $i local.get $prec i32.eq
      local.get $prec i32.const 0 i32.gt_s
      i32.and
      if
        local.get $pos i32.const 1 i32.sub local.set $pos
        local.get $pos i32.const 46 i32.store8   ;; '.'
      end
      i32.const 10 call $__bn_divmod local.set $d
      local.get $pos i32.const 1 i32.sub local.set $pos
      local.get $pos local.get $d i32.const 48 i32.add i32.store8
      local.get $i i32.const 1 i32.add local.set $i
      global.get $__bnlen                          ;; continue while bn != 0 OR i < prec+1
      local.get $i local.get $prec i32.const 1 i32.add i32.lt_s
      i32.or
      br_if $dl
    end end
    local.get $alt local.get $prec i32.eqz i32.and ;; '#' with prec 0 → trailing point
    if
      local.get $end i32.const 46 i32.store8
      local.get $end i32.const 1 i32.add local.set $end
    end
    local.get $pos
    local.get $end local.get $pos i32.sub
    local.get $sign
    local.get $width
    local.get $mode
    call $__pf_emit
  )

""");
        }

        // ---- region big-integer for the %e/%g Dragon formatter ---------------------
        // Two big-integers (numerator R, denominator S) scaled into [1,10) so each
        // significant digit is R/S ∈ 0..9, obtained by repeated subtraction (no
        // quotient estimation). Each region carries its limb count in its first word
        // (so two can be live at once, unlike the single-global %f bignum); helpers
        // take a base pointer. Lengths stay trimmed of leading zeros for $__r_cmp.
        if (_runtimeUsed.Contains("__rbn"))
        {
            sb.Append("""
  (func $__r_set (param $base i32) (param $v i64)   ;; region = v (v < 2^53)
    local.get $base i32.const 4 i32.add local.get $v i32.wrap_i64 i32.store
    local.get $base i32.const 8 i32.add local.get $v i64.const 32 i64.shr_u i32.wrap_i64 i32.store
    local.get $base
    local.get $v i64.const 4294967295 i64.gt_u
    if (result i32) i32.const 2
    else local.get $v i64.eqz if (result i32) i32.const 0 else i32.const 1 end end
    i32.store
  )

  (func $__r_mul (param $base i32) (param $m i32)   ;; region *= m (small u32)
    (local $i i32) (local $n i32) (local $carry i64) (local $p i64) (local $addr i32)
    local.get $base i32.load local.set $n
    i32.const 0 local.set $i
    i64.const 0 local.set $carry
    block $d loop $l
      local.get $i local.get $n i32.ge_s br_if $d
      local.get $base i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $addr i32.load i64.extend_i32_u
      local.get $m i64.extend_i32_u
      i64.mul
      local.get $carry i64.add local.set $p
      local.get $addr local.get $p i32.wrap_i64 i32.store
      local.get $p i64.const 32 i64.shr_u local.set $carry
      local.get $i i32.const 1 i32.add local.set $i
      br $l
    end end
    local.get $carry i64.eqz i32.eqz
    if
      local.get $base i32.const 4 i32.add local.get $n i32.const 2 i32.shl i32.add
      local.get $carry i32.wrap_i64 i32.store
      local.get $n i32.const 1 i32.add local.set $n
    end
    local.get $base local.get $n i32.store
  )

  (func $__r_copy (param $dst i32) (param $src i32)
    (local $i i32) (local $n i32)
    local.get $src i32.load local.set $n
    local.get $dst local.get $n i32.store
    i32.const 0 local.set $i
    block $d loop $l
      local.get $i local.get $n i32.ge_s br_if $d
      local.get $dst i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add
      local.get $src i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add i32.load
      i32.store
      local.get $i i32.const 1 i32.add local.set $i
      br $l
    end end
  )

  (func $__r_cmp (param $a i32) (param $b i32) (result i32)   ;; -1 / 0 / 1
    (local $na i32) (local $nb i32) (local $i i32) (local $va i32) (local $vb i32)
    local.get $a i32.load local.set $na
    local.get $b i32.load local.set $nb
    local.get $na local.get $nb i32.gt_u if i32.const 1 return end
    local.get $na local.get $nb i32.lt_u if i32.const -1 return end
    local.get $na i32.const 1 i32.sub local.set $i
    block $d loop $l
      local.get $i i32.const 0 i32.lt_s br_if $d
      local.get $a i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add i32.load local.set $va
      local.get $b i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add i32.load local.set $vb
      local.get $va local.get $vb i32.gt_u if i32.const 1 return end
      local.get $va local.get $vb i32.lt_u if i32.const -1 return end
      local.get $i i32.const 1 i32.sub local.set $i
      br $l
    end end
    i32.const 0
  )

  (func $__r_sub (param $a i32) (param $b i32)   ;; a -= b  (a >= b)
    (local $na i32) (local $nb i32) (local $i i32) (local $borrow i64) (local $diff i64) (local $bv i64) (local $addr i32)
    local.get $a i32.load local.set $na
    local.get $b i32.load local.set $nb
    i64.const 0 local.set $borrow
    i32.const 0 local.set $i
    block $d loop $l
      local.get $i local.get $na i32.ge_s br_if $d
      local.get $a i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add local.set $addr
      local.get $i local.get $nb i32.lt_s
      if (result i64)
        local.get $b i32.const 4 i32.add local.get $i i32.const 2 i32.shl i32.add i32.load i64.extend_i32_u
      else i64.const 0 end
      local.set $bv
      local.get $addr i32.load i64.extend_i32_u
      local.get $bv i64.sub
      local.get $borrow i64.sub
      local.set $diff
      local.get $addr
      local.get $diff i64.const 4294967295 i64.and i32.wrap_i64
      i32.store
      local.get $diff i64.const 63 i64.shr_u i64.const 1 i64.and local.set $borrow
      local.get $i i32.const 1 i32.add local.set $i
      br $l
    end end
    block $td loop $tl                    ;; trim leading zero limbs
      local.get $na i32.const 0 i32.le_s br_if $td
      local.get $a i32.const 4 i32.add local.get $na i32.const 1 i32.sub i32.const 2 i32.shl i32.add i32.load
      i32.eqz i32.eqz br_if $td
      local.get $na i32.const 1 i32.sub local.set $na
      br $tl
    end end
    local.get $a local.get $na i32.store
  )

""");
        }

        // The %e/%g significant-digit generator (scaled Dragon). Decompose v = M·2^E2,
        // form R/S = |value| exactly, scale into [1,10) tracking the decimal exponent
        // X, then emit `ndigits` digits (raw 0-9) into FpEDig by repeated subtraction,
        // rounding the last digit half-to-even (a carry can ripple to a new leading
        // digit, bumping X). Returns X; the caller (finite, nonzero) lays out the field.
        if (_runtimeUsed.Contains("__dragon"))
        {
            sb.Append($$"""
  (func $__dragon (param $v f64) (param $ndigits i32) (result i32)
    (local $bits i64) (local $exp i32) (local $mant i64) (local $M i64) (local $E2 i32)
    (local $X i32) (local $i i32) (local $d i32) (local $c i32) (local $lastOdd i32) (local $carry i32) (local $j i32)
    local.get $v i64.reinterpret_f64 local.set $bits
    local.get $bits i64.const 52 i64.shr_u i64.const 2047 i64.and i32.wrap_i64 local.set $exp
    local.get $bits i64.const 4503599627370495 i64.and local.set $mant
    local.get $exp i32.eqz
    if
      local.get $mant local.set $M
      i32.const -1074 local.set $E2
    else
      local.get $mant i64.const 4503599627370496 i64.or local.set $M
      local.get $exp i32.const 1075 i32.sub local.set $E2
    end
    i32.const {{FpR}} local.get $M call $__r_set      ;; R = M
    i32.const {{FpS}} i64.const 1 call $__r_set       ;; S = 1
    local.get $E2 i32.const 0 i32.ge_s
    if                                                ;; R <<= E2
      i32.const 0 local.set $i
      block $ad loop $al
        local.get $i local.get $E2 i32.ge_s br_if $ad
        i32.const {{FpR}} i32.const 2 call $__r_mul
        local.get $i i32.const 1 i32.add local.set $i
        br $al
      end end
    else                                              ;; S <<= -E2
      i32.const 0 local.set $i
      block $bd loop $bl
        local.get $i i32.const 0 local.get $E2 i32.sub i32.ge_s br_if $bd
        i32.const {{FpS}} i32.const 2 call $__r_mul
        local.get $i i32.const 1 i32.add local.set $i
        br $bl
      end end
    end
    i32.const 0 local.set $X
    block $sd loop $sl                                 ;; while R < S: R *= 10, X--
      i32.const {{FpR}} i32.const {{FpS}} call $__r_cmp i32.const 0 i32.ge_s br_if $sd
      i32.const {{FpR}} i32.const 10 call $__r_mul
      local.get $X i32.const 1 i32.sub local.set $X
      br $sl
    end end
    block $ud loop $ul                                 ;; while R >= 10*S: S *= 10, X++
      i32.const {{FpMul}} i32.const {{FpS}} call $__r_copy
      i32.const {{FpMul}} i32.const 10 call $__r_mul
      i32.const {{FpR}} i32.const {{FpMul}} call $__r_cmp i32.const 0 i32.lt_s br_if $ud
      i32.const {{FpS}} i32.const 10 call $__r_mul
      local.get $X i32.const 1 i32.add local.set $X
      br $ul
    end end
    i32.const 0 local.set $i                           ;; generate ndigits digits
    block $gd loop $gl
      local.get $i local.get $ndigits i32.ge_s br_if $gd
      i32.const 0 local.set $d
      block $qd loop $ql
        i32.const {{FpR}} i32.const {{FpS}} call $__r_cmp i32.const 0 i32.lt_s br_if $qd
        i32.const {{FpR}} i32.const {{FpS}} call $__r_sub
        local.get $d i32.const 1 i32.add local.set $d
        br $ql
      end end
      i32.const {{FpEDig}} local.get $i i32.add local.get $d i32.store8
      local.get $i i32.const 1 i32.add local.get $ndigits i32.lt_s
      if i32.const {{FpR}} i32.const 10 call $__r_mul end   ;; ×10 for the next digit
      local.get $i i32.const 1 i32.add local.set $i
      br $gl
    end end
    i32.const {{FpEDig}} local.get $ndigits i32.const 1 i32.sub i32.add i32.load8_u
    i32.const 1 i32.and local.set $lastOdd            ;; parity of the last emitted digit
    i32.const {{FpMul}} i32.const {{FpR}} call $__r_copy      ;; compare 2*R vs S
    i32.const {{FpMul}} i32.const 2 call $__r_mul
    i32.const {{FpMul}} i32.const {{FpS}} call $__r_cmp local.set $c
    local.get $c i32.const 0 i32.gt_s
    local.get $c i32.eqz local.get $lastOdd i32.and
    i32.or
    if                                                ;; round half-to-even: bump the digits
      i32.const 1 local.set $carry
      local.get $ndigits i32.const 1 i32.sub local.set $j
      block $rd loop $rl
        local.get $carry i32.eqz br_if $rd
        local.get $j i32.const 0 i32.lt_s br_if $rd
        i32.const {{FpEDig}} local.get $j i32.add i32.load8_u i32.const 1 i32.add local.set $d
        local.get $d i32.const 10 i32.eq
        if
          i32.const {{FpEDig}} local.get $j i32.add i32.const 0 i32.store8
        else
          i32.const {{FpEDig}} local.get $j i32.add local.get $d i32.store8
          i32.const 0 local.set $carry
        end
        local.get $j i32.const 1 i32.sub local.set $j
        br $rl
      end end
      local.get $carry
      if                                              ;; carried out of the top: "1" + zeros, X++
        i32.const {{FpEDig}} i32.const 1 i32.store8
        local.get $X i32.const 1 i32.add local.set $X
      end
    end
    local.get $X
  )

""");
        }

        // %e — "[-]d.ddde±XX". Handle sign / inf / nan / zero, then the Dragon digits
        // and the exponent (signed, at least two digits). Field layout via $__pf_emit.
        if (_runtimeUsed.Contains("__pf_e"))
        {
            sb.Append($$"""
  (func $__pf_e (param $v f64) (param $prec i32) (param $posSign i32) (param $width i32) (param $mode i32) (param $alt i32) (param $upper i32)
    (local $bits i64) (local $exp i32) (local $sign i32) (local $X i32) (local $p i32) (local $i i32) (local $ax i32) (local $uc i32)
    local.get $upper i32.const 5 i32.shl local.set $uc  ;; 0 or 32: lowercase − $uc = uppercase
    local.get $v i64.reinterpret_f64 local.set $bits
    local.get $bits i64.const 0 i64.lt_s
    if (result i32) i32.const 45 else local.get $posSign end
    local.set $sign
    local.get $bits i64.const 52 i64.shr_u i64.const 2047 i64.and i32.wrap_i64 local.set $exp
    local.get $exp i32.const 2047 i32.eq                ;; inf / nan (INF / NAN when upper)
    if
      local.get $bits i64.const 4503599627370495 i64.and i64.eqz
      if
        i32.const {{FpEOut}} i32.const 105 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 1 i32.add i32.const 110 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 2 i32.add i32.const 102 local.get $uc i32.sub i32.store8
      else
        i32.const {{FpEOut}} i32.const 110 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 1 i32.add i32.const 97 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 2 i32.add i32.const 110 local.get $uc i32.sub i32.store8
      end
      i32.const {{FpEOut}} i32.const 3 local.get $sign local.get $width
      local.get $mode i32.const 1 i32.eq if (result i32) i32.const 1 else i32.const 0 end
      call $__pf_emit
      return
    end
    local.get $bits i64.const 9223372036854775807 i64.and i64.eqz   ;; |v| == 0
    if
      i32.const 0 local.set $i                          ;; digits all zero, X = 0
      block $zd loop $zl
        local.get $i local.get $prec i32.gt_s br_if $zd
        i32.const {{FpEDig}} local.get $i i32.add i32.const 0 i32.store8
        local.get $i i32.const 1 i32.add local.set $i
        br $zl
      end end
      i32.const 0 local.set $X
    else
      local.get $v local.get $prec i32.const 1 i32.add call $__dragon local.set $X
    end
    i32.const {{FpEOut}} local.set $p                    ;; assemble d.ddde±XX
    local.get $p i32.const {{FpEDig}} i32.load8_u i32.const 48 i32.add i32.store8
    local.get $p i32.const 1 i32.add local.set $p
    local.get $prec i32.const 0 i32.gt_s local.get $alt i32.or
    if
      local.get $p i32.const 46 i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    i32.const 1 local.set $i
    block $fd loop $fl
      local.get $i local.get $prec i32.gt_s br_if $fd
      local.get $p i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
      local.get $i i32.const 1 i32.add local.set $i
      br $fl
    end end
    local.get $p i32.const 101 local.get $uc i32.sub i32.store8   ;; 'e' / 'E'
    local.get $p i32.const 1 i32.add local.set $p
    local.get $X i32.const 0 i32.lt_s
    if (result i32)
      local.get $p i32.const 45 i32.store8 i32.const 0 local.get $X i32.sub
    else
      local.get $p i32.const 43 i32.store8 local.get $X
    end
    local.set $ax
    local.get $p i32.const 1 i32.add local.set $p
    local.get $ax i32.const 100 i32.ge_s               ;; exponent digits (>= 2)
    if
      local.get $p local.get $ax i32.const 100 i32.div_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    local.get $p local.get $ax i32.const 10 i32.div_u i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
    local.get $p i32.const 1 i32.add local.set $p
    local.get $p local.get $ax i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
    local.get $p i32.const 1 i32.add local.set $p
    i32.const {{FpEOut}}
    local.get $p i32.const {{FpEOut}} i32.sub
    local.get $sign local.get $width local.get $mode
    call $__pf_emit
  )

""");
        }

        // %g — P significant digits, then choose %f-style (when -4 <= X < P) or
        // %e-style presentation, stripping trailing zeros unless '#' (alt). Reuses the
        // Dragon generator; shares the inf/nan/zero handling shape with %e.
        if (_runtimeUsed.Contains("__pf_g"))
        {
            sb.Append($$"""
  (func $__pf_g (param $v f64) (param $P i32) (param $posSign i32) (param $width i32) (param $mode i32) (param $alt i32) (param $upper i32)
    (local $bits i64) (local $exp i32) (local $sign i32) (local $X i32) (local $p i32) (local $i i32) (local $ax i32) (local $ndig i32) (local $uc i32)
    local.get $upper i32.const 5 i32.shl local.set $uc  ;; 0 or 32: lowercase − $uc = uppercase
    local.get $v i64.reinterpret_f64 local.set $bits
    local.get $bits i64.const 0 i64.lt_s
    if (result i32) i32.const 45 else local.get $posSign end
    local.set $sign
    local.get $bits i64.const 52 i64.shr_u i64.const 2047 i64.and i32.wrap_i64 local.set $exp
    local.get $exp i32.const 2047 i32.eq                ;; inf / nan (INF / NAN when upper)
    if
      local.get $bits i64.const 4503599627370495 i64.and i64.eqz
      if
        i32.const {{FpEOut}} i32.const 105 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 1 i32.add i32.const 110 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 2 i32.add i32.const 102 local.get $uc i32.sub i32.store8
      else
        i32.const {{FpEOut}} i32.const 110 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 1 i32.add i32.const 97 local.get $uc i32.sub i32.store8
        i32.const {{FpEOut}} i32.const 2 i32.add i32.const 110 local.get $uc i32.sub i32.store8
      end
      i32.const {{FpEOut}} i32.const 3 local.get $sign local.get $width
      local.get $mode i32.const 1 i32.eq if (result i32) i32.const 1 else i32.const 0 end
      call $__pf_emit
      return
    end
    local.get $bits i64.const 9223372036854775807 i64.and i64.eqz   ;; |v| == 0
    if
      i32.const 0 local.set $i
      block $zd loop $zl
        local.get $i local.get $P i32.ge_s br_if $zd
        i32.const {{FpEDig}} local.get $i i32.add i32.const 0 i32.store8
        local.get $i i32.const 1 i32.add local.set $i
        br $zl
      end end
      i32.const 0 local.set $X
    else
      local.get $v local.get $P call $__dragon local.set $X
    end
    local.get $P local.set $ndig                        ;; strip trailing zeros unless '#'
    local.get $alt i32.eqz
    if
      block $kd loop $kl
        local.get $ndig i32.const 1 i32.le_s br_if $kd
        i32.const {{FpEDig}} local.get $ndig i32.const 1 i32.sub i32.add i32.load8_u i32.eqz i32.eqz br_if $kd
        local.get $ndig i32.const 1 i32.sub local.set $ndig
        br $kl
      end end
    end
    i32.const {{FpEOut}} local.set $p
    local.get $X i32.const -4 i32.ge_s local.get $X local.get $P i32.lt_s i32.and
    if                                                  ;; ---- %f-style ----
      local.get $X i32.const 0 i32.ge_s
      if                                                ;; integer part = digits[0..X]
        i32.const 0 local.set $i
        block $id loop $il
          local.get $i local.get $X i32.gt_s br_if $id
          local.get $p
          local.get $i local.get $ndig i32.lt_s
          if (result i32) i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 48 i32.add else i32.const 48 end
          i32.store8
          local.get $p i32.const 1 i32.add local.set $p
          local.get $i i32.const 1 i32.add local.set $i
          br $il
        end end
        local.get $ndig local.get $X i32.const 1 i32.add i32.gt_s local.get $alt i32.or
        if                                              ;; fractional digits[X+1 ..]
          local.get $p i32.const 46 i32.store8
          local.get $p i32.const 1 i32.add local.set $p
          local.get $X i32.const 1 i32.add local.set $i
          block $jd loop $jl
            local.get $i local.get $ndig i32.ge_s br_if $jd
            local.get $p i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 48 i32.add i32.store8
            local.get $p i32.const 1 i32.add local.set $p
            local.get $i i32.const 1 i32.add local.set $i
            br $jl
          end end
        end
      else                                              ;; X < 0: "0." + (-X-1) zeros + digits
        local.get $p i32.const 48 i32.store8
        local.get $p i32.const 1 i32.add i32.const 46 i32.store8
        local.get $p i32.const 2 i32.add local.set $p
        i32.const 0 local.set $i
        block $ld loop $ll
          local.get $i i32.const 0 local.get $X i32.sub i32.const 1 i32.sub i32.ge_s br_if $ld
          local.get $p i32.const 48 i32.store8
          local.get $p i32.const 1 i32.add local.set $p
          local.get $i i32.const 1 i32.add local.set $i
          br $ll
        end end
        i32.const 0 local.set $i
        block $md loop $ml
          local.get $i local.get $ndig i32.ge_s br_if $md
          local.get $p i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 48 i32.add i32.store8
          local.get $p i32.const 1 i32.add local.set $p
          local.get $i i32.const 1 i32.add local.set $i
          br $ml
        end end
      end
    else                                                ;; ---- %e-style ----
      local.get $p i32.const {{FpEDig}} i32.load8_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
      local.get $ndig i32.const 1 i32.gt_s local.get $alt i32.or
      if
        local.get $p i32.const 46 i32.store8
        local.get $p i32.const 1 i32.add local.set $p
        i32.const 1 local.set $i
        block $ed loop $el
          local.get $i local.get $ndig i32.ge_s br_if $ed
          local.get $p i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 48 i32.add i32.store8
          local.get $p i32.const 1 i32.add local.set $p
          local.get $i i32.const 1 i32.add local.set $i
          br $el
        end end
      end
      local.get $p i32.const 101 local.get $uc i32.sub i32.store8   ;; 'e' / 'E'
      local.get $p i32.const 1 i32.add local.set $p
      local.get $X i32.const 0 i32.lt_s
      if (result i32)
        local.get $p i32.const 45 i32.store8 i32.const 0 local.get $X i32.sub
      else
        local.get $p i32.const 43 i32.store8 local.get $X
      end
      local.set $ax
      local.get $p i32.const 1 i32.add local.set $p
      local.get $ax i32.const 100 i32.ge_s
      if
        local.get $p local.get $ax i32.const 100 i32.div_u i32.const 48 i32.add i32.store8
        local.get $p i32.const 1 i32.add local.set $p
      end
      local.get $p local.get $ax i32.const 10 i32.div_u i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
      local.get $p local.get $ax i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    i32.const {{FpEOut}}
    local.get $p i32.const {{FpEOut}} i32.sub
    local.get $sign local.get $width local.get $mode
    call $__pf_emit
  )

""");
        }

        // ---- heap: a bump allocator over the region above the shadow stack --------
        // $__hp bumps UP from HeapBase (end of page 1); the stack grows DOWN inside
        // page 1, so they never meet. Each block carries an i32 size header (payload at
        // block+8, kept 8-aligned) so realloc can copy the old bytes; free never
        // reclaims. malloc grows linear memory on demand and returns NULL if it can't.
        if (_runtimeUsed.Contains("malloc"))
        {
            sb.Append("""
  (func $malloc (param $n i32) (result i32)
    (local $block i32) (local $end i32)
    global.get $__hp
    local.set $block             ;; block = heap pointer (8-aligned)
    local.get $block             ;; end = block + 8 (header) + align8(n)
    i32.const 8
    i32.add
    local.get $n
    i32.const 7
    i32.add
    i32.const -8
    i32.and
    i32.add
    local.set $end
    block $enough                ;; grow linear memory if the bump would overrun it
      local.get $end
      memory.size
      i32.const 16
      i32.shl                    ;; current size in bytes (pages * 65536)
      i32.le_u
      br_if $enough
      local.get $end             ;; grow by ceil((end - bytes) / 65536) pages
      memory.size
      i32.const 16
      i32.shl
      i32.sub
      i32.const 65535
      i32.add
      i32.const 16
      i32.shr_u
      memory.grow
      i32.const -1
      i32.eq
      if
        i32.const 0              ;; grow failed → NULL
        return
      end
    end
    local.get $end
    global.set $__hp             ;; commit the bump
    local.get $block             ;; store the size header
    local.get $n
    i32.store
    local.get $block             ;; return the payload pointer (block + 8)
    i32.const 8
    i32.add
  )

""");
        }

        if (_runtimeUsed.Contains("calloc"))
        {
            sb.Append("""
  (func $calloc (param $nmemb i32) (param $size i32) (result i32)
    (local $bytes i32) (local $p i32) (local $i i32)
    local.get $nmemb
    local.get $size
    i32.mul
    local.set $bytes
    local.get $bytes
    call $malloc
    local.set $p
    local.get $p                 ;; zero the payload when non-NULL
    if
      block $zdone
        loop $zlp
          local.get $i
          local.get $bytes
          i32.ge_u
          br_if $zdone
          local.get $p
          local.get $i
          i32.add
          i32.const 0
          i32.store8
          local.get $i
          i32.const 1
          i32.add
          local.set $i
          br $zlp
        end
      end
    end
    local.get $p
  )

""");
        }

        if (_runtimeUsed.Contains("realloc"))
        {
            sb.Append("""
  (func $realloc (param $p i32) (param $n i32) (result i32)
    (local $np i32) (local $old i32) (local $cnt i32) (local $i i32)
    local.get $p                 ;; realloc(NULL, n) == malloc(n)
    i32.eqz
    if
      local.get $n
      call $malloc
      return
    end
    local.get $p                 ;; old payload size from the header
    i32.const 8
    i32.sub
    i32.load
    local.set $old
    local.get $n                 ;; allocate the new block
    call $malloc
    local.set $np
    local.get $np
    i32.eqz
    if
      i32.const 0                ;; allocation failed → NULL (old block kept)
      return
    end
    local.get $old               ;; cnt = min(old, n)
    local.get $n
    i32.lt_u
    if (result i32)
      local.get $old
    else
      local.get $n
    end
    local.set $cnt
    block $cdone                 ;; copy cnt bytes old → new
      loop $clp
        local.get $i
        local.get $cnt
        i32.ge_u
        br_if $cdone
        local.get $np
        local.get $i
        i32.add
        local.get $p
        local.get $i
        i32.add
        i32.load8_u
        i32.store8
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $clp
      end
    end
    local.get $np
  )

""");
        }

        return sb.ToString();
    }

    /// <summary>The scratch value-local for a store of type <paramref name="t"/>
    /// (i32/i64), marking it for declaration.</summary>
    private string ScratchFor(CType t)
    {
        switch (ValType(t))
        {
            case "i64": _scratch64 = true; return "$__t64";
            case "f32": _scratchF32 = true; return "$__tf32";
            case "f64": _scratchF64 = true; return "$__tf64";
            default: _scratch32 = true; return "$__t32";
        }
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>The wasm instruction for an arithmetic/relational binary op on a
    /// common operand type — dispatching to the float or the integer instruction set.
    /// Both operands have already been converted to <paramref name="operand"/>.</summary>
    private string ArithBinOp(BinOp op, CType operand) =>
        ValType(operand) is "f32" or "f64" ? FloatBinOp(op, operand) : IntBinOp(op, operand);

    /// <summary>Float arithmetic and comparisons. wasm float compares have no
    /// signedness suffix; there is no float remainder or bitwise/shift op (C forbids
    /// <c>%</c> and the bitwise ops on floating operands — <c>fmod</c> is a call).</summary>
    private string FloatBinOp(BinOp op, CType operand)
    {
        var vt = ValType(operand);
        return op switch
        {
            BinOp.Add => $"{vt}.add",
            BinOp.Sub => $"{vt}.sub",
            BinOp.Mul => $"{vt}.mul",
            BinOp.Div => $"{vt}.div",
            BinOp.Eq => $"{vt}.eq",
            BinOp.Ne => $"{vt}.ne",
            BinOp.Lt => $"{vt}.lt",
            BinOp.Gt => $"{vt}.gt",
            BinOp.Le => $"{vt}.le",
            BinOp.Ge => $"{vt}.ge",
            _ => throw new IrUnsupportedException($"the wat target does not support floating-point operator {op}"),
        };
    }

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
        if (p is CType.Prim prim)
        {
            if (!prim.Integer) { return prim.Bytes <= 4 ? "f32.load" : "f64.load"; }
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
        if (p is CType.Prim prim)
        {
            if (!prim.Integer) { return prim.Bytes <= 4 ? "f32.store" : "f64.store"; }
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
        var vt = ValType(c.Type);
        // An i32 already serves as a wasm condition; an i64 or a float must be reduced
        // to an i32 truth value (x != 0).
        if (vt is "f32" or "f64") { Line($"{vt}.const 0"); Line($"{vt}.ne"); }
        else if (vt == "i64") { Line("i64.const 0"); Line("i64.ne"); }
    }

    private void EmitBool(CExpr c)
    {
        EmitExpr(c);
        var vt = ValType(c.Type);
        if (vt is "f32" or "f64") { Line($"{vt}.const 0"); Line($"{vt}.ne"); }
        else if (vt == "i64") { Line("i64.const 0"); Line("i64.ne"); }
        else { Line("i32.const 0"); Line("i32.ne"); }
    }

    private string ValType(CType t) => _wat.RenderType(t);

    private static bool IsSignedInt(CType t) =>
        t.Unqualified is CType.Prim { Integer: true, Signed: true } or CType.Enum;

    private void Line(string text) => _out.Append(' ', _indent * 2).Append(text).Append('\n');
}
