#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // Expressions — paren-heavy to stay precedence-safe.
    public EmitContent Visit(C.Assign n)
    {
        // RHS is a value-context comma needing hoisting (Lua's `f->code =
        // luaM_newvectorchecked(…)`): lift its leading statements and make the
        // assignment itself the value. (The plain `lhs = value` form is used — the
        // Lua cases are pointer stores, no enum/atomic/volatile reconcile needed.)
        if (n.Arg2.Content is EmitContent.SeqExpr se)
        {
            return new EmitContent.SeqExpr(se.LeadingStmts,
                $"({T(n.Arg0)} = {se.ValueExpr})", TyOf(n.Arg0));
        }
        // An atomic lvalue stores seq-cst via Atomic.Store (which returns the stored
        // value, so `x = v` works as an rvalue too). Checked before volatile.
        if (ALValueOf(n.Arg0) is string alv)
        {
            var arhs = T(n.Arg2);
            if (EnumOf(n.Arg2) is not null) { arhs = $"(int)({arhs})"; }
            // Cast to the lvalue type so the generic Atomic.Store infers T and C's
            // int→uint/float conversions are honoured.
            var ct = (TyOf(n.Arg0) as CType.Sized)?.CsType ?? "int";
            return $"Atomic.Store(ref {alv}, ({ct})({arhs}))";
        }
        // A volatile lvalue stores through Volatile.Write(ref lv, rhs). (Volatile
        // is eligible-scalar only, never enum, so the enum reconcile below doesn't
        // apply — but still decay an enum *source*.) Volatile.Write returns void,
        // so this is a statement-position store; assignment-as-rvalue with a
        // volatile LHS isn't supported (documented).
        if (VLValueOf(n.Arg0) is string vlv)
        {
            var wrhs = T(n.Arg2);
            if (EnumOf(n.Arg2) is not null) { wrhs = $"(int)({wrhs})"; }
            return VolatileWriteOf(vlv, wrhs);
        }
        // `c = E`: an enum-typed lvalue takes an int→enum cast on a non-matching
        // source; a non-enum lvalue (`x = c`) decays an enum source to int. The
        // assignment expression's value carries the lvalue's enum type.
        var lhsEnum = EnumOf(n.Arg0);
        // A bare function-name rhs (`fp = hookf;`, a fn-ptr lvalue) decays to its
        // address. DecayFnName is a no-op for non-fn-name text, so the enum
        // reconcile below is unaffected.
        var rhs = DecayFnName(T(n.Arg2));
        var rhsEnum = EnumOf(n.Arg2);
        if (lhsEnum is not null)
        {
            if (rhsEnum != lhsEnum) { rhs = $"({lhsEnum})({rhs})"; }
            return new EmitContent.Text($"({T(n.Arg0)} = {rhs})", lhsEnum);
        }
        if (rhsEnum is not null) { rhs = $"(int)({rhs})"; }
        // A narrowing / sign-incompatible store into an integer lvalue takes the
        // C-conversion cast C# requires (`b = i;` where b is lu_byte) — and
        // -Wconversion flags a width-narrowing. Source type is the rhs's; an enum
        // rhs already decayed to int above, so its non-integer CType is skipped.
        else if (TyOf(n.Arg0) is CType.Sized lt)
        {
            rhs = CoerceStore(rhs, TyOf(n.Arg2), ConstOfItem(n.Arg2), lt.CsType, n.Arg0.Position.Line);
        }
        return $"({T(n.Arg0)} = {rhs})";
    }
    // `lhs OP= rhs`. C#'s enum compound-assign is unreliable (`enum |= int`,
    // `enum *= int` are errors), so when the lvalue is enum-typed we expand to
    // the explicit `lhs = (Enum)((int)lhs OP rhs)` form — the lvalue is assumed
    // side-effect-free, which holds for the simple variables C enum/flags code
    // uses. Otherwise keep `OP=`, decaying an enum rhs to int (`x += c` would be
    // `int += enum` → C# error).
    private string CompoundAssign(Item lhsIt, string op, Item rhsIt)
    {
        var lhs = T(lhsIt);
        var rhs = IntDecay(rhsIt);
        // Atomic lvalue: `lv op= rhs` → the seq-cst *Fetch helper returning the NEW
        // value (matching C's compound-assign expression value). Checked first.
        if (ALValueOf(lhsIt) is string alv)
        {
            var csType = (TyOf(lhsIt) as CType.Sized)?.CsType ?? "int";
            return AtomicCompound(alv, csType, op, rhs);
        }
        // Volatile lvalue: `lhs` is already the fenced read `Volatile.Read(ref lv)`,
        // so expand `lv OP= rhs` to a fenced read-modify-write.
        if (VLValueOf(lhsIt) is string vlv) { return VolatileWriteOf(vlv, $"{lhs} {op} {rhs}"); }
        if (EnumOf(lhsIt) is { } e) { return $"({lhs} = ({e})((int){lhs} {op} {rhs}))"; }
        return $"({lhs} {op}= {rhs})";
    }

    // `++x` / `x++` (and `--`) on a volatile lvalue → a fenced read-modify-write
    // `Volatile.Write(ref lv, Volatile.Read(ref lv) ± 1)` (`T(operand)` is already
    // the fenced read). Pre/post produce the same store (the expression VALUE
    // differs, but a volatile inc/dec is virtually always a statement — documented).
    // Returns null for a non-volatile operand so the caller keeps the plain form.
    private EmitContent? VolatileStep(Item operand, string op)
    {
        if (VLValueOf(operand) is string lv) { return VolatileWriteOf(lv, $"{T(operand)} {op} 1"); }
        return null;
    }
    public EmitContent Visit(C.AddAssign n) => CompoundAssign(n.Arg0, "+", n.Arg2);
    public EmitContent Visit(C.SubAssign n) => CompoundAssign(n.Arg0, "-", n.Arg2);
    public EmitContent Visit(C.MulAssign n) => CompoundAssign(n.Arg0, "*", n.Arg2);
    public EmitContent Visit(C.DivAssign n) => CompoundAssign(n.Arg0, "/", n.Arg2);
    public EmitContent Visit(C.ModAssign n) => CompoundAssign(n.Arg0, "%", n.Arg2);
    // Logical `||` and `&&` — wrap each operand with Cond.B so the C-truthy
    // conversion works for int / double / pointer AND bool (when the
    // operand is already a comparison result like `a == NULL`). The
    // previous `!= 0` form broke when an operand was bool because
    // `bool != 0` isn't a valid C# expression.
    // `&&` / `||` likewise yield C `int` 0/1 — same CBool wrap so the result is
    // usable as an int (`int flag = a && b;`). Operands keep their Cond.B truthy
    // conversion; the bool the C# `&&`/`||` produces is cast to CBool.
    public EmitContent Visit(C.Lor n) =>
        new EmitContent.Text($"((CBool)({CondOf(n.Arg0)} || {CondOf(n.Arg2)}))", Ty: new CType.Sized("int"));
    public EmitContent Visit(C.Land n) =>
        new EmitContent.Text($"((CBool)({CondOf(n.Arg0)} && {CondOf(n.Arg2)}))", Ty: new CType.Sized("int"));
    // Equality: same CBool wrap on the textual fallback. The setjmp path returns
    // a SetjmpCheckZero variant consumed directly by StmtIfElse (a conditional
    // context), so it must NOT be wrapped. Enum operands decay to int.
    public EmitContent Visit(C.Eq n) => MaybeSetjmpCompare(n.Arg0, n.Arg2, isEquals: true)
        ?? (EmitContent)RelText(n.Arg0, "==", n.Arg2);
    public EmitContent Visit(C.Neq n) => MaybeSetjmpCompare(n.Arg0, n.Arg2, isEquals: false)
        ?? (EmitContent)RelText(n.Arg0, "!=", n.Arg2);

    /// <summary>
    /// If one side of an <c>==</c>/<c>!=</c> comparison is a
    /// <see cref="EmitContent.SetjmpCall"/> and the other is the
    /// literal <c>0</c>, return a <see cref="EmitContent.SetjmpCheckZero"/>
    /// variant for the enclosing <c>StmtIfElse</c> to consume. Otherwise
    /// return null so the caller falls back to the normal textual emit.
    /// </summary>
    private static EmitContent? MaybeSetjmpCompare(Item left, Item right, bool isEquals)
    {
        if (left.Content is EmitContent.SetjmpCall lsj && IsLiteralZero(right))
        {
            return new EmitContent.SetjmpCheckZero(lsj.EnvName, TruthyOnFirstCall: isEquals);
        }
        if (right.Content is EmitContent.SetjmpCall rsj && IsLiteralZero(left))
        {
            return new EmitContent.SetjmpCheckZero(rsj.EnvName, TruthyOnFirstCall: isEquals);
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="it"/> is the literal integer <c>0</c>.
    /// Accepts the raw lexer token (NUM "0") and the visitor-produced
    /// <c>EmitContent.Text("0")</c>; rejects everything else including
    /// <c>0.0</c>, <c>(0)</c> (which would be a parenthesised primary),
    /// and any constant expression that happens to evaluate to zero.
    /// </summary>
    private static bool IsLiteralZero(Item it) => it.Content switch
    {
        EmitContent.Text { Value: "0" } => true,
        string s => s == "0",
        _ => false,
    };
    // Relational operators yield C `int` 0/1, NOT a bool — `int x = a > b;`,
    // `(a>0)+(b>0)`, `return a<b;` from an int function, and `printf("%d", a==b)`
    // are all legal C. C# `<`/`>`/… produce `bool`, which can't land in those
    // int positions, so we cast the result to `CBool` (the integer-typed _Bool
    // value): CBool→int carries it into arithmetic/assignment/args/return, and a
    // `Cond.B(CBool)` overload carries it into conditional positions. Nested
    // comparisons (`(a>b)==(c>d)`) resolve via CBool→int on both operands.
    public EmitContent Visit(C.Lt n) => RelText(n.Arg0, "<", n.Arg2);
    public EmitContent Visit(C.Gt n) => RelText(n.Arg0, ">", n.Arg2);
    public EmitContent Visit(C.Le n) => RelText(n.Arg0, "<=", n.Arg2);
    public EmitContent Visit(C.Ge n) => RelText(n.Arg0, ">=", n.Arg2);
    // A relational / equality operator yields C `int` 0/1 (CBool), with operands
    // reconciled per C's usual arithmetic conversions (so `size_t < int` doesn't
    // trip CS0034). The CBool result carries into int positions and Cond.B alike.
    private EmitContent.Text RelText(Item a, string op, Item b)
    {
        var (sa, sb, _) = ReconcileInt(a, b);
        // A bare function name as a comparison operand (`hook != hookf`) is a
        // function-to-pointer decay in C. C#'s `&fn` is a method-group address with
        // no type of its own, so it can't be compared to a `delegate*` directly —
        // cast it to the OTHER operand's fn-ptr type (`hook != (lua_Hook)(&hookf)`).
        // Tag the result `int`: C makes a comparison `int` 0/1, so the conversion
        // layer inserts the cast C# needs when it stores into a narrower/other
        // integer (`lu_byte f = a < b;` → `(byte)((CBool)…)` via CBool→int→byte).
        return new EmitContent.Text(
            $"((CBool)({DecayFnForCompare(a, sa, b)} {op} {DecayFnForCompare(b, sb, a)}))",
            Ty: new CType.Sized("int"));
    }

    /// <summary>
    /// If an operand's text <paramref name="s"/> decays to a function pointer
    /// (<c>&amp;fn</c>), give it a type by casting to <paramref name="other"/>'s
    /// fn-ptr CType — C#'s <c>&amp;method</c> is an untyped method-group address and
    /// can't be compared to a <c>delegate*</c> on its own. A non-fn-name operand
    /// passes through unchanged.
    /// </summary>
    private string DecayFnForCompare(Item operand, string s, Item other)
    {
        var decayed = DecayFnName(s);
        if (decayed != s && TyOf(other) is CType.Sized ot && IsFnPtrCsType(ot.CsType))
        {
            return $"({ot.CsType})({decayed})";
        }
        return decayed;
    }
    // Bitwise — same precedence and semantics in C# (binary `& | ^`, shifts, and
    // unary `~`). `& | ^` reconcile both operands per the usual arithmetic
    // conversions (a `ulong & int` mask would otherwise be CS0034); the result is
    // the common type. A SHIFT only reconciles its LEFT (value) operand — C# wants
    // the shift COUNT as `int`, so a wide/unsigned count is cast down (ShiftText).
    public EmitContent Visit(C.BOr n)  => BitText(n.Arg0, "|", n.Arg2);
    public EmitContent Visit(C.BXor n) => BitText(n.Arg0, "^", n.Arg2);
    public EmitContent Visit(C.BAnd n) => BitText(n.Arg0, "&", n.Arg2);
    public EmitContent Visit(C.Shl n)  => ShiftText(n.Arg0, "<<", n.Arg2);
    public EmitContent Visit(C.Shr n)  => ShiftText(n.Arg0, ">>", n.Arg2);
    private EmitContent BitText(Item a, string op, Item b)
    {
        var (sa, sb, ty) = ReconcileInt(a, b);
        // Fold a constant bitmask only when the result stays a signed ≤32-bit int
        // (so the int fold matches C — see IntFoldable), so `~(1 << TM)` etc. reach
        // the cast range-check; a wider/unsigned result carries no const.
        var fold = IntFoldable((ty as CType.Sized)?.CsType)
            ? FoldBinary(ConstOfItem(a), op, ConstOfItem(b)) : null;
        return new EmitContent.Text($"({sa} {op} {sb})", Ty: ty, ConstInt: fold,
            ConstExpr: IsConstExpr(a) && IsConstExpr(b));
    }
    // `v << n` / `v >> n` — C# requires the shift count to be `int` (or implicitly
    // convertible to it), so a `long` / `ulong` / `uint` / native count is cast to
    // `(int)` (CS0019 otherwise). The value operand keeps its type and width; the
    // result is the value operand's type.
    private EmitContent ShiftText(Item a, string op, Item b)
    {
        var count = IntDecay(b);
        if (IntOperandType(b) is string ct && ct != "int") { count = $"(int)({count})"; }
        // Fold `1 << K` (Lua's tag-bit masks) only when the value operand stays a
        // signed ≤32-bit int, so a folded mask reaches the cast range-check.
        var fold = IntFoldable(IntOperandType(a))
            ? FoldBinary(ConstOfItem(a), op, ConstOfItem(b)) : null;
        return new EmitContent.Text($"({IntDecay(a)} {op} {count})", Ty: TyOf(a), ConstInt: fold,
            ConstExpr: IsConstExpr(a) && IsConstExpr(b));
    }
    // `~c` folds to a (usually negative) constant — Lua's `cast_byte(~mask)` —
    // only when the operand is a signed ≤32-bit int (so the int fold matches C);
    // the folded value lets the enclosing cast wrap an out-of-range store in
    // `unchecked` (CS0221). A wider/unsigned operand (`~(size_t)0`) carries no const.
    public EmitContent Visit(C.BNot n)
    {
        var fold = ConstOfItem(n.Arg1) is int v && IntFoldable(IntOperandType(n.Arg1)) ? ~v : (int?)null;
        return new EmitContent.Text($"(~{IntDecay(n.Arg1)})", ConstInt: fold, ConstExpr: IsConstExpr(n.Arg1));
    }

    // Logical NOT: lower to `(Cond.B(E) ? 0 : 1)` so the result is int,
    // matching C's `!x` yielding 0 or 1 (never a bool). Cond.B picks the
    // right truthy overload based on E's type (int/double/pointer/bool).
    public EmitContent Visit(C.LNot n) =>
        new EmitContent.Text($"({CondOf(n.Arg1)} ? 0 : 1)", Ty: new CType.Sized("int"), ConstExpr: IsConstExpr(n.Arg1));

    // Ternary `c ? a : b` — Cond.B wraps the C-truthy condition. The two
    // branches need a common C# type; the user is responsible for keeping
    // them compatible (matches the C constraint that the branches share
    // arithmetic conversions).
    //
    // VOID-typed ternary: if a branch is void (a `(void)X` discard cast or a
    // void-typed nested ternary), the whole `?:` is void — C requires both arms
    // void then. C# can't express that as an expression (a void call / `(void)`
    // cast isn't a valid ternary arm), so lower to an `if`/`else` STATEMENT,
    // carried as a VoidCond for the statement/loop-body context to emit. Each arm
    // renders as a statement (a nested void ternary recurses; a comma / void-cast
    // expands its discard operands; anything else — typically a void call — becomes
    // `expr;`). Lua's GC write-barriers `(cond ? luaC_barrier_(…) : cast_void(0))`.
    public EmitContent Visit(C.Ternary n)
    {
        if (IsVoidBranch(n.Arg2) || IsVoidBranch(n.Arg4))
        {
            var ifStmt = $"if ({CondOf(n.Arg0)}) {VoidBranchStmt(n.Arg2)}"
                       + $"else {VoidBranchStmt(n.Arg4)}";
            return new EmitContent.VoidCond($"({CondOf(n.Arg0)} ? {T(n.Arg2)} : {T(n.Arg4)})", ifStmt);
        }
        // Tag the result with the arms' common integer type so the value flows into
        // a containing operator's usual-arithmetic reconcile (`x + (cond ? sizeof(S)
        // : 0)` — the ternary is int / size_t, not untyped). Non-integer arms leave
        // it untyped.
        return new EmitContent.Text(
            $"({CondOf(n.Arg0)} ? {T(n.Arg2)} : {T(n.Arg4)})", Ty: TernaryResultType(n.Arg2, n.Arg4));
    }

    // The common integer type of a ternary's two arms, for the type-synthesis
    // layer. Both arms integer → their usual-arithmetic common type. One arm a
    // known integer and the other an integer CONSTANT (`cond ? sizeof(S) : 0`) →
    // the typed arm's type (the constant converts to it). Otherwise null.
    private CType? TernaryResultType(Item a, Item b)
    {
        var ta = IntOperandType(a);
        var tb = IntOperandType(b);
        if (ta is not null && tb is not null)
        {
            return IntCommonType(ta, tb) is string c ? new CType.Sized(c) : null;
        }
        if (ta is not null && ConstOfItem(b) is not null) { return new CType.Sized(ta); }
        if (tb is not null && ConstOfItem(a) is not null) { return new CType.Sized(tb); }
        return null;
    }

    // A ternary arm is void when it's a `(void)X` discard cast or a void-typed
    // nested ternary (VoidCond) — both have no C# value.
    private static bool IsVoidBranch(Item it) =>
        it.Content is EmitContent.VoidCond
        || it.Content is EmitContent.Text { VoidCast: true };

    // Render a ternary arm as a C# statement block. A nested void ternary uses its
    // own if/else; a comma or `(void)X` cast expands its discard operands (each a
    // statement); anything else (e.g. a void-returning call) becomes `expr;`. The
    // block keeps a braceless enclosing `if`/`while`/`for` body well-formed.
    private string VoidBranchStmt(Item it)
    {
        if (it.Content is EmitContent.VoidCond vc) { return $"{{\n{IndentEach(vc.IfStatement)}}}\n"; }
        var stmts = CommaOpsOf(it) is { } ops
            ? string.Concat(ops.Select(o => $"{StripOuterParens(o)};\n"))
            : $"{StripOuterParens(T(it))};\n";
        return $"{{\n{IndentEach(stmts)}}}\n";
    }
    public EmitContent Visit(C.AndAssign n) => CompoundAssign(n.Arg0, "&", n.Arg2);
    public EmitContent Visit(C.OrAssign n)  => CompoundAssign(n.Arg0, "|", n.Arg2);
    public EmitContent Visit(C.XorAssign n) => CompoundAssign(n.Arg0, "^", n.Arg2);
    public EmitContent Visit(C.ShlAssign n) => CompoundAssign(n.Arg0, "<<", n.Arg2);
    public EmitContent Visit(C.ShrAssign n) => CompoundAssign(n.Arg0, ">>", n.Arg2);

    // Arithmetic — carries a folded ConstInt when both operands are constants, so
    // an integer-constant-expression position (e.g. `T a[sizeof(x)/sizeof(x[0])]`)
    // can use the literal value. ArithFold keeps the emitted text and tags the const.
    public EmitContent Visit(C.Add n) => ArithFold(n.Arg0, "+", n.Arg2);
    public EmitContent Visit(C.Sub n) => ArithFold(n.Arg0, "-", n.Arg2);
    public EmitContent Visit(C.Mul n) => ArithFold(n.Arg0, "*", n.Arg2);
    public EmitContent Visit(C.Div n) => ArithFold(n.Arg0, "/", n.Arg2);
    public EmitContent Visit(C.Mod n) => ArithFold(n.Arg0, "%", n.Arg2);
    public EmitContent Visit(C.Cast n)
    {
        var castType = T(n.Arg1);
        // A cast over a value-context comma needing hoisting — keep the SeqExpr,
        // wrapping the cast around its value (the leading statements ride along).
        if (n.Arg3.Content is EmitContent.SeqExpr cse)
        {
            return new EmitContent.SeqExpr(cse.LeadingStmts, $"({castType})({cse.ValueExpr})",
                new CType.Sized(castType));
        }
        // `(void)X` — a discard. C# has no void cast, and the value is thrown away,
        // so lower to a DISCARD: the operand's side effects, as statements. If X is
        // a comma (`(void)(save(), next())`, via the cast_void(save_and_next) idiom
        // in Lua's llex), each operand becomes a statement; a plain value becomes a
        // `_ = (X)` discard-assignment. Returned as a CommaSeq so an enclosing comma
        // flattens it (CommaOp splices a CommaSeq operand) and a statement / loop
        // controlling position emits the operands as statements. (`(void)voidcall()`
        // — a void value — would make `_ = …` invalid and fail loudly; rare.)
        if (castType.Trim() == "void")
        {
            // Discard operands: a comma's operands (all discarded — it's void), or
            // a `_ = (X)` discard-assignment for a plain value. Carried as CommaOps
            // on a Text so an enclosing paren/comma keeps them (CommaOp + Paren both
            // flatten via CommaOpsOf) and a statement / controlling position emits
            // them. The Value is the (invalid-in-C#) literal void cast: it's never
            // legitimately reached — a void expression can't be value-used in C — so
            // if some context did read it, Roslyn errors loudly rather than silently.
            // (void)fn — a bare function name cast to void is a C idiom to
            // suppress "unused" warnings. C# doesn't need it, and `_ = &fn`
            // can't infer the discard type (CS8183). Emit a comment-statement.
            var inner = T(n.Arg3);
            var decayed = DecayFnName(inner);
            var discardExpr = decayed != inner
                ? $"_ = (nint)0 /* {inner} */"   // fn-name: suppress via nint discard
                : $"_ = ({inner})";               // regular value discard
            var discard = CommaOpsOf(n.Arg3) is { } ops
                ? new List<string>(ops)
                : new List<string> { discardExpr };
            // Carry the discarded operand's CType (the `_ = (X)` form's type is X's).
            // When this `(void)X` is a comma operand reaching the value-tuple path, a
            // POINTER X (`(void)L`, `(void)(p->f)`) can't be a ValueTuple element, so
            // CommaTupleText nint-casts it — but only if it knows the operand's type.
            return new EmitContent.Text($"((void)({T(n.Arg3)}))", Ty: TyOf(n.Arg3),
                CommaOps: discard, VoidCast: true);
        }
        // `(S*)malloc(sizeof(S))` — propagate the MallocSizeof marker through a
        // matching pointer cast so the enclosing declaration can still recognise
        // it. The marker's low-level text grows to include the cast, so the
        // non-promoted path is byte-identical to before.
        if (n.Arg3.Content is EmitContent.MallocSizeof ms
            && castType.Replace(" ", "") == ms.StructType + "*")
        {
            // AlreadyCast: the low-level text now carries the `(S*)` cast, so the
            // decl-init reconcile must NOT add another (unlike the cast-less form).
            return new EmitContent.MallocSizeof(ms.StructType, $"(({castType}){ms.LowLevelText})", AlreadyCast: true);
        }
        // A cast to an enum type yields an enum value (C# allows int→enum and
        // enum→enum casts directly); tag it so downstream consumers reconcile.
        // Also carry the cast's type for sizeof (`sizeof((char)x)` == 1).
        // A cast of an integer CONSTANT to an integer type stays a constant
        // expression — keep the folded value so it can be an array bound (Lua's
        // `char space[cast_uint(LUA_IDSIZE + … )]` → `char space[(uint)(219)]`).
        // Resolve the target through typedefs first, so an aliased integer cast
        // (`lu_byte` → byte, `size_t` → ulong) is recognised too.
        var resolvedCast = ResolveTypedef(castType);
        var constInt = IsIntegerCsType(resolvedCast) ? ConstOfItem(n.Arg3) : null;
        var operandConst = IsConstExpr(n.Arg3);
        var castText = $"(({castType}){T(n.Arg3)})";
        // A constant cast OUT of the target integer type's range is a compile error
        // in C# (CS0221) unless wrapped in `unchecked` — Lua's `cast_byte(~mask)`
        // folds to a negative int, `(size_t)-1` to a huge unsigned, `cast_int(
        // MAX_SIZET/sizeof(t))` to a wide ulong. A runtime (non-constant) cast
        // truncates silently, so only a CONSTANT operand needs wrapping — and only
        // when it isn't provably in range (a fitting folded value stays bare, so
        // `(uint)(219)` keeps clean output). The fold misses uint-modular / ulong
        // values (ConstInt null), so the ConstExpr flag — true even then — gates it.
        if (IsIntegerCsType(resolvedCast) && operandConst
            && !(constInt is int cval && ConstFitsTarget(cval, resolvedCast)))
        {
            castText = $"unchecked({castText})";
        }
        return new EmitContent.Text(castText,
            _enumTags.Contains(castType) ? castType : null, new CType.Sized(castType),
            ConstInt: constInt, ConstExpr: operandConst);
    }

    // The C# integer types dotcc lowers C integer types to — a cast to one of
    // these preserves an integer-constant operand's compile-time value.
    private static bool IsIntegerCsType(string csType) => csType.Trim() switch
    {
        "byte" or "sbyte" or "short" or "ushort" or "int" or "uint"
            or "long" or "ulong" or "nint" or "nuint" => true,
        _ => false,
    };
    // `*p` / `p[i]` synthesize the element/pointee type for sizeof.
    public EmitContent Visit(C.Deref n)
    {
        var ty = TyOf(n.Arg1);
        // `*p` where p is a pointer-to-array (`int (*p)[3]`) is the pointed-to
        // ARRAY, which decays to the same flat pointer — so emit the base
        // unchanged (typed as the array), not a C# `*p` (which would deref to
        // the first element). `(*p)[i]` then strides like the array.
        if (ty is CType.PtrToArr pta) { return Typed(StripOuterParens(T(n.Arg1)), pta.Inner); }
        // `*fp` where fp is a FUNCTION pointer is a C no-op: the "function" value
        // decays straight back to the pointer (`(*fp)(args)` ≡ `fp(args)`). C#
        // function pointers are called directly and reject `*fp` (CS0193), so emit
        // the operand unchanged, keeping its fn-ptr type. A DATA pointer's `*p`
        // stays a real dereference below. Lua leans on `(*f)(L)` pervasively.
        if (ty is CType.Sized fp && IsFnPtrCsType(fp.CsType)) { return Typed(T(n.Arg1), ty); }
        // Pointer-to-volatile: `*p` is a volatile lvalue → fenced read, tagged for a
        // write-context parent (`*p = x` → Volatile.Write(ref *p, x)). Phase V2.
        if (VolatilePointeeOf(n.Arg1)) { return VolatileReadOf($"*{T(n.Arg1)}", null, ty?.ElementType()); }
        return Typed($"(*{T(n.Arg1)})", ty?.ElementType());
    }
    // `&x` — the address of the object, NOT a read of it. For a volatile lvalue,
    // use the bare lvalue (not the Volatile.Read form). The resulting C `volatile
    // T*` lowers to a plain `T*` (dotcc has no volatile pointer type).
    public EmitContent Visit(C.AddrOf n)
    {
        // `&global` / `&staticLocal` of a value type: the C# static field backing it
        // is a MOVEABLE variable, so a bare `&field` is CS0212 ("address of unfixed
        // expression") even though a C global's address is a stable constant. dotcc's
        // globals are unmanaged value types stored in non-relocatable static storage,
        // so the address IS stable; `Unsafe.AsPointer(ref field)` hands it back with
        // no `fixed` block (which couldn't span the address escaping the function
        // anyway — `return &absentkey;`). The Var visitor tags the operand + its type.
        if (n.Arg1.Content is EmitContent.Text { AddrFixedType: { } ft })
        {
            var fieldLv = T(n.Arg1);
            var ptr = QualifyPredefinedTypeName(ft) + "*";
            // NOTE: when ft is itself a pointer (e.g. UpVal*), the emitted
            // AsPointer(ref UpVal*_field) triggers CS0306 (pointer type as generic
            // argument). A proper fix needs to avoid both CS0306 and CS0212 — see
            // HANDOVER.md. For non-pointer ft, C# infers T correctly.
            return new EmitContent.Text(
                $"(({ptr})System.Runtime.CompilerServices.Unsafe.AsPointer(ref {fieldLv}))",
                Ty: new CType.Sized(ptr));
        }
        var lv = ALValueOf(n.Arg1) ?? VLValueOf(n.Arg1) ?? T(n.Arg1);
        // `&E` synthesizes a pointer CType (E's type + `*`), so a consumer can read
        // the pointee — e.g. the <stdatomic.h> lowering casts the value arg of
        // `atomic_store(&x, v)` to `x`'s type. Also makes `sizeof(&x)` resolve.
        CType? ptrTy = TyOf(n.Arg1) is CType.Sized s ? new CType.Sized(s.CsType + "*") : null;
        return new EmitContent.Text($"(&{lv})", Ty: ptrTy);
    }
    // Unary minus folds for the same reason as `~` — `(size_t)(-1)` / `(byte)(-2)`
    // need the folded value so the enclosing cast can wrap it in `unchecked`.
    public EmitContent Visit(C.Neg n)
    {
        var fold = ConstOfItem(n.Arg1) is int v && IntFoldable(IntOperandType(n.Arg1)) ? -v : (int?)null;
        return new EmitContent.Text($"(-{IntDecay(n.Arg1)})", ConstInt: fold, ConstExpr: IsConstExpr(n.Arg1));
    }
    // Prefix ++/-- — strip outer parens of operand to avoid CS0131 on a
    // parenthesised lvalue. `(x)++` would parse as post-inc on a parens
    // expression, but C# accepts `++x` directly. Enum-ness propagates (C# ++/--
    // work on enums) so a `c++` value used in an int slot still gets decayed.
    // `++x` / `--x` carry the operand's CType (pointer increment yields the same
    // pointer type; integer the same int) so a pointer result reaching the comma
    // value-tuple is nint-cast (Lua's `(void)(++mode)` discard with a `char*` mode).
    public EmitContent Visit(C.PreInc n) => AtomicStep(n.Arg1, isPost: false, "+") ?? VolatileStep(n.Arg1, "+") ?? (EmitContent)new EmitContent.Text($"(++{StripOuterParens(T(n.Arg1))})", EnumOf(n.Arg1), TyOf(n.Arg1));
    public EmitContent Visit(C.PreDec n) => AtomicStep(n.Arg1, isPost: false, "-") ?? VolatileStep(n.Arg1, "-") ?? (EmitContent)new EmitContent.Text($"(--{StripOuterParens(T(n.Arg1))})", EnumOf(n.Arg1), TyOf(n.Arg1));
    // Postfix ++/-- — strip a fully-redundant wrap, but a COMPOUND base keeps its
    // parens (PostfixBase): `(*p)++` must stay `((*p)++)`, not `(*p++)` (= `*(p++)`,
    // wrong precedence AND not a statement-expression — CS0201). Same fix as the
    // `.`/`->` postfix base (Phase 4y); a bare lvalue / member chain / index is
    // unwrapped. Prefix ++/-- bind looser so `++*p` = `++(*p)` is already correct.
    public EmitContent Visit(C.PostInc n) => AtomicStep(n.Arg0, isPost: true, "+") ?? VolatileStep(n.Arg0, "+") ?? (EmitContent)new EmitContent.Text($"({PostfixBase(StripOuterParens(T(n.Arg0)))}++)", EnumOf(n.Arg0), TyOf(n.Arg0));
    public EmitContent Visit(C.PostDec n) => AtomicStep(n.Arg0, isPost: true, "-") ?? VolatileStep(n.Arg0, "-") ?? (EmitContent)new EmitContent.Text($"({PostfixBase(StripOuterParens(T(n.Arg0)))}--)", EnumOf(n.Arg0), TyOf(n.Arg0));
    // Subscript `expr[i]`. For a 1-D array / pointer it's an ordinary C#
    // pointer subscript (matching C). For a MULTI-dimensional array, dotcc
    // flattened the storage, so a PARTIAL index (more dimensions remain) is
    // pointer arithmetic: `a[i]` is `a + i*stride`, where stride is the element
    // count of the remaining sub-array; a FULL index (last dimension) is the
    // ordinary subscript. The base is NOT outer-paren-stripped (a partial-index
    // base like `(a + i*3)` must keep its parens so the next `[…]` binds to the
    // whole expression, not the trailing operand). An enum index decays to int.
    public EmitContent Visit(C.Subscript n)
    {
        var baseTy = TyOf(n.Arg0);
        var baseText = T(n.Arg0);
        var idx = StripOuterParens(IntDecay(n.Arg2));
        if (baseTy is CType.Arr { Element: CType.Arr } arr)
        {
            return Typed($"({baseText} + ({idx}) * {FlatSize(arr.Element)})", arr.Element);
        }
        // Pointer-to-array `int (*p)[3]`: `p[k]` is `p + k*stride` yielding the
        // pointed-to array — same flat pointer arithmetic as a multi-dim partial
        // index.
        if (baseTy is CType.PtrToArr pta)
        {
            return Typed($"({baseText} + ({idx}) * {FlatSize(pta.Inner)})", pta.Inner);
        }
        // Pointer-to-volatile: `p[i]` is a volatile lvalue → fenced (phase V2).
        if (VolatilePointeeOf(n.Arg0)) { return VolatileReadOf($"{baseText}[{idx}]", null, baseTy?.ElementType()); }
        return Typed($"({baseText}[{idx}])", baseTy?.ElementType());
    }

    // Number of scalar elements in a (possibly nested) array CType — the flat
    // stride of a multi-dim array's sub-array. Scalars/pointers count as 1.
    private static int FlatSize(CType t) => t switch
    {
        CType.Arr a => a.Count * FlatSize(a.Element),
        _ => 1,
    };

    // Member access — `.` on a struct value, `->` on a struct pointer.
    // C# accepts both syntaxes in unsafe context (where all our user code
    // lives), so emit verbatim.
    public EmitContent Visit(C.MemberDot n)
    {
        var baseExpr = PostfixBase(StripOuterParens(T(n.Arg0)));
        var field = T(n.Arg2);
        // A C11 anonymous-union field is reached through the synthetic field that
        // holds the nested union (`o.i` → `o.__anonN.i`).
        if (PromotedSynth(n.Arg0, field) is string synth) { return $"({baseExpr}.{synth}.{Id(field)})"; }
        // A MULTI-dim array member decays to a flat element pointer tagged with its
        // strided CType, so `s.grid[i][j]` rewrites to flat pointer arithmetic. An
        // [InlineArray]-backed one goes through `(Elem*)&field`; a `fixed`-buffer one
        // is the field itself (decays to a pointer in unsafe context).
        if (FieldMultiDim(n.Arg0, field) is { } md)
        {
            var acc = md.IsInline ? $"(({md.Elem}*)&{baseExpr}.{Id(field)})" : $"{baseExpr}.{Id(field)}";
            return Typed(acc, md.Arr);
        }
        // An [InlineArray] member decays to its element pointer (like a C array):
        // `((T*)&s.field)`. Subscript / further decay then ride the ordinary
        // pointer paths (over-indexing into the malloc'd tail, which the
        // bounds-checked InlineArray indexer would reject); the InlineArr CType
        // carries count for sizeof.
        if (FieldInlineArr(n.Arg0, field) is { } ia)
        {
            return Typed($"(({ia.Elem}*)&{baseExpr}.{Id(field)})",
                new CType.InlineArr(new CType.Sized(ia.Elem), ia.Count));
        }
        // An atomic / volatile (eligible scalar) field reads seq-cst / fenced; the
        // bare lvalue is tagged for a write-context parent (such a field is never
        // enum-typed — enums aren't eligible — so no enum tag). Atomic wins.
        if (FieldAtomic(n.Arg0, field) is string adt) { return AtomicReadOf($"{baseExpr}.{Id(field)}", new CType.Sized(adt)); }
        if (FieldVolatile(n.Arg0, field)) { return VolatileReadOf($"{baseExpr}.{Id(field)}", null, null); }
        var text = $"({baseExpr}.{Id(field)})";
        // A pointer-to-volatile field — `s.p` carries the VolatilePointee tag so
        // `*s.p` / `s.p[i]` fence (phase V2).
        if (FieldVolatilePointee(n.Arg0, field)) { return new EmitContent.Text(text, Ty: FieldCType(n.Arg0, field), VolatilePointee: true); }
        // An enum-typed field reads as enum-typed (decays in operators, reconciles
        // at sinks) — same EnumType machinery as an enum variable.
        if (FieldEnum(n.Arg0, field) is string e) { return new EmitContent.Text(text, e, new CType.Sized(e)); }
        // Plain scalar/pointer/struct field — carry its CType so sizeof of the
        // member (and of `*field` / `field[i]`) and nested member chains resolve.
        return Typed(text, FieldCType(n.Arg0, field));
    }
    public EmitContent Visit(C.MemberArrow n)
    {
        // A COMPOUND base (e.g. `(p - 1)` from Lua's `getlastfree`) must keep
        // parens so `->` binds to the whole expression, not its trailing operand
        // (`p - 1->f` parses as `p - (1->f)`). Strip redundant outers for the
        // malloc-key match, then re-wrap for emit when the base isn't a bare
        // primary. (Subscript follows the same not-over-stripped rule.)
        var stripped = StripOuterParens(T(n.Arg0));
        var baseExpr = PostfixBase(stripped);
        var field = T(n.Arg2);
        var member = Id(field);
        // Count a `->` whose base is a malloc-candidate variable, and choose the
        // C# operator: a promoted stack value uses `.`, a low-level pointer `->`.
        // The malloc maps are keyed by the RAW C name, but `baseExpr` is the
        // emitted (possibly @-escaped) text — match on the unescaped name, emit
        // with the escaped one.
        var op = "->";
        var rawBase = Unescape(stripped);
        if (_currentFunctionName is string fn && _fnMalloc.TryGetValue(rawBase, out var mv))
        {
            mv.ArrowRefs++;
            if (_promotableIn.Contains((fn, rawBase))) { op = "."; }
        }
        // Anonymous-union field promotion: reach the synth field through the base
        // (with the chosen operator), then `.field` on the value.
        if (PromotedSynth(n.Arg0, field) is string synth) { return $"({baseExpr}{op}{synth}.{member})"; }
        // MULTI-dim array member → flat element pointer tagged with its strided CType
        // (so `p->grid[i][j]` strides), mirroring MemberDot.
        if (FieldMultiDim(n.Arg0, field) is { } md)
        {
            var acc = md.IsInline ? $"(({md.Elem}*)&{baseExpr}{op}{member})" : $"{baseExpr}{op}{member}";
            return Typed(acc, md.Arr);
        }
        // [InlineArray] member → decay to the element pointer (`((T*)&p->field)`),
        // mirroring the MemberDot case. `->` binds tighter than `&`, so the
        // address-of grabs the field.
        if (FieldInlineArr(n.Arg0, field) is { } ia)
        {
            return Typed($"(({ia.Elem}*)&{baseExpr}{op}{member})",
                new CType.InlineArr(new CType.Sized(ia.Elem), ia.Count));
        }
        // An atomic / volatile (eligible scalar) field reads seq-cst / fenced (atomic wins).
        if (FieldAtomic(n.Arg0, field) is string adt) { return AtomicReadOf($"{baseExpr}{op}{member}", new CType.Sized(adt)); }
        if (FieldVolatile(n.Arg0, field)) { return VolatileReadOf($"{baseExpr}{op}{member}", null, null); }
        var text = $"({baseExpr}{op}{member})";
        // Pointer-to-volatile field — tag so `*p->q` / `p->q[i]` fence (phase V2).
        if (FieldVolatilePointee(n.Arg0, field)) { return new EmitContent.Text(text, Ty: FieldCType(n.Arg0, field), VolatilePointee: true); }
        if (FieldEnum(n.Arg0, field) is string e) { return new EmitContent.Text(text, e, new CType.Sized(e)); }
        // Plain field — carry its CType (see MemberDot).
        return Typed(text, FieldCType(n.Arg0, field));
    }

    // The operand of a postfix `.`/`->` must be a primary/postfix expression.
    // After StripOuterParens a COMPOUND base (a top-level binary/ternary op, a
    // leading unary op, or a leading cast) has lost the parens that made the
    // member operator bind to the whole thing — re-wrap it. A bare identifier,
    // member chain (`a.b->c`), call (`f(x)`), or index (`a[i]`) is left as-is, so
    // clean output and the malloc-promote keying (which matches the unwrapped
    // name) are preserved.
    private static string PostfixBase(string s) => NeedsPostfixWrap(s) ? $"({s})" : s;

    private static bool NeedsPostfixWrap(string s)
    {
        s = s.Trim();
        if (s.Length == 0) { return false; }
        var c0 = s[0];
        // Leading unary operator (`*p`, `&x`, `-n`, `!b`, `~m`).
        if (c0 is '*' or '&' or '-' or '+' or '!' or '~') { return true; }
        // A surviving leading `(` (StripOuterParens already removed a fully
        // redundant wrap) is a cast `(T)x` or a paren-group with a suffix —
        // either binds looser than a postfix op, so wrap.
        if (c0 == '(') { return true; }
        // A top-level (depth-0) operator other than the member ops `.` / `->`
        // means a binary/ternary expression. Skip string/char-literal contents
        // (an emitted expr can contain `L("…")`).
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' || c == '\'') { i = SkipCsLiteral(s, i, c); continue; }
            if (c is '(' or '[' or '{') { depth++; }
            else if (c is ')' or ']' or '}') { depth--; }
            else if (depth == 0)
            {
                if (c == '-' && i + 1 < s.Length && s[i + 1] == '>') { i++; continue; }  // `->`
                if (c == '.') { continue; }                                               // `.`
                if (c is '+' or '-' or '*' or '/' or '%' or '<' or '>' or '='
                      or '!' or '&' or '|' or '^' or '?' or ':' or '~') { return true; }
            }
        }
        return false;
    }

    // Index of the closing quote of a C# string/char literal starting at `start`
    // (the caller's loop `++` then moves past it); honors `\` escapes.
    private static int SkipCsLiteral(string s, int start, char quote)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote) { return i; }
            i++;
        }
        return i;
    }

    // If `field` is a C11 anonymous-union field promoted from `baseItem`'s struct
    // type, return the synthetic field name that holds the nested union; else null.
    // The base's struct type comes from its synthesized CType (a struct var carries
    // CType.Sized("S"); a struct pointer CType.Sized("S*") — peel the `*`).
    private string? PromotedSynth(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = StructKeyOf(s.CsType);
        return _promotedFields.TryGetValue(t, out var pf) && pf.TryGetValue(field, out var synth) ? synth : null;
    }

    // The enum type of `field` on the struct that `baseItem`'s CType names (a
    // struct value carries CType.Sized("S"), a pointer CType.Sized("S*") — peel
    // the `*`), or null. Same base-type resolution as PromotedSynth.
    private string? FieldEnum(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = StructKeyOf(s.CsType);
        return _structFieldEnums.TryGetValue(t, out var fe) && fe.TryGetValue(field, out var e) ? e : null;
    }

    // The CType of `field` on the struct/union that baseItem's CType names (a
    // struct value carries CType.Sized("S"), a pointer CType.Sized("S*") — peel
    // the `*`), or null. Same base-type resolution as FieldEnum. Lets a plain
    // member access carry its field's type so `sizeof(s.f)`, `sizeof(*p->f)`,
    // `sizeof(p->f[i])`, and nested chains (`L->stack.p`) resolve.
    private CType? FieldCType(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = StructKeyOf(s.CsType);
        return _structFieldTypes.TryGetValue(t, out var ft) && ft.TryGetValue(field, out var cty) ? cty : null;
    }

    // The struct/union name whose members a base of C# type `cs` exposes. A base
    // may be a struct VALUE (`TValue`), a spelled-out pointer (`TValue*` → peel to
    // `TValue`), a POINTER TYPEDEF (Lua's `StkId` → resolve to `StackValue*` →
    // peel to `StackValue`), or a pointer to an aliased struct (`LStream*` → peel
    // to `LStream` → resolve to `luaL_Stream`). The star can appear EITHER on the
    // spelled type OR inside a typedef target, so peel-stars and resolve-typedef
    // must alternate: each hop trims trailing `*`, returns on a known aggregate,
    // else resolves one typedef level. `_structFieldTypes` is keyed under every
    // aggregate name (tag + typedef), so its key set is the reliable
    // "is this a known aggregate" oracle shared by every field side-table
    // (FieldEnum / FieldAtomic / FieldVolatile / …). Bounded against any cycle.
    // True when `s` is a single C# identifier (optionally @-escaped) — no dots,
    // operators, parens, or whitespace. Used to recognise a redundantly
    // parenthesised bare-name callee so the cast-ambiguous `(f)(args)` can be
    // unwrapped to `f(args)`.
    private static bool IsSimpleIdentifier(string s)
    {
        s = s.Trim();
        if (s.Length == 0) { return false; }
        var i = s[0] == '@' ? 1 : 0;
        if (i >= s.Length || !(char.IsLetter(s[i]) || s[i] == '_')) { return false; }
        for (i++; i < s.Length; i++)
        {
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) { return false; }
        }
        return true;
    }

    private string StructKeyOf(string cs)
    {
        var t = cs;
        for (var i = 0; i < 8; i++)
        {
            t = t.TrimEnd('*');
            if (_structFieldTypes.ContainsKey(t)) { return t; }
            var resolved = ResolveTypedef(t);
            if (resolved == t) { return t; }
            t = resolved;
        }
        return t.TrimEnd('*');
    }

    // (element C# type, count) of `field` if it's an [InlineArray] member of the
    // struct that baseItem's CType names, else null. Same base-type resolution as
    // FieldEnum. Used to decay such a field access to the element pointer.
    private (string Elem, int Count)? FieldInlineArr(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = StructKeyOf(s.CsType);
        return _structInlineArrFields.TryGetValue(t, out var m) && m.TryGetValue(field, out var info)
            ? info : null;
    }

    // (element, InlineArray-backed?, strided CType) of `field` if it's a MULTI-dim
    // array member of the struct baseItem's CType names, else null. Same base-type
    // resolution as FieldInlineArr. Used to decay a multi-dim member to a flat
    // pointer tagged with its multi-dim CType (so `s.f[i][j]` strides).
    private (string Elem, bool IsInline, CType Arr)? FieldMultiDim(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return null; }
        var t = StructKeyOf(s.CsType);
        return _structMultiDimMembers.TryGetValue(t, out var m) && m.TryGetValue(field, out var info)
            ? info : null;
    }

    // `sizeof(Type)` — emit C# sizeof. Valid in unsafe contexts for any
    // unmanaged type (which all our types are). Returns a structured marker so
    // a single-arg malloc can recognise `malloc(sizeof(S))` structurally; T()
    // renders it back to `sizeof(S)` everywhere else.
    public EmitContent Visit(C.SizeofType n) => new EmitContent.SizeofType(T(n.Arg2));

    // `va_arg(ap, T)` (<stdarg.h>, C89) — read the next variadic argument as T.
    // Scalars: `(T)ap.Next()` — VaArg's explicit conversion to T fires, and a
    // typedef alias (e.g. `size_t` → `ulong`) resolves through C#, so no
    // per-typedef knowledge is needed here. Pointers: `(T)ap.NextPtr()` —
    // NextPtr returns void*, then a standard (T*) pointer cast. Tagged with its
    // CType so a va_arg result composes (e.g. under sizeof).
    public EmitContent Visit(C.VaArgExpr n)
    {
        var ap = T(n.Arg2);
        var ty = T(n.Arg4);
        var expr = ty.EndsWith("*", System.StringComparison.Ordinal)
            ? $"({ty}){ap}.NextPtr()"
            : $"({ty}){ap}.Next()";
        return Typed(expr, new CType.Sized(ty));
    }

    // `sizeof expr` — read the operand's synthesized CType (propagated up by the
    // expression visitors) and emit its byte size. Arrays compute
    // `count * sizeof(element)` (the C# pointer-lowering makes C# sizeof wrong);
    // everything else defers to C# `sizeof(type)`. The result is itself an int
    // (size_t in C). If the operand's type wasn't synthesized, fail clearly
    // rather than emit a wrong size.
    public EmitContent Visit(C.SizeofExpr n)
    {
        var t = TyOf(n.Arg1)
            ?? throw new CompileException(
                "`sizeof` of this expression isn't supported yet — dotcc resolves sizeof of a "
                + "variable, array, subscript, dereference, cast, literal, or call result. "
                + "Use `sizeof(Type)` if you can.");
        // `sizeof` yields `size_t` in C (an UNSIGNED type — `unsigned long` →
        // `ulong` in dotcc's LP64 model, matching gcc/MSVC's 64-bit size_t and
        // `offsetof` above). C#'s `sizeof` operator is `int`, so cast to ulong; the
        // usual-arithmetic-conversion layer then reconciles `ulong / sizeof` as
        // unsigned (it'd be CS0034 against a bare int). Carry the folded byte size
        // so it still works in an integer constant-expression position (array bound).
        return new EmitContent.Text($"(ulong){SizeofText(t)}", Ty: new CType.Sized("ulong"),
            ConstInt: SizeofConstOfCType(t), ConstExpr: true);
    }

    // `offsetof(Type, member)` (C89) — the byte offset of `member` within `Type`.
    // C# has no compile-time offsetof, and the C `&((T*)0)->m` trick faults at
    // runtime (C# null-checks the `->` deref, even in unsafe). The only reliable
    // form is a REAL instance + address subtraction (`(byte*)&t.m - (byte*)&t`), so
    // dotcc generates a tiny helper per (Type, member) using a stack `default`
    // instance and calls it. `&t.field` of a stack local needs no `fixed`, and the
    // field is referenced by name (no MemberArrow rewrite) so an array/[InlineArray]
    // member yields its offset, not its decayed element pointer. Result is size_t.
    public EmitContent Visit(C.OffsetofExpr n)
    {
        var type = T(n.Arg2);
        var rawMember = T(n.Arg4);
        var member = Id(rawMember);
        // Prefer a compile-time CONSTANT when dotcc can model the struct's layout.
        // This is REQUIRED when offsetof feeds a constant position — e.g. Lua's
        // `char padding[offsetof(Limbox_aux, follows_pNode)]` array-member bound,
        // which lowers to a C# `fixed[N]` and needs a literal N. The folded value
        // matches the C ABI / .NET blittable layout, so it agrees with the runtime
        // form (used as the fallback below when the layout isn't modellable). The
        // literal carries ConstInt so it can drive ParseDims / case labels / etc.
        if (TryOffsetOf(type, rawMember) is int off)
        {
            return new EmitContent.Text(
                off.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Ty: new CType.Sized("ulong"), ConstInt: off);
        }
        var key = IdentFrag(type) + "__" + IdentFrag(rawMember);
        if (_offsetofKeys.Add(key))
        {
            // A `fixed` buffer member DECAYS to a pointer (its address is `t.field`);
            // a regular field / [InlineArray] wrapper uses `&t.field`. Resolve the
            // struct's fixed-buffer set via the type name (or its tag/alias).
            var isFixed = _structFixedBufferMembers.TryGetValue(type, out var fb) && fb.Contains(rawMember);
            var addr = isFixed ? $"(byte*)__t.{member}" : $"(byte*)&__t.{member}";
            // Partial class so each helper can be appended independently (C# merges).
            _structs.Append("static unsafe partial class __Offsets\n{\n    internal static ulong ")
                .Append(key).Append("() { ").Append(type).Append(" __t = default; return (ulong)(")
                .Append(addr).Append(" - (byte*)&__t); }\n}\n\n");
        }
        return Typed($"__Offsets.{key}()", new CType.Sized("ulong"));
    }

    // Set of emitted offsetof helper keys (dedup — one helper per Type+member).
    private readonly HashSet<string> _offsetofKeys = new(System.StringComparer.Ordinal);

    // Sanitize a type/member name into a C# identifier fragment for a helper name.
    private static string IdentFrag(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) { sb.Append(char.IsLetterOrDigit(c) ? c : '_'); }
        return sb.ToString();
    }

    // C# expression for the byte size of a CType. An array is count*sizeof(elem)
    // (recursive for nested arrays); anything else is a direct C# `sizeof(T)`.
    private static string SizeofText(CType t) => t switch
    {
        CType.Arr a => $"({a.Count} * {SizeofText(a.Element)})",
        // An inline-array member's size is count*sizeof(element), like a C array
        // (NOT the lowered [InlineArray] wrapper's C# sizeof).
        CType.InlineArr ia => $"({ia.Count} * {SizeofText(ia.Element)})",
        CType.Sized s => $"sizeof({s.CsType})",
        // A pointer-to-array is a pointer: its size is the pointer size, NOT the
        // pointed-to array's size.
        CType.PtrToArr => "sizeof(void*)",
        _ => throw new CompileException("internal: unknown CType in sizeof"),
    };

    // A bare function name used as a value (e.g. a call argument, a struct/array
    // initializer element, an assignment rhs) decays to its address in C
    // (function-to-pointer conversion). C# requires the explicit `&`, so prepend
    // it — that's what lets `qsort(…, compare)` (bare name) pass a function-pointer
    // argument. Outer parens are seen through (`(luaB_next)` → `&luaB_next`) but
    // the emitted text's `@`-escaping is preserved (`@new` → `&@new`); the lookup
    // is by the raw C name. Guarded so a local/param shadowing a function name is
    // left alone.
    private string DecayFnName(string argText)
    {
        var inner = StripOuterParens(argText);
        var raw = Unescape(inner);
        return !_localNames.Contains(raw) && _fnReturnTypes.ContainsKey(raw) ? "&" + inner : argText;
    }

    public EmitContent Visit(C.Call n)
    {
        var callee = T(n.Arg0);
        // A PARENTHESIZED simple-identifier callee — Lua's `(f)(L, ud)` direct
        // fn-ptr call (its `LUAI_TRY` macro spells the call as `((f)(L, ud))`) —
        // is cast-ambiguous in C#: `(f)` parses as a cast and `(L, ud)` becomes a
        // ValueTuple (CS0306). The parens are redundant around a bare name, so
        // strip them; the result parses as the invocation it is. Stripping also
        // lets the `_localNames` shadow check below see the bare name. A COMPOUND
        // callee (`(*f)`, `(a ? b : c)`) keeps its parens — there the precedence
        // matters and there's no cast-ambiguity (a leading `*`/`?` can't be a type).
        if (IsSimpleIdentifier(StripOuterParens(callee))) { callee = StripOuterParens(callee); }
        var argsContent = (EmitContent.Args)n.Arg2.Content;
        var args = argsContent.Values;  // strongly-typed arg list, no sentinel splitting
        // A local/param that SHADOWS a libc builtin name (e.g. a function-pointer
        // local named `printf` or `malloc`) is an ordinary call through that
        // variable — skip the builtin lowering entirely. (_localNames is keyed by
        // the raw C name; callee is the emitted, possibly @-escaped text.) For
        // normal code no local is named after a builtin, so this never fires.
        if (_localNames.Contains(Unescape(callee)))
        {
            return $"{callee}({string.Join(", ", args.Select(DecayFnName))})";
        }
        // <stdatomic.h> generic functions (atomic_load / atomic_fetch_add /
        // atomic_compare_exchange / atomic_flag_* / atomic_thread_fence / …) — lower
        // by name onto the seq-cst Atomic.* helpers. Returns null for any other name.
        if (LowerAtomicCall(callee, argsContent) is { } atomicCall) { return atomicCall; }
        // malloc(sizeof(S)) — emit a MallocSizeof marker (carrying the struct
        // type for the stack-promotion peephole and the verbatim low-level
        // text for the fallback). Recognised structurally: the sole argument
        // reduced to a SizeofType marker, no string parsing.
        if (callee == "malloc" && args.Count == 1
            && argsContent.SoleArg is EmitContent.SizeofType sz)
        {
            // Use the bare C# `sizeof(T)` (int) for the byte count — `malloc` takes
            // `int`, and the rendered arg is now `(ulong)sizeof(T)` (sizeof is size_t),
            // which wouldn't bind. The struct type is recorded for the stack-promote.
            return new EmitContent.MallocSizeof(sz.TypeName, $"malloc(sizeof({sz.TypeName}))");
        }
        // free(p) — count it against the candidate `p` and, in the emit pass,
        // drop it entirely when `p` was promoted to a stack value (nothing to
        // free). Non-promotable / non-candidate frees fall through to a normal
        // call below.
        // Match the malloc maps on the RAW name (args[0] is the emitted,
        // possibly @-escaped argument text).
        var freeArg = args.Count == 1 ? Unescape(args[0]) : null;
        if (callee == "free" && freeArg is not null
            && _currentFunctionName is string fn && _fnMalloc.TryGetValue(freeArg, out var fmv))
        {
            fmv.FreeRefs++;
            if (_promotableIn.Contains((fn, freeArg))) { return ""; }
        }
        // setjmp(env) — return a SetjmpCall marker variant rather than
        // emit as a regular function call. The parent visitor (Equ for
        // `setjmp(env) == 0` or StmtIfElse for the bare condition)
        // recognises the variant and rewrites the surrounding if/else
        // into a try/catch shape. Any other context lets the variant
        // escape to T(), which throws CompileException — setjmp is a
        // non-local-jump primitive, not a regular call.
        if (callee == "setjmp" && args.Count == 1)
        {
            return new EmitContent.SetjmpCall(args[0]);
        }
        // <stdarg.h> variadic-access builtins. va_start/va_end/va_copy take only
        // expressions, so they parse as ordinary calls and are rewritten here by
        // name; va_arg (whose 2nd operand is a TYPE) has its own grammar
        // production + visitor. `_va` is the params array a variadic function's
        // ParamsVararg emit synthesizes (see Visit(C.ParamsVararg)).
        //   va_start(ap, last) -> ap = new VaList(_va)   (last is unused — the
        //                         array already holds exactly the variadic args)
        //   va_copy(dst, src)  -> dst = src              (VaList is a value type)
        //   va_end(ap)         -> ap.End()               (no-op)
        if (callee == "va_start" && args.Count == 2) { return $"{args[0]} = new VaList(_va)"; }
        if (callee == "va_copy" && args.Count == 2) { return $"{args[0]} = {args[1]}"; }
        if (callee == "va_end" && args.Count == 1) { return $"{args[0]}.End()"; }
        // printf-family fluent lowering. C `printf("%d %s", x, s)` → C#
        // `printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — works around
        // `params object[]` not accepting raw pointers. The callee name
        // arrives unmapped (post-MapBuiltin-as-identity) so we match the
        // C spelling. `fprintf(stream, fmt, …)` follows the same shape
        // but with the stream as the first call arg.
        // A varargs `%d` slot takes a C int; an enum argument is a C int too,
        // but `.Arg(int)` won't bind a C# enum — decay enum args to (int).
        var argEnums = argsContent.ArgEnums;
        string VarArg(int i) => argEnums is { } ae && ae[i] is not null ? $"(int){args[i]}" : args[i];
        if (callee == "printf")
        {
            var sb = new StringBuilder();
            sb.Append("printf(").Append(args[0]).Append(')');
            for (int i = 1; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        if (callee == "fprintf" && args.Count >= 2)
        {
            var sb = new StringBuilder();
            sb.Append("fprintf(").Append(args[0]).Append(", ").Append(args[1]).Append(')');
            for (int i = 2; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        // sprintf / snprintf share the printf fluent shape but format into a
        // caller buffer instead of a stream. The factory takes only the FIXED
        // args (sprintf: dst, fmt — snprintf: dst, n, fmt); the variadic %-args
        // become `.Arg(…)` and `.Done()` flushes into the buffer (returning the
        // would-be byte count). `.Done()` is appended unconditionally — a
        // SprintfBuilder left without it never copies — so this fires even with
        // zero varargs (`sprintf(dst, fmt)` → `sprintf(dst, fmt).Done()`).
        if (callee == "sprintf" && args.Count >= 2)
        {
            var sb = new StringBuilder();
            sb.Append("sprintf(").Append(args[0]).Append(", ").Append(args[1]).Append(')');
            for (int i = 2; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        if (callee == "snprintf" && args.Count >= 3)
        {
            var sb = new StringBuilder();
            // The factory's bound is `int n`, but C's `size_t n` arrives unsigned
            // or wider at some call sites (Lua's `sz - n`, an unsigned `maxitem`),
            // which C# won't narrow implicitly. Coerce the bound through the same
            // store-conversion rule used for assignments.
            var bound = CoerceStore(args[1], argsContent.ArgTypes is { } nt ? nt[1] : null,
                argsContent.ArgConsts is { } nc ? nc[1] : null, "int", n.Arg0.Position.Line);
            sb.Append("snprintf(").Append(args[0]).Append(", ").Append(bound).Append(", ").Append(args[2]).Append(')');
            for (int i = 3; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        // Coerce each argument to the callee's parameter type — C's implicit
        // argument conversion (int↔unsigned / narrowing / enum↔int) that C#
        // rejects. Only fires for a callee with recorded fixed-param types (user
        // functions + synthetic-header libc prototypes, which match the emitted
        // signatures); a variadic call's extra args (beyond the fixed params) keep
        // the plain fn-name decay, as do calls through fn-ptr locals / unknowns.
        var paramTypes = _fnParamTypes.TryGetValue(Unescape(callee), out var pts) ? pts : null;
        var argTypes = argsContent.ArgTypes;
        var argConsts = argsContent.ArgConsts;
        var callLine = n.Arg0.Position.Line;
        string EmitArg(int i)
        {
            var a = DecayFnName(args[i]);
            if (paramTypes is not null && i < paramTypes.Count)
            {
                a = CoerceArg(a, argTypes is { } at ? at[i] : null,
                    argEnums is { } ae2 ? ae2[i] : null,
                    argConsts is { } ac ? ac[i] : null, paramTypes[i], callLine);
            }
            return a;
        }
        var callText = $"{callee}({string.Join(", ", Enumerable.Range(0, args.Count).Select(EmitArg))})";
        // Tag the result with the callee's return type: the enum type drives
        // int↔enum reconciliation at the call site, and the CType lets sizeof of
        // a call result resolve.
        if (_fnReturnTypes.TryGetValue(Unescape(callee), out var rt))
        {
            return new EmitContent.Text(callText, _enumTags.Contains(rt) ? rt : null, new CType.Sized(rt));
        }
        return callText;
    }

    public EmitContent Visit(C.CallNoArgs n)
    {
        var callee = T(n.Arg0);
        if (callee == "printf") { return "printf(Libc.L(\"\\0\"u8)).Done()"; }
        return $"{callee}()";
    }

    // An argument's CType for call-site coercion: the synthesized type, or `int`
    // for a bare `sizeof` (a SizeofType marker carries no CType but is C# `int`),
    // so `f(sizeof(T))` into a `size_t` parameter reconciles like any int arg.
    private CType? ArgTypeOf(Item it) =>
        TyOf(it) ?? (IntOperandType(it) is string t ? new CType.Sized(t) : null);

    // ArgsCons (`ArgList → E ',' ArgList`) prepends the new expression
    // (Arg0, Text) onto the recursively-built ArgList (Arg2, Args).
    // ArgsOne wraps the single E into a one-element Args list.
    // Call / DeclArrInit / DeclArrInitImplicit consume via A() — no
    // sentinel splitting, no encoded strings.
    public EmitContent Visit(C.ArgsCons n)
    {
        var head = T(n.Arg0);
        var tailArgs = (EmitContent.Args)n.Arg2.Content;
        var tail = tailArgs.Values;
        var combined = new List<string>(tail.Count + 1) { head };
        combined.AddRange(tail);
        // Carry per-arg enum types so the printf path can decay enum→int.
        var enums = new List<string?>(tail.Count + 1) { EnumOf(n.Arg0) };
        enums.AddRange(tailArgs.ArgEnums ?? new string?[tail.Count]);
        // Carry per-arg CTypes (the <stdatomic.h> lowering reads the first arg's
        // pointer type to cast the value arg to the pointee).
        var types = new List<CType?>(tail.Count + 1) { ArgTypeOf(n.Arg0) };
        types.AddRange(tailArgs.ArgTypes ?? new CType?[tail.Count]);
        // Carry per-arg integer constants for the call-site coercion (a fitting
        // constant needs no cast; an out-of-range one needs `unchecked`).
        var consts = new List<int?>(tail.Count + 1) { ConstOfItem(n.Arg0) };
        consts.AddRange(tailArgs.ArgConsts ?? new int?[tail.Count]);
        return new EmitContent.Args(combined, ArgEnums: enums, ArgTypes: types, ArgConsts: consts);
    }

    // SoleArg keeps the single argument's structured Content alongside its
    // rendered text, so a one-arg call (malloc) can inspect it structurally.
    public EmitContent Visit(C.ArgsOne n) =>
        new EmitContent.Args(new[] { T(n.Arg0) }, n.Arg0.Content as EmitContent, new[] { EnumOf(n.Arg0) }, new[] { ArgTypeOf(n.Arg0) }, new[] { ConstOfItem(n.Arg0) });

    // Variable reference. Three emit-time rewrites:
    //   - `__func__` → `L("name\0"u8)` using `_currentFunctionName` (set
    //     by the enclosing FnSig action before this Var visit runs —
    //     LALR bottom-up, FnSig fully reduces before Block descends);
    //   - enumerator → `EnumName.Member` so bare `Red` lands as `Color.Red`;
    //   - builtin name → BCL helper for `malloc` / `free` / `printf`.
    // Otherwise pass the identifier through verbatim.
    public EmitContent Visit(C.Var n)
    {
        var name = T(n.Arg0);
        // Count every reference to a malloc candidate. The promotion check
        // (in FuncDef) requires TotalRefs == ArrowRefs + FreeRefs, so any use
        // that isn't a `->` base or a free arg (return, function arg, address-of,
        // pointer arithmetic, comparison, reassignment) tips the balance and
        // disqualifies the variable — exactly the escapes we must not promote.
        if (_fnMalloc.TryGetValue(name, out var mv)) { mv.TotalRefs++; }
        if (name == "__func__")
        {
            Gate(1999, "__func__", n.Arg0);  // C99 predefined identifier
            // `_currentFunctionName` is the enclosing function being
            // reduced. If it's null we're outside any function (illegal
            // C use of __func__) — emit a sentinel so Roslyn surfaces the
            // bug as an undefined-identifier diagnostic rather than us
            // silently producing wrong code.
            var fn = _currentFunctionName
                ?? throw new CompileException("`__func__` used outside any function definition");
            return Typed($"Libc.L(\"{fn}\\0\"u8)", new CType.Sized("byte*"));
        }
        // A block-scope `static` local shadows any global/enumerator of the
        // same name within this function — rewrite to its mangled global field.
        // A static-local ARRAY carries its element+count CType (registered under
        // the mangled name in _globalArrayInfo) so subscript/sizeof resolve just
        // as for any array; a static scalar has none to attach (unchanged).
        if (_fnStatics.TryGetValue(name, out var staticField))
        {
            // A static-local ARRAY is already a pointer (GlobalArrayFrom) — `&arr`
            // is rare and untreated. A SCALAR/struct static-local is a static field,
            // so tag its field type for `&` (CS0212 avoidance — see AddrFixedType).
            return _globalArrayInfo.TryGetValue(staticField, out var staticCty)
                ? new EmitContent.Text(staticField, null, staticCty)
                : new EmitContent.Text(staticField,
                    AddrFixedType: _globalTypes.TryGetValue(staticField, out var sft) ? sft : null);
        }
        // An enumerator constant resolves to `EnumName.Member` — but only if no
        // local/param of the same name shadows it. Without this guard a local
        // named like an enum constant would emit the (non-lvalue) `EnumName.X`
        // at every use and fail to compile.
        if (!_localNames.Contains(name) && _enumerators.TryGetValue(name, out var enumName))
        {
            // An enumerator is a compile-time constant (ConstExpr), so a cast chain
            // through it (`(int)TMS.TM_EQ + 1`) stays constant for the cast wrap.
            return new EmitContent.Text($"{enumName}.{Id(name)}", enumName, new CType.Sized(enumName),
                ConstInt: _enumeratorValues.TryGetValue(name, out var ev) ? ev : null, ConstExpr: true);
        }
        // An enum-typed variable read is itself enum-valued — tag it so consuming
        // nodes insert the int↔enum casts C# requires (`int x = c`, `c & MASK`,
        // …). Also carry the full CType (incl. array element+count) for sizeof.
        // Locals/params win over globals (shadowing). If a scope frame renamed
        // this local (block-shadow alpha-rename), emit the renamed identifier;
        // otherwise it's a global / function / builtin → raw name through
        // MapBuiltin. CType / enum lookups stay keyed by the RAW name.
        var resolved = ResolveLocal(name);
        var mapped = resolved is not null ? Id(resolved) : MapBuiltin(name);
        var cty = VarCType(name);
        var enumTag = cty is CType.Sized sz && _enumTags.Contains(sz.CsType) ? sz.CsType : null;
        // A volatile variable reads through Volatile.Read(ref …); the bare lvalue
        // (the emitted identifier) is tagged so a write-context parent — assignment,
        // compound-assign, ++/--, & — emits Volatile.Write / the bare address instead.
        // _Atomic reads seq-cst (Atomic.Load); checked first — atomic wins over a
        // co-applied volatile.
        if (IsAtomicVar(name)) { return AtomicReadOf(mapped, cty); }
        if (IsVolatileVar(name)) { return VolatileReadOf(mapped, enumTag, cty); }
        // A pointer-to-volatile read carries the VolatilePointee tag so a deref /
        // subscript of it (`*p`, `p[i]`) fences (phase V2).
        if (IsVolatilePointeeVar(name)) { return new EmitContent.Text(mapped, enumTag, cty, VolatilePointee: true); }
        // A file-scope global SCALAR/struct (not a local — `resolved` is null — and
        // not a pinned array) lowers to a static field; tag its type so `&` of it
        // routes through Unsafe.AsPointer (CS0212 avoidance — see AddrFixedType).
        var addrFixed = resolved is null && !_globalArrayInfo.ContainsKey(name)
            && _globalTypes.TryGetValue(name, out var gty) ? gty : null;
        return new EmitContent.Text(mapped, enumTag, cty, AddrFixedType: addrFixed);
    }

    // Synthesize a variable's CType from the symbol tables — array info first
    // (arrays lower to pointers, so this is the only place element+count
    // survive), then the plain local/global type. Null for builtins / unknowns.
    private CType? VarCType(string name)
    {
        if (_localArrayInfo.TryGetValue(name, out var la)) { return la; }
        if (_globalArrayInfo.TryGetValue(name, out var ga)) { return ga; }
        if (_localTypes.TryGetValue(name, out var lt)) { return new CType.Sized(lt); }
        if (_globalTypes.TryGetValue(name, out var gt)) { return new CType.Sized(gt); }
        return null;
    }
    // Integer literal. Decimal and hex (`0x…`) pass through (C# accepts both
    // verbatim); binary (`0b…`, C23) passes through too (C# has `0b`) but is
    // gated. A `0`-prefixed OCTAL constant (`0755`) is the one form C# can't
    // take literally — a leading `0` is plain decimal in C#, so `0755` would
    // silently mean 755 instead of 493. dotcc converts it to its value. C
    // suffixes (u/U/l/L, incl. the C99 `ll`) are normalised to C#'s (no `ll` —
    // both `l` and `ll` mean 64-bit `long` in C#).
    public EmitContent Visit(C.Num n)
    {
        var raw = T(n.Arg0);
        // Split trailing C suffix (u/U/l/L) off the digits. `ls` counts l/L
        // (>=2 is the C99 `long long` suffix); the suffix also fixes the
        // literal's type for sizeof.
        var end = raw.Length;
        var ls = 0;
        var hasU = false;
        while (end > 0 && (raw[end - 1] is 'u' or 'U' or 'l' or 'L'))
        {
            if (raw[end - 1] is 'l' or 'L') { ls++; } else { hasU = true; }
            end--;
        }
        var digits = raw[..end];
        // C23 digit separators (`1'000'000`) — strip the `'`s before parsing the
        // value; C# uses `_` and doesn't need them at all. The lexer only allows
        // a `'` between two digits, so removal is unambiguous.
        if (digits.IndexOf('\'') >= 0)
        {
            Gate(2023, "digit separators in a numeric literal", n.Arg0);  // C23
            digits = digits.Replace("'", "");
        }
        if (_dialectGate is not null && ls >= 2) { Gate(1999, "`long long` (ll) integer suffix", n.Arg0); }
        var ct = ls > 0 ? (hasU ? "ulong" : "long") : (hasU ? "uint" : "int");

        string valueText;
        int? constVal = null;  // folded integer value (for const-expression use, e.g. array bounds)
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (digits.Length >= 2 && digits[0] == '0' && (digits[1] is 'x' or 'X'))
        {
            valueText = digits + CsIntSuffix(hasU, ls);          // hex — C# accepts 0x… verbatim
            if (int.TryParse(digits[2..], System.Globalization.NumberStyles.HexNumber, inv, out var hv)) { constVal = hv; }
        }
        else if (digits.Length >= 2 && digits[0] == '0' && (digits[1] is 'b' or 'B'))
        {
            Gate(2023, "binary integer literal (`0b`)", n.Arg0);  // C23
            valueText = digits + CsIntSuffix(hasU, ls);          // binary — C# accepts 0b… verbatim
            try { constVal = (int)Convert.ToUInt32(digits[2..], 2); } catch { /* not foldable */ }
        }
        else if (digits.Length >= 2 && digits[0] == '0')
        {
            // C octal (`0`-prefix). Convert to its value and emit decimal, since
            // C# would read the leading 0 as a no-op and the literal as decimal.
            foreach (var d in digits)
            {
                if (d is < '0' or > '7')
                {
                    throw new CompileException($"invalid digit '{d}' in octal constant `{raw}`");
                }
            }
            ulong value;
            try { value = Convert.ToUInt64(digits, 8); }
            catch (OverflowException) { throw new CompileException($"octal constant `{raw}` is too large"); }
            valueText = value.ToString(inv) + CsIntSuffix(hasU, ls);
            if (value <= int.MaxValue) { constVal = (int)value; }
        }
        else
        {
            valueText = digits + CsIntSuffix(hasU, ls);          // decimal
            if (int.TryParse(digits, System.Globalization.NumberStyles.Integer, inv, out var dv)) { constVal = dv; }
        }
        // A numeric literal is always a constant expression (ConstExpr), even when
        // its value exceeds the 32-bit int fold and ConstInt is null (e.g. a large
        // `0xFFFFFFFF` / `4294967295u`) — so an out-of-range cast of it wraps in
        // `unchecked` rather than tripping CS0221.
        return new EmitContent.Text(valueText, Ty: new CType.Sized(ct), ConstInt: constVal, ConstExpr: true);
    }

    // Map a C integer suffix (any u/U + any l/L) to C#'s canonical form. C# has
    // no `ll` (both `l` and `ll` are 64-bit `long`), so any number of l's → "L".
    private static string CsIntSuffix(bool hasU, int lCount) => (hasU, lCount) switch
    {
        (false, 0) => "",
        (true, 0)  => "u",
        (false, _) => "L",     // any l's → long
        (true, _)  => "UL",    // u + any l's → ulong
    };
    public EmitContent Visit(C.Flt n)
    {
        var raw = T(n.Arg0);
        var last = raw.Length > 0 ? raw[^1] : '\0';
        // Hex float literal (C99) — `0x1.8p3`. C# has no hex-float literal, so
        // parse the value and emit it as a decimal double/float (the L is dropped
        // → double; the f → float). Gated C99.
        if (raw.Length > 1 && raw[0] == '0' && (raw[1] is 'x' or 'X'))
        {
            Gate(1999, "hex float literal", n.Arg0);
            var isFlt = last is 'f' or 'F';
            var body = (last is 'f' or 'F' or 'l' or 'L') ? raw[..^1] : raw;
            var val = ParseHexFloat(body);
            var lit = val.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (isFlt) { return Typed(lit + "f", new CType.Sized("float")); }
            // A bare integer-valued literal would be `int` in C#; force `double`.
            if (lit.IndexOf('.') < 0 && lit.IndexOf('E') < 0 && lit.IndexOf('e') < 0) { lit += ".0"; }
            return Typed(lit, new CType.Sized("double"));
        }
        // `f`/`F` → C# `float` (passes through verbatim — C# accepts the suffix).
        if (last is 'f' or 'F') { return Typed(raw, new CType.Sized("float")); }
        // `l`/`L` → C `long double`, which dotcc maps to C# `double`. C# has no
        // float `L` suffix, so strip it and emit a bare double literal.
        if (last is 'l' or 'L') { return Typed(raw[..^1], new CType.Sized("double")); }
        return Typed(raw, new CType.Sized("double"));
    }

    // Parse a C hex float literal body (suffix already stripped): `0xH.HpE`,
    // where the integer/fraction parts are hex and the `p` exponent is a signed
    // decimal power of two. Mirrors C's value computation: (hex mantissa) * 2^exp.
    // Exactly-representable constants round-trip; extreme exponents could differ
    // from gcc by an ULP via Math.Pow — acceptable for literal constants.
    private static double ParseHexFloat(string s)
    {
        var i = 2;  // skip 0x / 0X
        double mant = 0;
        while (i < s.Length && Uri.IsHexDigit(s[i])) { mant = mant * 16 + HexVal(s[i]); i++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            var scale = 1.0 / 16;
            while (i < s.Length && Uri.IsHexDigit(s[i])) { mant += HexVal(s[i]) * scale; scale /= 16; i++; }
        }
        var exp = 0;
        var sign = 1;
        if (i < s.Length && (s[i] is 'p' or 'P'))
        {
            i++;
            if (i < s.Length && (s[i] is '+' or '-')) { if (s[i] == '-') { sign = -1; } i++; }
            while (i < s.Length && s[i] is >= '0' and <= '9') { exp = exp * 10 + (s[i] - '0'); i++; }
        }
        return mant * System.Math.Pow(2, sign * exp);
    }

    // Adjacent string literals concatenate (C translation phase 6). strSeqOne/
    // Cons collect each segment's inner body (quotes stripped, escapes intact);
    // Visit(C.Str) decodes + emits once.
    public EmitContent Visit(C.StrSeqOne n) =>
        new EmitContent.StrParts(new[] { StripStrQuotes(T(n.Arg0)) });
    public EmitContent Visit(C.StrSeqCons n)
    {
        var prev = ((EmitContent.StrParts)n.Arg0.Content).Bodies;
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(StripStrQuotes(T(n.Arg1)));
        return new EmitContent.StrParts(combined);
    }

    public EmitContent Visit(C.Str n)
    {
        // Decode each segment's C escapes to bytes INDEPENDENTLY, then
        // concatenate — so a `\x…` in one segment can't greedily eat the next
        // segment's leading hex digit. Emit one greedy-safe u8 literal; the
        // CType length is the decoded byte count + 1 for the NUL.
        var parts = (EmitContent.StrParts)n.Arg0.Content;
        var items = new List<StrItem>();
        foreach (var body in parts.Bodies) { DecodeCStringBody(body, items); }
        // A decoded escape byte > 0x7F (`"\xff"`, `"\377"`) can't be one byte in
        // a C# u8 literal — C# UTF-8-encodes \x80+ into two bytes. Route those
        // strings to a constant byte-array (RVA-backed, see EmitByteArray) so the
        // exact C bytes survive; keep the readable u8 literal for everything else.
        if (items.Exists(it => it.IsByte && it.Value > 0x7F))
        {
            var (arr, alen) = EmitByteArray(items);
            return Typed($"Libc.L({arr})", new CType.Arr(new CType.Sized("byte"), alen + 1));
        }
        var (escaped, byteLen) = EmitU8(items);
        return Typed($"Libc.L(\"{escaped}\\0\"u8)", new CType.Arr(new CType.Sized("byte"), byteLen + 1));
    }

    // C char literal — type `int`, but our `char` is `byte`. Decode the escape
    // (or plain char) to its byte value: a plain printable ASCII char stays
    // readable as `(byte)'c'`, everything else (named/octal/hex escape, control)
    // lowers to `(byte)N`. sizeof('a') is sizeof(int) per C.
    public EmitContent Visit(C.Chr n)
    {
        var raw = T(n.Arg0);
        if (raw is null || raw.Length < 3) { return Typed("(byte)0", new CType.Sized("int")); }
        var body = raw[1..^1];
        var i = 0;
        var item = DecodeEscapeOrChar(body, ref i);
        string text;
        if (!item.IsByte && item.Value is >= 0x20 and <= 0x7E && item.Value != '\'' && item.Value != '\\')
        {
            text = $"(byte)'{(char)item.Value}'";
        }
        else
        {
            text = $"(byte){(item.IsByte ? item.Value : item.Value & 0xFF)}";
        }
        // A char constant is a compile-time constant; carry its value + ConstExpr.
        return new EmitContent.Text(text, Ty: new CType.Sized("int"),
            ConstInt: item.IsByte ? item.Value : item.Value & 0xFF, ConstExpr: true);
    }

    // Comma operator (`Expr → Expr ',' E`). Accumulate operands left-to-right
    // into a CommaSeq; the value/statement lowering happens at the consumer
    // (Paren / StmtExpr) since C# has no comma operator.
    public EmitContent Visit(C.CommaOp n)
    {
        // Flatten each side's own comma operands (a raw CommaSeq, or a
        // parenthesized comma / `(void)` discard carrying CommaOps) so a nested
        // comma `(a, b), c` / `a, (b, c)` becomes the flat [a, b, c] — the leading
        // operands are side effects, the last is the value.
        var parts = CommaParts(n.Arg0);
        parts.AddRange(CommaParts(n.Arg2));
        // If a NON-LAST operand is VOID (a void guard ternary — no C# value form),
        // the comma can't be a tuple. Hoist the leading operands as statements and
        // keep the last as the value (Lua's `luaM_newvectorchecked`). Otherwise the
        // ordinary value-tuple / discard-statements forms apply (a CommaSeq).
        if (parts.Take(parts.Count - 1).Any(p => p.IsVoid))
        {
            var leading = parts.Take(parts.Count - 1).Select(p => p.Stmt).ToList();
            return new EmitContent.SeqExpr(leading, parts[^1].Value, parts[^1].Ty ?? TyOf(n.Arg2));
        }
        // The comma expression's value (and type) is its LAST operand. Carry each
        // operand's CType so the value-tuple can nint-cast pointer operands.
        return new EmitContent.CommaSeq(
            parts.Select(p => p.Value).ToList(), TyOf(n.Arg2),
            parts.Select(p => p.Ty).ToList());
    }

    /// <summary>
    /// Flatten a comma operand into parts, each carrying a VALUE form (for the tuple
    /// path), a STATEMENT form (for the discard / hoist path), whether it's VOID
    /// (a guard ternary with no C# value — forces the hoisting SeqExpr path), and the
    /// operand's CType (for the pointer-nint-cast in the tuple). Flattens nested
    /// commas (a raw CommaSeq, a parenthesised comma's CommaOps, or a SeqExpr).
    /// </summary>
    private List<(string Value, string Stmt, bool IsVoid, CType? Ty)> CommaParts(Item it)
    {
        // A void guard ternary: its statement form is the if/else; no value form.
        if (it.Content is EmitContent.VoidCond vc)
        {
            return new() { (vc.Value, vc.IfStatement, true, (CType?)null) };
        }
        // A nested hoist: its leading statements (already statement strings) + its value.
        if (it.Content is EmitContent.SeqExpr se)
        {
            var ps = se.LeadingStmts.Select(s => (s, s, false, (CType?)null)).ToList();
            ps.Add((se.ValueExpr, $"{StripOuterParens(se.ValueExpr)};\n", false, se.Ty));
            return ps;
        }
        // A nested comma sequence — carry its per-operand types through.
        if (it.Content is EmitContent.CommaSeq cs)
        {
            return cs.Operands.Select((o, i) =>
                (o, $"{StripOuterParens(o)};\n", false,
                 cs.OperandTypes is { } ot && i < ot.Count ? ot[i] : null)).ToList();
        }
        // A parenthesised comma (Text carrying CommaOps): operand strings only; the
        // Text's own CType is the LAST operand's, so type just that one.
        if (CommaOpsOf(it) is { } ops)
        {
            var ty = TyOf(it);
            return ops.Select((o, i) =>
                (o, $"{StripOuterParens(o)};\n", false, i == ops.Count - 1 ? ty : null)).ToList();
        }
        var t = T(it);
        return new() { (t, $"{StripOuterParens(t)};\n", false, TyOf(it)) };
    }

    /// <summary>
    /// The value-context form of a comma sequence: a C# tuple yields all elements in
    /// order and the <c>.ItemN</c> picks the last (the comma's value). A POINTER
    /// operand can't be a <c>ValueTuple</c> type argument (CS0306), so it's cast to
    /// <c>nint</c> (pointer-width, round-trips) for the tuple; when the LAST operand
    /// (the comma's value) is a pointer, the <c>.ItemN</c> is cast back to that
    /// pointer type. Lua's <c>check_exp(c, e)</c> = <c>(lua_assert(c), e)</c> with a
    /// pointer <c>e</c>, nested in value position (so it can't be statement-hoisted).
    /// ≤7 operands.
    /// </summary>
    /// <summary>
    /// Render a hoisting comma (void leading operand) as an immediately-invoked
    /// delegate for a value position that CAN'T lift statements (deep inside a
    /// short-circuiting condition — hoisting there would change WHEN the side
    /// effect runs). `((Func&lt;T&gt;)(() =&gt; { leadingStmts; return value; }))()`
    /// preserves evaluation order in place. The captured locals may be pointers
    /// (Lua's `lua_State* L`); modern C# allows that, and the immediate invoke
    /// keeps them valid. A POINTER value can't be a `Func&lt;&gt;` type argument
    /// (CS0306), so it round-trips through `nint`. NOTE: this allocates a delegate
    /// (+ closure) per evaluation — see C-SUPPORT.md; the statement-sink hoist path
    /// (assignment / return / expression statement) avoids it, and a future
    /// optimization could hoist more positions or emit a local function.
    /// </summary>
    private string SeqExprDelegate(EmitContent.SeqExpr se)
    {
        var body = string.Concat(se.LeadingStmts);
        // A POINTER value can be neither a tuple element nor a Func<> type argument
        // (CS0306) — round-trip through nint (its pointer type is known here).
        if (se.Ty is CType.Sized s && IsPointerCsType(s.CsType))
        {
            return $"(({s.CsType})((System.Func<nint>)(() => {{ {body}return (nint)({se.ValueExpr}); }}))())";
        }
        // Otherwise: run the leading statements in a fixed-`int` delegate (its return
        // is ignored), then a tuple picks the value — so C# INFERS the value's type,
        // and we needn't synthesize it (a void operand made the tuple impossible, but
        // the VALUE is a normal expression). Captured locals may be pointers; the
        // immediate invoke keeps them valid.
        return $"(((System.Func<int>)(() => {{ {body}return 0; }}))(), {se.ValueExpr}).Item2";
    }

    private string CommaTupleText(IReadOnlyList<string> ops, IReadOnlyList<CType?>? types = null)
    {
        if (ops.Count > 7)
        {
            throw new CompileException(
                "comma-operator chains longer than 7 operands aren't supported");
        }
        // A pointer-shaped operand (a raw `T*`, a function pointer `delegate*<…>`,
        // OR a pointer / fn-ptr TYPEDEF — `StkId` → `StackValue*`, `lua_CFunction`
        // → `delegate*<…>`) can't be a ValueTuple type argument; return the type
        // name so it round-trips through nint. Null for a non-pointer operand.
        string? PtrAt(int i) =>
            types is { } t && i < t.Count && t[i] is CType.Sized { CsType: var cs }
            && IsPointerCsType(cs) ? cs : null;
        var elems = new List<string>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            var op = StripOuterParens(ops[i]);
            elems.Add(PtrAt(i) is not null ? $"(nint)({op})" : op);
        }
        var tuple = $"({string.Join(", ", elems)}).Item{ops.Count}";
        return PtrAt(ops.Count - 1) is { } lastPtr ? $"(({lastPtr})({tuple}))" : tuple;
    }

    // True when a lowered C# type is pointer-shaped — a raw object pointer (`T*`),
    // a function pointer (`delegate*<…>`), or a typedef for either (a scalar
    // pointer typedef resolves to `…*`; a fn-ptr typedef is registered in
    // `_pointerTypedefNames`). Such a type can't be a ValueTuple / generic type
    // argument (CS0306), so the comma-tuple lowering round-trips it through nint.
    private bool IsPointerCsType(string cs)
    {
        if (cs.EndsWith("*", StringComparison.Ordinal)
            || cs.StartsWith("delegate*", StringComparison.Ordinal)
            || _pointerTypedefNames.Contains(cs))
        {
            return true;
        }
        var r = ResolveTypedef(cs);
        return r.EndsWith("*", StringComparison.Ordinal)
            || r.StartsWith("delegate*", StringComparison.Ordinal)
            || _pointerTypedefNames.Contains(r);
    }

    /// <summary>
    /// True when a lowered C# type is a FUNCTION pointer specifically — a bare
    /// <c>delegate*&lt;…&gt;</c> or a typedef that resolves to one (<c>lua_CFunction</c>
    /// → <c>delegate*&lt;lua_State*, int&gt;</c>). Distinct from <see cref="IsPointerCsType"/>,
    /// which also matches data pointers: <c>*p</c> on a data pointer is a real
    /// dereference, but on a function pointer it's a C no-op (the function decays
    /// back to the pointer).
    /// </summary>
    private bool IsFnPtrCsType(string cs) =>
        cs.StartsWith("delegate*", StringComparison.Ordinal)
        // A fn-ptr typedef (`lua_CFunction`, `BinOp`) is a `using` alias, NOT in
        // _typedefUnderlying — its name is registered in _pointerTypedefNames. A
        // simple-typedef CHAIN onto one resolves first, then is looked up there.
        || _pointerTypedefNames.Contains(cs)
        || _pointerTypedefNames.Contains(ResolveTypedef(cs))
        || ResolveTypedef(cs).StartsWith("delegate*", StringComparison.Ordinal);

    /// <summary>
    /// The raw comma operands an Item carries, whether it's a bare comma sequence
    /// (<c>a, b</c> — a CommaSeq straight from CommaOp) or a PARENTHESIZED one
    /// (<c>(a, b)</c> — a Text carrying CommaOps, value being the tuple). Null for
    /// a non-comma. Lets a discard context (statement / <c>(void)</c> cast /
    /// controlling expression) recover the operands and emit them as statements.
    /// </summary>
    private static IReadOnlyList<string>? CommaOpsOf(Item it) => it.Content switch
    {
        EmitContent.CommaSeq cs => cs.Operands,
        EmitContent.Text { CommaOps: { } ops } => ops,
        _ => null,
    };

    /// <summary>
    /// Emit the leading (all-but-last) operands of a comma sequence as discard
    /// statements — the C semantics of a comma's non-last operands (evaluated for
    /// side effects, value discarded). A void operand (a void call, or a <c>(void)</c>
    /// cast that lowered to its operands) is legal here, unlike in the tuple form.
    /// </summary>
    private static string CommaLeadingStmts(IReadOnlyList<string> ops)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < ops.Count - 1; i++)
        {
            sb.Append(StripOuterParens(ops[i])).Append(";\n");
        }
        return sb.ToString();
    }

    public EmitContent Visit(C.Paren n)
    {
        // Comma operator in value position: C# has no comma operator, but a
        // tuple evaluates its elements left-to-right and yields them all, so
        // `(a, b, c).Item3` reproduces "evaluate a, b, c in order, value is c".
        // (≤7 operands — ValueTuple's direct ItemN range; longer chains don't
        // occur in real code, so fail clearly rather than emit a wrong shape.)
        // Pointer/void operands can't go in a C# tuple — those reach Roslyn as a
        // type error rather than a silent miscompile, which is the safe failure.
        if (n.Arg1.Content is EmitContent.CommaSeq seq)
        {
            // Value form is the tuple (consumers reach it via T()); ALSO carry the
            // raw operands so a discard context (statement / `(void)` cast /
            // controlling expression) can re-emit them as statements — where a void
            // side-effect operand is legal but a tuple element isn't.
            // When a NON-LAST operand is VOID (a `void` call — Lua's `(checktab(…),
            // luaL_len(…))` / `(luaO_tostring(…), 1)`), it can't be a tuple element
            // (CS8210), so the VALUE form instead becomes an immediately-invoked
            // delegate (runs the leading operands, yields the last). CommaOps stays
            // the raw operands, so a discard context never touches the delegate —
            // only a genuine value consumer does. A void operand that's LAST makes
            // the whole comma void; that only legitimately reaches a discard context
            // (handled via CommaOps), so it stays out of the delegate guard.
            var voidNonLast = seq.OperandTypes is { } ots && seq.Operands.Count >= 2
                && ots.Take(seq.Operands.Count - 1).Any(t => t is CType.Sized { CsType: "void" });
            string value;
            if (voidNonLast)
            {
                var leading = seq.Operands.Take(seq.Operands.Count - 1)
                    .Select(o => $"{StripOuterParens(o)};\n").ToList();
                value = SeqExprDelegate(new EmitContent.SeqExpr(leading, seq.Operands[^1], seq.LastType));
            }
            else
            {
                value = CommaTupleText(seq.Operands, seq.OperandTypes);
            }
            return new EmitContent.Text(value, Ty: seq.LastType, CommaOps: seq.Operands);
        }
        // A redundant paren around a void-typed ternary stays void — Lua wraps the
        // barrier macro in parens (`(cond ? … : cast_void(0))`), and a nested arm is
        // itself a parenthesised void ternary. Keep the VoidCond so the statement
        // context (and an enclosing void ternary's IsVoidBranch) still recognise it.
        if (n.Arg1.Content is EmitContent.VoidCond pvc)
        {
            return new EmitContent.VoidCond($"({pvc.Value})", pvc.IfStatement);
        }
        // A parenthesised value-context comma needing hoisting (Lua's
        // `luaM_newvectorchecked` IS wrapped in parens) — keep the SeqExpr so the
        // statement sink still recovers the leading statements + value.
        if (n.Arg1.Content is EmitContent.SeqExpr pse)
        {
            return new EmitContent.SeqExpr(pse.LeadingStmts, $"({pse.ValueExpr})", pse.Ty);
        }
        // Propagate the volatile-lvalue and pointee-volatile tags through the parens
        // so `(x) = …` / `&(x)` see the write/address context and `(*p)` / `(p)[i]`
        // still fence (the bare lvalue / pointee-ness is unchanged by parenthesising).
        // Also carry CommaOps through a REDUNDANT paren (`((a, b))`, or the outer
        // paren of `cast_void`'s `((void)(x))`) so a discard context still recovers
        // the operands — a paren around a comma is still that comma.
        return new EmitContent.Text($"({T(n.Arg1)})", EnumOf(n.Arg1), TyOf(n.Arg1),
            VolatileLValue: VLValueOf(n.Arg1), VolatilePointee: VolatilePointeeOf(n.Arg1),
            AtomicLValue: ALValueOf(n.Arg1), ConstInt: ConstOfItem(n.Arg1),
            CommaOps: CommaOpsOf(n.Arg1),
            // Keep the `(void)X` discard-cast tag through redundant parens — Lua's
            // `cast_void(x)` = `((void)(x))`, and a ternary arm is that wrapped again
            // — so an enclosing void ternary's IsVoidBranch still recognises it.
            VoidCast: n.Arg1.Content is EmitContent.Text { VoidCast: true },
            // A paren around a constant expression is still constant (so a cast of a
            // parenthesised constant — Lua's `cast_byte((~mask))` — wraps correctly).
            ConstExpr: IsConstExpr(n.Arg1));
    }

    // C23 keyword constants (only reached under -std=c23, via the rewriter's
    // ID->terminal promotion). `_Bool` lowers to C# `bool`, so the boolean
    // literals lower to their C# spellings; `nullptr` matches <stddef.h>'s
    // `#define NULL null` lowering. Pre-C23 these spellings stay ID and reach
    // the macro-supplied values through `Visit(C.Var)` instead.
    // C23 `true`/`false` lower to the integer literals 1/0 (normalized through
    // CBool's int conversion when stored to a `_Bool`, and usable directly as
    // ints — matching C, where `true`/`false` have value 1/0). Emitting them as
    // ints (not the C# `true`/`false` keywords) is also what lets a user
    // identifier spelled `true`/`false` be @-escaped: the keyword spelling now
    // only ever reaches Visit(Var) when it's a real identifier.
    public EmitContent Visit(C.LitTrue n)    => "1";
    public EmitContent Visit(C.LitFalse n)   => "0";
    public EmitContent Visit(C.LitNullptr n) => "null";

    // `_Static_assert(expr [, "msg"]);` (C11; C23 lowercase `static_assert`
    // and message-optional form). A compile-time-only construct with no
    // observable runtime behaviour, so for any program where the assertion
    // holds the correct emit is *nothing*. dotcc has no constant evaluator
    // yet, so we don't verify the condition — we drop it to a self-delimiting
    // block comment (carrying the message for traceability) and let Roslyn
    // compile the rest. This is observably equivalent to clang for every valid
    // program; a *false* static_assert that clang would reject is silently
    // accepted (documented limitation in C-SUPPORT.md). Works at both file
    // scope (Fn) and block scope (Stmt) — the comment is inert in either.
    // `_Static_assert` (and the C23 lowercase `static_assert` promoted onto it)
    // is a C11 feature — gate under < c11. Arg0 is the `_Static_assert` token.
    public EmitContent Visit(C.StaticAssert n)          { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(T(n.Arg4)); }
    public EmitContent Visit(C.StaticAssertNoMsg n)     { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(null); }
    public EmitContent Visit(C.StaticAssertStmt n)      { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(T(n.Arg4)); }
    public EmitContent Visit(C.StaticAssertStmtNoMsg n) { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(null); }

    /// <summary>
    /// Render a dropped <c>_Static_assert</c> as an inert block comment.
    /// <paramref name="rawMsg"/> is the raw STRING lexeme (quotes included) or
    /// null for the C23 message-less form. Any <c>*/</c> in the message is
    /// neutralised so it can't close the comment early.
    /// </summary>
    private static string StaticAssertComment(string? rawMsg)
    {
        var tail = rawMsg is null ? "" : ": " + rawMsg.Replace("*/", "* /");
        return $"/* static_assert (compile-time, not evaluated){tail} */";
    }

    /// <summary>
    /// Identity for now — kept as a seam in case future grammar features
    /// need to remap a C identifier to a different C# name before emit.
    /// </summary>
    /// <remarks>
    /// Previously this remapped <c>printf → Printf</c>, <c>malloc → Malloc</c>,
    /// <c>free → Free</c> to reach BuildShell's top-level (uppercase) helper
    /// functions. After the DotCC.Libc unification, the emitted shell has
    /// <c>using static Libc;</c> which brings the lowercase C-spelled
    /// methods directly into scope — so the remapping became unnecessary.
    /// </remarks>
    private static string MapBuiltin(string name) => Id(name);

}
