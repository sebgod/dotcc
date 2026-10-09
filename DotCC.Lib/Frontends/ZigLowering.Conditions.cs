#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Conditions settled at compile time: an <c>if</c> or loop condition folded from comptime integers,
/// strings, type equality, a comptime aggregate or the interpreter, so an untaken arm is never lowered. One concern
/// of the <see cref="ZigLowering"/> binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>True for <c>@inComptime()</c> or <c>!@inComptime()</c>, parenthesized or not.</summary>
    private static bool IsInComptimeTest(Item cond)
    {
        while (cond.Content is Zig.Grouped g) { cond = g.Arg1; }
        if (cond.Content is Zig.PreNot n) { cond = n.Arg1; }
        while (cond.Content is Zig.Grouped g2) { cond = g2.Arg1; }
        return cond.Content is Zig.BuiltinCallNoArgs { Arg0: var tok } && Tok(tok) == "@inComptime";
    }
    /// <summary>An <c>and</c> whose left operand is a constant false, or an <c>or</c> whose left operand is a constant true:
    /// settled by that operand alone (the right one need not be comptime). Null otherwise.</summary>
    private bool? TrySettleByLeftOperand(Item condItem)
    {
        var cur = condItem;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        var (left, isAnd) = cur.Content switch
        {
            Zig.BoolAnd a => (a.Arg0, true),
            Zig.BoolOr o => (o.Arg0, false),
            _ => ((Item?)null, false),
        };
        if (left is null) { return null; }
        long? value;
        using (EnterThrowawayHoist())
        {
            try { value = _ir.ConstEval(LowerExpr(left)); }
            catch (IrUnsupportedException) { return null; }
        }
        return value switch
        {
            0 when isAnd => false,
            not null and not 0 when !isAnd => true,
            _ => null,
        };
    }
    /// <summary>Settle an <c>if</c> condition at lowering time when it is a COMPTIME question — a tag
    /// comparison (<c>builtin.os.tag == .windows</c>) or a module-exported boolean constant
    /// (<c>builtin.link_libc</c>). Null when it is not one, so the ordinary two-armed lowering runs.
    ///
    /// <para>Deliberately narrower than "any condition <see cref="IrModule.ConstEval"/> settles":
    /// that wider rule is what zig itself does, but it would change the emitted shape of every
    /// existing <c>if</c> over a constant, and nothing measured needs it. These two forms are what a
    /// platform conditional is made of, and they had no runtime meaning to lose.</para></summary>
    private bool? TryFoldComptimeCondition(Item condItem)
    {
        var cur = condItem;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.CmpEq eq && TryFoldComptimeTagCompare(eq.Arg0, eq.Arg2, negate: false, out var eqVal))
        {
            return eqVal is LitBool { Value: var e } && e;
        }
        if (cur.Content is Zig.CmpNe ne && TryFoldComptimeTagCompare(ne.Arg0, ne.Arg2, negate: true, out var neVal))
        {
            return neVal is LitBool { Value: var n } && n;
        }
        // `T == u8` / `T != Elem` — a TYPE comparison (the W4 lift; `std.meta`/`std.math` type bodies
        // ask it). Only when BOTH operands are types — a type has no runtime value, so there is no runtime
        // comparison this could be stealing.
        if (cur.Content is Zig.CmpEq teq && TryFoldTypeEquality(teq.Arg0, teq.Arg2) is { } typesEqual)
        {
            return typesEqual;
        }
        if (cur.Content is Zig.CmpNe tne && TryFoldTypeEquality(tne.Arg0, tne.Arg2) is { } typesDiffer)
        {
            return !typesDiffer;
        }
        // `if (builtin.link_libc)` / `if (!builtin.single_threaded)` — a module-exported bool.
        if (cur.Content is Zig.PreNot not)
        {
            return TryFoldComptimeCondition(not.Arg1) is { } inner ? !inner : null;
        }
        // `and` / `or` over two comptime questions — `builtin.os.tag == .linux and builtin.link_libc`. A settled
        // LEFT side that decides the result short-circuits, exactly as zig does (`T == bool and cpu.has(…)` in
        // std.simd never analyses the right side when `T` is not `bool`): the right side is not evaluated at all,
        // at comptime or at runtime. Otherwise both sides must settle, since a runtime half must still run.
        if (cur.Content is Zig.BoolAnd conj)
        {
            var la = TryFoldComptimeCondition(conj.Arg0);
            if (la == false) { return false; }
            return la == true ? TryFoldComptimeCondition(conj.Arg2) : null;
        }
        if (cur.Content is Zig.BoolAndSwitch conjSw)
        {
            return TryFoldComptimeCondition(conjSw.Arg0) is { } lsw && TryFoldComptimeCondition(conjSw.Arg2) is { } rsw
                ? lsw && rsw
                : null;
        }
        if (cur.Content is Zig.BoolOrSwitch disjSw)
        {
            return TryFoldComptimeCondition(disjSw.Arg0) is { } losw && TryFoldComptimeCondition(disjSw.Arg2) is { } rosw
                ? losw || rosw
                : null;
        }
        if (cur.Content is Zig.BoolOr disj)
        {
            var lo = TryFoldComptimeCondition(disj.Arg0);
            if (lo == true) { return true; }
            return lo == false ? TryFoldComptimeCondition(disj.Arg2) : null;
        }
        // `@hasDecl(root, "std_options")` / `@hasField(T, "x")`: a membership question, always comptime.
        if (cur.Content is Zig.BuiltinCall { Arg0: var memberTok } memberCall && Tok(memberTok) is "@hasDecl" or "@hasField")
        {
            try { return TryEvalMembershipBuiltin(memberCall)?.Value; }
            catch (IrUnsupportedException) { return null; }
        }
        // A comptime bool bound earlier in the body (`const is_comptime = @TypeOf(x) == comptime_int;`).
        if (cur.Content is Zig.Ident bid && _symbols.Resolve(Tok(bid.Arg0)) is null
            && _comptimeValues.TryGetValue(Tok(bid.Arg0), out var boundBool) && boundBool is LitBool { Value: var bb })
        {
            return bb;
        }
        // A comptime bool SEED (std.array_hash_map's `if (store_hash) {} else ctx` in an instance with `comptime store_hash:
        // bool`, task #135): a comptime var holds its value, so the untaken arm is never lowered, as zig never analyses it.
        if (cur.Content is Zig.Ident sid && _symbols.Resolve(Tok(sid.Arg0)) is { } seedSym
            && _comptimeVars.TryGetValue(seedSym, out var seed) && seed.Type.Unqualified.Equals(CType.Bool))
        {
            return seed.Value != 0;
        }
        // A module-level comptime bool (`if (runtime_safety)` in debug.zig), folded from its declaration, so
        // the question has an answer while containers are still registering, before any global exists, and
        // stays foldable once it is one (zig forbids a local shadowing a declaration, so the name is it).
        if (cur.Content is Zig.Ident tid && Tok(tid.Arg0) is var topName
            && _symbols.Resolve(topName) is null or { IsGlobal: true }
            && _topLevelConstRhs.TryGetValue(topName, out var topRhs) && _foldingTopLevelConsts.Add(topName))
        {
            try { return TryFoldComptimeCondition(topRhs); }
            finally { _foldingTopLevelConsts.Remove(topName); }
        }
        // `switch (builtin.mode) { .Debug, .ReleaseSafe => true, … }`: a switch over a comptime tag folds to
        // the value of the prong it selects.
        if (cur.Content is Zig.SwitchExpr)
        {
            var (subject, prongs) = cur.Content switch
            {
                Zig.SwitchExpr s => (s.Arg2, s.Arg5),
                _ => throw new System.InvalidOperationException(),
            };
            if (SelectComptimeProng(subject, prongs, out var chosenPayload) is not { Expr: { } chosen } chosenProng) { return null; }
            if (chosenProng.CaptureName is null) { return TryFoldComptimeCondition(chosen); }
            // `.int => |info| @sizeOf(T) * 8 == info.bits` (std.meta.hasUniqueRepresentation): the capture
            // binds the tag's payload for the prong's question.
            EnterComptimeProng(chosenProng, chosenPayload);
            try { return TryFoldComptimeCondition(chosen); }
            finally { ExitComptimeProng(); }
        }
        // `if (comptime typeContainsSlice(Key)) @compileError(…)` (std.hash.autoHash): a comptime call to a
        // generic whose parameters are all comptime TYPES and whose body is `return <question>;` folds by
        // asking the question with the arguments bound, so the guarded `@compileError` is never analysed.
        if (cur.Content is Zig.PreComptime { Arg1: var comptimeCall } && TryFoldComptimeBoolCall(comptimeCall) is { } called)
        {
            return called;
        }
        // The same question asked without `comptime` (`if (std.meta.hasUniqueRepresentation(Key))` in autoHash):
        // a function of TYPES only, with a single `return`, is pure, so its answer is the same at comptime.
        if (cur.Content is Zig.CallArgs && TryFoldComptimeBoolCall(cur) is { } plainCalled)
        {
            return plainCalled;
        }
        // A comparison inside the body of a comptime TYPE question (`@sizeOf(T) * 8 == info.bits` in
        // hasUniqueRepresentation): there every operand is comptime by construction (the function takes only
        // types), so the interpreter's answer is the question's. NOT folded elsewhere: ConstEval reads a local
        // `const` through its initializer with plain integer arithmetic, which is not a wrapping `u8`'s.
        if (_comptimeBoolCallDepth > 0 && cur.Content is Zig.CmpEq or Zig.CmpNe or Zig.CmpLt or Zig.CmpGt or Zig.CmpLe or Zig.CmpGe
            && TryConstEvalCondition(cur) is { } compared)
        {
            return compared;
        }
        // `fmt[0] == 'b'` / `fmt.len != 3` over a comptime string (std.Io.Writer.printValue's `3 => if (fmt[0] == 'b' and
        // fmt[1] == '6' and fmt[2] == '4') switch (…)`, task #121): zig settles it at compile time and never analyses the
        // guarded arm, whose `invalidFmtError` is a `@compileError` for any other format.
        if (cur.Content is Zig.CmpEq or Zig.CmpNe && TryFoldComptimeStringCompare(cur) is { } stringCompared)
        {
            return stringCompared;
        }
        // A comparison of comptime NUMBERS (std.math.log10_int's `bit_size > (1 << (11 - i)) * 5 * @log2(10.0)`, task #171):
        // zig settles it at compile time and never analyses what it guards (there, `pow10` of a 10240-digit power).
        if (cur.Content is Zig.CmpEq or Zig.CmpNe or Zig.CmpLt or Zig.CmpGt or Zig.CmpLe or Zig.CmpGe
            && TryFoldComptimeNumberCompare(cur) is { } numbersCompared)
        {
            return numbersCompared;
        }
        if (cur.Content is Zig.TrueLit) { return true; }
        if (cur.Content is Zig.FalseLit) { return false; }
        // A question about a comptime AGGREGATE (`cpu.has(.x86, .avx2)` over a `comptime cpu: std.Target.Cpu`
        // parameter, `cpu.arch.isX86()`): every operand is comptime, so the interpreter's answer is the question's.
        if (IsRootedAtComptimeAggregate(cur) && TryInterpretCondition(cur) is { } aggregateAnswer)
        {
            return aggregateAnswer;
        }
        return TryFoldImportedComptimeValue(cur, out var v) && v is LitBool { Value: var b } ? b : null;
    }
    /// <summary>The value of an <c>if</c> condition in a position where zig REQUIRES it to be compile-time-known (a
    /// <c>comptime { … }</c> block, a type-returning body, a type alias), or null when it is not. Wider than
    /// <see cref="TryFoldComptimeCondition"/>, which must leave a runtime condition alone: here every condition is
    /// comptime, so after the comptime questions it tries the const folder, then the comptime interpreter, which runs a
    /// condition that CALLS (std.bit_set.Array's <c>!std.math.isPowerOfTwo(@bitSizeOf(MaskIntType))</c>). The lowering
    /// goes to a throwaway hoist and is discarded. A condition that does not lower throws, as the lowering does.</summary>
    private bool? TryFoldRequiredComptimeCondition(Item cond)
    {
        if (TryFoldComptimeCondition(cond) is { } folded) { return folded; }
        using (EnterThrowawayHoist())
        {
            var lowered = LowerExpr(cond);
            if (_ir.ConstEval(lowered) is { } v) { return v != 0; }
            return _ir.EvalComptimeValue(lowered) switch
            {
                IrModule.CtBool { Value: var calledBool } => calledBool,
                IrModule.CtInt { Value: var calledInt } => calledInt != 0,
                _ => null,
            };
        }
    }
    /// <summary>A comparison whose operands are both side-effect-free numeric shapes (<see cref="IsPureNumericShape"/>), settled
    /// by the interpreter over their lowering (discarded), or null. Outside a call frame the interpreter reads no runtime
    /// variable, so an operand that is one (or anything else it cannot evaluate) leaves the comparison unsettled; the
    /// shape gate keeps the throwaway lowering from declaring or instantiating anything.</summary>
    private bool? TryFoldComptimeNumberCompare(Item comparison)
    {
        var (left, right) = comparison.Content switch
        {
            Zig.CmpEq c => (c.Arg0, c.Arg2), Zig.CmpNe c => (c.Arg0, c.Arg2), Zig.CmpLt c => (c.Arg0, c.Arg2),
            Zig.CmpGt c => (c.Arg0, c.Arg2), Zig.CmpLe c => (c.Arg0, c.Arg2), Zig.CmpGe c => (c.Arg0, c.Arg2),
            _ => (comparison, comparison),
        };
        if (!IsPureNumericShape(left) || !IsPureNumericShape(right)) { return null; }
        CExpr lowered;
        try
        {
            using (EnterThrowawayHoist()) { lowered = LowerExpr(comparison); }
        }
        catch (IrUnsupportedException) { return null; }
        return _ir.EvalComptimeValue(lowered) is IrModule.CtBool { Value: var answer } ? answer : null;
    }
    /// <summary>Is <paramref name="e"/> built only from literals, names, field reads (<c>@typeInfo(T).int.bits</c>, <c>x.len</c>),
    /// arithmetic, and the pure numeric builtins? Such an expression lowers without side effects on the lowering state (no
    /// call is instantiated, no block declares a name).</summary>
    private static bool IsPureNumericShape(Item e) => e.Content switch
    {
        Zig.IntLit or Zig.FloatLit or Zig.CharLit or Zig.Ident => true,
        Zig.Grouped g => IsPureNumericShape(g.Arg1),
        Zig.PreNeg p => IsPureNumericShape(p.Arg1),
        Zig.PreBitNot p => IsPureNumericShape(p.Arg1),
        Zig.Field f => IsPureNumericShape(f.Arg0),
        Zig.Add a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.Sub a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.Mul a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.DivOp a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.ModOp a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.Shl a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.Shr a => IsPureNumericShape(a.Arg0) && IsPureNumericShape(a.Arg2),
        Zig.BuiltinCall { Arg0: var bTok, Arg2: var bArgs } => Tok(bTok) is "@log2" or "@log10" or "@log" or "@sqrt" or "@exp"
                or "@floor" or "@ceil" or "@trunc" or "@typeInfo" or "@bitSizeOf" or "@sizeOf" or "@TypeOf"
            && Flatten(bArgs).All(IsPureNumericShape),
        _ => false,
    };
    /// <summary>An <c>==</c> / <c>!=</c> with an operand read off a comptime STRING (a byte <c>fmt[i]</c> or its <c>.len</c>) and
    /// the other a constant (task #121), or null when either side is not settled at compile time.</summary>
    private bool? TryFoldComptimeStringCompare(Item comparison)
    {
        var (left, right, equal) = comparison.Content switch
        {
            Zig.CmpEq eq => (eq.Arg0, eq.Arg2, true),
            Zig.CmpNe ne => (ne.Arg0, ne.Arg2, false),
            _ => (comparison, comparison, true),
        };
        if (!IsComptimeStringRead(left) && !IsComptimeStringRead(right)) { return null; }
        return ComptimeScalarOperand(left) is { } l && ComptimeScalarOperand(right) is { } r ? (l == r) == equal : null;
    }
    /// <summary>Is <paramref name="operand"/> a byte or the length of a comptime string?</summary>
    private bool IsComptimeStringRead(Item operand)
    {
        while (operand.Content is Zig.Grouped g) { operand = g.Arg1; }
        return operand.Content switch
        {
            Zig.Index ix => ComptimeStringArg(ix.Arg0) is not null,
            Zig.Field f => Tok(f.Arg2) == "len" && ComptimeStringArg(f.Arg0) is not null,
            _ => false,
        };
    }
    /// <summary>The value of a comptime scalar operand: a byte of a comptime string at a constant index, its length, or a
    /// constant (a character or integer literal). Null when it is none of those, or the string spells an escape (its raw
    /// text is not its bytes).</summary>
    private long? ComptimeScalarOperand(Item operand)
    {
        while (operand.Content is Zig.Grouped g) { operand = g.Arg1; }
        switch (operand.Content)
        {
            // Only at a LITERAL index (`fmt[0]`): a `comptime var` index's constant is its declaration value, not its current one.
            case Zig.Index { Arg2.Content: Zig.IntLit indexLit } ix when ComptimeStringArg(ix.Arg0) is { } text && !text.Contains('\\'):
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                return DecodeZigInt(Tok(indexLit.Arg0)).Value is { } k && k >= 0 && k < bytes.Length ? bytes[k] : null;
            }
            case Zig.Field f when Tok(f.Arg2) == "len" && ComptimeStringArg(f.Arg0) is { } lenText && !lenText.Contains('\\'):
                return System.Text.Encoding.UTF8.GetByteCount(lenText);
            case Zig.CharLit or Zig.IntLit:
            {
                using var hoist = EnterThrowawayHoist();
                return _ir.ConstEval(LowerExpr(operand));
            }
            default:
                return null;
        }
    }
    /// <summary>Compare two TYPE operands at comptime, or null when either is not a type (so the caller
    /// keeps looking). Types compare by their resolved <see cref="CType"/> AND their declared integer
    /// width: dotcc widens <c>u21</c> and <c>u32</c> to the same C# <c>uint</c>, but they are different
    /// types in zig, and <c>T == u32</c> must say so.</summary>
    /// <summary>True when an expression is a member access or method call whose innermost base names a comptime
    /// aggregate the interpreter holds (<see cref="IrModule.ComptimeGlobals"/>): <c>cpu.has(…)</c>, <c>cpu.arch</c>; or the
    /// synthetic <c>builtin</c> module itself (<c>builtin.cpu.arch.endian()</c>).</summary>
    private bool IsRootedAtComptimeAggregate(Item expr)
    {
        var cur = expr;
        while (true)
        {
            switch (cur.Content)
            {
                case Zig.Grouped g: cur = g.Arg1; continue;
                case Zig.Field f: cur = f.Arg0; continue;
                case Zig.CallArgs ca: cur = ca.Arg0; continue;
                case Zig.CallNoArgs cn: cur = cn.Arg0; continue;
                case Zig.Ident id:
                    return _symbols.Resolve(Tok(id.Arg0)) is { } sym && _ir.ComptimeGlobals.ContainsKey(sym)
                        // `builtin.cpu.arch.endian()` through `const builtin = @import("builtin");` (task #180): the compiler-provided
                        // module is comptime by definition, so a question rooted at it is the interpreter's to answer.
                        || _symbols.Resolve(Tok(id.Arg0)) is null && _importSpecs.TryGetValue(Tok(id.Arg0), out var importSpec)
                           && importSpec == "builtin";
                default:
                    return false;
            }
        }
    }
    /// <summary>Lower a condition (under a throwaway hoist) and ask the interpreter for its boolean value; null when
    /// it does not evaluate.</summary>
    private bool? TryInterpretCondition(Item cond)
    {
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(cond); }
        return _ir.ResolveComptimeFold(lowered) switch
        {
            LitBool b => b.Value,
            LitInt i when i.Value is { } n => n != 0,
            _ => null,
        };
    }
    private bool? TryFoldTypeEquality(Item left, Item right)
    {
        // `T == comptime_int` (std.math.Log2Int's first line). A `comptime T: type` bound to a concrete type is
        // never a comptime number; `@TypeOf(x)` of a comptime_int `anytype` is (CType.ComptimeInt, target T5).
        if (IsComptimeNumberTypeName(right) && TryTypeAliasRhs(left, out var leftType))
        {
            return Tok(((Zig.Ident)right.Content).Arg0) == "comptime_int" && leftType.Unqualified is CType.Prim { IsComptimeInt: true };
        }
        if (IsComptimeNumberTypeName(left) && TryTypeAliasRhs(right, out var rightType))
        {
            return Tok(((Zig.Ident)left.Content).Arg0) == "comptime_int" && rightType.Unqualified is CType.Prim { IsComptimeInt: true };
        }
        // A zig float dotcc does not lower (`T == f16 or T == f32 or T == f64` in std.fmt.parseFloat) is never equal to
        // a type that does.
        if (IsUnmodeledPrimitiveType(right) && TryTypeAliasRhs(left, out _)) { return false; }
        if (IsUnmodeledPrimitiveType(left) && TryTypeAliasRhs(right, out _)) { return false; }
        if (!TryTypeAliasRhs(left, out var lt)) { return null; }
        var lb = DeclaredBitsOfTypeArg(left);
        if (!TryTypeAliasRhs(right, out var rt)) { return null; }
        var rb = DeclaredBitsOfTypeArg(right);
        // A side with no recorded width is its carrier's (an alias whose width was never tracked); comparing the raw null
        // against `u64`'s 64 had made `DT == u64` false for `const DT = if (…) u64 else u128;`, silently (task #77).
        static int? Carrier(CType t) => t.Unqualified is CType.Prim { Integer: true, Bytes: var bytes } ? bytes * 8 : null;
        return lt.Unqualified.Equals(rt.Unqualified) && (lb ?? Carrier(lt)) == (rb ?? Carrier(rt));
    }
    /// <summary>True for the bare names <c>comptime_int</c> / <c>comptime_float</c> — zig's untyped
    /// compile-time number types, which dotcc never binds a type parameter to.</summary>
    private static bool IsComptimeNumberTypeName(Item item)
        => item.Content is Zig.Ident id && Tok(id.Arg0) is "comptime_int" or "comptime_float";
    /// <summary>True for a zig primitive type name dotcc does not lower (<c>f16</c>, <c>f80</c>,
    /// <c>c_longdouble</c>, the zero-width <c>u0</c> / <c>i0</c>): a comparison against one still answers, since no
    /// lowered type is it (std.bit_set's <c>if (MaskInt == u0) return;</c>).</summary>
    private static bool IsUnmodeledPrimitiveType(Item item)
        => item.Content is Zig.Ident id && Tok(id.Arg0) is "f16" or "f80" or "c_longdouble" or "u0" or "i0";
}
