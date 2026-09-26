#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>A function whose body cannot lower because it reaches a container dotcc withdrew, compiled to a runtime
/// trap instead (task #63). std.fmt.allocPrint's <c>Writer.Allocating</c> puts <c>Allocating.sendFile</c> in its
/// vtable, so the function is lowered although allocPrint never calls it, and its body needs <c>std.Io.File</c>,
/// whose <c>handle: std.posix.fd_t</c> is the platform floor dotcc does not model (GitHub issue #126).
///
/// <para>The rule is narrow on purpose, so "fail loudly" still holds everywhere else: one of the function's parameters
/// must be a single-item pointer to a withdrawn container. No dotcc program can construct such an argument (the type has
/// no layout), so no valid call reaches the function and what its body does is unobservable: only a function-value
/// reference (a vtable slot) keeps it alive. The body's own failure is whatever reading that argument hits (the
/// withdrawn container named again, as std's `File.Handle` is, or a field of the opaque pointer it became).
/// A direct RUNTIME call is still a compile error: the trapped function is recorded in the comptime-return map,
/// whose call-graph check (<see cref="CheckComptimeReturnsAtRuntime"/>) raises the original failure for a runtime
/// call reaching it from <c>main</c>.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>The trap body for <paramref name="fn"/> when its body failed on <paramref name="failure"/> and one of its
    /// parameters points to a withdrawn container, else null (the failure then propagates as before).</summary>
    private Block? WithdrawnParamTrap(Symbol fn, IrUnsupportedException failure)
    {
        if (!_fnParamInfos.TryGetValue(fn, out var infos)) { return null; }
        string? param = null;
        foreach (var info in infos)
        {
            if (info.Kind == ParamKind.Runtime && PointsToWithdrawnContainer(info.TypeAst)) { param = info.Name; break; }
        }
        if (param is null) { return null; }
        _comptimeReturnFns.TryAdd(fn,
            $"zig: '{fn.Name}' is called at runtime, but its body cannot lower (its parameter '{param}' points to a type "
            + "dotcc cannot lower): " + failure.Message);
        // Quotes and backslashes are dropped: the message becomes a C# string literal as it stands.
        var message = ($"dotcc: '{fn.Name}' was compiled as a trap: its parameter '{param}' points to a type dotcc cannot "
                       + "lower (the platform floor, see github.com/sebgod/dotcc/issues/126), so no dotcc program can call "
                       + "it with a real argument").Replace("\"", "'").Replace("\\", "/");
        var trap = new Call("__dotcc_unreachable",
            new List<CExpr> { new LitStr(new[] { "\"" + message + "\"" }) { Type = new CType.Pointer(CType.UChar.WithQuals(TypeQual.Const)) } },
            new List<CType>(), null) { Type = CType.Void };
        return new Block(new List<CStmt> { new ExprStmt(trap) });
    }

    /// <summary>The per-body lowering state a body pushes and pops as it lowers, taken before it starts, so a body
    /// abandoned part-way (<see cref="WithdrawnParamTrap"/>) leaves nothing behind for the next one.</summary>
    private readonly record struct BodyState(int ScopeDepth, List<CStmt>? Hoist, bool HoistImpureSeen, Item? LoopBeingWrapped,
        int ComptimeBoolCallDepth, int InlineUnrollDepth, int ComptimeDepth, int LabeledBlocks, int LabeledLoops,
        int LoopBreakTargets, int LoopValues);

    /// <summary>Snapshot the per-body state (see <see cref="BodyState"/>).</summary>
    private BodyState CaptureBodyState() => new(_symbols.Depth, _hoist, _hoistImpureSeen, _loopBeingWrapped,
        _comptimeBoolCallDepth, _inlineUnrollDepth, _comptimeDepth, _labeledBlocks.Count, _labeledLoops.Count,
        _loopBreakTargets.Count, _loopValues.Count);

    /// <summary>Put the per-body state back to <paramref name="s"/>: scopes and target stacks truncated, depths reset.</summary>
    private void RestoreBodyState(BodyState s)
    {
        _symbols.TruncateScopes(s.ScopeDepth);
        _hoist = s.Hoist;
        _hoistImpureSeen = s.HoistImpureSeen;
        _loopBeingWrapped = s.LoopBeingWrapped;
        _comptimeBoolCallDepth = s.ComptimeBoolCallDepth;
        _inlineUnrollDepth = s.InlineUnrollDepth;
        _comptimeDepth = s.ComptimeDepth;
        TruncateStack(_labeledBlocks, s.LabeledBlocks);
        TruncateStack(_labeledLoops, s.LabeledLoops);
        TruncateStack(_loopBreakTargets, s.LoopBreakTargets);
        TruncateStack(_loopValues, s.LoopValues);
    }

    /// <summary>Pop <paramref name="stack"/> down to <paramref name="count"/> entries.</summary>
    private static void TruncateStack<T>(Stack<T> stack, int count)
    {
        while (stack.Count > count) { stack.Pop(); }
    }

    /// <summary>Is <paramref name="typeAst"/> a single-item pointer (<c>*T</c>, <c>*const T</c>, aligned or not) whose
    /// pointee is a container dotcc withdrew (<see cref="LowerPointee"/> lowers it to an opaque pointer)?</summary>
    private bool PointsToWithdrawnContainer(Item typeAst)
    {
        var pointee = typeAst.Content switch
        {
            Zig.TyPointer p => p.Arg1,
            Zig.TyPtrConst p => p.Arg2,
            Zig.TyPointerAlign p => p.Arg2,
            Zig.TyPtrConstAlign p => p.Arg3,
            _ => null,
        };
        if (pointee is null) { return false; }
        try { LowerType(pointee); }
        catch (ZigFailedContainerException) { return true; }
        return false;
    }
}
