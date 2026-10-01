#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// Lowers the typed IR to WebAssembly text (<c>.wat</c>) — the second backend
/// behind <see cref="ITarget"/>, a peer of <see cref="CSharpBackend"/> rather than a
/// rewrite of the pipeline (both consume the same <see cref="IrModule"/> via
/// <see cref="DotCC.Compiler.BuildIr"/>). Where CSharpBackend prints precedence-driven
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
internal sealed partial class WatBackend
{
    // Top of the shadow stack while the data leaves it room: it grows DOWN from the end
    // of the first page, while the data (strings, globals) grows UP from a 1 KiB null
    // guard. Data past half the page moves the stack top past the data (StackBytes).
    // The heap lives ABOVE the stack top: $__hp bumps UP from there and malloc grows
    // linear memory on demand, while the stack grows DOWN from the same point, so the
    // two never collide.
    private const int StackTop = 65536;
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

    // Break/continue targets, modelling C's rules: `break` leaves the nearest
    // enclosing loop OR switch, so both push here; `continue` only ever targets the
    // nearest enclosing LOOP, so a switch does not push to it (a `continue` inside a
    // switch body still steps the surrounding loop).
    private readonly List<string> _breakTargets = new();
    private readonly List<string> _contTargets = new();
    private int _labelSeq;
    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
    private CType _currentRet = CType.Int;

    /// <summary>The module being emitted: its layout model sizes and places aggregates
    /// (<see cref="IrModule.SizeOfConst"/>, <see cref="IrModule.OffsetOfConst"/>), the same
    /// model <c>sizeof</c> and <c>offsetof</c> fold from.</summary>
    private IrModule? _unit;

    private IrModule Unit => _unit ?? throw new InvalidOperationException("the wat backend has no module");

    /// <summary>Each file-scope object with external linkage → its fixed address in the data
    /// area, where it lives for the program (see <see cref="PlaceGlobals"/>). Keyed by name, so
    /// an <c>extern</c> declaration in one unit reaches the object another unit defines.</summary>
    private readonly Dictionary<string, int> _globals = new(StringComparer.Ordinal);

    /// <summary>Each object with internal linkage (a <c>static</c> at file or block scope) → its
    /// address. Keyed by the symbol: two units' <c>static const double S1</c> are two objects
    /// under one name (musl's <c>__sin.c</c> and <c>__sindf.c</c>).</summary>
    private readonly Dictionary<Symbol, int> _tuGlobals = new(ReferenceEqualityComparer.Instance);

    /// <summary>The address <see cref="PlaceGlobals"/> gave <paramref name="sym"/>.</summary>
    private bool TryGlobalAddr(Symbol sym, out int addr) =>
        sym.IsTuLocal ? _tuGlobals.TryGetValue(sym, out addr) : _globals.TryGetValue(sym.TargetName, out addr);

    /// <summary>The address of <c>errno</c>, a slot of its own in the data area (dotcc's
    /// headers leave the name to the runtime, so it reaches the backend unresolved).</summary>
    private int? _errnoAddr;

    /// <summary>The frame buffer of each pointer a promoted <c>malloc</c> points at (an
    /// <see cref="ArrayDecl"/> whose symbol stays a pointer, C#'s <c>stackalloc</c>).</summary>
    private readonly Dictionary<Symbol, int> _arrayBuffers = new();

    /// <summary>True while the initializer stores address absolute memory (the globals'
    /// start function) rather than the current frame.</summary>
    private bool _absoluteInit;

    /// <summary>The functions used as values, in table order: a function pointer is its index
    /// in the module's <c>funcref</c> table, from 1 (0 is the null pointer, which traps when
    /// called), handed out as functions are first referred to.</summary>
    private readonly List<string> _fnTable = new();
    private readonly Dictionary<string, int> _fnTableIndex = new(StringComparer.Ordinal);

    /// <summary>The function signatures <c>call_indirect</c> checks against, each declared
    /// once as a module <c>(type …)</c>: signature text → type name.</summary>
    private readonly Dictionary<string, string> _sigTypes = new(StringComparer.Ordinal);

    /// <summary>True once the program calls <c>exit</c>, which imports WASI's <c>proc_exit</c>.</summary>
    private bool _usesProcExit;

    /// <summary>The frame slots each call that passes or returns a struct by value uses: the
    /// copy of each aggregate argument, and the result's slot, by call node (reference
    /// identity), placed with the function's frame before its body is emitted.</summary>
    private readonly Dictionary<CExpr, (int? Result, Dictionary<int, int> Args)> _callTemps = new(ReferenceEqualityComparer.Instance);

    /// <summary>The frame slot of each compound literal in an expression (<c>(struct P){1, 2}</c>,
    /// <c>(int[]){1, 2}</c>), by node; a declaration's initializer is stored into its own slot.</summary>
    private readonly Dictionary<CExpr, int> _literalSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>The C stack the program gets when its data does not fit below the default
    /// stack top: the stack then starts past the data and grows down through this.</summary>
    private const int StackBytes = 1 << 20;

