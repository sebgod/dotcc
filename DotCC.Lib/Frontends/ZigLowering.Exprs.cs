#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions: <c>LowerExpr</c>/<c>LowerExprSink</c> and every value-position
/// lowering (operators, literals, calls, builtins, catch/orelse, value control flow).
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    // ---- expressions -----------------------------------------------------

    private CExpr LowerExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit i: return DecodeZigInt(Tok(i.Arg0));
            case Zig.FloatLit f: return new LitFloat(LowerZigFloat(Tok(f.Arg0))) { Type = CType.Double };

            // `true`/`false` — boolean literals (a `bool` value, like `null`/`undefined`). Typed
            // `_Bool` (→ the store-normalising `CBool`, which takes a C# `bool`).
            case Zig.TrueLit:  return new LitBool(true) { Type = CType.Bool };
            case Zig.FalseLit: return new LitBool(false) { Type = CType.Bool };

            // A char literal `'x'` / `'\n'` / `'\xNN'` / `'\u{1F600}'` — Zig's `comptime_int` = the
            // codepoint. The `\u{…}` form is decoded Zig-side (the shared escape machinery has no
            // `\u{…}` arm — and adding one would change the C front-end's `\u` handling); everything
            // else reuses the shared decoder, then lowers to an int literal like a C char constant.
            case Zig.CharLit c:
            {
                var raw = Tok(c.Arg0);
                var body = raw.Length >= 2 ? raw[1..^1] : "";
                var v = body.StartsWith("\\u{", System.StringComparison.Ordinal) && body.EndsWith("}", System.StringComparison.Ordinal)
                    ? System.Convert.ToInt32(body[3..^1].Replace("_", ""), 16)
                    : DotCC.EmitHelpers.DecodeCharLiteral(body);
                return new LitInt(v.ToString(CultureInfo.InvariantCulture), v) { Type = CType.Int };
            }

            // A string literal. Zig's escape set overlaps C's for the common cases
            // (`\n`/`\t`/`\\`/`\"`/`\xNN`), so we reuse the C string machinery: the (escape-expanded)
            // quoted lexeme becomes a single LitStr segment, typed `char[N]` (decoded byte count incl.
            // NUL) so it decays to `char*` exactly like a C literal — the C# backend lowers it to the
            // same pooled `Libc.L("…"u8)` pointer. Two Zig-specific reshapes happen FIRST so the shared
            // decoder is untouched: a `\\`-prefixed multiline string is folded to one quoted lexeme of
            // its raw (un-escaped) content; a `\u{…}` unicode escape is expanded to `\xNN` UTF-8 bytes.
            case Zig.StrLit s:
            {
                var raw = Tok(s.Arg0);
                var lexeme = raw.StartsWith("\\", System.StringComparison.Ordinal)
                    ? FoldZigMultilineString(raw)
                    : ExpandZigUnicodeEscapes(raw);
                var segs = new List<string> { lexeme };
                DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
                return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
            }
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                // A const bound to a provable allocator (Milestone F/U) emitted no runtime decl; used
                // here as a VALUE (e.g. passed to a `std.mem.Allocator` parameter) it materializes the
                // matching fat pointer — `ZigAlloc.CHeap()` for the C-heap default, or
                // `ZigAlloc.FbaAllocator(&fba)` for a devirtualized `fba.allocator()` site. (As a
                // `.alloc`/`.free` RECEIVER it never reaches this case — LowerMethodCall
                // short-circuits to the devirt path.)
                if (_defaultAllocatorBindings.TryGetValue(name, out var boundKind))
                {
                    return boundKind == AllocKind.Fba ? MaterializeFba(_fbaAllocatorSites[name]) : MaterializeCHeap();
                }
                if (_symbols.Resolve(name) is { } sym)
                {
                    // A `comptime var`/`comptime const` (Milestone T) — substitute its CURRENT
                    // lowering-time value as a literal, so an `inline while` condition / body folds.
                    if (_comptimeVars.TryGetValue(sym, out var cv)) { return ComptimeVarLit(cv.Value, cv.Type); }
                    // A comptime OPTIONAL (a `comptime x: ?T` seed, `const arg_pos = comptime switch (…) { .none => null, … }`):
                    // `null` or its payload, as a literal the interpreter and a runtime use both read.
                    if (_comptimeOptionalVars.TryGetValue(sym, out var co))
                    {
                        var optType = new CType.Optional(co.Inner);
                        return co.HasValue
                            ? new Cast(optType, ComptimeVarLit(co.Value, co.Inner)) { Type = optType }
                            : new DefaultLit { Type = optType };
                    }
                    // A comptime STRING var (`comptime var literal: []const u8 = "";`): its current value.
                    if (_comptimeStringVars.TryGetValue(sym, out var csv)) { return csv; }
                    // A comptime AGGREGATE var (the comptime engine's E3): a live reference the interpreter
                    // reads and mutates, rendered at runtime as the value it has here.
                    if (_ir.ComptimeGlobals.TryGetValue(sym, out var agg))
                    {
                        return new ComptimeFold(new VarRef(sym) { Type = sym.Type, IsLValue = true })
                        {
                            Type = sym.Type,
                            Live = true,
                            Resolved = _ir.SpliceComptimeValue(agg),
                        };
                    }
                    return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
                }
                // A lazy module's top-level function named as a VALUE (`.drain = fixedDrain` in
                // std.Io.Writer.fixed): declared on demand, like a bare call to it, then a function
                // reference exactly as a root unit's is. A generic template has no one address to take.
                if (_lazy && EnsureDeclLowered(name) is { } lazyFn)
                {
                    if (_genericFns.ContainsKey(lazyFn) || _typeReturningGenerics.ContainsKey(lazyFn))
                    {
                        throw new IrUnsupportedException(
                            $"zig: generic function '{name}' used as a value (it has no single address) is not supported");
                    }
                    return new VarRef(lazyFn) { Type = lazyFn.Type };
                }
                // A bare (unqualified) sibling container const (Milestone R, part 6): inside a
                // container const's RHS re-lower (`_currentConstContainer` set), an unresolved name may
                // name a SIBLING const — inline it (comptime), or one of an ENCLOSING container's, zig's
                // lexical scoping for a nested container. A METHOD body sees its container's consts the same
                // way (`self == slot_tombstone` in hash_map's Metadata). Outside those, the unresolved error holds.
                for (var cc = _currentConstContainer ?? _currentContainer; cc is not null; cc = _containerParents.GetValueOrDefault(cc))
                {
                    if (_containerConsts.TryGetValue(cc, out var sibs) && sibs.TryGetValue(name, out var sib))
                    {
                        return LowerContainerConst(cc, name, sib.typeItem, sib.rhs);
                    }
                }
                // A sibling FUNCTION named bare as a value, from the same scopes (std.Io.Writer.Allocating's
                // `.rebase = growingRebase` in its vtable const).
                for (var fc = _currentConstContainer ?? _currentContainer; fc is not null; fc = _containerParents.GetValueOrDefault(fc))
                {
                    if (EnsureMethodDeclared(fc, name) is { Kind: SymKind.Func } siblingFn && !_genericFns.ContainsKey(siblingFn))
                    {
                        return new VarRef(siblingFn) { Type = siblingFn.Type };
                    }
                }
                // A name bound to a COMPTIME value with no runtime symbol — today an `inline for`
                // capture over a member list of strings or enum values (road-to-zig-std S6), which
                // deliberately emits no `const`. Substituting the folded literal is what makes the
                // capture usable as an ordinary value (printed, compared, passed) inside the body.
                // Narrow by construction: an ordinary comptime `const` also emits its runtime decl,
                // so it resolves as a symbol above and never reaches here.
                if (_comptimeValues.TryGetValue(name, out var comptimeBound)) { return comptimeBound; }
                // A `const X = @compileError("…");` tombstone named in a VALUE position (road-to-zig-std
                // S7) — the declaration was inert; the reference is what zig analyses, so raise here.
                RaiseIfPoisoned(name);
                // A bound IMPORT reaching the value path means a navigation into it found nothing and
                // fell back to lowering the base. Saying so beats "unresolved identifier", which reads
                // as if the import itself were missing — and it is the failure a synthetic module
                // produces most (road-to-zig-std S3: `root` is empty by design, so every probe misses).
                if (_importSpecs.TryGetValue(name, out var importedSpec))
                {
                    throw new IrUnsupportedException(
                        $"zig: `{name}` is the imported module `{importedSpec}`, not a value — the declaration "
                        + "named on it is one dotcc does not model");
                }
                // A comptime FUNCTION alias or parameter named as a value: its function's address.
                if (_fnAliases.TryGetValue(name, out var fnValue))
                {
                    return new VarRef(fnValue.Sym) { Type = fnValue.Sym.Type };
                }
                // A lazy module's top-level value const (`use_vectors_for_comparison` in mem.zig).
                if (LowerLazyValueConst(name) is { } lazyConst) { return lazyConst; }
                RaiseIfSkippedDecl(name);   // declared here, but the declaration did not parse
                // `unreachable` (a keyword; an identifier in this grammar) as a statement or a statement
                // prong (`0 => unreachable,` in std.Io.Writer.print): zig's safe builds panic "reached
                // unreachable code", and C23's `unreachable()` already lowers to that loud throw.
                if (name == "unreachable")
                {
                    return new Call("__dotcc_unreachable", new List<CExpr>(), new List<CType>(), null) { Type = CType.Void };
                }
                // The enclosing function is named, since a deep std wall is otherwise hard to place.
                throw new IrUnsupportedException(_currentFnName.Length > 0
                    ? $"unresolved identifier '{name}' (in '{_currentFnName}')"
                    : $"unresolved identifier '{name}'");
            }
            case Zig.Grouped g:
            {
                // A parenthesized value-position control-flow construct — `( if … )` / `( switch … )` /
                // `( blk: {…} )` / `( while/for … else … )` — used in a SUB-expression (Phase B of the
                // ANF milestone). It lowers to a STATEMENT filling a result temp (LowerValueIfSwitch /
                // LowerLabeledValueBlock / LowerLoopValue), so hoist that statement to the buffer and
                // evaluate to the temp. The sink is unknown here (the paren's use decides it), so the
                // temp type is inferred from the first branch/break value.
                if (IsValueControlFlowStmt(g.Arg1) || IsLabeledValue(g.Arg1))
                {
                    var savedImpure = _hoistImpureSeen;
                    Symbol? captured = null;
                    // The consumer captures the result temp and contributes nothing (an empty Seq) — the
                    // whole temp-filling statement goes to the buffer and the paren evaluates to the temp.
                    Func<Symbol, CStmt> cap = sym => { captured = sym; return new Seq(new List<CStmt>()); };
                    CStmt filled = IsLabeledValue(g.Arg1)
                        ? LowerLabeledValue(g.Arg1, null, cap)
                        : LowerValueControlFlowStmt(g.Arg1, null, cap);
                    _hoistImpureSeen = savedImpure;   // internals are sequenced in the buffer
                    var buf = RequireHoistable("value if/switch/block/loop in a sub-expression");
                    if (captured is not { } vsym)
                    {
                        throw new IrUnsupportedException("internal: value control-flow did not bind a result temp");
                    }
                    buf.Add(filled);
                    return new VarRef(vsym) { Type = vsym.Type };
                }
                var inner = LowerExpr(g.Arg1);
                return new Paren(inner) { Type = inner.Type };
            }

            // if (cond) a else b  — the if-EXPRESSION, lowered to a ternary. Both
            // branches are RhsExpr; the backend wraps the condition in Cond.B.
            case Zig.IfExpr e:
            {
                // A comptime condition (a TYPE comparison `Result == Accumulate` in std.fmt.parseIntWithSign,
                // a comptime tag) selects its arm at lowering time, as a statement `if`'s does: the other arm
                // may not even lower (it may name a value only the taken arm's types admit).
                if (TryFoldComptimeCondition(e.Arg2) is { } taken) { return LowerExpr(taken ? e.Arg4 : e.Arg6); }
                var then = LowerExpr(e.Arg4);
                return new CondExpr(LowerExpr(e.Arg2), then, LowerExpr(e.Arg6)) { Type = then.Type };
            }
            // `x != if (c) a else b` (the right operand of a comparison): the same ternary, arms at the operand level.
            case Zig.IfOperand io:
            {
                if (TryFoldComptimeCondition(io.Arg2) is { } takenOperand) { return LowerExpr(takenOperand ? io.Arg4 : io.Arg6); }
                var thenOperand = LowerExpr(io.Arg4);
                return new CondExpr(LowerExpr(io.Arg2), thenOperand, LowerExpr(io.Arg6)) { Type = thenOperand.Type };
            }
            case Zig.IfExprReturnThen ir:
                return LowerIfReturnThen(ir.Arg2, ir.Arg5, ir.Arg7, null);
            // Value-position captured `if` — `if (opt) |x| thenE else elseE` (S4a). The payload binds
            // `x` in the then-branch, so a pure ternary can't express it; it hoists (ANF) to a result
            // temp assigned by a real `if`. See LowerIfCaptureExpr.
            case Zig.IfExprCapture ec:
                return LowerIfCaptureExpr(ec.Arg2, Tok(ec.Arg5), ec.Arg7, ec.Arg9);
            case Zig.IfExprCaptureErr ee:
                return LowerIfCaptureExpr(ee.Arg2, Tok(ee.Arg5), ee.Arg7, ee.Arg12, null, Tok(ee.Arg10));
            // A switch EXPRESSION reached with no result-location type (e.g. `x = switch(y){…}`
            // where the LHS type still flows in via LowerExprSink, or an inferred `const`). The
            // sink-carrying path is in LowerExprSink; here the arm types are inferred.
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, null);
            case Zig.SwitchExprTrailing s: return LowerSwitchExpr(s.Arg2, s.Arg5, null);
            // `comptime switch` / `comptime if` in value position (see the LowerExprSink cases).
            case Zig.ComptimeSwitchExpr c: return LowerExpr(c.Arg1);
            case Zig.ComptimeLabeledBlock clb: return ComptimeLabeledBlockValue(clb.Arg1, null);
            case Zig.ComptimeIfExpr c:     return LowerExpr(c.Arg1);

            // A labeled value-block in a pure-expression position (an if/switch-expression arm, a
            // binary sub-operand) — it produces a value via statements, which a C# expression can't
            // host, so it's supported only as a full `=` / `return` / assignment RHS (intercepted in
            // DeclOf / LowerReturn / StmtAssign before reaching here). A clear deferred error.
            case Zig.LabeledBlock or Zig.LabeledSwitch:
                throw new IrUnsupportedException(
                    $"a labeled value-block (`{Tok(expr.Content is Zig.LabeledBlock lbl ? lbl.Arg0 : ((Zig.LabeledSwitch)expr.Content).Arg0)}: {{ … }}`) is supported only as a full initializer, " +
                    "`return`, or assignment right-hand side (including as a value-position if/switch branch there, " +
                    "Milestone Y part 1) — not inside a sub-expression yet");

            // arithmetic
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.AddSwitch a: return Bin(BinOp.Add, a.Arg0, a.Arg2);   // `x + switch (…) {…}`
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
            case Zig.DivOp a:   return RejectRuntimeSignedDivision(Bin(BinOp.Div, a.Arg0, a.Arg2));
            case Zig.ModOp a:   return RejectRuntimeSignedDivision(Bin(BinOp.Mod, a.Arg0, a.Arg2));
            // comparison (non-associative in the grammar)
            case Zig.CmpEq a:   return Bin(BinOp.Eq, a.Arg0, a.Arg2);
            case Zig.CmpNe a:   return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpEqIf a: return Bin(BinOp.Eq, a.Arg0, a.Arg2);   // `x == if (c) a else b`
            case Zig.CmpNeIf a: return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpLt a:   return Bin(BinOp.Lt, a.Arg0, a.Arg2);
            case Zig.CmpGt a:   return Bin(BinOp.Gt, a.Arg0, a.Arg2);
            case Zig.CmpLe a:   return Bin(BinOp.Le, a.Arg0, a.Arg2);
            case Zig.CmpGe a:   return Bin(BinOp.Ge, a.Arg0, a.Arg2);
            // boolean (short-circuit)
            case Zig.BoolOr a:  return ShortCircuit(BinOp.LogOr, a.Arg0, a.Arg2);
            case Zig.BoolAnd a: return ShortCircuit(BinOp.LogAnd, a.Arg0, a.Arg2);
            case Zig.BoolOrSwitch a:  return ShortCircuit(BinOp.LogOr, a.Arg0, a.Arg2);    // `a or switch (…) {…}`
            case Zig.BoolAndSwitch a: return ShortCircuit(BinOp.LogAnd, a.Arg0, a.Arg2);   // `a and switch (…) {…}`
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
            case Zig.PreTry p:
            {
                var inner = LowerExpr(p.Arg1);
                if (inner.Type.Unqualified is not CType.ErrorUnion eu)
                {
                    throw new IrUnsupportedException("zig `try` requires an error-union operand");
                }
                var unwrapped = new ZigTry(inner) { Type = eu.Payload };
                // A `create`-style error-union-over-pointer (`Error!*T`, Milestone U) carries its
                // payload as a `nuint` (a pointer can't be an `ErrUnion<T>` generic arg), so
                // `ErrUnion.Try(...)` yields a `nuint`; cast it back to the `T*` the payload names.
                // `create` is the only producer of a pointer-payload union, so the cast is
                // exactly-and-only correct here.
                if (eu.Payload.Unqualified is CType.Pointer)
                {
                    return new Cast(eu.Payload, unwrapped) { Type = eu.Payload };
                }
                return unwrapped;
            }
            // `comptime EXPR` (Milestone T) — force compile-time evaluation of a value. The inner
            // expression is lowered now and wrapped in a ComptimeFold, resolved at once if it evaluates
            // (a pending callee body lowers on demand), else queued and evaluated + spliced after pass 2.
            // Either way a `comptime fib(10)` sees its callee's lowered body regardless of declaration
            // order. The fold carries the inner expression's type.
            case Zig.PreComptime p:
            {
                CExpr inner;
                _comptimeDepth++;   // a call under `comptime` runs at compile time (task #92)
                try { inner = LowerExpr(p.Arg1); }
                finally { _comptimeDepth--; }
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

            // `base.field` (Suffix '.' IDENT) — three meanings split here. When the base is a bare
            // identifier naming a container TYPE (not a variable): `Type.NAME` resolves to a
            // namespaced VALUE const if the container declares one (the comptime RHS, inlined fresh
            // here with its annotation as the sink); else `EnumName.member` → an EnumConstRef.
            // Otherwise it's struct field access on a value/pointer; Zig has no `->`, so `p.x` on a
            // pointer auto-derefs (emit `->`). The field type comes from the shared aggregate table.
            case Zig.Field fld:
            {
                // A `@typeInfo(T)` payload field (road-to-zig-std S5) folds to a literal before any
                // runtime meaning is considered — the base is a comptime `std.builtin.Type` value, not
                // a variable or a container, so no other arm below could resolve it. The member-LIST
                // use (`field_names.len`) is checked first: its base is itself a payload field, so the
                // plain payload fold would see `.len` on a list and have nothing to say about it.
                if (TryFoldTypeInfoListValue(expr, out var tiListValue)) { return tiListValue; }
                if (TryFoldTypeInfoValue(expr, out var tiValue)) { return tiValue; }
                // A comptime constant a MODULE exports — `builtin.link_libc`, `builtin.cpu.arch`
                // (road-to-zig-std S3a). Restricted to module-rooted reads on purpose: a local struct
                // constant's field access still lowers to an ordinary runtime field read, so nothing
                // that worked before changes shape. A cross-module constant had no lowering at all.
                if (TryFoldImportedComptimeValue(expr, out var importedValue)) { return importedValue; }
                var fieldName = Tok(fld.Arg2);
                // A dotted std path used as a VALUE (Milestone F): the C-heap default
                // (`std.heap.page_allocator`/`c_allocator`) materializes a runtime Allocator; a std
                // TYPE used as a value, or any unmodeled std path, errors. (A `.alloc(…)` /
                // `.init(…)` CALL never reaches here — the callee Field goes through LowerMethodCall.)
                // `std.Target.x86.cpu.x86_64_v3` — a VALUE const (or an enum member) of a container ANOTHER
                // module declares, named through the module path: lowered in the owning module, where its
                // initializer resolves (the target-identity segment T3: a CPU model for builtin.cpu).
                if (fld.Arg0.Content is Zig.Field && !IsCuratedStdPath(fld.Arg0)
                    && TryResolveModuleNestedType(fld.Arg0) is { Type: var moduleType, Owner: var moduleOwner })
                {
                    if (moduleType.Unqualified is CType.Enum moduleEnum) { return moduleOwner.ResolveEnumLit(fieldName, moduleEnum); }
                    if (ContainerTypeName(moduleType) is { } moduleContainer
                        && moduleOwner._containerConsts.TryGetValue(moduleContainer, out var moduleConsts)
                        && moduleConsts.TryGetValue(fieldName, out var moduleEntry))
                    {
                        return moduleOwner.LowerContainerConst(moduleContainer, fieldName, moduleEntry.typeItem, moduleEntry.rhs);
                    }
                }
                // `builtin.cpu` as a VALUE (`cacheLineForCpu(builtin.cpu)`): a top-level value const of the module
                // the base names, lowered there on demand.
                if (!IsCuratedStdPath(fld.Arg0) && !TryResolveStdPath(expr, out _)
                    && ResolveModulePath(fld.Arg0) is { Lowering: { } constModule }
                    && constModule.LowerExportedValueConst(fieldName) is { } moduleConst)
                {
                    return moduleConst;
                }
                // A module's top-level FUNCTION as a value (`const f = util.helper;`, `&root.helper`): declared on
                // demand in its own module, then a function reference exactly as a bare `helper` is.
                if (!IsCuratedStdPath(fld.Arg0) && !TryResolveStdPath(expr, out _)
                    && ResolveModulePath(fld.Arg0) is { Lowering: { } fnModule }
                    && fnModule.ResolveExportedDecl(fieldName, raiseIfSkipped: true) is { Sym.Kind: SymKind.Func } fnDecl)
                {
                    if (fnDecl.Owner.IsGenericTemplate(fnDecl.Sym))
                    {
                        throw new IrUnsupportedException(
                            $"zig: generic function '{fieldName}' used as a value (it has no single address) is not supported");
                    }
                    return new VarRef(fnDecl.Sym) { Type = fnDecl.Sym.Type };
                }
                // `std.options.fmt_max_depth`: a FIELD of a module's value const (`pub const options: Options = …`
                // in std.zig), the const lowered in its own module and the field read off it.
                if (fld.Arg0.Content is Zig.Field { Arg0: var constModulePath, Arg2: var constNameTok }
                    && !IsCuratedStdPath(constModulePath)
                    && ResolveModulePath(constModulePath) is { Lowering: { } aggregateModule })
                {
                    // A default-initialized const: only the field's default, so its struct need not lower whole.
                    if (aggregateModule.LowerDefaultedConstField(Tok(constNameTok), fieldName) is { } defaulted) { return defaulted; }
                    if (aggregateModule.LowerExportedValueConst(Tok(constNameTok)) is { } aggregateConst
                        && _ir.StructFieldType(aggregateConst.Type, fieldName) is { } aggregateFieldType)
                    {
                        return new Member(aggregateConst, fieldName, false) { Type = aggregateFieldType };
                    }
                }
                if (TryResolveStdPath(expr, out var stdPath))
                {
                    // Both StdAllocatorValues rows are the C heap today, so a value use
                    // materializes the one runtime fat pointer; a distinct AllocKind row would
                    // switch on the kind here. A curated TYPE path used as a value gets the
                    // specific "type, not a value" message; the rest the registry-derived list.
                    if (StdAllocatorValues.ContainsKey(stdPath)) { return MaterializeCHeap(); }
                    // Any other std VALUE (`std.atomic.cache_line`, array_list's growth policy) is the real
                    // std's: a top-level value const of the module the path names, lowered there.
                    if (!StdTypes.ContainsKey(stdPath) && !StdGenericTypes.ContainsKey(stdPath)
                        && ResolveModulePath(fld.Arg0) is { Lowering: { } valueOwner }
                        && valueOwner.LowerExportedValueConst(fieldName) is { } exportedValue)
                    {
                        return exportedValue;
                    }
                    throw new IrUnsupportedException(
                        StdTypes.ContainsKey(stdPath) || StdGenericTypes.ContainsKey(stdPath)
                            ? $"zig `{stdPath}` is a type, not a value"
                            : $"zig std path `{stdPath}` is not modeled (values: {string.Join(" / ", StdAllocatorValues.Keys)})");
                }
                if (fld.Arg0.Content is Zig.Ident cbid
                    && _symbols.Resolve(Tok(cbid.Arg0)) is null
                    && TryLookupContainerType(Tok(cbid.Arg0), out var cbaseTy)
                    && ContainerTypeName(cbaseTy) is { } cContainer
                    && _containerConsts.TryGetValue(cContainer, out var cconsts)
                    && cconsts.TryGetValue(fieldName, out var centry))
                {
                    // A namespaced container const — re-lower its RHS (comptime; inlined per use).
                    return LowerContainerConst(cContainer, fieldName, centry.typeItem, centry.rhs);
                }
                // The same through an alias of a container ANOTHER module reified (`const D = std.enums.EnumIndexer(E);`
                // then `D.count`): the const lowers in the module that declares it.
                if (fld.Arg0.Content is Zig.Ident obid
                    && _symbols.Resolve(Tok(obid.Arg0)) is null
                    && TryLookupContainerType(Tok(obid.Arg0), out var obaseTy)
                    && ContainerTypeName(obaseTy) is { } oContainer
                    && TryLowerContainerConstAnywhere(oContainer, fieldName) is { } otherConst)
                {
                    return otherConst;
                }
                // `Decimal(T).min_exponent` — a const of the struct a type-returning CALL names (std.fmt.parse_float's
                // convertSlow): the call reifies (memoized), and the const lowers in the module that declares it.
                if (fld.Arg0.Content is Zig.CallArgs or Zig.CallNoArgs
                    && TryEvalTypeReturningCall(fld.Arg0, out var calledType) && ContainerTypeName(calledType) is { } calledContainer
                    && TryLowerContainerConstAnywhere(calledContainer, fieldName) is { } calledConst)
                {
                    return calledConst;
                }
                // A namespaced container `var` (Milestone R, part 6) — `Type.name` resolves to the
                // mangled global's VarRef (an lvalue, so `Type.name = x` / `+= x` write through it).
                if (fld.Arg0.Content is Zig.Ident cvid
                    && _symbols.Resolve(Tok(cvid.Arg0)) is null
                    && TryLookupContainerType(Tok(cvid.Arg0), out var cvBaseTy)
                    && ContainerTypeName(cvBaseTy) is { } cvContainer
                    && _containerVars.TryGetValue(cvContainer, out var cvars)
                    && cvars.TryGetValue(fieldName, out var cvSym))
                {
                    return new VarRef(cvSym) { Type = cvSym.Type, IsLValue = true };
                }
                if (fld.Arg0.Content is Zig.Ident bid
                    && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
                    && baseTy.Unqualified is CType.Enum en)
                {
                    return ResolveEnumLit(fieldName, en);
                }
                // `Number.Mode.decimal` / `Outer.Inner.NAME` — the same two forms through a QUALIFIED
                // nested container base (a nested enum's member, a nested container's const).
                if (fld.Arg0.Content is Zig.Field && TryResolveQualifiedNestedType(fld.Arg0) is { } qualifiedBase)
                {
                    if (qualifiedBase.Unqualified is CType.Enum qen) { return ResolveEnumLit(fieldName, qen); }
                    if (ContainerTypeName(qualifiedBase) is { } qContainer
                        && _containerConsts.TryGetValue(qContainer, out var qconsts)
                        && qconsts.TryGetValue(fieldName, out var qentry))
                    {
                        return LowerContainerConst(qContainer, fieldName, qentry.typeItem, qentry.rhs);
                    }
                }
                // `E.member` where E is a registered error set (Milestone X, part 2) — the
                // set-qualified form of `error.member`, resolving to the same flat code (membership
                // erased). A USE as a value: bound to a const/var, compared (`x == E.member`), a
                // `catch`/`switch` operand. The error-RETURN form is handled in LowerReturn. Part 3
                // rejects a member not declared in the set (a good compiler rejects illegal programs).
                if (TryErrorSetMember(expr, out var esSet, out var esMember))
                {
                    ValidateSetMember(esSet, esMember);
                    return LowerErrorLit(esMember);
                }
                // `Allocating.drain` — a container's own FUNCTION named as a value (std.Io.Writer.Allocating's vtable
                // literal, `.drain = Allocating.drain`): declared on demand, then a function reference as a bare one is.
                if (fld.Arg0.Content is Zig.Ident methodBase && _symbols.Resolve(Tok(methodBase.Arg0)) is null
                    && TryLookupContainerType(Tok(methodBase.Arg0), out var methodBaseType)
                    && ContainerTypeName(methodBaseType) is { } methodContainer
                    && EnsureMethodDeclared(methodContainer, fieldName) is { Kind: SymKind.Func } containerFn
                    && !_genericFns.ContainsKey(containerFn))
                {
                    return new VarRef(containerFn) { Type = containerFn.Type };
                }
                var structExpr = LowerExpr(fld.Arg0);
                var arrow = structExpr.Type.Unqualified is CType.Pointer;   // Zig `p.x` auto-derefs
                // Slice `.len` / `.ptr` — the runtime Slice<T> exposes `Len` (ulong) and
                // `Ptr` (T*); a `[]const T`'s `.ptr` is a pointer-to-const.
                if (structExpr.Type.Unqualified is CType.Slice slc)
                {
                    return fieldName switch
                    {
                        "len" => new Member(structExpr, "Len", arrow) { Type = CType.ULong, IsLValue = true },
                        "ptr" => new Member(structExpr, "Ptr", arrow) { Type = new CType.Pointer(slc.Element), IsLValue = true },
                        _ => throw new IrUnsupportedException($"slice has no field '{fieldName}' (only .len / .ptr)"),
                    };
                }
                // Curated `std.ArrayList(T)` fields (wall-plan W0): `items` — the occupied
                // prefix as a mutable `[]T` (subscript / `.len` / `for (list.items) |x|` all
                // ride the ordinary slice lowering on the result) — and `capacity`. The runtime
                // ZigList<T> exposes them as `Items` / `Cap`; anything else is a clear error.
                if (structExpr.Type.Unqualified is CType.ZigList zlist)
                {
                    return fieldName switch
                    {
                        "items" => new Member(structExpr, "Items", arrow) { Type = new CType.Slice(zlist.Element) },
                        "capacity" => new Member(structExpr, "Cap", arrow) { Type = CType.ULong, IsLValue = true },
                        _ => throw new IrUnsupportedException(
                            $"zig std.ArrayList has no modeled field '{fieldName}' (only .items / .capacity)"),
                    };
                }
                // Fixed-array `.len` — a `[N]T`'s length is the comptime-known element count N
                // (Zig). The array lowered to a pointer (no runtime length field), so fold to a
                // literal. A fixed array has no `.ptr` (that's a slice / many-item-pointer field —
                // take `&arr` for a pointer); reject any other field clearly.
                if (structExpr.Type.Unqualified is CType.Array arrTy)
                {
                    if (fieldName != "len")
                    {
                        throw new IrUnsupportedException($"array has no field '{fieldName}' (only .len)");
                    }
                    if (arrTy.Count is not int arrLen)
                    {
                        throw new IrUnsupportedException("array `.len` requires a compile-time-known length");
                    }
                    // A string literal is `*const [N:0]u8` — its `.len` is N, while the lowered array
                    // type counts the NUL too (C's `char[N+1]`), as CoerceToSlice already accounts for.
                    if (IsStringLiteralValue(structExpr)) { arrLen--; }
                    return new LitInt(arrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), arrLen) { Type = CType.ULong };
                }
                // A TUPLE's `.len` is its element count (std.StaticStringMap.initComptime's `kvs_list.len` over `.{ .{ "one", 1 }, … }`).
                if (fieldName == "len" && structExpr.Type.Unqualified is CType.Tuple lenTuple)
                {
                    var tupleLen = lenTuple.Elements.Count;
                    return new LitInt(tupleLen.ToString(System.Globalization.CultureInfo.InvariantCulture), tupleLen) { Type = CType.ULong };
                }
                // `.len` through a pointer to an array (`tables.len` with `tables = &small`, std.fmt.float.render's table
                // pointers): the pointee's comptime count, as zig reads it.
                if (fieldName == "len" && structExpr.Type.Unqualified is CType.Pointer { Pointee: var lenPointee }
                    && lenPointee.Unqualified is CType.Array { Count: int ptrArrLen })
                {
                    return new LitInt(ptrArrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), ptrArrLen) { Type = CType.ULong };
                }
                // Tagged-union payload access `u.variant` → `u.__payload.variant` (unchecked,
                // like Zig's release-mode field access; the tag isn't a user-facing field).
                if (TryContainerName(structExpr.Type, out var cname)
                    && _unions.TryGetValue(cname, out var uinfo)
                    && uinfo.Variants.TryGetValue(fieldName, out var vpayload) && vpayload is not null)
                {
                    var payloadBase = new Member(structExpr, uinfo.PayloadFieldName, arrow) { Type = new CType.Named(uinfo.PayloadTypeName!), IsLValue = true };
                    return new Member(payloadBase, fieldName, false) { Type = vpayload, IsLValue = true };
                }
                var ftype = _ir.StructFieldType(structExpr.Type, fieldName)
                    ?? throw new IrUnsupportedException($"no field '{fieldName}' on type {structExpr.Type.Describe()}");
                return new Member(structExpr, fieldName, arrow) { Type = ftype, IsLValue = true };
            }

            // A bare enum literal `.member` or anonymous struct literal `.{…}` outside a
            // typed sink — Zig requires a known result type for both, so reject loudly here
            // (the sink-aware paths in LowerExprSink handle the valid cases).
            case Zig.EnumLit:
                throw new IrUnsupportedException(
                    "zig enum literal `.member` needs a known result type (use a typed declaration, a return, an assignment, or a switch on the enum)");
            // A `.{…}` with no sink: a POSITIONAL list is an inferred tuple literal (`const t =
            // .{a, b};`); a NAMED list still needs a struct result type, so LowerStructInit errors
            // there as before (Milestone G routes both through the one method).
            case Zig.AnonStructInit:
            case Zig.AnonStructInitEmpty:
                return LowerStructInit(expr, null);

            // Typed struct literal `Type{ .field = … }` (Zig's CurlySuffixExpr). The struct type
            // is named explicitly, so — unlike `.{…}` — it needs no sink and is valid anywhere.
            case Zig.TypedStructInit t:       // CurlySuffix -> Type '{' FieldInits '}'
                return LowerTypedStructInit(t.Arg0, Flatten(t.Arg2));
            case Zig.TypedStructInitEmpty t:  // CurlySuffix -> Type '{' '}'
                return LowerTypedStructInit(t.Arg0, []);

            // Postfix deref `p.*` and subscript `a[i]` → the C Unary(Deref)/Index IR.
            case Zig.Deref d:
            {
                var operand = LowerExpr(d.Arg0);
                // `s[a..b].*` (std.fmt's `specifier_arg[0..specifier_arg.len].*`): zig allows it only for a
                // comptime-known length and yields an array COPY. Bound to a `const`, the copy is never
                // written, so the slice itself stands for it; `&copy` coerces back (CoerceToSlice).
                if (operand.Type.Unqualified is CType.Slice) { return operand; }
                // `"ABC…".*` (std.base64's `standard_alphabet_chars`): a string literal is `*const [N:0]u8`, so `.*` is the
                // array VALUE, which dotcc represents by its element pointer, the literal itself (task #75). It had been
                // the first byte.
                if (operand.Type.Unqualified is CType.Array && IsStringLiteralValue(operand)) { return operand; }
                var pointee = operand.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => operand.Type,
                };
                return new Unary(UnOp.Deref, operand) { Type = pointee, IsLValue = true };
            }
            case Zig.Index ix:
            {
                // A comptime member-list index (`field_names[0]`) folds to a literal — the base is a
                // comptime list with no runtime storage, so it must be caught before LowerExpr sees it.
                if (TryFoldTypeInfoListValue(expr, out var tiIdx)) { return tiIdx; }
                // `fmt[i]` over a comptime STRING with a comptime index (std.Io.Writer.print's format scan)
                // is that byte, a comptime value, so a `switch (fmt[i])` over it folds.
                if (EvalComptimeValue(ix.Arg0) is LitStr cstr)
                {
                    CExpr ciExpr;
                    using (EnterThrowawayHoist()) { ciExpr = LowerExpr(ix.Arg2); }
                    if (_ir.ConstEval(ciExpr) is { } ci)
                    {
                        var bytes = DotCC.EmitHelpers.StringByteValues(cstr.Segments);
                        if (ci >= 0 && ci < bytes.Count)
                        {
                            return new LitInt(bytes[(int)ci].ToString(System.Globalization.CultureInfo.InvariantCulture), bytes[(int)ci])
                            { Type = LowerPrim("u8") };
                        }
                    }
                }
                var baseExpr = LowerExpr(ix.Arg0);
                // An index is a `usize` result location, so a cast builtin there infers it (std.base64's
                // `encoder.alphabet_chars[@truncate(bits >> 18 & 0x3f)]`).
                var idx = ix.Arg2.Content is Zig.BuiltinCall { Arg0: var idxCast }
                          && Tok(idxCast) is "@truncate" or "@intCast" or "@bitCast" or "@enumFromInt"
                    ? LowerExprSink(ix.Arg2, CType.ULong)
                    : LowerExpr(ix.Arg2);
                // `v[i]` of a SIMD vector: a lane read (T5).
                if (baseExpr.Type.Unqualified is CType.Vector laneVector) { return VectorLane(baseExpr, idx, laneVector); }
                // A tuple subscript `t[N]` (N a literal) reads the Nth element → `.ItemN+1`
                // (Milestone G). A tuple has no runtime indexing (the field is statically named),
                // so a non-literal index is rejected.
                if (baseExpr.Type.Unqualified is CType.Tuple tup)
                {
                    if (idx is not LitInt { Value: { } n })
                    {
                        throw new IrUnsupportedException(
                            "zig tuple index must be an integer literal (a tuple has no runtime indexing)");
                    }
                    if (n < 0 || n >= tup.Elements.Count)
                    {
                        throw new IrUnsupportedException(
                            $"zig tuple index {n} is out of range (the tuple has {tup.Elements.Count} element(s))");
                    }
                    return new TupleIndex(baseExpr, (int)n, tup.Elements[(int)n]) { Type = tup.Elements[(int)n] };
                }
                // A slice subscript indexes through its data pointer: `s[i]` → `s.Ptr[i]`.
                if (baseExpr.Type.Unqualified is CType.Slice slc)
                {
                    var ptr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
                    return new DotCC.Ir.Index(ptr, idx) { Type = slc.Element, IsLValue = true };
                }
                // `buf[i]` through a `*[N]T` indexes the pointed-at ARRAY's elements (zig auto-derefs), not an
                // array of arrays: it used to lower to `(buf + i * N)`, a non-lvalue at the wrong stride.
                if (PointedArray(baseExpr) is ({ } pointed, { Element: var pointedElem }))
                {
                    return new DotCC.Ir.Index(pointed, idx) { Type = pointedElem, IsLValue = true };
                }
                var elem = baseExpr.Type switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => CType.Int,
                };
                return new DotCC.Ir.Index(baseExpr, idx) { Type = elem, IsLValue = true };
            }

            // Slicing `a[lo..hi]` → a sub-slice fat pointer `{ a.ptr + lo, hi - lo }`. The base
            // may be a slice (re-slice through `.Ptr`), a pointer, or an array (decays); the
            // element type + const-ness ride into the resulting `[]T` / `[]const T`.
            case Zig.SliceRange sr:
                return BuildSlice(LowerExpr(sr.Arg0), LowerExpr(sr.Arg2), LowerExpr(sr.Arg4));

            // Open-ended slicing `a[lo..]` → the high bound is the source length, so the
            // result is `{ a.ptr + lo, sourceLen - lo }`. Only a known-length source (slice
            // or array) has a length; a bare pointer is rejected (as Zig does).
            case Zig.SliceOpen so:
                return BuildSlice(LowerExpr(so.Arg0), LowerExpr(so.Arg2), null);

            // Sentinel-terminated slicing `a[lo .. hi :s]` / `a[lo .. :s]`: the same slice, since a
            // sentinel is erased in the type (as `[:0]T`'s is). zig asserts `a[hi] == s` only in a SAFE
            // build mode, and dotcc reports `.ReleaseFast`, so the check is not emitted.
            case Zig.SliceRangeSentinel srs:
                return BuildSlice(LowerExpr(srs.Arg0), LowerExpr(srs.Arg2), LowerExpr(srs.Arg4));
            case Zig.SliceOpenSentinel sos:
                return BuildSlice(LowerExpr(sos.Arg0), LowerExpr(sos.Arg2), null);

            // `.?` optional unwrap. A value optional (CType.Optional → C# `T?`) unwraps via
            // `.Value` (panics on none, matching Zig's `.?`-on-null). An optional POINTER is
            // a bare `T*`, so unwrapping is the identity (the non-null pointer is the same
            // value). [V1: the pointer form does not runtime-check for null.]
            case Zig.Unwrap u:
            {
                var operand = LowerExpr(u.Arg0);
                if (operand.Type.Unqualified is CType.Optional opt)
                {
                    return new Member(operand, "Value", false) { Type = opt.Inner };
                }
                return operand;
            }

            // `@builtin(...)`. Several Zig builtins are RESULT-LOCATION-typed (`@intCast`,
            // `@ptrCast`, …) — they infer the target from the sink, which only LowerExprSink
            // carries; reached here (no sink) they error clearly. The sink-carrying forms (and
            // the sink-free `@as`/`@intFromEnum`/`@sizeOf`/`@alignCast`) share one lowering.
            case Zig.BuiltinCall b: return LowerBuiltinCall(b, null);
            // `struct { fn f(…) … }.f` in value position: the closure idiom's method as a function value.
            case Zig.StructMemberExpr sme:
            {
                var method = ReifyClosureExpr(expr, sme);
                return new VarRef(method) { Type = method.Type };
            }
            // `@inComptime()` (std.mem.eql's `!@inComptime() and …` fast-path guard): code dotcc lowers
            // runs at runtime, so it is `false`; the comptime interpreter never evaluates this node.
            // Inline assembly is parsed so a comptime-dead target path (std.crypto.sha2's SHA-NI rounds) folds away; one
            // that is actually reached has no C# form.
            case Zig.AsmExpr or Zig.AsmVolatileExpr:
                throw new IrUnsupportedException(
                    "zig inline assembly (`asm`) is out of scope: dotcc targets .NET, so a reached `asm` has no lowering "
                    + "(std guards its assembly paths behind comptime target checks, which fold away when dotcc's target lacks the feature)");
            case Zig.BuiltinCallNoArgs nb when Tok(nb.Arg0) == "@inComptime":
                return new LitBool(false) { Type = CType.Bool, InComptime = true };
            // `@returnAddress()`: the address an allocator records for its diagnostics (std's `rawAlloc(n, a, @returnAddress())`).
            // Managed code has no return address to give, and nothing dotcc lowers reads it, so it is 0.
            case Zig.BuiltinCallNoArgs nb when Tok(nb.Arg0) == "@returnAddress":
                return new LitInt("0", 0) { Type = CType.ULong };
            case Zig.BuiltinCallNoArgs nb:
                throw new IrUnsupportedException($"zig builtin `{Tok(nb.Arg0)}()` in value position is not supported yet");

            // `null` — reuse the C null-pointer node (renders C# `null`, valid for BOTH a
            // pointer sink `T*` and a value-optional sink `T?`). In Zig `null` only appears
            // at a typed sink, so the backend's store-coercion gives it the right form.
            case Zig.NullLit: return new NullPtr { Type = new CType.Pointer(CType.Void) };

            // `undefined` — uninitialized storage. Without a sink we can only emit a zeroed
            // `default` (an over-approximation of Zig's "any value"; a correct program writes
            // before reading). At a typed sink LowerExprSink types it precisely; an array local
            // takes the dedicated stackalloc path in DeclOf.
            case Zig.UndefinedLit: return new DefaultLit { Type = CType.Int };
            // `{}` — zig's void value. It has no runtime representation: the C# backend erases void
            // parameters and their arguments, and a discarded one emits nothing.
            case Zig.VoidArg or Zig.VoidValue: return new DefaultLit { Type = CType.Void };

            // `a orelse b`. A value optional → C#'s `??` (single-eval LHS, lazy RHS) via
            // NullCoalesce. An optional POINTER → `a != null ? a : b` (C# `??` doesn't apply
            // to pointers); the LHS is named twice there, so a non-trivial (side-effecting)
            // left operand is rejected rather than silently double-evaluated. `orelse return`
            // (a noreturn RHS) isn't expressible in the grammar yet — that's Milestone B2.
            case Zig.OrElse o:
            {
                var left = LowerExpr(o.Arg0);
                // A comptime-known optional (`comptime st.nextArg(null) orelse @compileError(…)`, std.fmt): its
                // payload, or the fallback when it is null; an untaken fallback is never lowered.
                if (left is ComptimeFold { Resolved: { } known } && left.Type.Unqualified is CType.Optional)
                {
                    return known is DefaultLit ? LowerExpr(o.Arg2) : known;
                }
                // A comptime-only `?comptime_int` call (`std.simd.suggestVectorLength(u8) orelse 0`) may already be its
                // folded value: a payload literal, or `null` as a default.
                var foldedOptional = left is ComptimeFold { Resolved: { } fk } ? fk : left;
                if (left.Type.Unqualified is not CType.Optional and not CType.Pointer
                    && foldedOptional is LitInt or DefaultLit or Cast { Operand: LitInt }
                    && ReturnsOptionalComptimeInt(o.Arg0.Content is Zig.PreComptime lpc ? lpc.Arg1 : o.Arg0))
                {
                    return foldedOptional is DefaultLit ? LowerExpr(o.Arg2) : foldedOptional;
                }
                // `opt orelse error.E` is an ERROR UNION (zig peer-resolves the payload with the error): the payload when
                // there is one, else the error. Lowered at the payload type instead, the error's flat CODE was the value
                // (std.fmt.parseFloat's `parseInfOrNan(…) orelse error.InvalidCharacter` returned 10.0 for "abc").
                if (o.Arg2.Content is Zig.ErrorLit orErr && left.Type.Unqualified is CType.Optional { Inner: var orPayload })
                {
                    var unionType = new CType.ErrorUnion(orPayload);
                    return new Call("ErrUnion.OrError", new List<CExpr> { left, LowerErrorLit(Tok(orErr.Arg2)) }) { Type = unionType };
                }
                // The fallback is at the payload's result type (`alignment orelse default_alignment`, an enum literal).
                var right = left.Type.Unqualified is CType.Optional { Inner: var fallbackSink }
                    ? LowerExprSink(o.Arg2, fallbackSink)
                    : LowerExpr(o.Arg2);
                if (left.Type.Unqualified is CType.Optional opt)
                {
                    return new NullCoalesce(left, right) { Type = opt.Inner };
                }
                if (left.Type.Unqualified is CType.Pointer)
                {
                    if (!IsSimpleReeval(left))
                    {
                        throw new IrUnsupportedException(
                            "zig `orelse` on a pointer with a non-trivial left operand not lowered yet (it would be double-evaluated)");
                    }
                    var notNull = new Binary(BinOp.Ne, left, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
                    return new CondExpr(notNull, left, right) { Type = left.Type };
                }
                throw new IrUnsupportedException("zig `orelse` requires an optional left operand");
            }

            // `a catch b` — the error union's payload on success, else the fallback `b` (no
            // propagation). A simple, side-effect-free `b` keeps the eager `ErrUnion.Catch(a, b)`
            // (C# evaluates `b` before the call, which is observationally identical to Zig's lazy
            // form only when `b` has no side effects). A side-effecting `b` (Milestone N, part 3)
            // needs a LAZY lowering that runs `b` only on error, which requires a statement context
            // (a `const`/`var` initializer — see DeclOf / LowerCatchValue); in a sub-expression
            // position only the eager form is available.
            case Zig.CatchOp c:
            {
                // Lower ONCE (snapshot the impurity watermark first — the union operand is a call that
                // sets it). A simple, side-effect-free fallback → the eager `ErrUnion.Catch` (no pre,
                // the union call legitimately counts as a side effect). A side-effecting fallback →
                // pre-statements; hoist them to a temp before the enclosing statement (the ANF pass).
                var savedC = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(c.Arg0, null, c.Arg2);
                if (pre.Count == 0) { return value; }
                return HoistLowered("catch", pre, value, savedC);
            }
            // `a catch |e| b` (Milestone N, part 3) — bind the error to `e` for the fallback `b`,
            // evaluated lazily (only on error). The bind is a statement, so hoist it (ANF).
            case Zig.CatchCapture cc:
            {
                var savedCc = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(cc.Arg0, Tok(cc.Arg3), cc.Arg5);
                return HoistLowered("catch |e|", pre, value, savedCc);
            }

            // `a catch return [x]` / `a orelse return [x]` (control-flow fallback) — the `return` is a
            // statement. In a full-RHS position DeclOf/LowerStmt handle it; in a SUB-expression it is
            // hoisted to a temp before the enclosing statement (ANF): the conditional `return` and the
            // payload capture become buffer statements, and the construct evaluates to the payload temp.
            // The statement-shaped arms (road-to-zig-std) — `orelse break`, `catch |err| switch (err) {…}`,
            // `orelse { …; return; }` — take exactly the same hoist: a conditional arm, then the payload.
            case Zig.CatchReturn or Zig.CatchReturnVoid or Zig.OrElseReturn or Zig.OrElseReturnVoid
              or Zig.OrElseArm or Zig.CatchArm or Zig.CatchCaptureArm:
            {
                IsControlFlowFallback(expr, out var cfLhs, out var cfIsCatch, out var cfCap, out var cfArm);
                var buf = RequireHoistable(cfIsCatch ? "catch (control-flow fallback)" : "orelse (control-flow fallback)");
                var savedImpure = _hoistImpureSeen;
                Symbol? anfSym = null;
                var cfStmt = LowerControlFlowFallback(cfLhs, cfIsCatch, cfCap, cfArm, payload =>
                {
                    anfSym = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = payload.Type });
                    return new DeclStmt(new List<LocalDecl> { new(anfSym, payload) });
                });
                _hoistImpureSeen = savedImpure;   // the construct's internals are sequenced in the buffer
                if (anfSym is not { } payloadSym)
                {
                    throw new IrUnsupportedException("internal: control-flow fallback did not bind a payload");
                }
                buf.Add(cfStmt);
                return new VarRef(payloadSym) { Type = payloadSym.Type };
            }

            // A bare `error.Foo` value (Milestone N): the error's stable code in the flat global
            // set, typed `CType.ErrorSet` (rendered `ushort`). This makes error values usable
            // outside `return error.Foo;` — bound to a const/var and compared (`e == error.Foo`)
            // via the ordinary `==`/`!=` lowering, since equal codes mean equal error values. V1
            // still erases the named SET (an explicit `error{A,B}` decl / a named `E!T` is later
            // Milestone N work); the comparison just matches codes.
            case Zig.ErrorLit el:
                return LowerErrorLit(Tok(el.Arg2));

            // `error{A, B}` as a VALUE is not meaningful in dotcc's erased model — it is only a
            // `const E = error{…};` declaration (handled in TryComptimeConstBinding, emits nothing)
            // or an (ignored) set in an `E!T` return type.
            case Zig.ErrorSet:
            case Zig.ErrorSetEmpty:
                throw new IrUnsupportedException(
                    "zig `error{…}` set literal is only valid as a `const E = error{…};` declaration or an `E!T` return-type set");

            // `A || B` (error-set merge) as a VALUE is not meaningful in dotcc's erased model — it is
            // only a `const E = A || B;` declaration (handled in TryComptimeConstBinding, emits nothing).
            case Zig.ErrSetMerge:
                throw new IrUnsupportedException(
                    "zig error-set merge `A || B` is only valid as a `const E = A || B;` declaration (dotcc erases error sets)");

            // Array/string concat `a ++ b` and array repeat `a ** n` are COMPTIME aggregate operations.
            // V1 folds the LITERAL case (road-to-zig-std S9): string literals (`"a" ++ "b"`) and typed
            // array literals (`[_]u8{1,2} ++ [_]u8{3}`). A non-literal operand (a comptime const, `@typeName`,
            // an anon `.{…}` needing sink inference) still needs the comptime-value engine (S5–S6) and is a
            // loud cut inside the fold helpers.
            case Zig.Concat cc: return LowerConcat(cc.Arg0, cc.Arg2);
            case Zig.Repeat rp: return LowerRepeat(rp.Arg0, rp.Arg2);

            // call of a named function (bare-identifier callee).
            case Zig.CallArgs c:   return LowerCall(c.Arg0, c.Arg2);
            case Zig.CallNoArgs c: return LowerCall(c.Arg0, null);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }

    // The cap on a `**` repeat count / a folded literal element count — a guard so a pathological
    // `"x" ** 1_000_000_000` can't blow up the segment/element list (well beyond any real use).
    private const long MaxRepeat = 1 << 16;

    /// <summary>Lower a Zig comptime concat <c>a ++ b</c>. V1 folds the LITERAL case:
    /// <list type="bullet">
    /// <item>two STRING literals → a single string literal whose raw quoted segments are the operands'
    /// segments concatenated — exactly C's adjacent-string-literal concatenation, reusing the identical
    /// decode/encode path (<see cref="DotCC.EmitHelpers"/>), so escapes / UTF-8 / the trailing NUL are
    /// handled once and the bytes are identical;</item>
    /// <item>two TYPED ARRAY literals of the same element type (<c>[_]u8{1,2} ++ [_]u8{3}</c>) → one array
    /// literal over the merged positional elements, re-fed through the shared <see cref="BuildArrayInit"/>
    /// (so the result is byte-identical to writing the merged literal directly).</item>
    /// </list>
    /// A non-literal operand (a comptime const, <c>@typeName</c>, a sink-needing anon <c>.{…}</c>, or a
    /// mismatched element type) still needs the comptime aggregate engine (road-to-zig-std S5–S6) and is a
    /// loud cut.</summary>
    private CExpr LowerConcat(Item leftItem, Item rightItem)
    {
        // String fold — each operand resolved to a comptime STRING value (a literal, a `const` bound to
        // one, or a nested `++`/`**` fold), then segments concatenated.
        if (TryFoldStringConcat(leftItem, rightItem) is { } str) { return str; }
        // Array-shaped fold — each operand is a typed `[N]T{…}` literal, a `const` bound to one, or an
        // anon `.{…}` (element type BORROWED from the other, typed operand). The merged positional items
        // re-feed the one BuildArrayInit the plain `[_]T{…}` uses.
        var lIsArr = TryArrayOperand(leftItem, out var lElem, out var lItems);
        var rIsArr = TryArrayOperand(rightItem, out var rElem, out var rItems);
        if (lIsArr && rIsArr)
        {
            var elem = lElem ?? rElem;
            if (elem is null)
            {
                throw new IrUnsupportedException(
                    "zig `a ++ b`: both operands are untyped anon `.{…}` — the common element/tuple type needs the "
                    + "comptime aggregate engine (road-to-zig-std S5); give one operand an explicit `[_]T{…}` type");
            }
            if (lElem is not null && rElem is not null && !lElem.Equals(rElem))
            {
                throw new IrUnsupportedException(
                    $"zig `a ++ b`: array element types differ ({lElem.Describe()} vs {rElem.Describe()})");
            }
            var merged = new List<Item>(lItems.Count + rItems.Count);
            merged.AddRange(lItems);
            merged.AddRange(rItems);
            return BuildArrayInit(merged, new CType.Array(elem, null));   // inferred length = element count
        }
        // Two array VALUES of comptime-known length (std.unicode's `break :first a ++ b ++ c` over `@splat` locals, task
        // #82): a new array of both lengths, element by element. Both operands are read once per element, so each must
        // be an lvalue-like array (a local, a global, a field, or a nested concat), never a call.
        CType? leftProbe = null, rightProbe = null;
        using (EnterThrowawayHoist())
        {
            try { leftProbe = LowerExpr(leftItem).Type; rightProbe = LowerExpr(rightItem).Type; }
            catch (IrUnsupportedException) { leftProbe = null; }
        }
        if (leftProbe?.Unqualified is CType.Array { Count: int ln } la && rightProbe?.Unqualified is CType.Array { Count: int rn } ra
            && la.Element.Equals(ra.Element) && ln + rn <= MaxRepeat)
        {
            var left = LowerExpr(leftItem);
            var right = LowerExpr(rightItem);
            if (left is not (VarRef or Member or StackArray) || right is not (VarRef or Member or StackArray))
            {
                throw new IrUnsupportedException("zig `a ++ b` of array values: each operand must be a named array (bind a call's result to a `const` first)");
            }
            IEnumerable<CExpr> Elems(CExpr arr, int n) => arr is StackArray sa
                ? sa.Elems
                : Enumerable.Range(0, n).Select(k => (CExpr)new DotCC.Ir.Index(arr,
                    new LitInt(k.ToString(System.Globalization.CultureInfo.InvariantCulture), k) { Type = CType.Int }) { Type = la.Element });
            var joined = Elems(left, ln).Concat(Elems(right, rn)).ToList();
            return new StackArray(la.Element, joined) { Type = new CType.Array(la.Element, ln + rn) };
        }
        throw new IrUnsupportedException(
            "zig `a ++ b`: only string / array-literal concatenation (incl. a `const` bound to a comptime string/array, "
            + "and an anon `.{…}` borrowing a typed operand's element type) is lowered (road-to-zig-std S9/S5); an "
            + "`@typeName`-of-user-type or a non-literal operand needs the fuller comptime aggregate engine (S5–S6)");
    }

    /// <summary>Fold <c>a ++ b</c> when both operands are comptime STRING values (a string literal, a
    /// <c>const</c> bound to one, or a nested string <c>++</c>/<c>**</c>): concatenate the raw quoted
    /// segments (C adjacent-string-literal concatenation, reusing the shared encode path so escapes /
    /// UTF-8 / NUL are handled once). Returns null when either operand is not a comptime string — the
    /// caller then tries the array-literal fold or the loud cut.</summary>
    private LitStr? TryFoldStringConcat(Item leftItem, Item rightItem)
    {
        if (EvalComptimeValue(leftItem) is LitStr ls && EvalComptimeValue(rightItem) is LitStr rs)
        {
            var segs = new List<string>(ls.Segments.Count + rs.Segments.Count);
            segs.AddRange(ls.Segments);
            segs.AddRange(rs.Segments);
            DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
            return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
        }
        return null;
    }

    /// <summary>Lower a Zig comptime repeat <c>a ** n</c> with a comptime-integer count. V1 folds the
    /// LITERAL case, symmetric with <see cref="LowerConcat"/>: a STRING literal repeats its raw segments
    /// (<c>"x" ** 3</c> → the pooled literal <c>"xxx"</c>); a TYPED ARRAY literal repeats its positional
    /// elements through the shared <see cref="BuildArrayInit"/>. <c>n == 0</c> is a loud cut for arrays (an
    /// empty array literal is unsupported) and yields the empty string literal for strings. A non-literal
    /// operand, a non-constant count, or a negative / oversized count is a loud cut (the general case needs
    /// the comptime aggregate engine, S5–S6).</summary>
    private CExpr LowerRepeat(Item leftItem, Item countItem)
    {
        // The count resolves to a comptime integer (a literal or a `const` bound to one).
        if (EvalComptimeValue(countItem) is not LitInt { Value: { } n } || n < 0 || n > MaxRepeat)
        {
            throw new IrUnsupportedException(
                $"zig `a ** n`: the repeat count must be a comptime integer in [0, {MaxRepeat}]"
                + " (a non-constant count needs the comptime aggregate engine, S5–S6)");
        }
        // String fold — the operand resolved to a comptime string value, segments repeated.
        if (EvalComptimeValue(leftItem) is LitStr ls)
        {
            var segs = new List<string>(ls.Segments.Count * (int)n);
            for (var i = 0; i < n; i++) { segs.AddRange(ls.Segments); }
            DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
            return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
        }
        // Array-literal fold — a typed `[N]T{…}` or a `const` bound to one (an anon `.{…} ** n` has no
        // element type to borrow, so it stays a loud cut below).
        if (TryArrayOperand(leftItem, out var elem, out var items) && elem is not null)
        {
            var merged = new List<Item>(items.Count * (int)n);
            for (var i = 0; i < n; i++) { merged.AddRange(items); }
            return BuildArrayInit(merged, new CType.Array(elem, null));   // n == 0 → empty → BuildArrayInit loud-cuts
        }
        throw new IrUnsupportedException(
            "zig `a ** n`: only string / typed-array-literal repeat (incl. a `const` bound to a comptime string/array) "
            + "is lowered (road-to-zig-std S9); an untyped anon `.{…}` operand or a non-literal operand needs the fuller "
            + "comptime aggregate engine (S5–S6)");
    }

    /// <summary>Evaluate an expression to a comptime VALUE, or null if it is not comptime-known in the
    /// forms V1 handles (road-to-zig-std S5 seed). Today: a string literal (→ <see cref="LitStr"/>), an
    /// integer literal (→ <see cref="LitInt"/>), a bare name bound to a comptime literal
    /// (<see cref="_comptimeValues"/>), or a nested string <c>++</c>/<c>**</c> fold. Never throws — it
    /// only lowers the pure literal forms and otherwise returns null, so it is safe to call
    /// speculatively (incl. while recording a <c>const</c> binding in any pass). Consumed by the
    /// <c>++</c>/<c>**</c> folds; the natural growth point as the comptime engine lands
    /// (<c>@typeName</c>, aggregate values, comptime calls).</summary>
    private CExpr? EvalComptimeValue(Item item) => item.Content switch
    {
        Zig.StrLit => LowerExpr(item),   // pure — → LitStr
        Zig.IntLit => LowerExpr(item),   // pure — → LitInt (with a folded Value)
        Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } csym && _comptimeStringVars.TryGetValue(csym, out var csv)
            ? csv
            : _comptimeValues.GetValueOrDefault(Tok(id.Arg0)),
        // `fmt[a..b]` / `fmt[a..]` over a comptime string with comptime bounds: the sub-string.
        Zig.SliceRange sr => TrySliceComptimeString(sr.Arg0, sr.Arg2, sr.Arg4),
        Zig.SliceOpen so => TrySliceComptimeString(so.Arg0, so.Arg2, null),
        Zig.Grouped g => EvalComptimeValue(g.Arg1),   // `(expr)` — unwrap so a parenthesized fold composes
        // `fmt[a..b].*` (a comptime slice deref'd to its array) and `&arr` of one (std.Io.Writer.print's
        // `Placeholder.parse(&placeholder_array)`): the same bytes, still a comptime string.
        Zig.Deref d => EvalComptimeValue(d.Arg0) as LitStr,
        Zig.PreAddrOf a => EvalComptimeValue(a.Arg1) as LitStr,
        Zig.Concat c => TryFoldStringConcat(c.Arg0, c.Arg2),
        Zig.Repeat r => TryFoldStringRepeat(r.Arg0, r.Arg2),
        Zig.BuiltinCall b => TryEvalTypeNameBuiltin(b),   // `@typeName(T)` → comptime string (else null)
        // A byte-slice field of a comptime AGGREGATE the interpreter holds (std.Io.Writer.print's
        // `placeholder.specifier_arg`, a `const placeholder = comptime Placeholder.parse(…)`).
        Zig.Field => TryComptimeAggregateString(item),
        // NOT a module-qualified constant (`builtin.link_libc`), deliberately: this runs during pass 0
        // of module PREPARATION, and resolving a module path from here would prepare the target module
        // as a side effect of merely RECORDING a const — the eager fan-out S2's laziness exists to
        // prevent ("std.zig's 66 re-exports don't fan out at prepare time"). A cross-module constant
        // folds at LOWERING time instead, in the Field case of LowerExpr and in the comptime-condition
        // fold, where resolving another module is exactly what is wanted (road-to-zig-std S3a).
        _ => null,
    };

    /// <summary><c>a or b</c> / <c>a and b</c> whose LEFT side is a settled comptime question: zig never analyses the
    /// right side when the left decides the result (std.math.cast's <c>(is_comptime or maxInt(@TypeOf(x)) &gt; …)</c>,
    /// where <c>maxInt(comptime_int)</c> would not compile), so it is not lowered at all. Otherwise the ordinary
    /// runtime operator.</summary>
    private CExpr ShortCircuit(BinOp op, Item left, Item right)
    {
        if (TryFoldComptimeCondition(left) is { } settled && settled == (op == BinOp.LogOr))
        {
            return new LitBool(settled) { Type = CType.Bool };
        }
        return Bin(op, left, right);
    }

    /// <summary>The comptime string a field path rooted at a comptime aggregate holds, spliced to a string literal;
    /// null when the root is not one (checked before anything lowers, so this stays cheap and side-effect free
    /// for every other field) or the value is not a byte slice.</summary>
    private LitStr? TryComptimeAggregateString(Item expr)
    {
        var root = expr;
        while (root.Content is Zig.Field or Zig.Grouped)
        {
            root = root.Content is Zig.Field rf ? rf.Arg0 : ((Zig.Grouped)root.Content).Arg1;
        }
        if (root.Content is not Zig.Ident rid || _symbols.Resolve(Tok(rid.Arg0)) is not { } rootSym
            || !_ir.ComptimeGlobals.ContainsKey(rootSym))
        {
            return null;
        }
        CExpr lowered;
        using (EnterThrowawayHoist()) { lowered = LowerExpr(expr); }
        return _ir.EvalComptimeValue(lowered) is IrModule.CtSlice slice && _ir.SpliceComptimeValue(slice) is SliceNew { Ptr: LitStr str }
            ? str : null;
    }

    /// <summary>Each <c>comptime var</c> holding a comptime STRING (std.Io.Writer.print's
    /// <c>comptime var literal: []const u8 = "";</c>), by symbol → its current value, updated by assignments
    /// at lowering time (<see cref="TryAssignComptimeVar"/>) and substituted where it is read.</summary>
    private readonly Dictionary<Symbol, LitStr> _comptimeStringVars = new();

    /// <summary>A comptime slice of a comptime string (<c>fmt[start..end]</c>), or null when the base is not
    /// a comptime string or a bound does not fold. Built from the decoded bytes, each spelled <c>\xNN</c>.</summary>
    private LitStr? TrySliceComptimeString(Item baseItem, Item loItem, Item? hiItem)
    {
        if (EvalComptimeValue(baseItem) is not LitStr str) { return null; }
        var bytes = DotCC.EmitHelpers.StringByteValues(str.Segments);
        long? lo, hi;
        using (EnterThrowawayHoist())
        {
            lo = _ir.ConstEval(LowerExpr(loItem));
            hi = hiItem is { } h ? _ir.ConstEval(LowerExpr(h)) : bytes.Count;
        }
        if (lo is not { } l || hi is not { } e || l < 0 || e > bytes.Count || l > e) { return null; }
        return ComptimeStringFromBytes(bytes.Skip((int)l).Take((int)(e - l)));
    }

    /// <summary>A string literal holding exactly <paramref name="bytes"/>, each spelled as a <c>\xNN</c> escape
    /// (so any byte round-trips through the shared C-string decoder), typed as its NUL-terminated array.</summary>
    /// <summary>The comptime string an anonymous list of comptime-known bytes spells (<c>.{ 'e', info.exp_char_lower }</c>),
    /// or null when an element is not positional or does not evaluate to a byte at compile time.</summary>
    /// <summary>The comptime string a <c>comptime cs: []const u8</c> argument spells: any comptime string value, or
    /// <c>&amp;.{ c0, c1 }</c> of comptime-known bytes (std.fmt.parse_float's <c>stream.firstIsLower(&amp;.{info.exp_char_lower})</c>).
    /// Only at such a parameter: elsewhere an anonymous list stays the tuple or array it is.</summary>
    private LitStr? EvalComptimeStringArg(Item arg)
        => EvalComptimeValue(arg) as LitStr
           ?? (arg.Content is Zig.PreAddrOf { Arg1.Content: Zig.AnonStructInit list } ? TryComptimeByteList(list.Arg2) : null);

    private LitStr? TryComptimeByteList(Item fieldInits)
    {
        var bytes = new List<int>();
        foreach (var element in Flatten(fieldInits))
        {
            if (element.Content is not Zig.FieldInitPositional pos) { return null; }
            CExpr lowered;
            using (EnterThrowawayHoist())
            {
                try { lowered = LowerExpr(pos.Arg0); }
                catch (IrUnsupportedException) { return null; }
            }
            var value = _ir.ConstEval(lowered)
                ?? (_ir.EvalComptimeValue(lowered) is IrModule.CtInt { Value: var big } && big >= 0 && big <= 255 ? (long)big : null);
            if (value is not { } b || b is < 0 or > 255) { return null; }
            bytes.Add((int)b);
        }
        return bytes.Count > 0 ? ComptimeStringFromBytes(bytes) : null;
    }

    private static LitStr ComptimeStringFromBytes(IEnumerable<int> bytes)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var b in bytes) { sb.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)); }
        sb.Append('"');
        var segs = new List<string> { sb.ToString() };
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
    }

    /// <summary>Evaluate <c>@typeName(T)</c> to a comptime string value, or null if it is not that
    /// builtin or the type is not spellable (see <see cref="ZigTypeSpelling"/>). Non-throwing — the
    /// comptime-value path (<see cref="EvalComptimeValue"/>) needs a null-return, not the loud cut
    /// <see cref="LowerBuiltinCall"/> raises, so an unspellable operand falls through to the loud cut
    /// there or the caller's own.</summary>
    private LitStr? TryEvalTypeNameBuiltin(Zig.BuiltinCall b)
    {
        if (Tok(b.Arg0) != "@typeName") { return null; }
        var args = Flatten(b.Arg2);
        return args.Count == 1 && ZigTypeSpelling(args[0]) is { } spelling ? ZigStringLiteral(spelling) : null;
    }

    /// <summary>Fold <c>a ** n</c> when the operand is a comptime STRING value and the count a comptime
    /// integer — the string-repeat counterpart of <see cref="TryFoldStringConcat"/>, used both by
    /// <see cref="LowerRepeat"/> and by <see cref="EvalComptimeValue"/> (so a nested `s ** n` inside a
    /// larger fold resolves). Returns null when the operand is not a comptime string or the count not a
    /// valid comptime integer.</summary>
    private LitStr? TryFoldStringRepeat(Item leftItem, Item countItem)
    {
        if (EvalComptimeValue(leftItem) is LitStr ls
            && EvalComptimeValue(countItem) is LitInt { Value: { } n } && n >= 0 && n <= MaxRepeat)
        {
            var segs = new List<string>(ls.Segments.Count * (int)n);
            for (var i = 0; i < n; i++) { segs.AddRange(ls.Segments); }
            DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
            return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
        }
        return null;
    }

    /// <summary>Recognize an ARRAY-SHAPED operand of a comptime <c>++</c>/<c>**</c> fold and yield its
    /// <paramref name="element"/> type + RAW positional element items (re-lowered by
    /// <see cref="BuildArrayInit"/> at the merged extent). Three forms:
    /// <list type="bullet">
    /// <item>a DIRECT typed array literal <c>[N]T{…}</c> / <c>[_]T{…}</c> — <paramref name="element"/> is
    /// its element type;</item>
    /// <item>a bare name bound to an array-literal <c>const</c> (<see cref="_comptimeArrayConsts"/>) —
    /// same, lowering the recorded element-type item now (body-lowering time, safe);</item>
    /// <item>an anon <c>.{…}</c> — items only, with <paramref name="element"/> <c>null</c> (the type is
    /// BORROWED from the other, typed operand by the caller; two untyped anons are a loud cut).</item>
    /// </list>
    /// Also unwraps a <c>(grouped)</c> operand. Returns false for any non-array operand (a string
    /// literal, a non-literal expression) — those take the string path or the loud cut.</summary>
    private bool TryArrayOperand(Item item, out CType? element, out IReadOnlyList<Item> posItems)
    {
        // `(expr)` — unwrap so a parenthesized array operand composes.
        if (item.Content is Zig.Grouped g) { return TryArrayOperand(g.Arg1, out element, out posItems); }
        // A DIRECT typed array literal `[N]T{…}` / `[_]T{…}`.
        if (item.Content is Zig.TypedStructInit { Arg0.Content: Zig.TyArray ta } tsi)
        {
            element = LowerType(ta.Arg3);
            posItems = Flatten(tsi.Arg2);
            return true;
        }
        // A `const` bound to an array literal (road-to-zig-std S5) — the raw items were recorded WITHOUT
        // lowering; lower the element TYPE now (body-lowering time, safe) and re-feed BuildArrayInit.
        if (item.Content is Zig.Ident id && _comptimeArrayConsts.TryGetValue(Tok(id.Arg0), out var rec))
        {
            element = LowerType(rec.ElemTypeItem);
            posItems = rec.Items;
            return true;
        }
        // An anon `.{…}` — the element type is inferred from the OTHER operand (null here); the items
        // lower at that borrowed element type via BuildArrayInit. (A named `.field =` element is rejected
        // there — an array literal is all-positional.)
        if (item.Content is Zig.AnonStructInit anon)
        {
            element = null;
            posItems = Flatten(anon.Arg2);
            return true;
        }
        element = null;
        posItems = [];
        return false;
    }

    /// <summary>Lower a call. Two callee shapes: a bare identifier bound to a named function
    /// (free function or <c>extern</c>/libc) → <see cref="BuildCall"/>; a <c>base.name(args)</c>
    /// field callee → <see cref="LowerMethodCall"/> (a UFCS instance method or a static/associated
    /// function). An indirect / function-pointer callee is still deferred.</summary>
    private CExpr LowerCall(Item calleeItem, Item? argListItem)
    {
        var result = LowerCallInner(calleeItem, argListItem);
        // A call is a side effect, evaluated AFTER its arguments. Mark the ANF impurity watermark so a
        // LATER sibling `catch`/`orelse` in the same statement is NOT hoisted past this call (which
        // would reorder it). An argument's own `catch`/`orelse` was already lowered (and checked)
        // inside LowerCallInner, BEFORE this set — so `f(a catch b)` still hoists cleanly.
        if (_hoist is not null) { _hoistImpureSeen = true; }
        return result;
    }

    private CExpr LowerCallInner(Item calleeItem, Item? argListItem)
        => LowerCallItems(calleeItem, argListItem is null ? new List<Item>() : Flatten(argListItem));

    /// <summary>The body of <see cref="LowerCallInner"/> over already-split argument items, so a call spelled
    /// another way (<c>@call(modifier, f, .{ a, b })</c>) lowers exactly as <c>f(a, b)</c>.</summary>
    private CExpr LowerCallItems(Item calleeItem, List<Item> argItems)
    {

        // `base.method(args)` — a method (UFCS) or associated-function call.
        if (calleeItem.Content is Zig.Field fld)
        {
            return LowerMethodCall(fld, argItems);
        }

        // `.name(args)` reaching here had no container-typed result location (LowerExprSink takes the
        // decl literal when it does) — zig rejects it the same way, for want of a type to look `name` up in.
        if (calleeItem.Content is Zig.EnumLit declLit)
        {
            throw new IrUnsupportedException(
                $"decl literal `.{Tok(declLit.Arg1)}(…)` needs a struct/union result type to resolve against "
                + "(a typed `const`/`var`, a typed parameter, a `return` of a typed function)");
        }
        // Inside std's own mem.zig a bare `asBytes(…)` / `sliceAsBytes(…)` (std.mem.indexOf's `sliceAsBytes(haystack)`)
        // is the curated lowering too, as `std.mem.asBytes(…)` from any other module is.
        if (calleeItem.Content is Zig.Ident { Arg0: var curatedTok } && Tok(curatedTok) is "asBytes" or "sliceAsBytes"
            && IsStdModule("mem.zig"))
        {
            return LowerStdMemCall(Tok(curatedTok), argItems);
        }
        if (calleeItem.Content is not Zig.Ident id)
        {
            throw new IrUnsupportedException("zig call: only a bare-identifier or `base.method` callee is lowered yet (got "
                + (calleeItem.Content?.GetType().Name ?? "null") + ")");
        }
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name);
        // In a lazy module, a bare call to a not-yet-lowered SIBLING declares it on demand (its body is
        // enqueued for the top-level drain), so `isPrint` can call `isAscii`/`isControl` (road-to-zig-std S2).
        if (sym is null && _lazy) { sym = EnsureDeclLowered(name); }
        // A re-export alias (`pub const indexOfScalar = findScalar;`, `const f = util.f;`): call the
        // declaration it names, in its own module when that is another one.
        if (sym is null && ResolveExportedDecl(name, raiseIfSkipped: true) is { } aliased)
        {
            if (aliased.Owner != this) { return CallExportedDecl(aliased.Owner, aliased.Sym, argItems); }
            sym = aliased.Sym;
        }
        // A local comptime alias of a function (`const add = switch (sign) { .pos => math.add, … };`).
        if (sym is null && _fnAliases.TryGetValue(name, out var fnAlias))
        {
            return CallExportedDecl(fnAlias.Owner, fnAlias.Sym, argItems);
        }
        // A bare call to a function of the container in scope or one enclosing it (`self.* = init(allocator);`
        // inside AlignedManaged): zig resolves a container's declarations lexically, as `Self.init(…)`.
        if (sym is null)
        {
            for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
            {
                if (EnsureMethodDeclared(c, name) is { } sibling) { return CallStaticMethod(sibling, argItems); }
            }
        }
        // A top-level const naming a container's function (`pub const featureSet =
        // CpuFeature.FeatureSetFns(Feature).featureSet;` in std/Target/x86.zig): the call is that method call.
        if (sym is null && ResolveMethodAlias(name) is { } aliasedMethod)
        {
            return CallStaticMethod(aliasedMethod, argItems);
        }
        if (sym is null) { throw new IrUnsupportedException($"call to unresolved name '{name}'"); }
        // A type-returning generic (wall-plan W4) is a COMPTIME type constructor — calling it in value
        // position is meaningless; it must appear in a TYPE position (a type annotation / alias / typed
        // literal), where LowerType reifies it. Reject a value-position call clearly.
        if (_typeReturningGenerics.ContainsKey(sym))
        {
            throw new IrUnsupportedException(
                $"'{name}' is a type-returning generic — use it in a TYPE position (`const x: {name}(…) = …` / "
                + $"`{name}(…){{…}}`), not as a value");
        }
        // A real (named) function → a direct, by-name call. A GENERIC (comptime-param template,
        // wall-plan W3a) instead instantiates a specialized body per resolved comptime-argument tuple
        // and calls the mangled instance.
        if (sym.Kind is SymKind.Func && sym.Type.Unqualified is CType.Func)
        {
            if (_genericFns.TryGetValue(sym, out var generic)) { return InstantiateGeneric(sym, generic, argItems); }
            return BuildCall(sym, argItems, receiver: null);
        }
        // A fn-pointer VALUE — a `delegate*` local / parameter typed `CType.Func` (Milestone W,
        // part 1a) → an INDIRECT call through the variable, each argument result-located against
        // the fn-pointer's parameter type (Zig result-locates call arguments). Renders as
        // `op(args)` over the (renamed-safe) VarRef.
        if (sym.Type.Unqualified is CType.Func fnptr)
        {
            var callee = new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            if (argItems.Count != fnptr.Params.Count)
            {
                throw new IrUnsupportedException(
                    $"call through fn-pointer '{name}': expected {fnptr.Params.Count} argument(s), got {argItems.Count}");
            }
            var args = new List<CExpr>(argItems.Count);
            for (var i = 0; i < argItems.Count; i++)
            {
                args.Add(LowerExprSink(argItems[i], fnptr.Params[i]));
            }
            return new IndirectCall(callee, args) { Type = fnptr.Return };
        }
        throw new IrUnsupportedException($"'{name}' is not callable (expected a function or a fn-pointer value)");
    }

    /// <summary>Build an IR <see cref="Call"/> to a resolved function symbol, optionally with a
    /// synthesized leading <paramref name="receiver"/> argument (an instance method's <c>self</c>).
    /// Carries the callee's parameter types (so the backend coerces each argument as C does at a
    /// call) and the symbol (so it emits the legalized target name); an <c>extern</c>/libc symbol
    /// (<see cref="Symbol.FromSystemHeader"/>) drops the symbol so the call binds to dotcc's
    /// <c>Libc</c> runtime by bare name. Each fixed argument's parameter type is its sink (Zig
    /// result-locates call arguments), accounting for the receiver's parameter slot.</summary>
    /// <summary>True for a void-typed argument the C# backend may erase without losing anything: the
    /// void value itself, or a read of a void symbol. zig's <c>void</c> has no runtime representation, so
    /// the backend drops void parameters and their arguments; a SIDE-EFFECTING void argument
    /// (<c>f(g())</c> with <c>g</c> returning void) would lose its call that way, so it is rejected.</summary>
    private static bool IsErasableVoid(CExpr e) => e is DefaultLit or VarRef || e is Paren p && IsErasableVoid(p.Inner)
        // A `void` FIELD read (std.sort's `lessThanFn(ctx.sub_ctx, …)` with `context: void`) has no effect either.
        || e is Member { Base: var fieldBase } && IsPurePath(fieldBase);

    /// <summary>A variable, or a field path off one: reading it has no side effect.</summary>
    private static bool IsPurePath(CExpr e) => e switch
    {
        VarRef => true,
        Paren p => IsPurePath(p.Inner),
        Member m => IsPurePath(m.Base),
        _ => false,
    };

    private CExpr BuildCall(Symbol sym, IReadOnlyList<Item> argItems, CExpr? receiver)
    {
        RecordRuntimeCall(sym);
        var fn = (CType.Func)sym.Type.Unqualified;
        var args = new List<CExpr>(argItems.Count + 1);
        var paramOffset = 0;
        if (receiver is not null) { args.Add(receiver); paramOffset = 1; }

        // Each fixed argument's parameter type is its sink (Zig result-locates a call argument),
        // so `f(.member)` / `f(.{…})` resolve against the parameter. The receiver, if any, has
        // already consumed parameter slot 0. A variadic tail argument has no fixed parameter
        // type → no sink (plain LowerExpr).
        for (var i = 0; i < argItems.Count; i++)
        {
            var pIndex = i + paramOffset;
            var paramSink = pIndex < fn.Params.Count ? fn.Params[pIndex] : null;
            var arg = LowerExprSink(argItems[i], paramSink);
            // `utf8Decode2(bytes[0..2].*)` (std.unicode): the comptime-length slice stands for its array copy (see the
            // `.*` lowering), and an array parameter is its element pointer, so the slice passes its `.Ptr`. A
            // parameter is immutable in zig, so no copy is observable.
            if (paramSink?.Unqualified is CType.Array { Element: var arrayParamElem } && arg.Type.Unqualified is CType.Slice
                && argItems[i].Content is Zig.Deref)
            {
                arg = new Member(arg, "Ptr", false) { Type = new CType.Pointer(arrayParamElem) };
            }
            // An aggregate VALUE (a slice, an array, a tuple) never coerces to a scalar parameter in zig ("expected type
            // 'u8', found '[]const [:0]const u8'"); passing one on would only make C# reject the emitted call.
            if (paramSink?.Unqualified is CType.Prim && arg.Type.Unqualified is CType.Slice or CType.Array or CType.Tuple)
            {
                throw new IrUnsupportedException(
                    $"call to '{sym.Name}': expected type {paramSink.Describe()}, found {arg.Type.Describe()}");
            }
            if (paramSink?.Unqualified is CType.VoidType && !IsErasableVoid(arg))
            {
                throw new IrUnsupportedException(
                    $"call to '{sym.Name}': a side-effecting argument to a `void` parameter is not supported yet "
                    + "(void has no runtime value, so the argument is erased); evaluate it as its own statement first");
            }
            args.Add(arg);
        }

        // A variadic callee (printf) needs AT LEAST the fixed params; the rest are the variadic
        // tail. A fixed-arity callee needs an exact match.
        var arityOk = fn.Variadic ? args.Count >= fn.Params.Count : args.Count == fn.Params.Count;
        if (!arityOk)
        {
            throw new IrUnsupportedException(
                $"call to '{sym.Name}': expected {(fn.Variadic ? "at least " : "")}{fn.Params.Count} argument(s), got {args.Count}");
        }
        // Zig parity (the differential oracle caught dotcc being too lenient here): an untyped
        // comptime numeric literal has no fixed-size ABI type, so Zig forbids passing it to a
        // C-variadic — `printf("%d", 42)` is an error, `@as(c_int, 42)` is required. The variadic
        // tail begins at argItems index `fn.Params.Count - paramOffset`. (Methods are never
        // variadic, so paramOffset is 0 whenever this branch runs.)
        if (fn.Variadic)
        {
            for (var k = fn.Params.Count - paramOffset; k < argItems.Count; k++)
            {
                if (IsComptimeUntypedNumeric(argItems[k]))
                {
                    _ir.Diagnostics.Add(new Diagnostic(Severity.Error,
                        "integer and float literals passed to variadic function must be casted to a fixed-size number type",
                        SrcPos.From(argItems[k])));
                }
            }
        }

        // An extern/libc function (FromSystemHeader) renders by its bare name — no CalleeSym —
        // so it binds to dotcc's Libc runtime (and printf/scanf hit the fluent builder), exactly
        // as a C program's libc call does. A user Zig function (or method) carries its symbol so
        // the (possibly legalized / mangled) target name is used.
        var calleeSym = sym.FromSystemHeader ? null : sym;
        return new Call(sym.Name, args, fn.Params, calleeSym) { Type = fn.Return };
    }

    /// <summary>Lower a <c>base.name(args)</c> call. Two shapes: (A) a STATIC / associated call
    /// <c>Type.func(args)</c> — the base is a bare identifier naming a registered struct (and is
    /// NOT a variable in scope) — every argument is explicit, no receiver; (B) an INSTANCE call
    /// <c>expr.method(args)</c> — the base value is the receiver, adjusted (Zig UFCS auto-ref/
    /// deref) to the method's declared first-parameter form. Both rewrite to the mangled free
    /// function <c>TypeName_method</c> recorded in <see cref="_methods"/>.</summary>
    /// <summary>Call a function that <paramref name="owner"/> declares, from this module: a GENERIC
    /// export (a <c>comptime</c> / <c>anytype</c> parameter, <c>std.fmt.bufPrint</c>) instantiates in its
    /// OWN module with the arguments read here (road-to-zig-std G3), since calling the template symbol
    /// directly would bind its empty placeholder signature; anything else is a direct call.</summary>
    private CExpr CallExportedDecl(ZigLowering owner, Symbol sym, IReadOnlyList<Item> argItems)
        => owner.TryResolveExportedGenericInstance(sym, argItems, caller: this) is { } inst
            ? FoldIfComptimeOnly(owner, inst.Instance, BuildCall(inst.Instance, inst.RuntimeArgs, receiver: null))
            : BuildCall(sym, argItems, receiver: null);

    /// <summary>The function a container CONST names (<c>pub const hash = getAutoHashFn(K, @This());</c> in
    /// hash_map's AutoContext, the closure idiom's function value), or null when <paramref name="container"/>
    /// declares no such const or it is not a comptime function value. Resolved by the module holding the
    /// const, in the container's scope with its comptime seeds live, and memoized.</summary>
    private Symbol? ContainerFnConst(string container, string name)
    {
        var owner = _shared.ContainerConstOwners.TryGetValue(container, out var o) ? o : this;
        return owner.OwnContainerFnConst(container, name);
    }

    /// <summary>The owner-side half of <see cref="ContainerFnConst"/>.</summary>
    private Symbol? OwnContainerFnConst(string container, string name)
    {
        if (_containerFnConsts.TryGetValue((container, name), out var known)) { return known; }
        if (!_containerConsts.TryGetValue(container, out var consts) || !consts.TryGetValue(name, out var entry)
            || entry.typeItem is not null)
        {
            return null;
        }
        using var seeds = EnterReifiedSeeds(container);
        using var scope = EnterContainer(container);
        var fn = TryResolveComptimeFnValue(entry.rhs, this)?.Fn;
        _containerFnConsts[(container, name)] = fn;
        return fn;
    }

    /// <summary>Memo of <see cref="OwnContainerFnConst"/>: (container, const) → the function it names, or null.</summary>
    private readonly Dictionary<(string Container, string Name), Symbol?> _containerFnConsts = new();

    /// <summary>Call a container function through its type (<c>Self.init(…)</c>, <c>Map(K, V).init(…)</c>): a
    /// direct call, or, for a GENERIC method, an instantiation in the module that owns it.</summary>
    /// <summary>The function a top-level const aliases, when it names a container's function through a type
    /// (<c>pub const featureSet = CpuFeature.FeatureSetFns(Feature).featureSet;</c> in std/Target/x86.zig), resolved
    /// in this module (the type call reifies here); null for any other const.</summary>
    internal Symbol? ResolveMethodAlias(string name)
    {
        if (!_topLevelConstRhs.TryGetValue(name, out var rhs) || rhs.Content is not Zig.Field f) { return null; }
        CType? owner = f.Arg0.Content switch
        {
            Zig.CallArgs or Zig.CallNoArgs => TryEvalTypeReturningCall(f.Arg0, out var called) ? called : null,
            Zig.Ident id => TryLookupContainerType(Tok(id.Arg0), out var named) ? named : null,
            Zig.Field => TryResolveQualifiedNestedType(f.Arg0),
            _ => null,
        };
        return owner is not null && ContainerTypeName(owner) is { } container
            ? EnsureMethodDeclared(container, Tok(f.Arg2)) ?? ContainerFnConst(container, Tok(f.Arg2))
            : null;
    }

    private CExpr CallStaticMethod(Symbol method, IReadOnlyList<Item> argItems)
    {
        if (_shared.GenericMethodOwners.TryGetValue(method, out var owner) && owner._genericFns.TryGetValue(method, out var g))
        {
            var (instance, runtimeArgs) = owner.ResolveGenericInstance(method, g, argItems, argScope: this);
            return FoldIfComptimeOnly(owner, instance, BuildCall(instance, runtimeArgs, receiver: null));
        }
        return BuildCall(method, argItems, receiver: null);
    }

    private CExpr LowerMethodCall(Zig.Field fld, IReadOnlyList<Item> argItems)
    {
        var methodName = Tok(fld.Arg2);

        // --- curated `std.mem` helpers (a static call on the std.mem namespace) ---
        // Routed before the generic dispatch. dotcc models no `std` in general — only this curated
        // set of the most common slice utilities. An unmodeled member is a clear, specific error without
        // a std tree; with one (DOTCC_ZIG_LIB_DIR) it navigates to real source (TakesCuratedStdCall).
        if (TryResolveStdPath(fld.Arg0, out var stdNs) && stdNs == "std.mem" && TakesCuratedStdCall(stdNs, methodName))
        {
            return LowerStdMemCall(methodName, argItems);
        }

        // --- curated `std.debug.print` (wall-plan W6) --- the biggest remaining std idiom. Like the
        // std.mem helpers it's a curated path, not a general std model; only `print` is modeled.
        if (TryResolveStdPath(fld.Arg0, out var stdDbg) && stdDbg == "std.debug" && TakesCuratedStdCall(stdDbg, methodName))
        {
            return LowerStdDebugCall(methodName, argItems);
        }

        // --- curated `std.testing` assertions (`dotcc zig test`) --- `expect` / `expectEqual`, each
        // returning an error union so a `try` propagates a failing assertion to the test's boundary
        // (and thence to the generated test runner). A curated path, like the ones above.
        if (TryResolveStdPath(fld.Arg0, out var stdTest) && stdTest == "std.testing" && TakesCuratedStdCall(stdTest, methodName))
        {
            return LowerStdTestingCall(methodName, argItems);
        }

        // --- a call navigated to an @import'ed module: `util.func(args)` / `std.ascii.isDigit(args)` ---
        // AFTER the curated std.* fast-paths (so std.mem/debug/testing keep their curated lowering),
        // resolve the receiver as a module path — a relative import, or the real `std` root and its
        // re-export chain (std → ascii = @import("ascii.zig")) — and lower the referenced function on
        // demand: its signature lowers now (so the call binds) and its body is enqueued for the
        // top-level drain (road-to-zig-std S1/S2).
        //
        // A path the CURATED model owns is never navigated. Upstream std re-exports its allocators as
        // whole FILES (`pub const FixedBufferAllocator = @import("heap/FixedBufferAllocator.zig");`), so
        // with a real std tree configured (DOTCC_ZIG_LIB_DIR) `std.heap.FixedBufferAllocator` IS a
        // navigable module and navigation would beat the curated allocator below — then die lowering
        // upstream's own `init` signature. That made real-std navigation and the curated allocators
        // mutually exclusive; the guard restores S1's stated rule that the curated set is checked first
        // in EVERY position. (The complementary direction — a NON-curated std TYPE falling back to
        // navigation — is the separate S4d lift.)
        if (!IsCuratedStdPath(fld.Arg0) && ResolveModulePath(fld.Arg0) is { } navMod)
        {
            // `std.Target.x86.featureSet(…)`: the module exports the name as a const aliasing a container's
            // function, which the module resolves; the call and its arguments stay here.
            if (navMod.Lowering?.ResolveMethodAlias(methodName) is { } navAliased)
            {
                return CallStaticMethod(navAliased, argItems);
            }
            // Through any re-export (`pub const indexOfScalar = findScalar;`), to the module that owns it.
            var nav = navMod.Lowering?.ResolveExportedDecl(methodName, raiseIfSkipped: true)
                ?? throw new IrUnsupportedException(
                    $"zig module '{System.IO.Path.GetFileName(navMod.Path)}' has no exported function '{methodName}'");
            return CallExportedDecl(nav.Owner, nav.Sym, argItems);
        }

        // A member call on the `std.ArrayList(T)` TYPE (`std.ArrayList(i32).init(alloc)`) is
        // the pre-0.15 MANAGED API, which no longer exists in the pinned zig — reject it by
        // name with the migration path (the generic std-path error would only say `std`).
        if (fld.Arg0.Content is Zig.CallArgs mca && TryResolveStdPath(mca.Arg0, out var mlPath) && mlPath == "std.ArrayList")
        {
            throw new IrUnsupportedException(
                "zig std.ArrayList's managed API (`std.ArrayList(T).init(alloc)` + allocator-less calls) was removed in zig 0.15 — "
                + "use the unmanaged API: `var list: std.ArrayList(T) = .empty;` with a per-call allocator "
                + "(`try list.append(alloc, v)`, `list.deinit(alloc)`)");
        }

        // --- Zig allocators (Milestone F), before the generic method dispatch ---
        // `std.heap.FixedBufferAllocator.init(buf)` — a static call on the std FBA type.
        if (methodName == "init" && TryResolveStdPath(fld.Arg0, out var basePath) && basePath == "std.heap.FixedBufferAllocator")
        {
            return LowerFbaInit(argItems);
        }
        // `std.heap.ArenaAllocator.init(backing)` — a static call on the std arena type (Milestone U).
        if (methodName == "init" && TryResolveStdPath(fld.Arg0, out var arenaBase) && arenaBase == "std.heap.ArenaAllocator")
        {
            return LowerArenaInit(argItems);
        }
        // Any OTHER member call on a curated std TYPE: the model owns the path (so it was not
        // navigated above) but doesn't provide this function — say so precisely, instead of falling
        // through to the instance path and reporting the type as "a type, not a value".
        if (TryResolveStdPath(fld.Arg0, out var curatedBase) && StdTypes.ContainsKey(curatedBase))
        {
            throw new IrUnsupportedException(
                $"zig `{curatedBase}` has no modeled function '{methodName}' — dotcc curates these std "
                + "types, and models `init` on std.heap.FixedBufferAllocator / std.heap.ArenaAllocator");
        }
        // `a.alloc(T, n)` / `a.free(s)` (and the deferred `create`/`destroy`) on a known-default
        // (→ devirt) or an Allocator-typed receiver (→ indirect). A same-named method on a
        // non-allocator receiver falls through to the generic dispatch below.
        if (methodName is "alloc" or "alignedAlloc" or "dupe" or "free" or "create" or "destroy" or "realloc" or "resize" or "remap"
            or "rawAlloc" or "rawResize" or "rawRemap" or "rawFree"
            && TryLowerAllocatorMethod(fld, methodName, argItems, out var allocExpr))
        {
            return allocExpr;
        }

        // (A) `Type.func(args)` — a bare-identifier base naming a registered container type (a
        // struct/union `Named`, an enum `Enum`, or the self alias `Self`), and NOT a variable →
        // the associated/static function; all arguments are explicit (no receiver). A self alias
        // maps to the real container name so the method lookup and the mangled target match the
        // explicit-name form. (An `EnumName.member` non-call resolves to a tag constant earlier, in
        // the Zig.Field case; this branch only fires for a CALL whose method is a function.)
        if (fld.Arg0.Content is Zig.Ident bid
            && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
            && ContainerTypeName(baseTy) is { } typeName
            && _symbols.Resolve(Tok(bid.Arg0)) is null)
        {
            // A GENERIC top-level function of a file-as-struct module, called through the type (inside
            // the module, `const Writer = @This(); Writer.print(w, …)`): an ordinary exported generic call.
            if (_shared.FileStructOwners.TryGetValue(typeName, out var staticOwner)
                && staticOwner.FileStructGenericTemplate(methodName) is { } template)
            {
                return CallExportedDecl(staticOwner, template, argItems);
            }
            if ((EnsureMethodDeclared(typeName, methodName) ?? ContainerFnConst(typeName, methodName)) is not { } staticSym)
            {
                throw new IrUnsupportedException($"'{typeName}' has no function '{methodName}'");
            }
            return CallStaticMethod(staticSym, argItems);
        }
        // (A1) `Outer.Inner.func(args)` — the same static call through a QUALIFIED nested container.
        if (fld.Arg0.Content is Zig.Field
            && TryResolveQualifiedNestedType(fld.Arg0) is { } qualifiedTy
            && ContainerTypeName(qualifiedTy) is { } qualifiedName)
        {
            if (EnsureMethodDeclared(qualifiedName, methodName) is not { } qStaticSym)
            {
                throw new IrUnsupportedException($"'{qualifiedName}' has no function '{methodName}'");
            }
            return CallStaticMethod(qStaticSym, argItems);
        }
        // (A1b) `std.fmt.Placeholder.parse(…)` — a static call through a type ANOTHER module declares, named by
        // its module path. Also from a lazy module: this is body lowering, never a lazy module's pass 0, so
        // resolving the path lowers only what the call needs.
        if (fld.Arg0.Content is Zig.Field && !IsCuratedStdPath(fld.Arg0)
            && TryResolveModuleNestedType(fld.Arg0) is { Type: var moduleTy, Owner: var tyOwner } && ContainerTypeName(moduleTy) is { } moduleTyName)
        {
            // Declared in the module that owns the type, so its body (and a generic's instances) resolve there.
            if ((tyOwner.EnsureMethodDeclared(moduleTyName, methodName) ?? tyOwner.ContainerFnConst(moduleTyName, methodName)) is not { } mStaticSym)
            {
                throw new IrUnsupportedException($"'{moduleTyName}' has no function '{methodName}'");
            }
            return CallStaticMethod(mStaticSym, argItems);
        }
        // A static call through a module type whose declaration did not PARSE (`std.Io.Writer.Allocating.initCapacity(…)`
        // while Allocating had a parse gap): say so, rather than fall through to a "path not modeled" read of the base.
        if (fld.Arg0.Content is Zig.Field { Arg0: var skippedBase, Arg2: var skippedName } && !IsCuratedStdPath(fld.Arg0)
            && ResolveModulePath(skippedBase)?.Lowering is { } skippedOwner)
        {
            skippedOwner.RaiseIfSkippedDecl(Tok(skippedName));
        }

        // (A2) `Generic(args).func(…)` — the base is a call to a type-returning generic (wall-plan W4),
        // naming the REIFIED container directly instead of through an alias. This is the idiomatic
        // spelling (`std.ArrayList(u8).init(…)`), so a reified struct's static methods must be reachable
        // this way and not only via `const L = ArrayList(u8);` (road-to-zig-std G4). Evaluating the base
        // reifies it — memoized, so a repeat call reuses the one instance — and then the method resolves
        // exactly like (A). `TryEvalTypeReturningCall` returns false (no side effect) for any other call
        // base, so an ordinary `getBox().get()` still falls through to (B).
        if (fld.Arg0.Content is Zig.CallArgs or Zig.CallNoArgs
            && TryEvalTypeReturningCall(fld.Arg0, out var reifiedBase)
            && ContainerTypeName(reifiedBase) is { } reifiedName)
        {
            if ((EnsureMethodDeclared(reifiedName, methodName) ?? ContainerFnConst(reifiedName, methodName)) is not { } reifiedSym)
            {
                throw new IrUnsupportedException($"'{reifiedName}' has no function '{methodName}'");
            }
            return CallStaticMethod(reifiedSym, argItems);
        }

        // (B) `expr.method(args)` — the base is an instance of a container type.
        // A method on a TYPED comptime tag of a module (`builtin.cpu.arch.endian()` in std.mem, with a real std):
        // the receiver is that enum's constant, and the method is the enum's own, from its source.
        if (IsModuleRooted(fld.Arg0) && TryReadComptimeAggregateField(fld.Arg0) is { Tag: { } typedTag, TagType: { } tagType, TagTypeScope: { } tagScope }
            && tagScope.LowerType(tagType).Unqualified is CType.Enum tagEnum)
        {
            var tagRecv = tagScope.ResolveEnumLit(typedTag, tagEnum);
            if (EnsureMethodDeclared(tagEnum.Name, methodName) is not { } tagMethod)
            {
                throw new IrUnsupportedException($"enum '{tagEnum.Name}' has no method '{methodName}'");
            }
            var tagFn = (CType.Func)tagMethod.Type.Unqualified;
            return BuildCall(tagMethod, argItems, tagFn.Params.Count > 0 ? AdjustReceiver(tagRecv, tagFn.Params[0]) : null);
        }
        var recv = LowerExpr(fld.Arg0);
        // `fba.allocator()` / `arena.allocator()` — a FixedBufferAllocator (Milestone F) or an
        // ArenaAllocator (Milestone U) hands out an Allocator fat pointer over itself. Needs `&self`
        // as the vtable context; the result is opaque (→ the indirect dispatch path). Handled before
        // the generic _methods lookup (both are runtime types, not Zig-declared containers).
        if (methodName == "allocator" && recv.Type.Unqualified is CType.Named { Name: var allocTy }
            && allocTy is FbaTypeName or ArenaTypeName)
        {
            if (argItems.Count != 0)
            {
                throw new IrUnsupportedException($"zig `{allocTy}.allocator()` takes no arguments");
            }
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            var selfAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
            var factory = allocTy == FbaTypeName ? "ZigAlloc.FbaAllocator" : "ZigAlloc.ArenaToAllocator";
            return new Call(factory, new List<CExpr> { selfAddr },
                new List<CType> { new CType.Pointer(new CType.Named(allocTy)) }, null) { Type = new CType.Allocator() };
        }
        // `arena.deinit()` — free the whole arena chunk chain (Milestone U). A static wrapper called
        // by-ref on the local, like `allocator()`. The headline use is `defer arena.deinit();`.
        if (methodName == "deinit" && recv.Type.Unqualified is CType.Named { Name: ArenaTypeName })
        {
            if (argItems.Count != 0)
            {
                throw new IrUnsupportedException("zig `arena.deinit()` takes no arguments");
            }
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } sd })
            {
                sd.AddressTaken = true;
            }
            var arenaAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
            return new Call("ZigAlloc.ArenaDeinit", new List<CExpr> { arenaAddr },
                new List<CType> { new CType.Pointer(new CType.Named(ArenaTypeName)) }, null) { Type = CType.Void };
        }
        // Curated `std.ArrayList(T)` member calls (wall-plan W0) — routed on the receiver's
        // ZigList type before the generic container dispatch (a runtime type, not a
        // Zig-declared container). Mutating methods are INSTANCE methods on the runtime
        // struct, so calling on an lvalue receiver mutates in place (zig's `*Self` methods);
        // the allocator is an explicit per-call argument (the unmanaged API, zig 0.15+).
        // The curated `std.mem.Alignment` carrier's methods (std-internal code names them: hash_map's
        // `max_align.forward(total)`): static helpers on the runtime `Alignment`, receiver first.
        if (recv.Type.Unqualified is CType.Named { Name: AlignmentTypeName })
        {
            (string Helper, CType Ret, int Arity)? alignMethod = methodName switch
            {
                "toByteUnits" => ("Alignment.ToByteUnits", CType.ULong, 0),
                "forward" => ("Alignment.Forward", CType.ULong, 1),
                "backward" => ("Alignment.Backward", CType.ULong, 1),
                "check" => ("Alignment.Check", CType.Bool, 1),
                _ => null,
            };
            if (alignMethod is not { } am || argItems.Count != am.Arity)
            {
                throw new IrUnsupportedException(
                    $"zig std.mem.Alignment has no modeled member '{methodName}' taking {argItems.Count} argument(s) "
                    + "(curated: toByteUnits(), forward(addr), backward(addr), check(addr), fromByteUnits(n))");
            }
            var alignArgs = new List<CExpr> { recv };
            var alignTypes = new List<CType> { recv.Type };
            foreach (var a in argItems)
            {
                alignArgs.Add(LowerExprSink(a, CType.ULong));
                alignTypes.Add(CType.ULong);
            }
            return new Call(am.Helper, alignArgs, alignTypes, null) { Type = am.Ret };
        }
        if (recv.Type.Unqualified is CType.ZigList listTy)
        {
            return LowerZigListCall(recv, listTy, methodName, argItems);
        }
        if (!TryContainerName(recv.Type, out var container))
        {
            throw new IrUnsupportedException(
                $"zig method call `.{methodName}()` needs a struct (or pointer-to-struct) receiver, got {recv.Type.Describe()}");
        }
        // A GENERIC top-level function of a file-as-struct module called on an instance
        // (`w.print(fmt, args)`, road-to-zig-std G3): instantiate it in its own module with the receiver as
        // its first argument, then call the instance as a method.
        if (_shared.FileStructOwners.TryGetValue(container, out var fileOwner)
            && fileOwner.TryResolveFileStructGenericMethod(methodName, fld.Arg0, argItems, caller: this) is { } generic)
        {
            var gfn = (CType.Func)generic.Instance.Type.Unqualified;
            var gcall = BuildCall(generic.Instance, generic.RuntimeArgs, AdjustReceiver(recv, gfn.Params[0]));
            return FoldIfComptimeOnly(fileOwner, generic.Instance, gcall);
        }
        if (EnsureMethodDeclared(container, methodName) is not { } msym)
        {
            // A container CONST bound to a function value (hash_map's AutoContext: `pub const hash =
            // getAutoHashFn(K, @This());`) called on an instance: the function, with the receiver first.
            if (ContainerFnConst(container, methodName) is { } constFn)
            {
                var cfnType = (CType.Func)constFn.Type.Unqualified;
                if (cfnType.Params.Count == 0)
                {
                    throw new IrUnsupportedException(
                        $"'{container}.{methodName}' takes no parameters — call it as `{container}.{methodName}(…)`, not on an instance");
                }
                return BuildCall(constFn, argItems, AdjustReceiver(recv, cfnType.Params[0]));
            }
            // A FIELD holding a function pointer (`w.vtable.drain(w, data, n)`, std.Io.Writer's dispatch):
            // zig calls the field's value, with no receiver, each argument result-located against the
            // pointer's parameter type, like a call through a fn-pointer local.
            if (_ir.StructFieldType(recv.Type, methodName)?.Unqualified is CType.Func fieldFn)
            {
                if (argItems.Count != fieldFn.Params.Count)
                {
                    throw new IrUnsupportedException(
                        $"call through fn-pointer field '{container}.{methodName}': expected {fieldFn.Params.Count} argument(s), got {argItems.Count}");
                }
                var callee = new Member(recv, methodName, recv.Type.Unqualified is CType.Pointer) { Type = fieldFn, IsLValue = true };
                var fieldArgs = new List<CExpr>(argItems.Count);
                for (var i = 0; i < argItems.Count; i++)
                {
                    fieldArgs.Add(LowerExprSink(argItems[i], fieldFn.Params[i]));
                }
                return new IndirectCall(callee, fieldArgs) { Type = fieldFn.Return };
            }
            throw new IrUnsupportedException($"struct '{container}' has no method '{methodName}'");
        }
        // A GENERIC method (a `comptime` / `anytype` parameter): instantiate it in the module that owns it,
        // the receiver filling parameter 0 as it would in `Type.method(recv, …)`.
        if (_shared.GenericMethodOwners.TryGetValue(msym, out var genericOwner)
            && genericOwner._genericFns.TryGetValue(msym, out var genericMethod))
        {
            if (genericMethod.Params.Count == 0 || genericMethod.Params[0].Kind is not (ParamKind.Runtime or ParamKind.AnyType))
            {
                throw new IrUnsupportedException(
                    $"'{container}.{methodName}' called on an instance needs a runtime first parameter to take the receiver");
            }
            var withReceiver = new List<Item>(argItems.Count + 1) { fld.Arg0 };
            withReceiver.AddRange(argItems);
            var (instance, runtimeArgs) = genericOwner.ResolveGenericInstance(msym, genericMethod, withReceiver, argScope: this);
            var ifn = (CType.Func)instance.Type.Unqualified;
            var icall = BuildCall(instance, runtimeArgs.Skip(1).ToList(), AdjustReceiver(recv, ifn.Params[0]));
            return FoldIfComptimeOnly(genericOwner, instance, icall);
        }
        var mfn = (CType.Func)msym.Type.Unqualified;
        if (mfn.Params.Count == 0)
        {
            throw new IrUnsupportedException(
                $"'{container}.{methodName}' takes no parameters — call it as `{container}.{methodName}(…)`, not on an instance");
        }
        var receiver = AdjustReceiver(recv, mfn.Params[0]);
        return BuildCall(msym, argItems, receiver);
    }

    /// <summary>Lower a curated <c>std.ArrayList(T)</c> member call (wall-plan W0) to a
    /// <see cref="ZigListCall"/> on the runtime <c>ZigList&lt;T&gt;</c> instance. The unmanaged
    /// API only (zig 0.15+ re-pointed <c>std.ArrayList</c> at it): every growing call takes the
    /// allocator explicitly, <c>append</c>/<c>appendSlice</c> return <c>!void</c> (so <c>try</c>
    /// composes, <c>error.OutOfMemory</c> on exhaustion), <c>pop</c> returns <c>?T</c>. An
    /// unmodeled member is a clear error naming the curated set — never a silent drop.</summary>
    private CExpr LowerZigListCall(CExpr recv, CType.ZigList listTy, string methodName, IReadOnlyList<Item> argItems)
    {
        var elem = listTy.Element;
        switch (methodName)
        {
            case "append":
            {
                RequireListArgs(methodName, argItems, 2, "(alloc, item)");
                var a = LowerListAllocatorArg(methodName, argItems[0]);
                var item = LowerExprSink(argItems[1], elem);   // result-located at the element type
                return new ZigListCall(recv, "Append", new List<CExpr> { a, item, OomLit() })
                { Type = new CType.ErrorUnion(CType.Void) };
            }
            case "appendSlice":
            {
                RequireListArgs(methodName, argItems, 2, "(alloc, slice)");
                var a = LowerListAllocatorArg(methodName, argItems[0]);
                // `&arr` / a `[N]T` value / `&.{ 1, 1 }` coerces to a slice exactly as at any other slice sink: lowered AT
                // the slice (so an anonymous list literal is result-located at the element type), then coerced.
                var sliceType = new CType.Slice(elem);
                var sliceArg = LowerExprSink(argItems[1], sliceType);
                var s = sliceArg.Type?.Unqualified is CType.Slice ? sliceArg : CoerceToSlice(sliceArg, sliceType);
                return new ZigListCall(recv, "AppendSlice", new List<CExpr> { a, s, OomLit() })
                { Type = new CType.ErrorUnion(CType.Void) };
            }
            case "pop":
            {
                RequireListArgs(methodName, argItems, 0, "()");
                return new ZigListCall(recv, "Pop", new List<CExpr>()) { Type = new CType.Optional(elem) };
            }
            case "deinit":
            {
                RequireListArgs(methodName, argItems, 1, "(alloc)");
                var a = LowerListAllocatorArg(methodName, argItems[0]);
                return new ZigListCall(recv, "Deinit", new List<CExpr> { a }) { Type = CType.Void };
            }
            case "clearRetainingCapacity":
            {
                RequireListArgs(methodName, argItems, 0, "()");
                return new ZigListCall(recv, "ClearRetainingCapacity", new List<CExpr>()) { Type = CType.Void };
            }
            // The index / capacity members (task #74): each maps 1:1 onto the runtime ZigList.
            case "insert":
            {
                RequireListArgs(methodName, argItems, 3, "(alloc, index, item)");
                var a = LowerListAllocatorArg(methodName, argItems[0]);
                var at = LowerExprSink(argItems[1], CType.ULong);
                var item = LowerExprSink(argItems[2], elem);
                return new ZigListCall(recv, "Insert", new List<CExpr> { a, at, item, OomLit() })
                { Type = new CType.ErrorUnion(CType.Void) };
            }
            case "orderedRemove" or "swapRemove":
            {
                RequireListArgs(methodName, argItems, 1, "(index)");
                var at = LowerExprSink(argItems[0], CType.ULong);
                return new ZigListCall(recv, methodName == "orderedRemove" ? "OrderedRemove" : "SwapRemove", new List<CExpr> { at })
                { Type = elem };
            }
            case "getLast":
            {
                // zig 0.17-dev: `?T`, null for an empty list.
                RequireListArgs(methodName, argItems, 0, "()");
                return new ZigListCall(recv, "GetLast", new List<CExpr>()) { Type = new CType.Optional(elem) };
            }
            case "ensureTotalCapacity" or "ensureUnusedCapacity":
            {
                RequireListArgs(methodName, argItems, 2, "(alloc, n)");
                var a = LowerListAllocatorArg(methodName, argItems[0]);
                var n = LowerExprSink(argItems[1], CType.ULong);
                return new ZigListCall(recv, methodName == "ensureTotalCapacity" ? "EnsureTotalCapacity" : "EnsureUnusedCapacity",
                    new List<CExpr> { a, n, OomLit() }) { Type = new CType.ErrorUnion(CType.Void) };
            }
            case "appendAssumeCapacity":
            {
                RequireListArgs(methodName, argItems, 1, "(item)");
                var item = LowerExprSink(argItems[0], elem);
                return new ZigListCall(recv, "AppendAssumeCapacity", new List<CExpr> { item }) { Type = CType.Void };
            }
            case "shrinkRetainingCapacity":
            {
                RequireListArgs(methodName, argItems, 1, "(n)");
                var n = LowerExprSink(argItems[0], CType.ULong);
                return new ZigListCall(recv, "ShrinkRetainingCapacity", new List<CExpr> { n }) { Type = CType.Void };
            }
            default:
                throw new IrUnsupportedException(
                    $"zig std.ArrayList has no modeled member '{methodName}' (curated: append, appendSlice, insert, pop, "
                    + "orderedRemove, swapRemove, getLast, ensureTotalCapacity, ensureUnusedCapacity, "
                    + "appendAssumeCapacity, shrinkRetainingCapacity, deinit, clearRetainingCapacity, items, capacity)");
        }
    }

    /// <summary>Arity check for a curated list member — a clear error naming the expected shape.</summary>
    private static void RequireListArgs(string method, IReadOnlyList<Item> argItems, int count, string shape)
    {
        if (argItems.Count != count)
        {
            throw new IrUnsupportedException(
                $"zig std.ArrayList `.{method}{shape}` takes {count} argument(s); got {argItems.Count}");
        }
    }

    /// <summary>Lower the explicit allocator argument of an unmanaged-API list call. A std
    /// default path (<c>std.heap.c_allocator</c>) materializes a runtime <c>Allocator</c> via
    /// the ordinary expression lowering; anything not Allocator-typed is a clear error (the
    /// managed pre-0.15 API — <c>init(alloc)</c> + allocator-less calls — is NOT modeled,
    /// matching the pinned zig, which no longer has it).</summary>
    private CExpr LowerListAllocatorArg(string method, Item arg)
    {
        var a = LowerExpr(arg);
        if (a.Type.Unqualified is not CType.Allocator)
        {
            throw new IrUnsupportedException(
                $"zig std.ArrayList `.{method}` expects a std.mem.Allocator as its first argument, got {a.Type.Describe()}");
        }
        return a;
    }

    /// <summary>The <c>error.OutOfMemory</c> code as a literal argument — the same flat-set code
    /// <see cref="AllocCall"/> carries in <c>OomCode</c>. Typed <c>int</c> (NOT ushort): a plain
    /// in-range constant literal converts implicitly to the runtime's <c>ushort oom</c>
    /// parameter, whereas a UShort-typed literal renders with a <c>u</c> suffix (uint), which
    /// C# refuses to narrow (CS1503).</summary>
    private CExpr OomLit()
    {
        var code = ErrorCode("OutOfMemory");
        return new LitInt(code.ToString(System.Globalization.CultureInfo.InvariantCulture), code) { Type = CType.Int };
    }

    /// <summary>Try to lower an allocator method call <c>a.alloc(T, n)</c> / <c>a.free(s)</c> /
    /// <c>a.create(T)</c> / <c>a.destroy(p)</c> (Milestone F/U). The receiver is DEVIRTUALIZED to a
    /// direct call when provable: the statically-known C-heap default (→ <c>ZigAlloc.*CHeap</c>, a
    /// direct <c>Libc.malloc</c>/<c>free</c>) or a provable <c>fba.allocator()</c> site (→
    /// <c>ZigAlloc.*Fba(&amp;fba, …)</c>, a direct FBA bump). Otherwise the receiver is lowered and,
    /// if it is an <see cref="CType.Allocator"/>, dispatched indirectly through its vtable. Returns
    /// <c>false</c> (so the caller falls through to the generic method dispatch) when the receiver is
    /// neither — i.e. a same-named method on a non-allocator type.</summary>
    private bool TryLowerAllocatorMethod(Zig.Field fld, string methodName, IReadOnlyList<Item> argItems, out CExpr result)
    {
        result = null!;
        bool devirt = TryKnownAllocatorKind(fld.Arg0, out var kind);
        CExpr? recv = null;
        // For an FBA-site devirt the `&fba` context rides on the IR node's FbaCtx (takes precedence
        // over a null Receiver, which alone would mean the C-heap default). The C-heap devirt leaves
        // both null; a non-devirt receiver is lowered and dispatched indirectly.
        CExpr? fbaCtx = devirt && kind == AllocKind.Fba ? FbaCtxFor(fld.Arg0) : null;
        if (!devirt)
        {
            recv = LowerExpr(fld.Arg0);
            if (recv.Type.Unqualified is not CType.Allocator) { return false; }   // not an allocator → generic dispatch
        }

        switch (methodName)
        {
            case "alloc":
            case "alignedAlloc":   // (type, comptime alignment, count) → Error![]align(a) T
            {
                // `alignedAlloc` carries an extra comptime alignment arg between the type and count.
                // dotcc's alignment model caps at 16 (ZigAlloc.AlignOf) and satisfies that by
                // construction on the C heap / arena while the FBA aligns up to it, so the requested
                // alignment is not threaded through — it is honored up to dotcc's natural alignment and
                // over-alignment (> the element's, or > 16) is the same documented cut as elsewhere.
                // The alignment arg is dropped (it is comptime and has no runtime effect here).
                int wantArgs = methodName == "alignedAlloc" ? 3 : 2;
                if (argItems.Count != wantArgs)
                {
                    throw new IrUnsupportedException($"zig allocator `.{methodName}` expects ({(methodName == "alignedAlloc" ? "type, alignment, count" : "type, count")}); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                var count = LowerExpr(argItems[wantArgs - 1]);
                result = new AllocCall(recv, elem, count, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Slice(elem)),
                };
                return true;
            }
            case "dupe":   // (type, slice) → Error![]T: a fresh allocation holding a copy (std.mem.join's `dupe(u8, &[1]u8{0})`)
            {
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.dupe` expects (type, slice); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                var sliceType = new CType.Slice(elem);
                // At a `[]const T` sink, so C# infers Dupe's `T` from the argument (no Slice → ConstSlice step).
                var src = LowerExprSink(argItems[1], new CType.Slice(elem.WithQuals(TypeQual.Const)));
                // One runtime call allocates AND copies, so the source is evaluated once. It takes the allocator as a
                // value: a devirtualized receiver materializes, as it does at any opaque allocator sink.
                var allocator = recv
                    ?? (kind == AllocKind.Fba && fld.Arg0.Content is Zig.Ident { Arg0: var fbaTok }
                        ? MaterializeFba(_fbaAllocatorSites[Tok(fbaTok)])
                        : MaterializeCHeap());
                result = new Call("ZigAlloc.Dupe", new List<CExpr> { allocator, src, OomLit() })
                {
                    Type = new CType.ErrorUnion(sliceType),
                };
                return true;
            }
            case "rawAlloc":    // (len, alignment, ret_addr) → ?[*]u8
            case "rawResize":   // (memory, alignment, new_len, ret_addr) → bool
            case "rawRemap":    // (memory, alignment, new_len, ret_addr) → ?[*]u8
            case "rawFree":     // (memory, alignment, ret_addr) → void
            {
                // The vtable's byte-level calls (std.Io.Writer.Allocating grows its buffer through them): one runtime
                // helper each, over the allocator as a value (a devirtualized receiver materializes).
                var bytes = new CType.Slice(CType.UChar);
                var alignmentType = new CType.Named(AlignmentTypeName);
                var (helper, paramTypes, resultType) = methodName switch
                {
                    "rawAlloc" => ("ZigAlloc.RawAlloc", new[] { CType.ULong, alignmentType, CType.ULong }, (CType)new CType.Pointer(CType.UChar)),
                    "rawResize" => ("ZigAlloc.RawResize", new[] { bytes, alignmentType, CType.ULong, CType.ULong }, CType.Bool),
                    "rawRemap" => ("ZigAlloc.RawRemap", new[] { bytes, alignmentType, CType.ULong, CType.ULong }, new CType.Pointer(CType.UChar)),
                    _ => ("ZigAlloc.RawFree", new[] { bytes, alignmentType, CType.ULong }, CType.Void),
                };
                if (argItems.Count != paramTypes.Length)
                {
                    throw new IrUnsupportedException($"zig allocator `.{methodName}` expects {paramTypes.Length} argument(s); got {argItems.Count}");
                }
                var allocatorValue = recv
                    ?? (kind == AllocKind.Fba && fld.Arg0.Content is Zig.Ident { Arg0: var rawFbaTok }
                        ? MaterializeFba(_fbaAllocatorSites[Tok(rawFbaTok)])
                        : MaterializeCHeap());
                var rawArgs = new List<CExpr> { allocatorValue };
                for (var i = 0; i < paramTypes.Length; i++) { rawArgs.Add(LowerExprSink(argItems[i], paramTypes[i])); }
                result = new Call(helper, rawArgs) { Type = resultType };
                return true;
            }
            case "free":
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.free` expects (slice); got {argItems.Count} argument(s)");
                }
                var sliceExpr = LowerExpr(argItems[0]);
                if (sliceExpr.Type.Unqualified is not CType.Slice slc)
                {
                    throw new IrUnsupportedException($"zig allocator `.free` expects a slice argument, got {sliceExpr.Type.Describe()}");
                }
                result = new FreeCall(recv, sliceExpr, slc.Element, fbaCtx) { Type = CType.Void };
                return true;
            }
            case "create":   // single-object alloc → Error!*T (Milestone U)
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.create` expects (type); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                // `Error!*T` is represented `ErrorUnion(Pointer(T))` at the IR-type level (so `try`
                // unwraps to a `*T`); the runtime carrier is `ErrUnion<nuint>` (a pointer can't be
                // an ErrUnion<T> generic arg). The `try` lowering casts the unwrapped nuint to T*.
                result = new CreateCall(recv, elem, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Pointer(elem)),
                };
                return true;
            }
            case "destroy":   // free a single object from `.create` (Milestone U)
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.destroy` expects (pointer); got {argItems.Count} argument(s)");
                }
                var ptrExpr = LowerExpr(argItems[0]);
                if (ptrExpr.Type.Unqualified is not CType.Pointer pp)
                {
                    throw new IrUnsupportedException($"zig allocator `.destroy` expects a pointer argument, got {ptrExpr.Type.Describe()}");
                }
                result = new DestroyCall(recv, ptrExpr, pp.Pointee, fbaCtx) { Type = CType.Void };
                return true;
            }
            case "realloc":   // grow/shrink a slice → Error![]T (Milestone U)
            {
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.realloc` expects (slice, new count); got {argItems.Count} argument(s)");
                }
                var oldSlice = LowerExpr(argItems[0]);
                if (oldSlice.Type.Unqualified is not CType.Slice rslc)
                {
                    throw new IrUnsupportedException($"zig allocator `.realloc` expects a slice argument, got {oldSlice.Type.Describe()}");
                }
                var newCount = LowerExpr(argItems[1]);
                result = new ReallocCall(recv, oldSlice, rslc.Element, newCount, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Slice(rslc.Element)),
                };
                return true;
            }
            case "resize":   // in-place resize → bool
            case "remap":    // resize-possibly-moving → ?[]T
            {
                // `resize` returns whether the block grew/shrank IN PLACE (no move); `remap` returns
                // the possibly-moved slice or null. Two live forks: a PROVABLE FBA site devirtualizes
                // (fbaCtx set — ZigAlloc.ResizeFba/RemapFba); an opaque Allocator dispatches through the
                // vtable (recv set — recv.Resize/Remap, whose answer is whatever the concrete runtime
                // allocator gives). Only the C-heap DEVIRT case (both null — `std.heap.page_allocator`
                // resolved statically) stays deferred: its in-place result is page-dependent (real zig
                // answers from malloc_usable_size / page rounding), so a devirtualized guess would
                // diverge from the oracle — use `.realloc` there (which always works).
                if (fbaCtx is null && recv is null)
                {
                    throw new IrUnsupportedException(
                        $"zig allocator `.{methodName}` on the statically-known C-heap default is deferred "
                        + "(the in-place page/malloc_usable_size result would diverge from real zig); "
                        + "use `.realloc`, or route through an opaque `std.mem.Allocator` / a FixedBufferAllocator");
                }
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.{methodName}` expects (slice, new count); got {argItems.Count} argument(s)");
                }
                var oldSlice = LowerExpr(argItems[0]);
                if (oldSlice.Type.Unqualified is not CType.Slice sl)
                {
                    throw new IrUnsupportedException($"zig allocator `.{methodName}` expects a slice argument, got {oldSlice.Type.Describe()}");
                }
                var newCount = LowerExpr(argItems[1]);
                result = methodName == "resize"
                    ? new ResizeCall(recv, oldSlice, sl.Element, newCount, fbaCtx) { Type = CType.Bool }
                    : new RemapCall(recv, oldSlice, sl.Element, newCount, fbaCtx) { Type = new CType.Optional(new CType.Slice(sl.Element)) };
                return true;
            }
            default:   // unreachable: the caller only dispatches the allocator method names above
                return false;
        }
    }

    /// <summary>Build the <c>&amp;fba</c> context for a devirtualized FBA-site allocator call
    /// (Milestone U). <paramref name="allocExpr"/> is the <c>a</c> identifier bound to an
    /// <c>fba.allocator()</c> site in <see cref="_fbaAllocatorSites"/> (the kind is
    /// <see cref="AllocKind.Fba"/>, so it is necessarily a <see cref="Zig.Ident"/>).</summary>
    private CExpr FbaCtxFor(Item allocExpr)
    {
        var fbaSym = _fbaAllocatorSites[Tok(((Zig.Ident)allocExpr.Content!).Arg0)];
        fbaSym.AddressTaken = true;
        // IsLValue=true so the backend takes `&fba` directly — without it, `&<rvalue>` would
        // materialize a COPY of `fba` per call (`__clN = fba; &__clN`), so the bump cursor would
        // not be shared across allocations.
        var fbaRef = new VarRef(fbaSym) { Type = fbaSym.Type, IsLValue = true };
        return new Unary(UnOp.AddrOf, fbaRef) { Type = new CType.Pointer(fbaSym.Type) };
    }

    /// <summary>Lower <c>std.heap.FixedBufferAllocator.init(&amp;buf)</c> (Milestone F) to
    /// <c>FixedBufferAllocator.Init(bytePtr, capacity)</c>. <paramref name="argItems"/> is the
    /// single <c>&amp;buf</c> argument, where <c>buf</c> is a <c>[N]T</c> array local — which
    /// already lowers to a stackalloc'd pointer, so the buffer pointer is the array value itself
    /// (cast to <c>byte*</c>) and the capacity is <c>N * sizeof(T)</c> bytes.</summary>
    private CExpr LowerFbaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `FixedBufferAllocator.init` expects (buffer); got {argItems.Count} argument(s)");
        }
        // Accept `&buf` (the idiom) or a bare `buf`; either way the array local is the byte run.
        var inner = argItems[0].Content is Zig.PreAddrOf ad ? ad.Arg1 : argItems[0];
        var buf = LowerExpr(inner);
        if (buf.Type.Unqualified is not CType.Array a || a.Count is not int n)
        {
            throw new IrUnsupportedException(
                "zig `FixedBufferAllocator.init` expects `&buf` where buf is a fixed-size `[N]T` array local");
        }
        var bytePtr = new Cast(new CType.Pointer(CType.UChar), buf) { Type = new CType.Pointer(CType.UChar) };
        long bytes = (long)n * a.Element.Unqualified.SizeOf;
        var cap = new LitInt(bytes.ToString(CultureInfo.InvariantCulture), bytes) { Type = CType.ULong };
        return new Call("FixedBufferAllocator.Init", new List<CExpr> { bytePtr, cap },
            new List<CType> { new CType.Pointer(CType.UChar), CType.ULong }, null) { Type = new CType.Named(FbaTypeName) };
    }

    /// <summary>Lower <c>std.heap.ArenaAllocator.init(backing)</c> (Milestone U) to
    /// <c>ArenaAllocator.Init(backing)</c>. The single argument is the backing
    /// <c>std.mem.Allocator</c> — the statically-known default materializes a runtime C-heap
    /// <see cref="CType.Allocator"/> through the ordinary value path (so the arena draws its chunks
    /// from a real allocator); an opaque allocator value is taken as-is.</summary>
    private CExpr LowerArenaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `ArenaAllocator.init` expects (backing allocator); got {argItems.Count} argument(s)");
        }
        var backing = LowerExpr(argItems[0]);
        if (backing.Type.Unqualified is not CType.Allocator)
        {
            throw new IrUnsupportedException(
                $"zig `ArenaAllocator.init` expects a `std.mem.Allocator` backing, got {backing.Type.Describe()}");
        }
        return new Call("ArenaAllocator.Init", new List<CExpr> { backing },
            new List<CType> { new CType.Allocator() }, null) { Type = new CType.Named(ArenaTypeName) };
    }

    /// <summary>Resolve the container name a receiver expression's type names — a
    /// <see cref="CType.Named"/> struct/union or a <see cref="CType.Enum"/>, as a value or a pointer
    /// to one (<c>Point</c> / <c>*Point</c> / <c>Color</c> / <c>*Color</c>) — for instance-method
    /// dispatch.</summary>
    private static bool TryContainerName(CType t, out string name)
    {
        var u = t.Unqualified;
        if (u is CType.Pointer p) { u = p.Pointee.Unqualified; }
        switch (u)
        {
            case CType.Named n: name = n.Name; return true;
            case CType.Enum e:  name = e.Name; return true;
            default: name = ""; return false;
        }
    }

    /// <summary>The container name a type names for static-call (<c>Type.func(…)</c>) dispatch — a
    /// struct/union (<see cref="CType.Named"/>) or an enum (<see cref="CType.Enum"/>); null for any
    /// other type.</summary>
    private static string? ContainerTypeName(CType t) => t.Unqualified switch
    {
        CType.Named n => n.Name,
        CType.Enum e  => e.Name,
        _ => null,
    };

    /// <summary>Adjust an instance-method receiver to the method's declared first-parameter form
    /// (Zig UFCS auto-ref/deref): a value receiver to a <c>*Self</c> method takes its address (a
    /// var/param operand is marked address-taken; a non-lvalue is materialized to a temp by the
    /// backend's <c>&amp;rvalue</c> rule); a pointer receiver to a value-<c>Self</c> method is
    /// dereferenced; matching forms (both pointer or both value) pass through unchanged.</summary>
    private static CExpr AdjustReceiver(CExpr recv, CType paramType)
    {
        var paramIsPtr = paramType.Unqualified is CType.Pointer;
        var recvIsPtr = recv.Type.Unqualified is CType.Pointer;
        if (paramIsPtr && !recvIsPtr)
        {
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            return new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
        }
        if (!paramIsPtr && recvIsPtr)
        {
            var pointee = ((CType.Pointer)recv.Type.Unqualified).Pointee;
            return new Unary(UnOp.Deref, recv) { Type = pointee, IsLValue = true };
        }
        return recv;
    }

    /// <summary>Lower a binary op, synthesizing the result type the way the C# backend
    /// will treat it: usual-arithmetic for arithmetic/bitwise, the promoted left type
    /// for a shift (operands promote independently), and <c>int</c> for a relational /
    /// boolean (the backend renders those as an integer-valued <c>(CBool)(…)</c>).</summary>
    /// <summary>zig's rule for <c>/</c> and <c>%</c> (task #98): on a SIGNED integer (and, for <c>%</c>, a float) whose
    /// operands are not both comptime-known the operator is ambiguous about rounding, so zig rejects it ("signed integers
    /// must use @divTrunc, @divFloor, or @divExact"; "... must use @rem or @mod"). An unsigned operand, a comptime_int, a
    /// comptime context and two comptime-known operands are all fine. Returns the division unchanged when legal.</summary>
    private CExpr RejectRuntimeSignedDivision(CExpr division)
    {
        if (_comptimeDepth > 0 || division is not Binary { Op: BinOp.Div or BinOp.Mod } b) { return division; }
        CheckZigDivision(b.Op, b.Left, b.Right);
        return division;
    }

    /// <summary>The check behind <see cref="RejectRuntimeSignedDivision"/>, for a binary or a compound <c>/=</c> /
    /// <c>%=</c>: the peer type is the typed operand's (a literal yields to its peer, as in zig).</summary>
    private void CheckZigDivision(BinOp op, CExpr left, CExpr right)
    {
        if (_comptimeDepth > 0) { return; }
        var peer = (left is LitInt && right is not LitInt ? right.Type : left.Type).Unqualified;
        var signedInt = peer is CType.Prim { Integer: true, Signed: true, IsComptimeInt: false };
        var isFloat = peer is CType.Prim { Integer: false } && peer != CType.Bool;
        if (!signedInt && !(op == BinOp.Mod && isFloat)) { return; }
        bool Known(CExpr e) => e is ComptimeFold || _ir.ConstEval(e) is not null;
        if (Known(left) && Known(right)) { return; }
        throw new CompileException(op == BinOp.Div
            ? $"zig: division with '{peer.Describe()}' operands: signed integers must use @divTrunc, @divFloor, or @divExact"
            : $"zig: remainder division with '{peer.Describe()}' operands: signed integers and floats must use @rem or @mod");
    }

    private CExpr Bin(BinOp op, Item l, Item r)
    {
        // `<comptime tag> == .member` (road-to-zig-std S5) — `@typeInfo(T).int.signedness == .unsigned`
        // and friends fold to a boolean literal here, before either side is lowered: the tag has no
        // runtime value to compare, and folding is what lets the surrounding comptime `if` prune.
        if (op is BinOp.Eq or BinOp.Ne && TryFoldComptimeTagCompare(l, r, op == BinOp.Ne, out var tagCmp))
        {
            return tagCmp;
        }
        // A TYPE comparison anywhere a value is wanted, not only as an `if` condition
        // (`std.debug.assert(T == f16 or T == f32 or T == f64)` in std.fmt.parse_float): types have no runtime value.
        if (op is BinOp.Eq or BinOp.Ne && TryFoldTypeEquality(l, r) is { } typesEqual)
        {
            return new LitBool(op == BinOp.Eq ? typesEqual : !typesEqual) { Type = CType.Bool };
        }
        // `==` / `!=` may compare an enum value against a bare `.member` literal (`self == .red`),
        // which Zig result-locates against the other operand's enum type — so those two operands
        // get the enum-aware lowering; everything else lowers both sides plainly.
        var (left, right) = op is BinOp.Eq or BinOp.Ne
            ? LowerComparisonOperands(l, r)
            // A shift amount is a result location (zig types it `Log2Int(T)`): `1 << @intCast(i)` infers a cast there.
            : op is BinOp.Shl or BinOp.Shr && r.Content is Zig.BuiltinCall { Arg0: var shiftCast }
                && Tok(shiftCast) is "@intCast" or "@truncate"
                ? (LowerExpr(l), LowerExprSink(r, CType.Int))
                : (LowerExpr(l), LowerExpr(r));
        // An operator over a SIMD vector is element-wise, a comparison a lane mask (T5).
        if (TryVectorBinary(op, left, right) is { } vectorOp) { return vectorOp; }
        // `1 << 52` (std.fmt.parse_float's `1 << (1 + fractional_bits)`, FloatInfo's `2 << 52`): an untyped literal is a
        // comptime_int, which is unbounded, but its C# `int` would shift by the count MOD 32 (`1 << 52` == `1 << 20`, a
        // silent wrong answer). It widens to the comptime_int carrier, unless the count is a constant below 31 (the result
        // then fits a positive `int` exactly).
        if (op is BinOp.Shl && left is LitInt && left.Type.Unqualified == CType.Int
            && !(_ir.ConstEval(right) is { } shiftCount && shiftCount is >= 0 and < 31))
        {
            left = left with { Type = CType.ComptimeInt };
        }
        // Pointer arithmetic on a Zig many-item pointer (`[*]T` / `[*c]T`, both lowered to
        // `CType.Pointer`): `p + i` / `p - i` yields the pointer type, and `p - q` yields a
        // signed offset (`long`). `UsualArithmetic` only knows `Prim`s — it returns `int` for a
        // pointer operand — so handle the pointer cases here, mirroring the C frontend's
        // `IrBuilder.BinaryType`. (Zig fixed arrays are values and don't decay in arithmetic, so
        // only `CType.Pointer` participates; you slice an array before pointer-walking it.)
        RejectUnrepresentableComptimeOperand(op, l, r, left, right);
        if (TryFoldWideComptimeInt(op, l, r, left, right) is { } wide) { return wide; }
        var lPtr = left.Type.Unqualified is CType.Pointer;
        var rPtr = right.Type.Unqualified is CType.Pointer;
        var type = op switch
        {
            BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
                or BinOp.LogAnd or BinOp.LogOr => CType.Int,
            BinOp.Shl or BinOp.Shr => CType.IntegerPromote(left.Type),
            BinOp.Add or BinOp.Sub when lPtr || rPtr
                => lPtr && rPtr ? CType.Long : (lPtr ? left.Type.Unqualified : right.Type.Unqualified),
            _ => CType.UsualArithmetic(left.Type, right.Type),
        };
        return new Binary(op, left, right) { Type = type };
    }

    /// <summary>comptime_int arithmetic that leaves the range its lowered carrier holds (task #83): <c>std.math.maxInt(usize) + 1</c>
    /// is 2^64 in zig, but its folded operand is a <c>ulong</c> literal, so the C# sum wrapped (or C# rejected the constant,
    /// CS0220). When both operands are comptime_int values (an untyped literal, an untyped comptime local, a comptime-only
    /// call's fold, or such an expression) and the exact 128-bit result does not fit the ordinary result type, the
    /// expression is that result as a comptime_int literal. An in-range result is left to the ordinary lowering.</summary>
    private CExpr? TryFoldWideComptimeInt(BinOp op, Item l, Item r, CExpr left, CExpr right)
    {
        if (op is not (BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Shl or BinOp.BitOr or BinOp.BitXor or BinOp.BitAnd))
        {
            return null;
        }
        if (!IsComptimeIntOperand(l, left) || !IsComptimeIntOperand(r, right)) { return null; }
        if (_ir.ConstEval128(left) is not { } lhs || _ir.ConstEval128(right) is not { } rhs) { return null; }
        System.Int128 value;
        try
        {
            value = op switch
            {
                BinOp.Add => checked(lhs + rhs),
                BinOp.Sub => checked(lhs - rhs),
                BinOp.Mul => checked(lhs * rhs),
                BinOp.Shl => rhs >= 0 && rhs < 127 ? checked(lhs * (System.Int128.One << (int)rhs)) : throw new System.OverflowException(),
                BinOp.BitOr => lhs | rhs,
                BinOp.BitXor => lhs ^ rhs,
                _ => lhs & rhs,
            };
        }
        catch (System.OverflowException) { return null; }   // beyond 128 bits: left to the ordinary lowering (and its loud cuts)
        var ordinary = CType.UsualArithmetic(left.Type, right.Type);
        var fits = ordinary.Unqualified switch
        {
            CType.Prim { Integer: true, IsComptimeInt: true } => true,
            CType.Prim { Integer: true, Bytes: var b and < 16, Signed: var sgn } => sgn
                ? value >= -(System.Int128.One << (b * 8 - 1)) && value < (System.Int128.One << (b * 8 - 1))
                : value >= 0 && value < (System.Int128.One << (b * 8)),
            _ => true,
        };
        if (fits) { return null; }
        var magnitude = System.Int128.Abs(value).ToString(CultureInfo.InvariantCulture);
        CExpr lit = new LitInt(magnitude, value >= long.MinValue && value <= long.MaxValue ? (long)value : null) { Type = CType.ComptimeInt };
        return value < 0 ? new Unary(UnOp.Neg, lit) { Type = CType.ComptimeInt } : lit;
    }

    /// <summary>True for an operand that is a comptime_int VALUE rather than a typed one: an untyped integer literal, an
    /// untyped comptime local, a comptime-only call's fold (a call that lowered to a literal), or anything already typed
    /// comptime_int.</summary>
    private bool IsComptimeIntOperand(Item item, CExpr lowered)
    {
        while (item.Content is Zig.Grouped g) { item = g.Arg1; }
        if (lowered.Type?.Unqualified is CType.Prim { IsComptimeInt: true }) { return true; }
        return item.Content switch
        {
            Zig.IntLit => true,
            Zig.Ident id => _symbols.Resolve(Tok(id.Arg0)) is { } sym && _comptimeIntLocals.Contains(sym),
            Zig.CallArgs or Zig.CallNoArgs => lowered is LitInt or ComptimeFold,
            _ => false,
        };
    }

    /// <summary>Reject, as zig does, a comptime-known operand its operator's type cannot hold (task #91): a literal shift
    /// amount that does not fit <c>Log2Int</c> of the shifted operand (<c>~@as(u64, 0) &gt;&gt; 88</c>: "type 'u6' cannot represent
    /// integer value '88'"), and an integer literal outside the range of its typed peer (<c>@intFromBool(b) * 10</c>: "type
    /// 'u1' cannot represent integer value '10'"). Only a width dotcc knows from the source is checked, so a comptime_int
    /// operand (unbounded) is never rejected.</summary>
    private void RejectUnrepresentableComptimeOperand(BinOp op, Item l, Item r, CExpr left, CExpr right)
    {
        if (op is BinOp.Shl or BinOp.Shr)
        {
            // Only a LITERAL amount is checked: a typed one (std.math.rotl's `1 +% ~ar`, `ar: Log2Int(T)`) fits by
            // construction, and dotcc folds its constant at the carrier's width rather than the declared one.
            var amountItem = r;
            while (amountItem.Content is Zig.Grouped ga) { amountItem = ga.Arg1; }
            if (left.Type.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false } shifted
                || amountItem.Content is not Zig.IntLit
                || _ir.ConstEval(right) is not { } amount)
            {
                return;
            }
            // A literal / comptime-known left side is comptime_int unless the source spells its width (`@as(u64, 0)`).
            var bits = DeclaredBitsOfValue(l) ?? (_ir.ConstEval(left) is null ? DeclaredBitsOfLowered(left) ?? shifted.Bytes * 8 : null);
            if (bits is not { } width || width <= 0) { return; }
            var log2 = width <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(width - 1));
            if (amount < 0 || amount >= width)
            {
                throw new CompileException(
                    $"zig: type 'u{log2}' cannot represent integer value '{amount}' (a shift of a {width}-bit operand takes an amount below {width})");
            }
            return;
        }
        if (op is not (BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Mod or BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor))
        {
            return;
        }
        foreach (var (literalItem, literal, peerItem, peer) in new[] { (r, right, l, left), (l, left, r, right) })
        {
            if (literalItem.Content is not Zig.IntLit || _ir.ConstEval128(literal) is not { } value
                || peer.Type.Unqualified is not CType.Prim { Integer: true, IsComptimeInt: false } peerPrim
                || DeclaredBitsOfValue(peerItem) is not { } peerBits || peerBits is <= 0 or > 64)
            {
                continue;
            }
            // `@intFromBool` is a `u1` and a `@clz` / `@ctz` / `@popCount` count a `Log2IntCeil(T)`, whatever carrier they lower to.
            var signed = peerPrim.Signed && !(peerItem.Content is Zig.BuiltinCall { Arg0: var peerTok }
                                              && Tok(peerTok) is "@intFromBool" or "@clz" or "@ctz" or "@popCount");
            var (min, max) = signed
                ? (-(System.Int128.One << (peerBits - 1)), (System.Int128.One << (peerBits - 1)) - 1)
                : (System.Int128.Zero, (System.Int128.One << peerBits) - 1);
            if (value < min || value > max)
            {
                throw new CompileException(
                    $"zig: type '{(signed ? "i" : "u")}{peerBits}' cannot represent integer value '{value}'");
            }
        }
    }

    /// <summary>Lower a Zig WRAPPING arithmetic operator (<c>+%</c>/<c>-%</c>/<c>*%</c>) —
    /// two's-complement arithmetic that wraps at the OPERAND width. Zig has no integer promotion,
    /// so the result type is the peer-resolved operand type (<see cref="PeerIntType"/>), and the
    /// wrap happens at that width. The emitted C# runs in the project's default <c>unchecked</c>
    /// context, where a narrowing cast truncates rather than throwing. For a sub-<c>int</c> peer
    /// width (<c>byte</c>/<c>short</c>/…) C# would promote the operands to <c>int</c> and so NOT
    /// wrap at the operand width — so a truncating <see cref="Cast"/> back to the peer type is
    /// inserted (correct even when the result is then widened: <c>u8 +% u8</c> wraps at 8 bits
    /// BEFORE any widening). At <c>int</c> and wider, native C# arithmetic already wraps at the
    /// right width, so no cast is needed.</summary>
    private CExpr WrapBin(BinOp op, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        CExpr inner = new Binary(op, left, right) { Type = t };
        // An arbitrary-width unsigned operand (`u3`, std.math.rotl's `1 +% ~ar` with `ar: Log2Int(u8)`) wraps at ITS width,
        // not its carrier's: 1 +% 6 is 7 in a u3 (task #97; it had been computed in C#'s int).
        if ((DeclaredBitsOfValue(l) ?? DeclaredBitsOfValue(r)) is { } bits) { inner = MaskToBits(inner, t, bits); }
        return t.SizeOf < 4 ? new Cast(t, inner) { Type = t } : inner;
    }

    /// <summary><paramref name="value"/> reduced to its low <paramref name="bits"/> bits, for an unsigned
    /// <paramref name="carrier"/> wider than that declared width (dotcc carries a <c>u3</c> in a byte); unchanged
    /// otherwise.</summary>
    private static CExpr MaskToBits(CExpr value, CType carrier, int bits)
    {
        if (carrier.Unqualified is not CType.Prim { Integer: true, Signed: false, Bytes: var bytes } || bits >= bytes * 8 || bits <= 0)
        {
            return value;
        }
        var mask = (1L << bits) - 1;
        var maskLit = new LitInt(mask.ToString(System.Globalization.CultureInfo.InvariantCulture), mask) { Type = CType.Int };
        return new Binary(BinOp.BitAnd, value, maskLit) { Type = CType.IntegerPromote(carrier) };
    }

    /// <summary>The fixed-width integer type a wrapping/saturating operator wraps (or saturates) at —
    /// Zig's peer-resolved operand type. Valid Zig gives both operands one shared type; a bare integer
    /// literal (a <c>comptime_int</c>, lowered to a <see cref="LitInt"/>) yields to its concrete-typed
    /// peer. With both concrete the wider wins (they are equal in valid Zig; ties resolve to the left).
    /// Two comptime literals have no fixed-width peer — Zig evaluates them at comptime (exact, then
    /// coerced to the result location, erroring if it overflows), so dotcc just picks the wider integer
    /// (a fit-checking comptime engine is out of scope; a non-fitting literal pair is already a Zig
    /// error, never round-trippable code).</summary>
    private static CType PeerIntType(CExpr left, CExpr right)
    {
        var lt = left.Type.Unqualified;
        var rt = right.Type.Unqualified;
        if (left is LitInt && right is not LitInt) return rt;
        if (right is LitInt && left is not LitInt) return lt;
        return lt.SizeOf >= rt.SizeOf ? lt : rt;
    }

    /// <summary>Lower a Zig SATURATING arithmetic operator (<c>+|</c>/<c>-|</c>/<c>*|</c>) to a
    /// <c>ZigMath.Sat{Add,Sub,Mul}&lt;T&gt;</c> call (<see cref="DotCC.Libc.ZigMath"/>) that clamps
    /// the true result to the operand type's range. Zig has no integer promotion, so the result
    /// type is the peer-resolved operand type (<see cref="PeerIntType"/>); both operands are coerced
    /// to it so C# infers the generic <c>T</c> and the runtime clamps at the right width. Unlike
    /// wrapping (a truncating cast in the unchecked context), a clamp has no native C# operator, so
    /// this routes through the spliced runtime.</summary>
    private CExpr SatBin(string helper, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        GuardNo128Saturation(t);
        var args = new List<CExpr> { CoerceToPeer(left, t), CoerceToPeer(right, t) };
        return new Call($"ZigMath.{helper}", args) { Type = t };
    }

    /// <summary>Reject a saturating op (<c>+|</c>/<c>-|</c>/<c>*|</c>) at a 128-bit operand width.
    /// <see cref="DotCC.Libc.ZigMath"/> clamps via an exact-in-128-bit accumulator, which a 16-byte
    /// operand would itself overflow — so it can't honor the saturation contract there. Wrapping
    /// (<c>+%</c>) and ordinary arithmetic on <c>i128</c>/<c>u128</c> are unaffected (native C#
    /// <c>Int128</c>/<c>UInt128</c>). A documented V1 cut.</summary>
    private static void GuardNo128Saturation(CType t)
    {
        if (t.Unqualified is CType.Prim { Integer: true, Bytes: >= 16 })
        {
            throw new IrUnsupportedException(
                "saturating arithmetic (+|/-|/*|) on a 128-bit integer is not supported — the exact " +
                "128-bit accumulator would itself overflow; use wrapping (+%) or clamp manually");
        }
    }

    /// <summary>Coerce a wrapping/saturating operand to the peer integer type, skipping the cast when
    /// it already has that type — so <c>i32 +| i32</c> emits <c>ZigMath.SatAdd(a, b)</c> with no
    /// redundant casts, while <c>u8 +| 5</c> casts the literal so C# infers <c>byte</c>.</summary>
    private static CExpr CoerceToPeer(CExpr e, CType t)
        => e.Type.Unqualified.Equals(t) ? e : new Cast(t, e) { Type = t };

    /// <summary>Lower a Zig SATURATING compound assignment (<c>x op|= y</c>). There is no native C#
    /// saturating compound operator, so it desugars to <c>target = ZigMath.Sat…(target, y)</c> at the
    /// LHS width. The lvalue is read on both sides; that is sound only when re-evaluating it has no
    /// side effects, so a non-repeatable target (an index/deref reached through a call) is a clear
    /// deferred error rather than a silent double-eval.</summary>
    private CStmt SatCompoundAssign(Item targetItem, string helper, Item valueItem)
        => RejectConstStore(targetItem) ?? new ExprStmt(SatCompoundAssignExpr(targetItem, helper, valueItem));

    /// <summary>The <c>Assign</c> CExpr for a saturating compound assignment <c>x op|= y</c>
    /// (<c>x = ZigMath.Sat…(x, y)</c>) — the core shared by the statement form (wrapped in an
    /// <see cref="ExprStmt"/>) and the <c>while (…) : (i +|= 1)</c> continue-expression.</summary>
    private CExpr SatCompoundAssignExpr(Item targetItem, string helper, Item valueItem)
    {
        var target = LowerExpr(targetItem);
        if (!IsRepeatableLValue(target))
        {
            throw new IrUnsupportedException(
                "a saturating compound assignment (`x op|= y`) to a target with side effects is not " +
                "supported yet — assign in two steps (`x = x op| y;`) with a simpler target");
        }
        GuardNo128Saturation(target.Type);
        var value = LowerExprSink(valueItem, target.Type);
        var call = new Call($"ZigMath.{helper}", new List<CExpr> { target, CoerceToPeer(value, target.Type) })
            { Type = target.Type };
        return new Assign(null, target, call) { Type = target.Type };
    }

    /// <summary>True when <paramref name="e"/> is an lvalue that can be re-evaluated without side
    /// effects — a variable / parameter, or a field / element / deref reached only through other
    /// repeatable sub-expressions and constants. A call anywhere makes it non-repeatable. Gates the
    /// double-read in <see cref="SatCompoundAssign"/>, whose desugar has no single-eval form.</summary>
    private static bool IsRepeatableLValue(CExpr e) => e switch
    {
        VarRef => true,
        LitInt or LitBool or LitFloat => true,
        Paren p => IsRepeatableLValue(p.Inner),
        Cast c => IsRepeatableLValue(c.Operand),
        Member m => IsRepeatableLValue(m.Base),
        Unary { Op: UnOp.Deref or UnOp.AddrOf } u => IsRepeatableLValue(u.Operand),
        DotCC.Ir.Index ix => IsRepeatableLValue(ix.Base) && IsRepeatableLValue(ix.Idx),
        _ => false,
    };

    /// <summary>Lower the two operands of an <c>==</c> / <c>!=</c> comparison, result-locating a
    /// bare enum literal <c>.member</c> against the OTHER operand's enum type — Zig's
    /// <c>self == .red</c> (the idiomatic enum-method test). The concrete side is lowered first; if
    /// it is enum-typed, the <c>.member</c> resolves to that enum's tag constant. When neither side
    /// is a bare <c>.member</c> (or the concrete side isn't an enum), both lower normally — so a
    /// bare literal with no enum partner still hits <see cref="LowerExpr"/>'s loud rejection.</summary>
    private (CExpr left, CExpr right) LowerComparisonOperands(Item l, Item r)
    {
        if (r.Content is Zig.EnumLit rel && l.Content is not Zig.EnumLit)
        {
            var left = LowerExpr(l);
            return left.Type.Unqualified is CType.Enum en
                ? (left, ResolveEnumLit(Tok(rel.Arg1), en))
                : (left, LowerExpr(r));
        }
        if (l.Content is Zig.EnumLit lel && r.Content is not Zig.EnumLit)
        {
            var right = LowerExpr(r);
            return right.Type.Unqualified is CType.Enum en
                ? (ResolveEnumLit(Tok(lel.Arg1), en), right)
                : (LowerExpr(l), right);
        }
        return (LowerExpr(l), LowerExpr(r));
    }

    /// <summary>Lower a value-prefix unary op. <c>!x</c> yields an int (the backend
    /// renders it 0/1); <c>-x</c>/<c>~x</c> take the integer-promoted operand type.</summary>
    private CExpr Pre(UnOp op, Item operandItem)
    {
        var operand = LowerExpr(operandItem);
        // `~x` of an unsigned integer keeps x's type, as zig has no integer promotion: `~@as(u8, 1)` is 254, not C#'s
        // `int` -2, and a `u3` complement is masked to its three bits (std.math.rotl's `~ar`, task #97).
        if (op == UnOp.BitNot && operand.Type.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: < 4 } narrow)
        {
            CExpr complement = new Unary(op, operand) { Type = CType.IntegerPromote(narrow) };
            if ((DeclaredBitsOfValue(operandItem) ?? DeclaredBitsOfLowered(operand)) is { } bits) { complement = MaskToBits(complement, narrow, bits); }
            return new Cast(narrow, complement) { Type = narrow };
        }
        var type = op == UnOp.LogNot ? CType.Int : CType.IntegerPromote(operand.Type);
        return new Unary(op, operand) { Type = type };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr
    /// (so `&(x)` still marks `x` AddressTaken). Mirrors <c>IrBuilder.Unparen</c>.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    /// <summary>True for an expression with no side effects, safe to render more than once
    /// — the pointer <c>orelse</c> lowers to <c>a != null ? a : b</c>, naming <c>a</c>
    /// twice. Conservative: a var/param read, a literal, <c>null</c>, or a parenthesized
    /// such; anything else (a call, an assignment) is rejected to avoid double evaluation.</summary>
    private static bool IsSimpleReeval(CExpr e) => e switch
    {
        VarRef or NullPtr or LitInt or LitFloat => true,
        Paren p => IsSimpleReeval(p.Inner),
        _ => false,
    };

    /// <summary>True if the expression is a comptime-only numeric value — an int/float
    /// literal, or arithmetic over such (Zig's <c>comptime_int</c>/<c>comptime_float</c>).
    /// These have no fixed-size ABI type, so Zig forbids passing them to a C-variadic.
    /// The moment a concrete-typed leaf appears (identifier, call, <c>@as</c>, deref,
    /// index) the expression is typed and allowed across the variadic boundary.</summary>
    private static bool IsComptimeUntypedNumeric(Item it) => it.Content switch
    {
        Zig.IntLit or Zig.FloatLit => true,
        Zig.Grouped g   => IsComptimeUntypedNumeric(g.Arg1),
        Zig.PreNeg p    => IsComptimeUntypedNumeric(p.Arg1),
        Zig.PreBitNot p => IsComptimeUntypedNumeric(p.Arg1),
        Zig.Add a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Sub a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Mul a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.DivOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.ModOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitAnd a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitXor a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitOr a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shl a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shr a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        _ => false,
    };

}
