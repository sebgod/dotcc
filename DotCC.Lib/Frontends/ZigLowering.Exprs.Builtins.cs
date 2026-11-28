#nullable enable

using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, builtins: <c>@builtin(...)</c> calls and inline assembly.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers a builtin call (<c>@name(...)</c>) or an <c>asm</c> expression in value position.</summary>
    private CExpr LowerBuiltinExpr(Item expr)
    {
        switch (expr.Content)
        {
            // `@builtin(...)`. Several Zig builtins are RESULT-LOCATION-typed (`@intCast`,
            // `@ptrCast`, …) — they infer the target from the sink, which only LowerExprSink
            // carries; reached here (no sink) they error clearly. The sink-carrying forms (and
            // the sink-free `@as`/`@intFromEnum`/`@sizeOf`/`@alignCast`) share one lowering.
            case Zig.BuiltinCall { Arg2: not null } b: return LowerBuiltinCall(b, null);
            // `@inComptime()` (std.mem.eql's `!@inComptime() and …` fast-path guard): code dotcc lowers
            // runs at runtime, so it is `false`; the comptime interpreter never evaluates this node.
            // Inline assembly is parsed so a comptime-dead target path (std.crypto.sha2's SHA-NI rounds) folds away; one
            // that is actually reached has no C# form.
            // An empty `asm volatile ("" : : [x] "r" (x))` (std.hash.XxHash3's disableAutoVectorization, task #174) emits no
            // instruction and writes nothing; it only keeps the optimizer from vectorizing across it, so it lowers to nothing.
            // Only with no outputs and inputs that are plain names (nothing to evaluate); every other `asm` stays cut.
            case Zig.AsmExpr { Arg1: not null, Arg3.Content: Zig.StrLit { Arg0: var asmTemplate }, Arg4.Content: Zig.AsmTailIn { Arg1.Content: Zig.AsmItemsNone, Arg3: var asmInputs } }
                when Tok(asmTemplate) == "\"\"" && AsmInputsArePlainNames(asmInputs):
                return new DefaultLit { Type = CType.Void };
            case Zig.AsmExpr:
                throw new IrUnsupportedException(
                    "zig inline assembly (`asm`) is out of scope: dotcc targets .NET, so a reached `asm` has no lowering "
                    + "(std guards its assembly paths behind comptime target checks, which fold away when dotcc's target lacks the feature)");
            case Zig.BuiltinCall { Arg2: null } nb when Tok(nb.Arg0) == "@inComptime":
                return new LitBool(false) { Type = CType.Bool, InComptime = true };
            // `@returnAddress()`: the address an allocator records for its diagnostics (std's `rawAlloc(n, a, @returnAddress())`).
            // Managed code has no return address to give, and nothing dotcc lowers reads it, so it is 0.
            case Zig.BuiltinCall { Arg2: null } nb when Tok(nb.Arg0) == "@returnAddress":
                return new LitInt("0", 0) { Type = CType.ULong };
            case Zig.BuiltinCall { Arg2: null } nb:
                throw new IrUnsupportedException($"zig builtin `{Tok(nb.Arg0)}()` in value position is not supported yet");

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }
}
