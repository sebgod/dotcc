#nullable enable

using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, operators: binary and prefix operators, <c>comptime</c> folds, concatenation and repeat.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers an operator node: arithmetic, wrapping and saturating forms, comparisons, boolean and bitwise operators, the prefixes, <c>comptime</c>, <c>++</c> and <c>**</c>.</summary>
    private CExpr LowerOperatorExpr(Item expr)
    {
        switch (expr.Content)
        {
            // `a ++ if (c) x else y` as a whole right-hand side (std.fmt.bytesToHex's `"0123456789" ++ if (case == .upper)
            // "ABCDEF" else "abcdef"`, task #170): the concatenation distributes into the arms, each a compile-time `++`,
            // and the condition (a runtime one in bytesToHex) selects between the two results.
            case Zig.Concat ci when ci.Arg2.Content is Zig.IfElseExpr { Arg4: null, Arg7: null } concatArms:
            {
                if (TryFoldComptimeCondition(concatArms.Arg2) is { } takenConcat)
                {
                    return LowerConcat(ci.Arg0, takenConcat ? concatArms.Arg5 : concatArms.Arg8);
                }
                var concatCond = LowerExpr(concatArms.Arg2);
                var thenConcat = LowerConcat(ci.Arg0, concatArms.Arg5);
                var elseConcat = LowerConcat(ci.Arg0, concatArms.Arg8);
                // Arms of two lengths make the `if` a slice, whose runtime value `++` cannot take (zig: "slice being
                // concatenated must be comptime-known"); one type (one length) is an array.
                if (!thenConcat.Type.Unqualified.Equals(elseConcat.Type.Unqualified))
                {
                    throw new CompileException("zig: unable to resolve comptime value: slice being concatenated must be comptime-known "
                        + "(the `if` arms of a `++` differ in length)");
                }
                return new CondExpr(concatCond, thenConcat, elseConcat) { Type = thenConcat.Type };
            }
            // `comptime switch` / `comptime if` in value position (see the LowerExprSink cases).
            case Zig.PreComptime { Arg1.Content: Zig.SwitchExpr } c: return LowerExpr(c.Arg1);
            case Zig.PreComptime { Arg1.Content: Zig.LabeledBlock or Zig.LabeledSwitch } clb: return ComptimeLabeledBlockValue(clb.Arg1, null);
            case Zig.PreComptime { Arg1.Content: Zig.IfElseExpr } c: return LowerExpr(c.Arg1);

            // arithmetic
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.Sub a:     return Bin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.Mul a:     return Bin(BinOp.Mul, a.Arg0, a.Arg2);
            // wrapping arithmetic (Milestone P) — two's-complement wrap at the operand width
            case Zig.AddWrap a: return WrapBin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.SubWrap a: return WrapBin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.MulWrap a: return WrapBin(BinOp.Mul, a.Arg0, a.Arg2);
            // saturating arithmetic (Milestone P) — clamp to the operand-type range via ZigMath
            case Zig.AddSat a:  return SatBin("SatAdd", a.Arg0, a.Arg2);
            case Zig.SubSat a:  return SatBin("SatSub", a.Arg0, a.Arg2);
            case Zig.MulSat a:  return SatBin("SatMul", a.Arg0, a.Arg2);
            case Zig.ShlSat a:  return SatShl(a.Arg0, a.Arg2);
            case Zig.DivOp a:   return RejectRuntimeSignedDivision(Bin(BinOp.Div, a.Arg0, a.Arg2), PeerZigType(a.Arg0, a.Arg2));
            case Zig.ModOp a:   return RejectRuntimeSignedDivision(Bin(BinOp.Mod, a.Arg0, a.Arg2), PeerZigType(a.Arg0, a.Arg2));
            // comparison (non-associative in the grammar)
            case Zig.CmpEq a:   return Bin(BinOp.Eq, a.Arg0, a.Arg2);
            case Zig.CmpNe a:   return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpLt a:   return Bin(BinOp.Lt, a.Arg0, a.Arg2);
            case Zig.CmpGt a:   return Bin(BinOp.Gt, a.Arg0, a.Arg2);
            case Zig.CmpLe a:   return Bin(BinOp.Le, a.Arg0, a.Arg2);
            case Zig.CmpGe a:   return Bin(BinOp.Ge, a.Arg0, a.Arg2);
            // A value switch as the left operand (std.math.signbit's `switch (@typeInfo(T)) { … } < 0`, task #187).
            // boolean (short-circuit)
            case Zig.BoolOr a:  return ShortCircuit(BinOp.LogOr, a.Arg0, a.Arg2);
            case Zig.BoolAnd a: return ShortCircuit(BinOp.LogAnd, a.Arg0, a.Arg2);
            // bitwise / shift
            case Zig.BitAnd a:  return Bin(BinOp.BitAnd, a.Arg0, a.Arg2);
            case Zig.BitXor a:  return Bin(BinOp.BitXor, a.Arg0, a.Arg2);
            case Zig.BitOr a:   return Bin(BinOp.BitOr, a.Arg0, a.Arg2);
            case Zig.Shl a:     return Bin(BinOp.Shl, a.Arg0, a.Arg2);
            case Zig.Shr a:     return Bin(BinOp.Shr, a.Arg0, a.Arg2);
            // value prefix
            case Zig.PreNeg p:    return Pre(UnOp.Neg, p.Arg1);
            // `-%x`: `0 -% x` at the operand's type, wrapping as the infix form does (C#'s unchecked arithmetic, narrowed back).
            case Zig.PreNegWrap p:
            {
                var operand = LowerExpr(p.Arg1);
                var t = operand.Type.Unqualified;
                var negated = new Binary(BinOp.Sub, new LitInt("0", 0) { Type = CType.Int }, operand) { Type = t };
                return new Cast(t, negated) { Type = t };
            }
            case Zig.PreBitNot p: return Pre(UnOp.BitNot, p.Arg1);
            case Zig.PreNot p:    return Pre(UnOp.LogNot, p.Arg1);
            // Address-of `&x` → a `*T` pointer. Mark a var/param operand AddressTaken so
            // the backend emits a moveable-variable pointer (mirrors IrBuilder.Un's
            // single-site rule). `try` still needs error unions (Milestone B).
            case Zig.PreAddrOf p:
            {
                // `&vtable` of a CONTAINER const (`.inner = .{ .vtable = &vtable }`): zig gives the const static
                // storage, so every `&` is one lasting address, never a copy on the current frame.
                if (TryStaticContainerConstAddress(p.Arg1) is { } constAddress) { return constAddress; }
                var operand = LowerExpr(p.Arg1);
                if (Unparen(operand) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
                {
                    s.AddressTaken = true;
                }
                // `&fn` (address of a function) is a fn-POINTER VALUE — collapse pointer-to-function to
                // the bare `CType.Func` (matching the `*const fn (…)` type collapse), so an INFERRED
                // `const f = &fn;` global/local is itself callable (a `Pointer(Func)` would not be).
                if (operand.Type.Unqualified is CType.Func)
                {
                    return new Unary(UnOp.AddrOf, operand) { Type = operand.Type };
                }
                // `&arr` of a LOCAL or MEMBER array is its `*[N]T`, and in C# an array already renders as its
                // element pointer, the same address: so the pointer-to-array is the array expression itself,
                // retyped (`const p = &arr;` is `byte* p = arr;`, not the element pointer's own address).
                // `&y` of storage zig cannot write (a `const`, a parameter, a field of one) is a `*const T` (task #95), so a
                // store through it is rejected like a store to `y`.
                var pointee = IsConstStorage(operand) ? operand.Type.WithQuals(TypeQual.Const) : operand.Type;
                if (operand.Type.Unqualified is CType.Array && operand is VarRef { Sym.IsGlobal: false } or Member)
                {
                    return operand with { Type = new CType.Pointer(pointee) };
                }
                return new Unary(UnOp.AddrOf, operand) { Type = new CType.Pointer(pointee) };
            }
            // `try e` — unwrap the error union's payload, or propagate its error by throwing
            // ZigErrorReturn (caught at the enclosing `!T` function's emitted try/catch — the
            // backend's Func wrap). An expression, so it works in any position.
            case Zig.PreTry p: return LowerTry(LowerExpr(p.Arg1));
            // `comptime EXPR` (Milestone T) — force compile-time evaluation of a value. The inner
            // expression is lowered now and wrapped in a ComptimeFold, resolved at once if it evaluates
            // (a pending callee body lowers on demand), else queued and evaluated + spliced after pass 2.
            // Either way a `comptime fib(10)` sees its callee's lowered body regardless of declaration
            // order. The fold carries the inner expression's type.
            case Zig.PreComptime p:
            {
                CExpr inner;
                using (EnterComptime()) { inner = LowerExpr(p.Arg1); }   // a call under `comptime` runs at compile time (task #92)
                var fold = new ComptimeFold(inner) { Type = inner.Type };
                // Evaluated NOW when it can be (the comptime engine's E2 lowers a pending callee on demand),
                // so a position that needs the value during lowering has it; otherwise after the drain.
                if (_ir.ResolveComptimeFold(inner) is { } now)
                {
                    // A folded `a or b` is zig's `bool`, though the IR types the operator C's `int`.
                    if (now is LitBool) { return new ComptimeFold(inner) { Type = CType.Bool, Resolved = now }; }
                    fold.Resolved = now;
                    return fold;
                }
                _pendingComptimeFolds.Add(fold);
                return fold;
            }

            // Array/string concat `a ++ b` and array repeat `a ** n` are COMPTIME aggregate operations.
            // V1 folds the LITERAL case (road-to-zig-std S9): string literals (`"a" ++ "b"`) and typed
            // array literals (`[_]u8{1,2} ++ [_]u8{3}`). A non-literal operand (a comptime const, `@typeName`,
            // an anon `.{…}` needing sink inference) still needs the comptime-value engine (S5–S6) and is a
            // loud cut inside the fold helpers.
            case Zig.Concat cc: return LowerConcat(cc.Arg0, cc.Arg2);
            case Zig.Repeat rp: return LowerRepeat(rp.Arg0, rp.Arg2);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }
}