    // Per-function shadow-stack frame: symbol → byte offset within the frame, the
    // frame's total size, and whether the function has one at all. Memory-resident
    // symbols are the address-taken ones (Symbol.AddressTaken) plus all arrays.
    private readonly Dictionary<Symbol, int> _frame = new();
    private int _frameSize;
    private bool _hasFrame;
    // Scratch locals, declared only when used (the body buffer makes that possible):
    // a saved store value (one per wasm value type) and a saved store address (i32)
    // for the read-modify-write of a compound assignment / ++/-- through memory.
    /// <summary>The scratch locals in use at this point of the emit, and the most of each kind
    /// one function held at once (its declarations), by kind: <c>i32</c>, <c>i64</c>,
    /// <c>f32</c>, <c>f64</c>, or <c>addr</c> (an i32 address). A use acquires one and releases
    /// it when done, so an expression evaluated while an outer one holds a scratch (the
    /// <c>li++</c> inside <c>log_[li++] = v</c>, evaluated while the stored value waits) gets
    /// its own instead of overwriting it.</summary>
    private readonly Dictionary<string, int> _scratchInUse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _scratchMax = new(StringComparer.Ordinal);
    // The label/dispatch variable for the CFG dispatch-loop lowering of a function that
    // uses goto/labels (see WatBackend.Cfg.cs). Declared only for such functions.
    private bool _scratchLbl;

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
            // %a is an exact bit-dump (no big-integer / Dragon machinery); it only
            // needs the shared field-layout helper.
            case "__pf_a": NeedRuntime("__pf_emit"); break;
            // %p reuses the unsigned-radix path for the "0x"+hex case and the field
            // layout directly for the "(nil)" case.
            case "__pf_p": NeedRuntime("__pf_int_u"); NeedRuntime("__pf_emit"); break;
            // The heap allocators: calloc/realloc are written in terms of malloc.
            // malloc itself has no helper deps (it uses the $__hp global + memory.grow);
            // free isn't a runtime function at all (it lowers to an inline drop).
            case "calloc": NeedRuntime("malloc"); break;
            case "realloc": NeedRuntime("malloc"); break;
        }
    }

    private WatBackend() { _out = _sb; }

    public static string Run(IrModule unit) => new WatBackend().Module(unit);

    /// <summary>Assemble the module: emit the function bodies first (interning string
    /// literals into data segments), then wrap them with the linear memory, the stack
    /// pointer global, the data segments, and the <c>main</c> export.</summary>
    private string Module(IrModule unit)
    {
        _unit = unit;
        foreach (var fn in unit.Functions) { _defined.Add(fn.Sym.Name); }
        PlaceGlobals(unit);

        _indent = 1;
        var hasMain = false;
        foreach (var fn in unit.Functions)
        {
            EmitFunc(fn);
            if (fn.Sym.Name == "main") { hasMain = true; }
        }
        var initGlobals = GlobalsInitFunc(unit);
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
        if (_usesProcExit)
        {
            // exit(): WASI proc_exit ends the program with its status.
            m.Append("  (import \"wasi_snapshot_preview1\" \"proc_exit\" (func $proc_exit (param i32)))\n");
        }
        // Export the memory only when the WASI shim needs to read iovecs out of it;
        // non-I/O modules keep the byte-identical plain `(memory 1)`.
        // The data (strings, globals) sits from DataBase up; the stack tops at StackTop
        // while the data leaves it room (every small program), else past the data, and
        // the heap starts where the stack tops.
        var stackTop = _dataEnd <= StackTop / 2 ? StackTop : AlignUp(_dataEnd, 16) + StackBytes;
        var pages = (stackTop + 65535) / 65536;
        m.Append(usesIo ? $"  (memory (export \"memory\") {pages})\n" : $"  (memory {pages})\n");
        foreach (var (sig, name) in _sigTypes) { m.Append($"  (type {name} (func{sig}))\n"); }
        if (_fnTable.Count > 0)
        {
            // Slot 0 stays empty: the null function pointer, which call_indirect traps on.
            m.Append($"  (table {_fnTable.Count + 1} funcref)\n");
            m.Append($"  (elem (i32.const 1) func {string.Join(" ", _fnTable.Select(n => "$" + n))})\n");
        }
        m.Append($"  (global $__sp (mut i32) (i32.const {stackTop}))\n");
        if (usesHeap)
        {
            // The bump-allocation pointer: next free heap byte, growing UP from the end
            // of the initial page (malloc grows linear memory past it on demand).
            m.Append($"  (global $__hp (mut i32) (i32.const {stackTop}))\n");
        }
        if (usesIo)
        {
            // The output sink for the byte primitives: $__ob = -1 means fd mode (the
            // fd_write target fd is $__fd, default 1 = stdout; fprintf(stderr,…) flips
            // it to 2); otherwise $__ob is a write cursor into linear memory (sprintf),
            // bounded by $__oend, with $__ocount tracking the total chars produced.
            m.Append("  (global $__ob (mut i32) (i32.const -1))\n");
            m.Append("  (global $__oend (mut i32) (i32.const 0))\n");
            m.Append("  (global $__ocount (mut i32) (i32.const 0))\n");
            m.Append("  (global $__fd (mut i32) (i32.const 1))\n");
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
        if (initGlobals.Length > 0)
        {
            m.Append(initGlobals);
            m.Append("  (start $__init_globals)\n");
        }
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
        _breakTargets.Clear();
        _contTargets.Clear();
        _labelSeq = 0;
        _frame.Clear();
        _frameSize = 0;
        _scratchInUse.Clear(); _scratchMax.Clear();
        _scratchLbl = false;
        _cfgSwitchLocals.Clear();

        var ret = fn.Sym.Type is CType.Func f ? f.Return : CType.Int;
        _currentRet = ret;
        _callTemps.Clear();
        _literalSlots.Clear();
        _arrayBuffers.Clear();

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
            // A struct parameter is the address of the caller's copy: it is memory already.
            if (p.AddressTaken && !IsAggregate(p.Type)) { Place(p); spillParams.Add(p); }
        }
        var bodyLocals = new List<Symbol>();
        foreach (var s in fn.Body.Stmts) { CollectLocals(s, bodyLocals); }
        foreach (var loc in bodyLocals)
        {
            if (loc.AddressTaken || IsAddressValued(loc.Type)) { Place(loc); }
            else { valueLocals.Add(loc); }
        }
        // A promoted malloc's buffer (an ArrayDecl over a pointer symbol) is frame memory.
        foreach (var s in fn.Body.Stmts) { ReserveArrayBuffers(s, ref cursor); }
        // Each call that passes or returns a struct by value gets slots of its own: a copy of
        // each aggregate argument (the callee may change its parameter) and the result's.
        foreach (var s in fn.Body.Stmts)
        {
            ForEachExpr(s, e =>
            {
                int PlaceTemp(CType t)
                {
                    cursor = AlignUp(cursor, SlotAlign(t));
                    var at = cursor;
                    cursor += Math.Max(1, WasmSizeOf(t));
                    return at;
                }
                if (e is StructInit or DefaultLit && IsAggregate(e.Type)) { _literalSlots[e] = PlaceTemp(e.Type); return; }
                if (e is StackArray sa) { _literalSlots[e] = PlaceTemp(new CType.Array(sa.Element, sa.Elems.Count)); return; }
                if (AggregateCallShape(e) is not { } shape) { return; }
                int? result = IsAggregate(shape.Fn.Return) ? PlaceTemp(shape.Fn.Return) : null;
                var args = new Dictionary<int, int>();
                for (var i = 0; i < shape.Args.Count; i++)
                {
                    var pt = i < shape.Fn.Params.Count ? shape.Fn.Params[i] : shape.Args[i].Type;
                    if (IsAggregate(pt)) { args[i] = PlaceTemp(pt); }
                }
                _callTemps[e] = (result, args);
            });
        }
        _frameSize = AlignUp(cursor, 8);
        _hasFrame = _frameSize > 0;

        // A struct result goes to the caller's slot, whose address is a hidden first parameter;
        // the function returns that address, the struct's value as a caller sees it.
        var ps = (IsAggregate(ret) ? " (param $__sret i32)" : "")
            + string.Concat(fn.Params.Select(p => $" (param ${p.TargetName} {_wat.RenderType(p.Type)})"));
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
        // A function that uses goto/labels can't be lowered by the structured emitter
        // (wasm has no arbitrary jump); route it through the CFG dispatch-loop instead.
        // Goto-free functions keep the clean structured emit.
        if (ContainsLabel(fn.Body.Stmts))
        {
            EmitViaCfg(fn, ret);
        }
        else
        {
            foreach (var s in fn.Body.Stmts) { EmitStmt(s); }
            EmitFnEnd(fn, ret);
        }
        _out = prev;

        foreach (var v in valueLocals) { Line($"(local ${v.TargetName} {_wat.RenderType(v.Type)})"); }
        foreach (var v in _cfgSwitchLocals) { Line($"(local ${v.TargetName} {_wat.RenderType(v.Type)})"); }
        if (_hasFrame) { Line("(local $__fp i32)"); }
        foreach (var local in ScratchLocals()) { Line(local); }
        if (_scratchLbl) { Line("(local $__lbl i32)"); }
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
            case Seq q:
                foreach (var x in q.Stmts) { CollectLocals(x, acc); }
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
            case Switch sw:
                foreach (var sec in sw.Sections)
                {
                    foreach (var st in sec.Body) { CollectLocals(st, acc); }
                }
                break;
            case Labeled lab:
                CollectLocals(lab.Body, acc);
                break;
            case CaseLabelStmt cl:
                CollectLocals(cl.Body, acc);
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

            // Brace-less sequence (multi-declarator decl that split into
            // DeclStmt + ArrayDecl) — wat has no block scoping for locals
            // anyway (CollectLocals hoists them), so emit flat like Block.
            case Seq q:
                foreach (var inner in q.Stmts) { EmitStmt(inner); }
                break;

            case DeclStmt d:
                foreach (var ld in d.Decls)
                {
                    if (ld.Init is not { } init) { continue; }
                    if (_frame.TryGetValue(ld.Sym, out var off) && IsAggregate(ld.Sym.Type))
                    {
                        EmitAggregateInit(off, ld.Sym.Type, init);
                    }
                    else if (_frame.TryGetValue(ld.Sym, out off))
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
                EmitDiscarded(es.Expr);
                break;

            case Return r:
                EmitReturnValue(r.Value);
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
                if (_breakTargets.Count == 0)
                {
                    throw new IrUnsupportedException("`break` outside a loop or switch");
                }
                Line($"br {_breakTargets[^1]}");
                break;

            case Continue:
                if (_contTargets.Count == 0)
                {
                    throw new IrUnsupportedException("`continue` outside a loop");
                }
                Line($"br {_contTargets[^1]}");
                break;

            case Switch sw:
                EmitSwitch(sw);
                break;

            case FallthroughMarker:
                // C23 `[[fallthrough]];` — a marker for the -Wimplicit-fallthrough
                // check only; no codegen.
                break;

            case CaseLabelStmt:
                throw new IrUnsupportedException("a case/default label nested inside another statement (Duff's device) is not supported on the wat target");

            case Goto:
            case Labeled:
                throw new IrUnsupportedException("`goto` and labels are not yet supported on the wat target");

            default:
                throw new IrUnsupportedException($"the wat target does not yet support the statement {s.GetType().Name}");
        }
    }

    /// <summary>A local array lives in the shadow-stack frame. A brace initializer
    /// stores each element into its slot; an uninitialised array is left as-is
    /// (reading it before assignment is UB in C, and the frame is reused memory).</summary>
    private void EmitArrayDecl(ArrayDecl ad)
    {
        if (_arrayBuffers.TryGetValue(ad.Sym, out var buffer))
        {
            // The pointer a promoted malloc returned: the address of its frame buffer.
            if (_frame.TryGetValue(ad.Sym, out var ptrSlot))
            {
                EmitFrameAddr(ptrSlot);
                EmitFrameAddr(buffer);
                Line(StoreInstr(ad.Sym.Type));
            }
            else
            {
                EmitFrameAddr(buffer);
                Line($"local.set ${ad.Sym.TargetName}");
            }
            return;
        }
        if (!_frame.TryGetValue(ad.Sym, out var baseOff))
        {
            throw new IrUnsupportedException("the wat target could not place array local in the frame");
        }
        if (ad.Inits is not { } inits) { return; }
        var elemSize = WasmSizeOf(ad.Element);
        for (var i = 0; i < inits.Count; i++)
        {
            if (IsAggregate(ad.Element))
            {
                EmitAggregateInit(baseOff + i * elemSize, ad.Element, inits[i]);
                continue;
            }
            EmitFrameAddr(baseOff + i * elemSize);
            EmitExpr(inits[i]);
            EmitConvert(inits[i].Type, ad.Element);
            Line(StoreInstr(ad.Element));
        }
    }

    /// <summary>Initialise the struct or union in the frame slot at <paramref name="offset"/>
    /// from <paramref name="init"/>: a brace initializer zeroes the slot (the members it
    /// does not reach are zero, C11 6.7.9p21) and stores each member it gives; a zeroed
    /// stack value (a promoted <c>malloc</c>) is the zeroing alone; any other aggregate
    /// expression is copied.</summary>
    private void EmitAggregateInit(int offset, CType type, CExpr init)
    {
        var size = WasmSizeOf(type);
        switch (init)
        {
            case StructInit si:
                EmitInitAddr(offset);
                Line("i32.const 0");
                Line($"i32.const {size}");
                Line("memory.fill");
                StoreAggregateMembers(offset, type, si);
                break;
            case StackNew or DefaultLit:
                EmitInitAddr(offset);
                Line("i32.const 0");
                Line($"i32.const {size}");
                Line("memory.fill");
                break;
            default:
                EmitInitAddr(offset);
                EmitExpr(init);
                Line($"i32.const {size}");
                Line("memory.copy");
                break;
        }
    }

    /// <summary>Store the members <paramref name="si"/> gives into the (zeroed) aggregate of
    /// type <paramref name="type"/> at frame offset <paramref name="offset"/>: a nested struct
    /// member recursively, an array member element by element, a zero store skipped.</summary>
    private void StoreAggregateMembers(int offset, CType type, StructInit si)
    {
        var name = ((CType.Named)type.Unqualified).Name;
        foreach (var m in si.Members)
        {
            if (m.FieldType.Unqualified is CType.VoidType) { continue; }
            if (Unit.StructFields.TryGetValue(name, out var fields)
                && fields.FirstOrDefault(f => f.Name == m.Name) is { BitWidth: not null })
            {
                throw new IrUnsupportedException($"the wat target does not yet support bit-field members ('{m.Name}')");
            }
            var at = offset + (Unit.OffsetOfConst(name, m.Name)
                ?? throw new IrUnsupportedException($"the wat target cannot place member '{m.Name}' of {name}"));
            StoreInitValue(at, m.FieldType, m.Value);
        }
    }

    /// <summary>Store one initializer value of type <paramref name="type"/> at frame offset
    /// <paramref name="at"/> into storage already zeroed.</summary>
    private void StoreInitValue(int at, CType type, CExpr value)
    {
        switch (value)
        {
            case StructInit nested when IsAggregate(type):
                StoreAggregateMembers(at, type, nested);
                return;
            case ArrayValue av when type.Unqualified is CType.Array:
            {
                var step = WasmSizeOf(av.Element);
                for (var i = 0; i < av.Elems.Count; i++) { StoreInitValue(at + i * step, av.Element, av.Elems[i]); }
                return;
            }
            case LitInt { Value: 0 }:
            case NullPtr:
                return;   // the storage is already zero
        }
        if (IsAggregate(type))
        {
            EmitInitAddr(at);
            EmitExpr(value);
            Line($"i32.const {WasmSizeOf(type)}");
            Line("memory.copy");
            return;
        }
        EmitInitAddr(at);
        EmitExpr(value);
        EmitConvert(value.Type, type);
        Line(StoreInstr(type));
    }

    /// <summary>Evaluate <paramref name="e"/> for its effects: whatever value it leaves is
    /// dropped (a void expression, a void call or a <c>(void)</c> cast, leaves none).</summary>
    private void EmitDiscarded(CExpr e)
    {
        EmitExpr(e);
        if (e.Type.Unqualified is not CType.VoidType) { Line("drop"); }
    }

    /// <summary>The function type and arguments of a call that passes or returns a struct by
    /// value (and so needs frame slots, see <see cref="_callTemps"/>), or null.</summary>
    private static (CType.Func Fn, IReadOnlyList<CExpr> Args)? AggregateCallShape(CExpr e)
    {
        var (fnType, args) = e switch
        {
            Call { CalleeSym.Type: var t } c => (t.Unqualified switch
            {
                CType.Func f => f,
                CType.Pointer { Pointee: var pt } => pt.Unqualified as CType.Func,
                _ => null,
            }, (IReadOnlyList<CExpr>)c.Args),
            IndirectCall ic => (ic.Callee.Type.Unqualified switch
            {
                CType.Func f => f,
                CType.Pointer { Pointee: var pt } => pt.Unqualified as CType.Func,
                _ => null,
            }, ic.Args),
            _ => ((CType.Func?)null, (IReadOnlyList<CExpr>)System.Array.Empty<CExpr>()),
        };
        if (fnType is null) { return null; }
        var any = IsAggregate(fnType.Return)
            || fnType.Params.Any(IsAggregate)
            || args.Any(a => IsAggregate(a.Type));
        return any ? (fnType, args) : null;
    }

    /// <summary>A call's arguments, each converted to its parameter's type: first the address of
    /// the result's slot when the callee returns a struct, and a struct argument as the address
    /// of a fresh copy in its slot (C passes a copy, which the callee may change).</summary>
    private void EmitCallArgs(CExpr call, IReadOnlyList<CExpr> args, IReadOnlyList<CType>? paramTypes)
    {
        _callTemps.TryGetValue(call, out var temps);
        if (temps.Result is { } resultSlot) { EmitFrameAddr(resultSlot); }
        for (var i = 0; i < args.Count; i++)
        {
            if (temps.Args is { } argSlots && argSlots.TryGetValue(i, out var slot))
            {
                EmitFrameAddr(slot);
                EmitExpr(args[i]);
                Line($"i32.const {WasmSizeOf(args[i].Type)}");
                Line("memory.copy");
                EmitFrameAddr(slot);
                continue;
            }
            if (IsAggregate(args[i].Type))
            {
                throw new IrUnsupportedException("the wat target has no frame slot for a struct argument here");
            }
            EmitExpr(args[i]);
            if (paramTypes is { } pts && i < pts.Count) { EmitConvert(args[i].Type, pts[i]); }
        }
    }

    /// <summary>Leave a function's return value: a scalar converted to the return type, or, for
    /// a struct, the value copied into the caller's slot ($__sret) and that slot's address.</summary>
    private void EmitReturnValue(CExpr? value)
    {
        if (value is null) { return; }
        if (IsAggregate(_currentRet))
        {
            Line("local.get $__sret");
            EmitExpr(value);
            Line($"i32.const {WasmSizeOf(_currentRet)}");
            Line("memory.copy");
            Line("local.get $__sret");
            return;
        }
        EmitExpr(value);
        EmitConvert(value.Type, _currentRet);
    }

    /// <summary>Visit every expression in <paramref name="s"/>, nested ones included, for the
    /// frame layout's look ahead at a function's calls (see <see cref="_callTemps"/>). A node it
    /// does not look into is one the backend refuses anyway.</summary>
    private static void ForEachExpr(CStmt s, Action<CExpr> visit)
    {
        void E(CExpr? e)
        {
            if (e is null) { return; }
            visit(e);
            switch (e)
            {
                case Paren p: E(p.Inner); break;
                case Unary u: E(u.Operand); break;
                case Binary b: E(b.Left); E(b.Right); break;
                case Assign a: E(a.Target); E(a.Value); break;
                case Cast c: E(c.Operand); break;
                case CondExpr ce: E(ce.Cond); E(ce.Then); E(ce.Else); break;
                case Index ix: E(ix.Base); E(ix.Idx); break;
                case Member m: E(m.Base); break;
                case Call c: foreach (var a in c.Args) { E(a); } break;
                case IndirectCall ic: E(ic.Callee); foreach (var a in ic.Args) { E(a); } break;
                case CommaOp co: foreach (var a in co.Items) { E(a); } break;
                case CommaSeq cs: foreach (var a in cs.Items) { E(a); } break;
                case StructInit si: foreach (var m in si.Members) { E(m.Value); } break;
                case ArrayValue av: foreach (var x in av.Elems) { E(x); } break;
            }
        }
        void Init(CExpr? init)
        {
            if (init is DefaultLit) { return; }
            if (init is StructInit si) { foreach (var m in si.Members) { Init(m.Value); } }
            else if (init is ArrayValue av) { foreach (var x in av.Elems) { Init(x); } }
            else { E(init); }
        }
        void S(CStmt? st)
        {
            switch (st)
            {
                case null: break;
                case Block b: foreach (var x in b.Stmts) { S(x); } break;
                case Seq q: foreach (var x in q.Stmts) { S(x); } break;
                // A declaration's own brace initializer is stored into the declared object's slot,
                // so only what is inside it is looked at.
                case DeclStmt d: foreach (var ld in d.Decls) { Init(ld.Init); } break;
                case ArrayDecl ad: if (ad.Inits is { } inits) { foreach (var x in inits) { Init(x); } } break;
                case ExprStmt es: E(es.Expr); break;
                case If i: E(i.Cond); S(i.Then); S(i.Else); break;
                case While w: E(w.Cond); S(w.Body); break;
                case DoWhile dw: S(dw.Body); E(dw.Cond); break;
                case For f: S(f.Init); E(f.Cond); E(f.Post); S(f.Body); break;
                case Return r: E(r.Value); break;
                case Switch sw: E(sw.Subject); foreach (var sec in sw.Sections) { foreach (var x in sec.Body) { S(x); } } break;
                case Labeled lab: S(lab.Body); break;
                case CaseLabelStmt cl: S(cl.Body); break;
            }
        }
        S(s);
    }

    /// <summary>The wasm instruction a one-argument <c>&lt;math.h&gt;</c> function is, exactly
    /// (IEEE-754 square root, absolute value and the directed roundings), or null. C's
    /// <c>round</c> is not <c>nearest</c>, which rounds halves to even.</summary>
    private static string? MathInstr(string callee) => callee switch
    {
        "sqrt" => "f64.sqrt", "fabs" => "f64.abs", "floor" => "f64.floor", "ceil" => "f64.ceil", "trunc" => "f64.trunc",
        "sqrtf" => "f32.sqrt", "fabsf" => "f32.abs", "floorf" => "f32.floor", "ceilf" => "f32.ceil", "truncf" => "f32.trunc",
        _ => null,
    };

    /// <summary><c>memcpy</c>/<c>memmove</c> (<c>memory.copy</c>, which allows overlap) and
    /// <c>memset</c> (<c>memory.fill</c>, which stores the value's low byte): destination,
    /// source or value, then the count, and the destination is the call's value.</summary>
    private void EmitBulkMemory(Call c, string instr)
    {
        if (c.Args.Count != 3)
        {
            throw new IrUnsupportedException($"the wat target expects {c.Callee} with 3 argument(s)");
        }
        EmitExpr(c.Args[0]);
        var dst = AcquireScratch("addr");
        Line($"local.tee {dst}");
        EmitExpr(c.Args[1]);
        EmitConvert(c.Args[1].Type, instr == "memory.fill" ? CType.Int : c.Args[1].Type);
        EmitExpr(c.Args[2]);
        EmitConvert(c.Args[2].Type, CType.Int);
        Line(instr);
        Line($"local.get {dst}");
        ReleaseScratch("addr");
    }

    /// <summary>Reserve the frame buffer of each promoted <c>malloc</c> in <paramref name="s"/>
    /// (see <see cref="_arrayBuffers"/>): its element size times its constant count.</summary>
    private void ReserveArrayBuffers(CStmt s, ref int cursor)
    {
        var decls = new List<ArrayDecl>();
        void Walk(CStmt? st)
        {
            switch (st)
            {
                case ArrayDecl ad when ad.Sym.Type.Unqualified is CType.Pointer: decls.Add(ad); break;
                case Block b: foreach (var x in b.Stmts) { Walk(x); } break;
                case Seq q: foreach (var x in q.Stmts) { Walk(x); } break;
                case If i: Walk(i.Then); Walk(i.Else); break;
                case While w: Walk(w.Body); break;
                case DoWhile dw: Walk(dw.Body); break;
                case For f: Walk(f.Init); Walk(f.Body); break;
                case Switch sw: foreach (var sec in sw.Sections) { foreach (var x in sec.Body) { Walk(x); } } break;
                case Labeled lab: Walk(lab.Body); break;
                case CaseLabelStmt cl: Walk(cl.Body); break;
            }
        }
        Walk(s);
        foreach (var ad in decls)
        {
            if (ad.CountExpr is not LitInt { Value: { } count })
            {
                throw new IrUnsupportedException($"the wat target needs a constant size for the buffer of '{ad.Sym.Name}'");
            }
            cursor = AlignUp(cursor, SlotAlign(ad.Element));
            _arrayBuffers[ad.Sym] = cursor;
            cursor += Math.Max(1, checked((int)count * WasmSizeOf(ad.Element)));
        }
    }

    /// <summary>The address of <c>errno</c>'s slot, placed in the data area on first use.</summary>
    private int ErrnoAddr()
    {
        if (_errnoAddr is { } at) { return at; }
        _dataEnd = AlignUp(_dataEnd, 4);
        _errnoAddr = _dataEnd;
        _dataEnd += 4;
        return _dataEnd - 4;
    }

    /// <summary>Intern a wide string literal's code units (<paramref name="width"/> bytes each,
    /// little-endian) and its terminator, as <see cref="InternBytes"/> interns a narrow one.</summary>
    private int InternUnits(IReadOnlyList<int> units, int width)
    {
        var bytes = new List<int>(units.Count * width + width - 1);
        foreach (var u in units)
        {
            for (var b = 0; b < width; b++) { bytes.Add((u >> (8 * b)) & 0xFF); }
        }
        // InternBytes adds one NUL byte; the rest of the terminator's width comes from here.
        for (var b = 1; b < width; b++) { bytes.Add(0); }
        _dataEnd = AlignUp(_dataEnd, width);
        return InternBytes(bytes);
    }

    /// <summary>Push the address an initializer store goes to: an offset in the current
    /// frame, or an absolute address while the globals' start function is emitted.</summary>
    private void EmitInitAddr(int at)
    {
        if (_absoluteInit) { Line($"i32.const {at}"); }
        else { EmitFrameAddr(at); }
    }

    /// <summary>Give every file-scope object (and block-scope static) a fixed address in the
    /// data area, before any function refers to one: C's static storage, which lives for the
    /// program and starts zeroed (wasm memory does).</summary>
    private void PlaceGlobals(IrModule unit)
    {
        foreach (var g in unit.Globals)
        {
            if (g.Flexible is not null)
            {
                throw new IrUnsupportedException($"the wat target does not yet support an initialized flexible array member ('{g.Sym.Name}')");
            }
            _dataEnd = AlignUp(_dataEnd, SlotAlign(g.Sym.Type));
            if (g.Sym.IsTuLocal) { _tuGlobals[g.Sym] = _dataEnd; } else { _globals[g.Sym.TargetName] = _dataEnd; }
            _dataEnd += Math.Max(1, WasmSizeOf(g.Sym.Type));
        }
    }

    /// <summary>Push a global's address (its value, for an array or an aggregate).</summary>
    private void EmitGlobalAddr(Symbol sym)
    {
        if (!TryGlobalAddr(sym, out var addr))
        {
            throw new IrUnsupportedException($"the wat target has no definition of the global '{sym.Name}'");
        }
        Line($"i32.const {addr}");
    }

    /// <summary>The start function (<c>$__init_globals</c>, run when the module is
    /// instantiated) that stores each global's initializer at its address, or empty when
    /// none needs a store (zero storage is already zero). An array's elements and a
    /// struct's members are stored one by one, as a local's are.</summary>
    private string GlobalsInitFunc(IrModule unit)
    {
        var prev = _out;
        var body = new StringBuilder();
        _out = body;
        _indent = 2;
        _hasFrame = false;
        _absoluteInit = true;
        _scratchInUse.Clear(); _scratchMax.Clear();
        try
        {
            foreach (var g in unit.Globals)
            {
                if (g.Init is not { } init) { continue; }
                if (!TryGlobalAddr(g.Sym, out var at)) { throw new InvalidOperationException($"global '{g.Sym.Name}' was never placed"); }
                switch (init)
                {
                    case PinnedArray pa:
                        if (pa.Elems is { } elems)
                        {
                            var step = WasmSizeOf(pa.Element);
                            for (var i = 0; i < elems.Count; i++) { StoreInitValue(at + i * step, pa.Element, elems[i]); }
                        }
                        break;
                    case StructInit si when IsAggregate(g.Sym.Type):
                        StoreAggregateMembers(at, g.Sym.Type, si);
                        break;
                    default:
                        StoreInitValue(at, g.Sym.Type, init);
                        break;
                }
            }
        }
        finally
        {
            _absoluteInit = false;
            _out = prev;
        }
        if (body.Length == 0) { return ""; }
        var func = new StringBuilder("  (func $__init_globals\n");
        foreach (var local in ScratchLocals()) { func.Append("    ").Append(local).Append('\n'); }
        func.Append(body).Append("  )\n");
        return func.ToString();
    }

    /// <summary>The table index of function <paramref name="fn"/>, the value a pointer to it
    /// holds, given it on first use.</summary>
    private int TableIndex(Symbol fn)
    {
        var name = fn.TargetName;
        if (!_defined.Contains(fn.Name))
        {
            throw new IrUnsupportedException($"the wat target cannot take the address of '{fn.Name}', which the program does not define");
        }
        if (!_fnTableIndex.TryGetValue(name, out var index))
        {
            _fnTable.Add(name);
            index = _fnTable.Count;
            _fnTableIndex[name] = index;
        }
        return index;
    }

    /// <summary>The module type name of the signature a call through a pointer of type
    /// <paramref name="fnType"/> checks: its parameters' and result's wasm value types.</summary>
    private string SigType(CType fnType)
    {
        var f = fnType.Unqualified switch
        {
            CType.Func ft => ft,
            CType.Pointer { Pointee: var pt } when pt.Unqualified is CType.Func pf => pf,
            _ => throw new IrUnsupportedException($"the wat target cannot call through a {fnType.Describe()}"),
        };
        if (f.Variadic)
        {
            throw new IrUnsupportedException("the wat target does not yet call a variadic function through a pointer");
        }
        var sig = (IsAggregate(f.Return) ? " (param i32)" : "") + string.Concat(f.Params.Where(p => p.Unqualified is not CType.VoidType).Select(p => $" (param {ValType(p)})"))
            + (f.Return.Unqualified is CType.VoidType ? "" : $" (result {ValType(f.Return)})");
        if (!_sigTypes.TryGetValue(sig, out var name))
        {
            name = $"$__sig{_sigTypes.Count}";
            _sigTypes[sig] = name;
        }
        return name;
    }

    /// <summary>A call through a function pointer: the arguments (each converted to its
    /// parameter's type when the pointer's type gives them), then the pointer (a table
    /// index), then <c>call_indirect</c>, which traps on a null or mistyped one.</summary>
    private void EmitCallIndirect(CExpr callee, IReadOnlyList<CExpr> args, IReadOnlyList<CType>? paramTypes, CExpr? callSite = null)
    {
        var sig = SigType(callee.Type);
        var fnParams = (callee.Type.Unqualified switch
        {
            CType.Func ft => ft,
            CType.Pointer { Pointee: var pt } => pt.Unqualified as CType.Func,
            _ => null,
        })?.Params;
        var types = paramTypes ?? fnParams;
        EmitCallArgs(callSite ?? callee, args, types);
        EmitExpr(callee);
        Line($"call_indirect (type {sig})");
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
        _breakTargets.Add(brk);
        _contTargets.Add(cont);
        EmitStmt(body);
        _contTargets.RemoveAt(_contTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
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

    /// <summary>Lower a C <c>switch</c> into wasm's structured control flow. Each
    /// section gets a nested <c>block</c>; the bodies are emitted in source order
    /// between the blocks' <c>end</c>s, so on a matched branch control runs that
    /// section's body and then FALLS THROUGH into the next — exactly C's semantics
    /// (a <c>break</c> exits via the enclosing <c>$swbrk</c>). The innermost code is
    /// the dispatch: the subject is evaluated once into a scratch local, then each
    /// <c>case</c> value is compared against it and branches to that section's block;
    /// if nothing matches it branches to <c>default</c>'s block, or past the whole
    /// switch when there is no default. <c>continue</c> is deliberately untouched —
    /// a switch does not push a continue target, so it still steps the enclosing loop.
    /// <para>Dispatch is a comparison chain (correct for any, even sparse, case
    /// values); a dense <c>br_table</c> is a possible later optimisation.</para></summary>
    private void EmitSwitch(Switch sw)
    {
        var n = _labelSeq++;
        var brk = $"$swbrk{n}";
        var k = sw.Sections.Count;
        var subjType = sw.Subject.Type;
        var vt = ValType(subjType);
        EmitExpr(sw.Subject);
        var subj = AcquireScratch(subjType);      // subject is read once per case; cache it
        Line($"local.set {subj}");

        var secLabel = new string[k];
        var defaultIdx = -1;
        for (var i = 0; i < k; i++)
        {
            secLabel[i] = $"$sw{n}_{i}";
            foreach (var lab in sw.Sections[i].Labels)
            {
                if (lab.CaseExpr is null) { defaultIdx = i; }
            }
        }

        // Open $swbrk, then the section blocks in REVERSE so section 0 is innermost:
        // its body (emitted right after its `end`) comes first and falls into the rest.
        Line($"block {brk}");
        _indent++;
        for (var i = k - 1; i >= 0; i--)
        {
            Line($"block {secLabel[i]}");
            _indent++;
        }

        // Dispatch (innermost): subject == caseValue ? br to that section : try the next;
        // fall through to default (or out of the switch) when nothing matches.
        for (var i = 0; i < k; i++)
        {
            foreach (var lab in sw.Sections[i].Labels)
            {
                if (lab.CaseExpr is not { } ce) { continue; }   // 'default' resolved below
                Line($"local.get {subj}");
                EmitExpr(ce);
                EmitConvert(ce.Type, subjType);
                Line($"{vt}.eq");
                Line($"br_if {secLabel[i]}");
            }
        }
        Line(defaultIdx >= 0 ? $"br {secLabel[defaultIdx]}" : $"br {brk}");
        ReleaseScratch(subjType);                 // the sections never read the subject

        // Close each section block in forward order, emitting its body right after the
        // `end` (so it runs on a hit AND falls into the next section — C fall-through).
        _breakTargets.Add(brk);
        for (var i = 0; i < k; i++)
        {
            _indent--;
            Line("end"); // $sw{n}_{i}
            foreach (var st in sw.Sections[i].Body) { EmitStmt(st); }
        }
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        _indent--;
        Line("end"); // $swbrk
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
                EmitAddress(ix);
                // A nested array decays, an aggregate element is its address.
                if (!IsAddressValued(e.Type)) { Line(LoadInstr(e.Type)); }
                break;
            case Member m:
                EmitAddress(m);
                if (!IsAddressValued(e.Type)) { Line(LoadInstr(e.Type)); }
                break;
            case IndirectCall ic:
                EmitCallIndirect(ic.Callee, ic.Args, ic.ParamTypes, ic);
                break;
            case CommaOp co:
                // Every operand left to right; all but the last are discarded.
                for (var i = 0; i < co.Items.Count - 1; i++) { EmitDiscarded(co.Items[i]); }
                EmitExpr(co.Items[^1]);
                break;
            case CommaSeq cs:
                for (var i = 0; i < cs.Items.Count - 1; i++) { EmitDiscarded(cs.Items[i]); }
                EmitExpr(cs.Items[^1]);
                break;
            case StructInit si when _literalSlots.TryGetValue(si, out var siSlot):
                // A compound literal: its slot zeroed, the members it gives stored, its address.
                EmitAggregateInit(siSlot, si.Type, si);
                EmitFrameAddr(siSlot);
                break;
            case StackArray sa when _literalSlots.TryGetValue(sa, out var saSlot):
            {
                var step = WasmSizeOf(sa.Element);
                EmitFrameAddr(saSlot);
                Line("i32.const 0");
                Line($"i32.const {Math.Max(1, step * sa.Elems.Count)}");
                Line("memory.fill");
                for (var i = 0; i < sa.Elems.Count; i++) { StoreInitValue(saSlot + i * step, sa.Element, sa.Elems[i]); }
                EmitFrameAddr(saSlot);
                break;
            }
            case DefaultLit dl when _literalSlots.TryGetValue(dl, out var dlSlot):
                // C23 `{}` of a struct: a zeroed slot's address.
                EmitAggregateInit(dlSlot, dl.Type, dl);
                EmitFrameAddr(dlSlot);
                break;
            case LitU16Str u16:
                Line($"i32.const {InternUnits(DotCC.EmitHelpers.StringU16Values(u16.Segments), 2)}");
                break;
            case LitU32Str u32:
                Line($"i32.const {InternUnits(DotCC.EmitHelpers.StringU32Values(u32.Segments), 4)}");
                break;
            case NameRef { RawName: "errno" }:
                Line($"i32.const {ErrnoAddr()}");
                Line("i32.load");
                break;
            case DefaultLit when !IsAddressValued(e.Type):
                // C23 `{}` of a scalar: its zero.
                Line($"{ValType(e.Type)}.const 0");
                break;
            case SizeOfExpr so:
                // The layout model's size (sizeof of a struct, an array, a pointer is 8).
                Line($"{ValType(e.Type)}.const {WasmSizeOf(so.Of)}");
                break;
            case OffsetOf oo:
                if (oo.StructType.Unqualified is not CType.Named on
                    || Unit.OffsetOfConstPath(on.Name, oo.Path) is not { } offset)
                {
                    throw new IrUnsupportedException($"the wat target cannot place offsetof({oo.StructType.Describe()}, {string.Join(".", oo.Path)})");
                }
                Line($"{ValType(e.Type)}.const {offset}");
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
            case CondExpr ce when ce.Type.Unqualified is CType.VoidType:
                // A void `?:` (a macro's `c ? f() : (void)0`) is an if/else run for effect.
                EmitCond(ce.Cond);
                Line("if");
                _indent++;
                EmitDiscarded(ce.Then);
                _indent--;
                Line("else");
                _indent++;
                EmitDiscarded(ce.Else);
                _indent--;
                Line("end");
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
        if (v.Sym.Kind == SymKind.Func)
        {
            Line($"i32.const {TableIndex(v.Sym)}");
            return;
        }
        if (v.Sym.IsGlobal)
        {
            EmitGlobalAddr(v.Sym);
            if (!IsAddressValued(v.Sym.Type)) { Line(LoadInstr(v.Sym.Type)); }
            return;
        }
        if (_frame.TryGetValue(v.Sym, out var off))
        {
            EmitFrameAddr(off);
            if (!IsAddressValued(v.Sym.Type)) { Line(LoadInstr(v.Sym.Type)); }
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
                if (!IsAddressValued(u.Type)) { Line(LoadInstr(u.Type)); }
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
        EmitAddress(u.Operand);
        var addr = AcquireScratch("addr");
        var valScratch = AcquireScratch(u.Operand.Type);
        Line($"local.set {addr}");
        Line($"local.get {addr}");
        Line(LoadInstr(u.Operand.Type));   // old value
        Line($"local.set {valScratch}");   // keep it
        Line($"local.get {addr}");
        Line($"local.get {valScratch}");
        Line($"{vt}.const {step}");
        Line($"{vt}.{op}");                 // new value
        if (!post) { Line($"local.tee {valScratch}"); }   // pre: result is the new value
        Line(StoreInstr(u.Operand.Type));
        Line($"local.get {valScratch}");    // post: old; pre: new (tee'd above)
        ReleaseScratch(u.Operand.Type);
        ReleaseScratch("addr");
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
        // Parentheses around the target change nothing: `(x) = v`, a macro's `(p->f) = v`.
        while (a.Target is Paren tp) { a = a with { Target = tp.Inner }; }
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

        // Memory lvalue: a frame-resident variable, *p, a[i], or s.f / p->f.
        if (a.Target is not (VarRef or Index or Member or Unary { Op: UnOp.Deref } or NameRef { RawName: "errno" }))
        {
            throw new IrUnsupportedException($"the wat target cannot assign to {a.Target.GetType().Name}");
        }
        if (IsAggregate(a.Target.Type))
        {
            // A struct or union assignment copies its bytes; its value is the target.
            EmitAddress(a.Target);
            var target = AcquireScratch("addr");
            Line($"local.tee {target}");
            EmitExpr(a.Value);
            Line($"i32.const {WasmSizeOf(a.Target.Type)}");
            Line("memory.copy");
            Line($"local.get {target}");
            ReleaseScratch("addr");
            return;
        }
        var tt = a.Target.Type;

        // A scratch holds a value or an address while another operand is evaluated; that
        // operand acquires its own (see AcquireScratch), so it cannot overwrite this one.
        if (a.CompoundOp is { } mop)
        {
            // *lv OP= v  — compute the address once, read-modify-write through it.
            var common = CType.UsualArithmetic(tt, a.Value.Type);
            EmitAddress(a.Target);
            var addr = AcquireScratch("addr");
            Line($"local.set {addr}");
            Line($"local.get {addr}");
            Line(LoadInstr(tt));
            EmitConvert(tt, common);
            EmitExpr(a.Value);
            EmitConvert(a.Value.Type, common);
            Line(ArithBinOp(mop, common));
            EmitConvert(common, tt);
            var scratch = AcquireScratch(tt);
            Line($"local.set {scratch}");
            Line($"local.get {addr}");
            Line($"local.get {scratch}");
            Line(StoreInstr(tt));
            Line($"local.get {scratch}");   // assignment is an expression
            ReleaseScratch(tt);
            ReleaseScratch("addr");
        }
        else
        {
            EmitExpr(a.Value);
            EmitConvert(a.Value.Type, tt);
            var scratch = AcquireScratch(tt);
            Line($"local.set {scratch}");
            EmitAddress(a.Target);
            Line($"local.get {scratch}");
            Line(StoreInstr(tt));
            Line($"local.get {scratch}");   // leave the stored value
            ReleaseScratch(tt);
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
        // A call through a function-pointer variable (`fp(x)`): its value is a table index.
        if (c.CalleeSym is { Kind: SymKind.Var or SymKind.Param } fpVar)
        {
            EmitCallIndirect(new VarRef(fpVar) { Type = fpVar.Type }, c.Args, c.ParamTypes, c);
            return;
        }
        // The printf family with a string-literal format is expanded inline (no
        // runtime function); a user-defined one, if any, wins and routes through the
        // generic path below.
        if (c.Callee == "printf" && !_defined.Contains("printf")) { EmitPrintf(c); return; }
        if (c.Callee == "fprintf" && !_defined.Contains("fprintf")) { EmitFprintf(c); return; }
        if (c.Callee == "sprintf" && !_defined.Contains("sprintf")) { EmitSprintf(c, bounded: false); return; }
        if (c.Callee == "snprintf" && !_defined.Contains("snprintf")) { EmitSprintf(c, bounded: true); return; }

        // The heap allocators lower to calls into the hand-written bump allocator;
        // free is a no-op drop. A user-defined one wins and routes through below.
        if (c.Callee == "malloc" && !_defined.Contains("malloc")) { EmitHeapAlloc(c, "malloc", 1); return; }
        if (c.Callee == "calloc" && !_defined.Contains("calloc")) { EmitHeapAlloc(c, "calloc", 2); return; }
        if (c.Callee == "realloc" && !_defined.Contains("realloc")) { EmitHeapAlloc(c, "realloc", 2); return; }
        if (c.Callee == "free" && !_defined.Contains("free")) { EmitFree(c); return; }

        // What a wasm instruction does is not a library call: the bulk-memory copies and
        // fill, and the program's end (WASI proc_exit; abort traps).
        if (c.Callee is "memcpy" or "memmove" && !_defined.Contains(c.Callee)) { EmitBulkMemory(c, "memory.copy"); return; }
        if (c.Callee == "memset" && !_defined.Contains("memset")) { EmitBulkMemory(c, "memory.fill"); return; }
        if (c.Callee is "exit" or "_Exit" && !_defined.Contains(c.Callee))
        {
            _usesProcExit = true;
            EmitExpr(c.Args[0]);
            EmitConvert(c.Args[0].Type, CType.Int);
            Line("call $proc_exit");
            Line("unreachable");
            return;
        }
        if (c.Callee == "abort" && !_defined.Contains("abort")) { Line("unreachable"); return; }
        // <assert.h>'s assert(e) is __dotcc_assert(e): e is tested by its own type (a pointer
        // or a double as well as an int) and a false one traps; unreachable() traps.
        if (c.Callee == "__dotcc_assert" && !_defined.Contains(c.Callee) && c.Args.Count == 1)
        {
            EmitBool(c.Args[0]);
            Line("i32.eqz");
            Line("if");
            Line("  unreachable");
            Line("end");
            return;
        }
        if (c.Callee is "__dotcc_unreachable" or "__builtin_unreachable" && !_defined.Contains(c.Callee)) { Line("unreachable"); return; }
        // <math.h> functions that are one wasm instruction.
        if (MathInstr(c.Callee) is { } math && !_defined.Contains(c.Callee) && c.Args.Count == 1)
        {
            var operand = math.StartsWith("f32", StringComparison.Ordinal) ? CType.Float : CType.Double;
            EmitExpr(c.Args[0]);
            EmitConvert(c.Args[0].Type, operand);
            Line(math);
            EmitConvert(operand, c.Type);
            return;
        }

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
        EmitCallArgs(c, c.Args, c.ParamTypes);
        // A user function is called by the name its definition is emitted under: a static
        // renamed out of the way of a same-named external one (BuildFuncDef) differs from
        // the C name.
        Line($"call ${(c.CalleeSym is { Kind: SymKind.Func } fs ? fs.TargetName : c.Callee)}");
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

    /// <summary>Expand <c>fprintf(stream, fmt, …)</c> with a string-literal format. The
    /// stream must be a standard one this backend can map to a WASI fd — <c>stdout</c>
    /// (1) or <c>stderr</c> (2), recognised structurally by the stream symbol's name;
    /// a real <c>FILE*</c> would need a runtime we don't have in wat, so anything else
    /// fails loud. Points <c>$__fd</c> at the target fd, runs the shared printf-family
    /// expansion (so <c>{d}</c>/<c>{s}</c>/… all work), then restores the stdout default.
    /// This is the path Zig's <c>std.debug.print</c> lowers onto (fprintf to stderr).</summary>
    private void EmitFprintf(Call c)
    {
        var fd = StdStreamFd(c.Args.Count > 0 ? c.Args[0] : null);
        if (fd < 0)
        {
            throw new IrUnsupportedException(
                "fprintf to a non-standard stream is unsupported in --target=wat (only stdout/stderr map to WASI fds)");
        }
        var fmt = FormatLiteral(c, 1, "fprintf");
        if (fd != 1)
        {
            Line($"i32.const {fd}");
            Line("global.set $__fd");
        }
        // The stream operand (a bare stdout/stderr VarRef, no side effects) is not
        // evaluated — its identity was consumed above to pick the fd.
        EmitFormatExpansion(fmt, c, firstArg: 2);
        if (fd != 1)
        {
            Line("i32.const 1");
            Line("global.set $__fd");   // restore stdout as the default sink
        }
        // Leave the (ignored) char-count result ONLY when the call is used for its
        // value — Zig's std.debug.print lowers to a VOID-typed fprintf, so leaving a
        // value there would unbalance the stack (the void statement won't drop it).
        if (c.Type.Unqualified is not CType.VoidType) { Line("i32.const 0"); }
    }

    /// <summary>Map a standard-stream operand to its WASI fd (<c>stdout</c>→1,
    /// <c>stderr</c>→2), or -1 if it isn't a recognised standard-stream reference.
    /// Casts are peeled — a <c>FILE*</c> parameter position wraps the bare stream
    /// symbol in an implicit conversion.</summary>
    private static int StdStreamFd(CExpr? stream)
    {
        while (stream is Cast cast) { stream = cast.Operand; }
        // Zig's std.debug.print builds a VarRef to a synthesized `stderr` symbol; C's
        // stdout/stderr resolve to a NameRef (dotcc's lenient path — they bind against
        // Libc statics at C# compile time, with no wat-side declaration).
        return stream switch
        {
            VarRef { Sym.Name: "stdout" } or NameRef { RawName: "stdout" } => 1,
            VarRef { Sym.Name: "stderr" } or NameRef { RawName: "stderr" } => 2,
            _ => -1,
        };
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
        // The '#' alternate form applies to the float conversions (force a decimal
        // point; for %g, also keep trailing zeros) and to the hex/octal integer
        // conversions (`0x`/`0X` prefix; a forced leading `0` for octal). For the
        // remaining conversions where C leaves `#` undefined (d/i/u/c/s), it still
        // fails loud rather than silently ignore the flag.
        if (spec.Alt && spec.Conv is not ('f' or 'F' or 'e' or 'E' or 'g' or 'G' or 'a' or 'A' or 'x' or 'X' or 'o'))
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
            case 'a': case 'A': EmitFloatConv(arg, spec, spec.Conv); break;
            case 'p':
                // glibc-shaped pointer: "(nil)" for null, else "0x" + lowercase hex.
                // No sign / precision / '#'; width + left-justify apply like a string.
                NeedRuntime("__pf_p");
                EmitExpr(arg);
                if (ValType(arg.Type) == "i64") { Line("i32.wrap_i64"); }  // a wasm32 address is i32
                Line($"i32.const {width}");
                Line($"i32.const {(spec.Left ? 1 : 0)}");
                Line("call $__pf_p");
                break;
            default:
                throw new IrUnsupportedException($"the wat target does not yet support the printf conversion '%{spec.Conv}'");
        }
    }

    /// <summary>Lower an unsigned integer conversion (<c>%u</c>/<c>%x</c>/<c>%X</c>/
    /// <c>%o</c>): the value is taken as its unsigned bit pattern; <c>+</c>/space don't
    /// apply (C only signs signed conversions). With the <c>#</c> flag, <c>%x</c>/
    /// <c>%X</c> get a packed <c>0x</c>/<c>0X</c> prefix (emitted in the sign position,
    /// so zero-padding lands after it) and <c>%o</c> sets the force-leading-zero flag;
    /// the runtime suppresses the prefix for a zero value, per C99.</summary>
    private void EmitUnsignedConv(CExpr arg, PrintfFormat.Spec spec, int radix, int alpha)
    {
        // A 1–2 byte radix prefix packed low-byte-first into one i32 (the same slot
        // $__pf_emit reads a sign from); 0 = none. Octal uses no string prefix — the
        // leading `0` is a forced digit instead (forceZero).
        var prefix = 0;
        var forceZero = 0;
        if (spec.Alt)
        {
            switch (spec.Conv)
            {
                case 'x': prefix = '0' | ('x' << 8); break;
                case 'X': prefix = '0' | ('X' << 8); break;
                case 'o': forceZero = 1; break;
            }
        }
        NeedRuntime("__pf_int_u");
        EmitIntArgAsI64(arg, signed: false);
        Line($"i64.const {radix}");
        Line($"i32.const {alpha}");
        Line($"i32.const {IntMinDigits(spec)}");
        Line($"i32.const {(spec.Width >= 0 ? spec.Width : 0)}");
        Line($"i32.const {IntMode(spec)}");
        Line($"i32.const {prefix}");
        Line($"i32.const {forceZero}");
        Line("call $__pf_int_u");
    }

    /// <summary>Lower a floating <c>%f</c>/<c>%e</c>/<c>%g</c>/<c>%a</c> conversion: push
    /// the value as an f64 (a <c>float</c> argument is promoted, matching C's default
    /// argument promotion), then the compile-time-constant field parameters, and call
    /// the matching runtime formatter. The decimal forms each build the EXACT value with
    /// big-integer arithmetic and round once (round-half-to-even), so the digits match a
    /// real libc: <c>%f</c> via value × 10^precision, <c>%e</c>/<c>%g</c> via a scaled
    /// Dragon digit generator. For <c>%g</c> the "precision" is significant digits
    /// (C99: a precision of 0 is taken as 1). <c>%a</c> is exact by construction — a
    /// direct dump of the IEEE-754 mantissa as hex nibbles — so it needs no big-integer
    /// machinery; an unspecified precision (passed as -1) means "as many nibbles as the
    /// value needs", and an explicit one rounds half-to-even.</summary>
    private void EmitFloatConv(CExpr arg, PrintfFormat.Spec spec, char conv)
    {
        // Uppercase conversions (%F/%E/%G/%A) differ only in casing: an 'E'/'P' marker
        // and uppercase INF/NAN / hex digits. The runtime helper is the same one; an
        // extra flag selects the case (it folds to lowercase − (upper << 5) per letter).
        var upper = conv is 'F' or 'E' or 'G' or 'A';
        var lc = char.ToLowerInvariant(conv);
        // %a's default precision is "exact" (-1), not 6 — it never invents trailing
        // zeros the way the decimal conversions do.
        var prec = spec.Precision >= 0 ? spec.Precision : (lc == 'a' ? -1 : 6);
        if (lc == 'g' && prec == 0) { prec = 1; }              // %g: precision 0 means 1
        if (prec > MaxFloatPrec)
        {
            throw new IrUnsupportedException($"the wat target does not yet support printf float precision > {MaxFloatPrec} (in '%{spec.Conv}')");
        }
        var runtime = lc switch { 'e' => "__pf_e", 'g' => "__pf_g", 'a' => "__pf_a", _ => "__pf_f" };
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
            case VarRef { Sym.Kind: SymKind.Func } fn:
                Line($"i32.const {TableIndex(fn.Sym)}");
                break;
            case VarRef { Sym.IsGlobal: true } g:
                EmitGlobalAddr(g.Sym);
                break;
            case NameRef { RawName: "errno" }:
                Line($"i32.const {ErrnoAddr()}");
                break;
            case StructInit or StackArray when _literalSlots.ContainsKey(lv):
                EmitExpr(lv);   // a compound literal is an lvalue: its slot, filled
                break;
            case VarRef { Sym.Kind: SymKind.Param } ap when IsAggregate(ap.Sym.Type) && !_frame.ContainsKey(ap.Sym):
                Line($"local.get ${ap.Sym.TargetName}");   // the address of the caller's copy
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
            case Member m:
            {
                // `s.f`: a struct's value is its address; `p->f`: the pointer is.
                var offset = MemberOffset(m);
                EmitExpr(m.Base);
                if (offset != 0) { Line($"i32.const {offset}"); Line("i32.add"); }
                break;
            }
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
    if                           ;; fd mode — one iovec, one fd_write to $__fd
      i32.const {{IoScratch}}
      local.get $ptr
      i32.store
      i32.const {{IoScratch + 4}}
      local.get $len
      i32.store
      global.get $__fd           ;; target fd (1 = stdout, 2 = stderr)
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

        // Write a staged number ($ptr,$len) into a field, then pad to $width per $mode
        // (0 = space-pad right, 1 = left-justify, 2 = zero-pad). $sign is a packed 1- or
        // 2-byte PREFIX, low byte first: a sign ('-'/'+'/' ', high byte 0) for the
        // signed/float paths, or the "0x"/"0X" radix prefix for `%#x`/`%#X` ('0' low,
        // 'x'/'X' high); 0 = no prefix. The prefix is emitted in the sign position, so
        // zero-padding lands after it ("0x0000ff", "-0042").
        if (_runtimeUsed.Contains("__pf_emit"))
        {
            sb.Append($$"""
  (func $__pf_emit (param $ptr i32) (param $len i32) (param $sign i32) (param $width i32) (param $mode i32)
    (local $pad i32) (local $lo i32) (local $hi i32) (local $plen i32)
    local.get $sign i32.const 255 i32.and local.set $lo            ;; unpack the prefix bytes
    local.get $sign i32.const 8 i32.shr_u i32.const 255 i32.and local.set $hi
    local.get $lo i32.const 0 i32.ne
    local.get $hi i32.const 0 i32.ne i32.add local.set $plen        ;; prefix length 0/1/2
    local.get $width             ;; pad = max(0, width - (plen + len))
    local.get $plen
    local.get $len
    i32.add
    i32.sub
    local.set $pad
    local.get $mode
    i32.const 1
    i32.eq
    if                           ;; left: prefix, body, spaces
      local.get $lo if local.get $lo call $__putb end
      local.get $hi if local.get $hi call $__putb end
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
      if                         ;; zero: prefix, '0' pad, body
        local.get $lo if local.get $lo call $__putb end
        local.get $hi if local.get $hi call $__putb end
        i32.const 48
        local.get $pad
        call $__fill
        local.get $ptr
        local.get $len
        call $__write
      else                       ;; right: spaces, prefix, body
        i32.const 32
        local.get $pad
        call $__fill
        local.get $lo if local.get $lo call $__putb end
        local.get $hi if local.get $hi call $__putb end
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

        // Unsigned radix (10/16/8): no sign. Stage the magnitude, then for the `#`
        // alternate form apply the `0x`/`0X` prefix ($pfx, suppressed for a zero value
        // per C99) or, for octal, force a leading `0` digit ($forceZero) when the top
        // digit isn't already zero. Emit through the shared field layout.
        if (_runtimeUsed.Contains("__pf_int_u"))
        {
            sb.Append($$"""
  (func $__pf_int_u (param $mag i64) (param $base i64) (param $alpha i32) (param $min i32) (param $width i32) (param $mode i32) (param $pfx i32) (param $forceZero i32)
    (local $ptr i32)
    local.get $mag
    local.get $base
    local.get $alpha
    local.get $min
    call $__fmt_radix
    local.set $ptr
    local.get $forceZero         ;; #o: prepend a '0' unless the result already starts with one
    if
      local.get $ptr i32.load8_u i32.const 48 i32.ne
      if
        local.get $ptr i32.const 1 i32.sub local.set $ptr
        local.get $ptr i32.const 48 i32.store8
      end
    end
    local.get $ptr
    i32.const {{NumBufEnd}}
    local.get $ptr
    i32.sub
    local.get $mag i64.eqz       ;; #x/#X: the 0x prefix only on a nonzero value
    if (result i32) i32.const 0 else local.get $pfx end
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

        // %a / %A — the hexadecimal floating constant "[-]0x1.hhhp±d". Exact by
        // construction: dump the IEEE-754 mantissa's 52 bits as 13 hex nibbles, the
        // leading digit being the implicit bit (1 normal, 0 subnormal / zero). With no
        // precision ($prec < 0) emit every nibble up to the last nonzero one (exact,
        // round-trippable — what Lua's %q needs); an explicit precision rounds to that
        // many nibbles, round-half-to-even, with the carry able to bump the leading
        // digit and the binary exponent. $upper selects 0X/P/ABCDEF and INF/NAN.
        if (_runtimeUsed.Contains("__pf_a"))
        {
            sb.Append($$"""
  (func $__pf_a (param $v f64) (param $prec i32) (param $posSign i32) (param $width i32) (param $mode i32) (param $alt i32) (param $upper i32)
    (local $bits i64) (local $exp i32) (local $mant i64) (local $sign i32) (local $first i32)
    (local $p i32) (local $i i32) (local $n i32) (local $outLen i32) (local $full i32)
    (local $uc i32) (local $hexb i32) (local $ax i32) (local $carry i32) (local $stick i32) (local $rp i32)
    local.get $upper i32.const 5 i32.shl local.set $uc          ;; 0 or 32: lowercase − $uc = uppercase
    i32.const 87 local.get $uc i32.sub local.set $hexb          ;; 'a'-10 (87) or 'A'-10 (55)
    local.get $v i64.reinterpret_f64 local.set $bits
    local.get $bits i64.const 0 i64.lt_s
    if (result i32) i32.const 45 else local.get $posSign end
    local.set $sign
    local.get $bits i64.const 52 i64.shr_u i64.const 2047 i64.and i32.wrap_i64 local.set $exp   ;; biased exponent
    local.get $bits i64.const 4503599627370495 i64.and local.set $mant                          ;; low 52 fraction bits
    local.get $exp i32.const 2047 i32.eq                        ;; inf / nan (INF / NAN when upper)
    if
      local.get $mant i64.eqz
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
    local.get $exp i32.eqz                                      ;; leading digit + unbiased exponent
    if
      i32.const 0 local.set $first i32.const -1022 local.set $exp        ;; subnormal: 0x0.…p-1022
    else
      i32.const 1 local.set $first local.get $exp i32.const 1023 i32.sub local.set $exp   ;; normal: 0x1.…
    end
    local.get $bits i64.const 9223372036854775807 i64.and i64.eqz        ;; |v| == 0 → 0x0p+0
    if
      i32.const 0 local.set $first i32.const 0 local.set $exp
    end
    i32.const 0 local.set $i                                    ;; 13 fraction nibbles: (mant >> (48-4i)) & 0xF
    block $nd loop $nl
      local.get $i i32.const 13 i32.ge_s br_if $nd
      i32.const {{FpEDig}} local.get $i i32.add
      local.get $mant
      i64.const 48 local.get $i i64.extend_i32_s i64.const 4 i64.mul i64.sub
      i64.shr_u i64.const 15 i64.and i32.wrap_i64
      i32.store8
      local.get $i i32.const 1 i32.add local.set $i
      br $nl
    end end
    i32.const 13 local.set $full                                ;; trim trailing zero nibbles
    block $td loop $tl
      local.get $full i32.const 0 i32.le_s br_if $td
      i32.const {{FpEDig}} local.get $full i32.const 1 i32.sub i32.add i32.load8_u i32.eqz i32.eqz br_if $td
      local.get $full i32.const 1 i32.sub local.set $full
      br $tl
    end end
    local.get $prec i32.const 0 i32.ge_s
    if (result i32) local.get $prec else local.get $full end
    local.set $outLen
    local.get $outLen i32.const 13 i32.lt_s                     ;; round at $outLen when nibbles are dropped
    if
      local.get $outLen local.set $rp
      i32.const {{FpEDig}} local.get $rp i32.add i32.load8_u local.set $n   ;; first dropped nibble
      i32.const 0 local.set $carry
      local.get $n i32.const 8 i32.gt_u
      if
        i32.const 1 local.set $carry
      else
        local.get $n i32.const 8 i32.eq
        if
          i32.const 0 local.set $stick                         ;; any nonzero nibble past $rp?
          local.get $rp i32.const 1 i32.add local.set $i
          block $sd loop $sl
            local.get $i i32.const 13 i32.ge_s br_if $sd
            i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.eqz i32.eqz
            if i32.const 1 local.set $stick end
            local.get $i i32.const 1 i32.add local.set $i
            br $sl
          end end
          local.get $stick
          if
            i32.const 1 local.set $carry                       ;; > half → up
          else                                                 ;; exact half → round to even
            local.get $rp i32.const 0 i32.gt_s
            if (result i32) i32.const {{FpEDig}} local.get $rp i32.const 1 i32.sub i32.add i32.load8_u else local.get $first end
            i32.const 1 i32.and local.set $carry
          end
        end
      end
      local.get $carry                                         ;; propagate the carry down the kept nibbles
      if
        local.get $rp i32.const 1 i32.sub local.set $i
        block $cd loop $cl
          local.get $carry i32.eqz br_if $cd
          local.get $i i32.const 0 i32.lt_s br_if $cd
          i32.const {{FpEDig}} local.get $i i32.add i32.load8_u i32.const 1 i32.add local.set $n
          i32.const {{FpEDig}} local.get $i i32.add local.get $n i32.const 15 i32.and i32.store8
          local.get $n i32.const 4 i32.shr_u local.set $carry
          local.get $i i32.const 1 i32.sub local.set $i
          br $cl
        end end
        local.get $carry                                       ;; carry into the leading digit
        if
          local.get $first i32.const 1 i32.add local.set $first
          local.get $first i32.const 16 i32.ge_s
          if
            i32.const 1 local.set $first local.get $exp i32.const 4 i32.add local.set $exp
          end
        end
      end
    end
    i32.const {{FpEOut}} local.set $p                           ;; assemble "0x" + digit + ".frac" + "p±d"
    local.get $p i32.const 48 i32.store8
    local.get $p i32.const 1 i32.add i32.const 120 local.get $uc i32.sub i32.store8   ;; 'x' / 'X'
    local.get $p i32.const 2 i32.add local.set $p
    local.get $p
    local.get $first i32.const 10 i32.lt_u
    if (result i32) local.get $first i32.const 48 i32.add else local.get $first local.get $hexb i32.add end
    i32.store8
    local.get $p i32.const 1 i32.add local.set $p
    local.get $outLen i32.const 0 i32.gt_s local.get $alt i32.or
    if
      local.get $p i32.const 46 i32.store8                      ;; '.'
      local.get $p i32.const 1 i32.add local.set $p
      i32.const 0 local.set $i
      block $fd loop $fl
        local.get $i local.get $outLen i32.ge_s br_if $fd
        local.get $i i32.const 13 i32.lt_s
        if (result i32) i32.const {{FpEDig}} local.get $i i32.add i32.load8_u else i32.const 0 end
        local.set $n
        local.get $p
        local.get $n i32.const 10 i32.lt_u
        if (result i32) local.get $n i32.const 48 i32.add else local.get $n local.get $hexb i32.add end
        i32.store8
        local.get $p i32.const 1 i32.add local.set $p
        local.get $i i32.const 1 i32.add local.set $i
        br $fl
      end end
    end
    local.get $p i32.const 112 local.get $uc i32.sub i32.store8   ;; 'p' / 'P'
    local.get $p i32.const 1 i32.add local.set $p
    local.get $exp i32.const 0 i32.lt_s                         ;; exponent sign + decimal (no leading zeros)
    if (result i32)
      local.get $p i32.const 45 i32.store8 i32.const 0 local.get $exp i32.sub
    else
      local.get $p i32.const 43 i32.store8 local.get $exp
    end
    local.set $ax
    local.get $p i32.const 1 i32.add local.set $p
    local.get $ax i32.const 1000 i32.ge_u
    if
      local.get $p local.get $ax i32.const 1000 i32.div_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    local.get $ax i32.const 100 i32.ge_u
    if
      local.get $p local.get $ax i32.const 100 i32.div_u i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    local.get $ax i32.const 10 i32.ge_u
    if
      local.get $p local.get $ax i32.const 10 i32.div_u i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
      local.get $p i32.const 1 i32.add local.set $p
    end
    local.get $p local.get $ax i32.const 10 i32.rem_u i32.const 48 i32.add i32.store8
    local.get $p i32.const 1 i32.add local.set $p
    i32.const {{FpEOut}}
    local.get $p i32.const {{FpEOut}} i32.sub
    local.get $sign local.get $width local.get $mode
    call $__pf_emit
  )

""");
        }

        // %p — a glibc-shaped pointer. A null pointer is the literal "(nil)"; otherwise
        // it is "0x" + the address in lowercase hex (no leading zeros), which is exactly
        // an unsigned hex with the "0x" alternate prefix — so reuse $__pf_int_u for that
        // case and only the "(nil)" string needs hand-staging.
        if (_runtimeUsed.Contains("__pf_p"))
        {
            sb.Append($$"""
  (func $__pf_p (param $ptr i32) (param $width i32) (param $mode i32)
    local.get $ptr i32.eqz
    if                           ;; null → "(nil)"
      i32.const {{NumBuf}} i32.const 40 i32.store8                  ;; '('
      i32.const {{NumBuf}} i32.const 1 i32.add i32.const 110 i32.store8   ;; 'n'
      i32.const {{NumBuf}} i32.const 2 i32.add i32.const 105 i32.store8   ;; 'i'
      i32.const {{NumBuf}} i32.const 3 i32.add i32.const 108 i32.store8   ;; 'l'
      i32.const {{NumBuf}} i32.const 4 i32.add i32.const 41 i32.store8    ;; ')'
      i32.const {{NumBuf}} i32.const 5 i32.const 0 local.get $width local.get $mode
      call $__pf_emit
    else                         ;; "0x" + lowercase hex via the unsigned-radix path
      local.get $ptr i64.extend_i32_u
      i64.const 16 i32.const 97 i32.const 1     ;; base 16, 'a' alpha, min 1 digit
      local.get $width local.get $mode
      i32.const 30768                           ;; "0x" packed prefix ('0' | 'x'<<8)
      i32.const 0                               ;; no forceZero
      call $__pf_int_u
    end
  )

""");
        }

        // ---- heap: a bump allocator over the region above the shadow stack --------
        // $__hp bumps UP from the stack top; the stack grows DOWN below it, so they
        // never meet. Each block carries an i32 size header (payload at
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
    /// <summary>Acquire a scratch local of kind <paramref name="kind"/> (a wasm value type, or
    /// <c>addr</c>), free until <see cref="ReleaseScratch"/>: the first of a kind keeps the plain
    /// name (<c>$__t32</c>, <c>$__taddr</c>), a nested one gets a suffix (<c>$__t32_1</c>).</summary>
    private string AcquireScratch(string kind)
    {
        var n = _scratchInUse.GetValueOrDefault(kind);
        _scratchInUse[kind] = n + 1;
        if (n + 1 > _scratchMax.GetValueOrDefault(kind)) { _scratchMax[kind] = n + 1; }
        return ScratchName(kind, n);
    }

    /// <summary>A scratch local for a value of type <paramref name="t"/>.</summary>
    private string AcquireScratch(CType t) => AcquireScratch(ValType(t));

    private void ReleaseScratch(string kind) => _scratchInUse[kind] = _scratchInUse[kind] - 1;

    private void ReleaseScratch(CType t) => ReleaseScratch(ValType(t));

    private static string ScratchName(string kind, int n) => kind switch
    {
        "i64" => "$__t64",
        "f32" => "$__tf32",
        "f64" => "$__tf64",
        "addr" => "$__taddr",
        _ => "$__t32",
    } + (n == 0 ? "" : $"_{n}");

    /// <summary>The <c>(local …)</c> declarations of the scratch locals the function used.</summary>
    private IEnumerable<string> ScratchLocals()
    {
        foreach (var kind in new[] { "i32", "i64", "f32", "f64", "addr" })
        {
            for (var n = 0; n < _scratchMax.GetValueOrDefault(kind); n++)
            {
                yield return $"(local {ScratchName(kind, n)} {(kind == "addr" ? "i32" : kind)})";
            }
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

    /// <summary>The instruction(s) that load a <paramref name="pointee"/> from the address on
    /// the stack. A pointer takes eight bytes in memory (C's LP64 view, which the layout model
    /// and <c>sizeof</c> folding share) and is an i32 address on the stack (wasm32), so it is
    /// read as an i64 and wrapped.</summary>
    private static string LoadInstr(CType pointee)
    {
        var p = pointee.Unqualified;
        if (p is CType.Pointer or CType.Func) { return "i64.load i32.wrap_i64"; }
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

    /// <summary>The instruction(s) that store the value on the stack at the address below it. A
    /// pointer is widened to the eight bytes it takes in memory (see <see cref="LoadInstr"/>).</summary>
    private static string StoreInstr(CType pointee)
    {
        var p = pointee.Unqualified;
        if (p is CType.Pointer or CType.Func) { return "i64.extend_i32_u i64.store"; }
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

    /// <summary>The bytes a <paramref name="t"/> takes in linear memory: C's LP64 sizes, so a
    /// pointer is eight bytes (an i32 address zero-extended), as <c>sizeof</c> and the layout
    /// model say; a struct or union is the layout model's size.</summary>
    private int WasmSizeOf(CType t) => t.Unqualified switch
    {
        CType.Pointer or CType.Func => 8,
        CType.Array a => (a.Count ?? 0) * WasmSizeOf(a.Element),
        CType.Named => checked((int)(Unit.SizeOfConst(t) ?? 0)),
        CType.Enum e => WasmSizeOf(e.Underlying),
        _ => t.SizeOf,
    };

    /// <summary>Natural alignment for a frame slot (a power-of-two ≤ 8): an array
    /// aligns to its element, a struct or union to its widest member (the layout
    /// model's), a scalar to its own size.</summary>
    private int SlotAlign(CType t)
    {
        var u = t.Unqualified is CType.Array a ? a.FlatElement.Unqualified : t.Unqualified;
        var align = u is CType.Named ? Unit.AlignOfConst(u) : WasmSizeOf(u);
        return Math.Min(8, Math.Max(1, align));
    }

    /// <summary>True for a struct or union: on the wasm stack its value is its address
    /// (an aggregate has no wasm value type), so reading one loads nothing, assigning
    /// one copies its bytes, and a member is an offset from that address.</summary>
    private static bool IsAggregate(CType t) => t.Unqualified is CType.Named;

    /// <summary>True when an expression of type <paramref name="t"/> evaluates to an
    /// address rather than a loaded value: an array (it decays) or an aggregate.</summary>
    private static bool IsAddressValued(CType t) => t.Unqualified is CType.Array || IsAggregate(t);

    /// <summary>The byte offset of member <paramref name="m"/> within its struct or union,
    /// from the layout model. A bit-field has no byte address of its own, which this
    /// slice does not lower yet.</summary>
    private int MemberOffset(Member m)
    {
        var owner = m.Arrow
            ? m.Base.Type.Unqualified switch
            {
                CType.Pointer p => p.Pointee,
                CType.Array a => a.Element,
                var other => other,
            }
            : m.Base is VarRef { Sym.Type: var symType } && symType.Unqualified is CType.Named ? symType : m.Base.Type;
        if (owner.Unqualified is not CType.Named n)
        {
            throw new IrUnsupportedException($"the wat target cannot take member '{m.Field}' of a {owner.Describe()}");
        }
        if (Unit.StructFields.TryGetValue(n.Name, out var fields)
            && fields.FirstOrDefault(f => f.Name == m.Field) is { BitWidth: not null })
        {
            throw new IrUnsupportedException($"the wat target does not yet support bit-field members ('{m.Field}')");
        }
        return Unit.OffsetOfConst(n.Name, m.Field)
            ?? throw new IrUnsupportedException($"the wat target cannot place member '{m.Field}' of {n.Name}");
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
