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
                    return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
                }
                // A bare (unqualified) sibling container const (Milestone R, part 6): inside a
                // container const's RHS re-lower (`_currentConstContainer` set), an unresolved name may
                // name a SIBLING const — inline it (comptime). Outside that, the unresolved error holds.
                if (_currentConstContainer is { } cc
                    && _containerConsts.TryGetValue(cc, out var sibs)
                    && sibs.TryGetValue(name, out var sib))
                {
                    return LowerContainerConst(cc, name, sib.typeItem, sib.rhs);
                }
                throw new IrUnsupportedException($"unresolved identifier '{name}'");
            }
            case Zig.Grouped g:
            {
                // A parenthesized value-position control-flow construct — `( if … )` / `( switch … )` /
                // `( blk: {…} )` / `( while/for … else … )` — used in a SUB-expression (Phase B of the
                // ANF milestone). It lowers to a STATEMENT filling a result temp (LowerValueIfSwitch /
                // LowerLabeledValueBlock / LowerLoopValue), so hoist that statement to the buffer and
                // evaluate to the temp. The sink is unknown here (the paren's use decides it), so the
                // temp type is inferred from the first branch/break value.
                if (IsValueControlFlowStmt(g.Arg1) || g.Arg1.Content is Zig.LabeledBlock)
                {
                    var savedImpure = _hoistImpureSeen;
                    Symbol? captured = null;
                    // The consumer captures the result temp and contributes nothing (an empty Seq) — the
                    // whole temp-filling statement goes to the buffer and the paren evaluates to the temp.
                    Func<Symbol, CStmt> cap = sym => { captured = sym; return new Seq(new List<CStmt>()); };
                    CStmt filled = g.Arg1.Content is Zig.LabeledBlock lbg
                        ? LowerLabeledValueBlock(Tok(lbg.Arg0), lbg.Arg2, null, cap)
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
                var then = LowerExpr(e.Arg4);
                return new CondExpr(LowerExpr(e.Arg2), then, LowerExpr(e.Arg6)) { Type = then.Type };
            }
            // A switch EXPRESSION reached with no result-location type (e.g. `x = switch(y){…}`
            // where the LHS type still flows in via LowerExprSink, or an inferred `const`). The
            // sink-carrying path is in LowerExprSink; here the arm types are inferred.
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, null);
            case Zig.SwitchExprTrailing s: return LowerSwitchExpr(s.Arg2, s.Arg5, null);

            // A labeled value-block in a pure-expression position (an if/switch-expression arm, a
            // binary sub-operand) — it produces a value via statements, which a C# expression can't
            // host, so it's supported only as a full `=` / `return` / assignment RHS (intercepted in
            // DeclOf / LowerReturn / StmtAssign before reaching here). A clear deferred error.
            case Zig.LabeledBlock lb:
                throw new IrUnsupportedException(
                    $"a labeled value-block (`{Tok(lb.Arg0)}: {{ … }}`) is supported only as a full initializer, " +
                    "`return`, or assignment right-hand side (including as a value-position if/switch branch there, " +
                    "Milestone Y part 1) — not inside a sub-expression yet");

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
            case Zig.DivOp a:   return Bin(BinOp.Div, a.Arg0, a.Arg2);
            case Zig.ModOp a:   return Bin(BinOp.Mod, a.Arg0, a.Arg2);
            // comparison (non-associative in the grammar)
            case Zig.CmpEq a:   return Bin(BinOp.Eq, a.Arg0, a.Arg2);
            case Zig.CmpNe a:   return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpLt a:   return Bin(BinOp.Lt, a.Arg0, a.Arg2);
            case Zig.CmpGt a:   return Bin(BinOp.Gt, a.Arg0, a.Arg2);
            case Zig.CmpLe a:   return Bin(BinOp.Le, a.Arg0, a.Arg2);
            case Zig.CmpGe a:   return Bin(BinOp.Ge, a.Arg0, a.Arg2);
            // boolean (short-circuit)
            case Zig.BoolOr a:  return Bin(BinOp.LogOr, a.Arg0, a.Arg2);
            case Zig.BoolAnd a: return Bin(BinOp.LogAnd, a.Arg0, a.Arg2);
            // bitwise / shift
            case Zig.BitAnd a:  return Bin(BinOp.BitAnd, a.Arg0, a.Arg2);
            case Zig.BitXor a:  return Bin(BinOp.BitXor, a.Arg0, a.Arg2);
            case Zig.BitOr a:   return Bin(BinOp.BitOr, a.Arg0, a.Arg2);
            case Zig.Shl a:     return Bin(BinOp.Shl, a.Arg0, a.Arg2);
            case Zig.Shr a:     return Bin(BinOp.Shr, a.Arg0, a.Arg2);
            // value prefix
            case Zig.PreNeg p:    return Pre(UnOp.Neg, p.Arg1);
            case Zig.PreBitNot p: return Pre(UnOp.BitNot, p.Arg1);
            case Zig.PreNot p:    return Pre(UnOp.LogNot, p.Arg1);
            // Address-of `&x` → a `*T` pointer. Mark a var/param operand AddressTaken so
            // the backend emits a moveable-variable pointer (mirrors IrBuilder.Un's
            // single-site rule). `try` still needs error unions (Milestone B).
            case Zig.PreAddrOf p:
            {
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
                return new Unary(UnOp.AddrOf, operand) { Type = new CType.Pointer(operand.Type) };
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
            // expression is lowered now, but wrapped in a deferred ComptimeFold and queued; it is
            // evaluated + spliced after pass 2 (so a `comptime fib(10)` sees its callee's lowered
            // body regardless of declaration order). The fold carries the inner expression's type.
            case Zig.PreComptime p:
            {
                var inner = LowerExpr(p.Arg1);
                var fold = new ComptimeFold(inner) { Type = inner.Type };
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
                var fieldName = Tok(fld.Arg2);
                // A dotted std path used as a VALUE (Milestone F): the C-heap default
                // (`std.heap.page_allocator`/`c_allocator`) materializes a runtime Allocator; a std
                // TYPE used as a value, or any unmodeled std path, errors. (A `.alloc(…)` /
                // `.init(…)` CALL never reaches here — the callee Field goes through LowerMethodCall.)
                if (TryResolveStdPath(expr, out var stdPath))
                {
                    // Both StdAllocatorValues rows are the C heap today, so a value use
                    // materializes the one runtime fat pointer; a distinct AllocKind row would
                    // switch on the kind here. A curated TYPE path used as a value gets the
                    // specific "type, not a value" message; the rest the registry-derived list.
                    if (StdAllocatorValues.ContainsKey(stdPath)) { return MaterializeCHeap(); }
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
                    return new LitInt(arrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), arrLen) { Type = CType.ULong };
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
                var baseExpr = LowerExpr(ix.Arg0);
                var idx = LowerExpr(ix.Arg2);
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

            // `null` — reuse the C null-pointer node (renders C# `null`, valid for BOTH a
            // pointer sink `T*` and a value-optional sink `T?`). In Zig `null` only appears
            // at a typed sink, so the backend's store-coercion gives it the right form.
            case Zig.NullLit: return new NullPtr { Type = new CType.Pointer(CType.Void) };

            // `undefined` — uninitialized storage. Without a sink we can only emit a zeroed
            // `default` (an over-approximation of Zig's "any value"; a correct program writes
            // before reading). At a typed sink LowerExprSink types it precisely; an array local
            // takes the dedicated stackalloc path in DeclOf.
            case Zig.UndefinedLit: return new DefaultLit { Type = CType.Int };

            // `a orelse b`. A value optional → C#'s `??` (single-eval LHS, lazy RHS) via
            // NullCoalesce. An optional POINTER → `a != null ? a : b` (C# `??` doesn't apply
            // to pointers); the LHS is named twice there, so a non-trivial (side-effecting)
            // left operand is rejected rather than silently double-evaluated. `orelse return`
            // (a noreturn RHS) isn't expressible in the grammar yet — that's Milestone B2.
            case Zig.OrElse o:
            {
                var left = LowerExpr(o.Arg0);
                var right = LowerExpr(o.Arg2);
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
            case Zig.CatchReturn or Zig.CatchReturnVoid or Zig.OrElseReturn or Zig.OrElseReturnVoid:
            {
                IsControlFlowFallback(expr, out var cfLhs, out var cfIsCatch, out var cfRet);
                var buf = RequireHoistable(cfIsCatch ? "catch return" : "orelse return");
                var savedImpure = _hoistImpureSeen;
                Symbol? anfSym = null;
                var cfStmt = LowerControlFlowFallback(cfLhs, cfIsCatch, cfRet, payload =>
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

            // call of a named function (bare-identifier callee).
            case Zig.CallArgs c:   return LowerCall(c.Arg0, c.Arg2);
            case Zig.CallNoArgs c: return LowerCall(c.Arg0, null);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
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
    {
        var argItems = argListItem is null ? new List<Item>() : Flatten(argListItem);

        // `base.method(args)` — a method (UFCS) or associated-function call.
        if (calleeItem.Content is Zig.Field fld)
        {
            return LowerMethodCall(fld, argItems);
        }

        if (calleeItem.Content is not Zig.Ident id)
        {
            throw new IrUnsupportedException("zig call: only a bare-identifier or `base.method` callee is lowered yet (got "
                + (calleeItem.Content?.GetType().Name ?? "null") + ")");
        }
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name)
            ?? throw new IrUnsupportedException($"call to unresolved name '{name}'");
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
    private CExpr BuildCall(Symbol sym, IReadOnlyList<Item> argItems, CExpr? receiver)
    {
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
            args.Add(LowerExprSink(argItems[i], paramSink));
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
    private CExpr LowerMethodCall(Zig.Field fld, IReadOnlyList<Item> argItems)
    {
        var methodName = Tok(fld.Arg2);

        // --- curated `std.mem` helpers (a static call on the std.mem namespace) ---
        // Routed before the generic dispatch. dotcc models no `std` in general — only this curated
        // set of the most common slice utilities; an unmodeled member is a clear, specific error.
        if (TryResolveStdPath(fld.Arg0, out var stdNs) && stdNs == "std.mem")
        {
            return LowerStdMemCall(methodName, argItems);
        }

        // --- curated `std.debug.print` (wall-plan W6) --- the biggest remaining std idiom. Like the
        // std.mem helpers it's a curated path, not a general std model; only `print` is modeled.
        if (TryResolveStdPath(fld.Arg0, out var stdDbg) && stdDbg == "std.debug")
        {
            return LowerStdDebugCall(methodName, argItems);
        }

        // --- curated `std.testing` assertions (`dotcc zig test`) --- `expect` / `expectEqual`, each
        // returning an error union so a `try` propagates a failing assertion to the test's boundary
        // (and thence to the generated test runner). A curated path, like the ones above.
        if (TryResolveStdPath(fld.Arg0, out var stdTest) && stdTest == "std.testing")
        {
            return LowerStdTestingCall(methodName, argItems);
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
        // `a.alloc(T, n)` / `a.free(s)` (and the deferred `create`/`destroy`) on a known-default
        // (→ devirt) or an Allocator-typed receiver (→ indirect). A same-named method on a
        // non-allocator receiver falls through to the generic dispatch below.
        if (methodName is "alloc" or "free" or "create" or "destroy" or "realloc" or "resize" or "remap"
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
            if (!_methods.TryGetValue(typeName, out var byType) || !byType.TryGetValue(methodName, out var staticSym))
            {
                throw new IrUnsupportedException($"'{typeName}' has no function '{methodName}'");
            }
            return BuildCall(staticSym, argItems, receiver: null);
        }

        // (B) `expr.method(args)` — the base is an instance of a container type.
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
        if (recv.Type.Unqualified is CType.ZigList listTy)
        {
            return LowerZigListCall(recv, listTy, methodName, argItems);
        }
        if (!TryContainerName(recv.Type, out var container))
        {
            throw new IrUnsupportedException(
                $"zig method call `.{methodName}()` needs a struct (or pointer-to-struct) receiver, got {recv.Type.Describe()}");
        }
        if (!_methods.TryGetValue(container, out var methods) || !methods.TryGetValue(methodName, out var msym))
        {
            throw new IrUnsupportedException($"struct '{container}' has no method '{methodName}'");
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
                // `&arr` / a `[N]T` value coerces to a slice exactly as at any other slice sink.
                var s = CoerceToSlice(LowerExpr(argItems[1]), new CType.Slice(elem));
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
            default:
                throw new IrUnsupportedException(
                    $"zig std.ArrayList has no modeled member '{methodName}' (curated: append, appendSlice, pop, deinit, clearRetainingCapacity, items, capacity)");
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
            {
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.alloc` expects (type, count); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                var count = LowerExpr(argItems[1]);
                result = new AllocCall(recv, elem, count, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Slice(elem)),
                };
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
            case "resize":   // in-place resize → bool (deferred — see below)
            case "remap":    // resize-possibly-moving → ?[]T (deferred — see below)
                // `resize` returns whether the block grew/shrank IN PLACE (no move); `remap` returns
                // the possibly-moved slice or null. Both outcomes are allocator-page-dependent (real
                // zig's page_allocator answers from page rounding), so matching the true/false / null
                // observably would need per-allocator in-place tracking dotcc doesn't model yet. Clear
                // deferred error rather than a divergent guess — use `.realloc` (which always works).
                throw new IrUnsupportedException(
                    $"zig allocator `.{methodName}` is deferred (its in-place / optional result is allocator-page-dependent); use `.realloc`");
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
    private CExpr Bin(BinOp op, Item l, Item r)
    {
        // `==` / `!=` may compare an enum value against a bare `.member` literal (`self == .red`),
        // which Zig result-locates against the other operand's enum type — so those two operands
        // get the enum-aware lowering; everything else lowers both sides plainly.
        var (left, right) = op is BinOp.Eq or BinOp.Ne
            ? LowerComparisonOperands(l, r)
            : (LowerExpr(l), LowerExpr(r));
        // Pointer arithmetic on a Zig many-item pointer (`[*]T` / `[*c]T`, both lowered to
        // `CType.Pointer`): `p + i` / `p - i` yields the pointer type, and `p - q` yields a
        // signed offset (`long`). `UsualArithmetic` only knows `Prim`s — it returns `int` for a
        // pointer operand — so handle the pointer cases here, mirroring the C frontend's
        // `IrBuilder.BinaryType`. (Zig fixed arrays are values and don't decay in arithmetic, so
        // only `CType.Pointer` participates; you slice an array before pointer-walking it.)
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
        var inner = new Binary(op, left, right) { Type = t };
        return t.SizeOf < 4 ? new Cast(t, inner) { Type = t } : inner;
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
        => new ExprStmt(SatCompoundAssignExpr(targetItem, helper, valueItem));

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
