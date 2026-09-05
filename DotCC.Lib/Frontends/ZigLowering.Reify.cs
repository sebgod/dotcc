#nullable enable

using System.Collections.Generic;
using System.Globalization;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The type-CONSTRUCTING builtins and the compile-time diagnostics (road-to-zig-std S7) —
/// the direction opposite to <c>@typeInfo</c>. S5 reads a type's description out; <c>@Int</c> builds
/// a type back from one, and <c>@compileError</c> is how a comptime program rejects the type it was
/// handed.
///
/// <para><b><c>@Int(signedness, bits)</c></b> (207 uses) is the whole of the family that pays. The
/// other constructors are measured and cut: <c>@Pointer</c> ×14, <c>@Struct</c> ×5, <c>@Enum</c> ×4,
/// <c>@Union</c> ×3 — and each of those takes comptime AGGREGATE arguments (an attributes struct, a
/// <c>[]const []const u8</c> of field names, a <c>*const [N]type</c>), which is a comptime-value
/// engine dotcc does not have. They get loud cuts naming what they would need, not half-built
/// reifiers.</para>
///
/// <para><b>The exact-width rule carries over.</b> <c>@bitSizeOf</c> (431 uses, and the operand of
/// most <c>@Int</c> calls: <c>@Int(.unsigned, @bitSizeOf(T))</c>) is answered from the SAME declared
/// spelling <c>@typeInfo(T).int.bits</c> uses (<see cref="DeclaredBitsOfTypeArg"/>), because dotcc
/// widens an arbitrary-width <c>uN</c> to the smallest standard width and the lowered type alone
/// would answer 32 where zig says 21. Composing the two round-trips: <c>@Int(.unsigned,
/// @bitSizeOf(u21))</c> is <c>u21</c> again, and the width rides the binding it is given.</para>
///
/// <para><b><c>@compileError</c> fires where it is REACHED, not where it is written.</b> That is not
/// a dotcc leniency — it is what the language reference specifies: "this function, WHEN SEMANTICALLY
/// ANALYZED, causes a compile error… there are several ways that code avoids being semantically
/// checked, such as using <c>if</c> or <c>switch</c> with compile time constants". Lowering IS
/// dotcc's semantic analysis, and the comptime folds already installed (S5a selects one <c>switch</c>
/// prong, W3a folds a comptime <c>if</c> in a generic instance, S2 never lowers an unreferenced
/// module decl) are exactly the ways code avoids it. Measured against the pinned std, that is where
/// the uses live: of 595, <b>231 are an <c>else =&gt;</c> prong</b> of a folded <c>switch</c> — the
/// arm S5a never lowers — and the rest are comptime <c>if</c> guards inside <c>comptime T: type</c>
/// functions. So no poison value is needed for them; raising the diagnostic on sight is correct.
///
/// <para>ONE shape does need a poison, and it is the one the campaign will actually walk into: a
/// top-level <c>pub const NAME = @compileError("use X instead");</c> — a deprecation tombstone, 25
/// of them in the pin, in <c>std/meta.zig</c> and <c>std/os/windows.zig</c> among others. Zig
/// analyses a declaration only when something references it, so the tombstone is inert until named;
/// firing at the declaration would make importing those modules impossible. Hence
/// <see cref="_poisonedConsts"/>: the binding records the message and emits nothing, and the
/// diagnostic is raised at the REFERENCE — in value position and in type position alike.</para></para>
///
/// <para>Nothing here reaches the IR: a constructed type is a <see cref="CType"/>, a width is a
/// literal, and a diagnostic throws. The value/type firewall in the comptime interpreter is
/// untouched, exactly as in S5/S6.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Each name bound to a <c>@compileError</c>, mapped to the author's message — a
    /// DEPRECATION TOMBSTONE (<c>pub const MACH_PORT_RIGHT = @compileError("use MACH.PORT.RIGHT");</c>).
    /// The declaration emits nothing and raises nothing; naming it raises. Name-keyed and
    /// function-flat like <see cref="_comptimeValues"/> / <see cref="_typeAliases"/> — the same W1
    /// leniency, and harmless here for the same reason: a poisoned name has no runtime symbol, so
    /// every lookup is guarded on the ordinary resolution having already missed.</summary>
    private readonly Dictionary<string, string> _poisonedConsts = new(System.StringComparer.Ordinal);

    /// <summary>The comptime eval-step budget in force, raised by <c>@setEvalBranchQuota</c> and never
    /// lowered (see <see cref="SetEvalBranchQuota"/>). Starts at the
    /// <see cref="IrBuilder.DefaultComptimeStepBudget"/> dotcc has always used.</summary>
    private long _evalStepBudget = IrBuilder.DefaultComptimeStepBudget;

    /// <summary>The width each lowered <c>@Int(…)</c> SITE was constructed with, keyed by AST
    /// reference like <see cref="_inlineStructNames"/>. dotcc widens <c>u21</c> to a 32-bit
    /// <c>uint</c>, so the constructed <see cref="CType"/> no longer knows it was built with 21 —
    /// this is how the width reaches <see cref="DeclaredBitsOfTypeArg"/> and therefore rides the
    /// binding, exactly as a source SPELLING does (S5b). Recording it at the site also means the
    /// width operand is evaluated once, not again per query.</summary>
    private readonly Dictionary<Item, int> _reifiedIntBits = new(ReferenceEqualityComparer.Instance);

    // ---- @Int and the rest of the reification family ----------------------

    /// <summary>Recognize and lower a builtin that CONSTRUCTS a type, for the type positions
    /// (<see cref="LowerType"/> and the type-alias RHS). <c>@Int</c> builds; every other member of the
    /// family is a loud cut naming what it would take. Returns false for a builtin that is not one of
    /// them (<c>@TypeOf</c>, <c>@This</c> — handled by their own cases), so the caller falls through
    /// to its own dispatch.</summary>
    private bool TryLowerReifyBuiltin(Zig.BuiltinCall b, out CType type)
    {
        type = CType.Void;
        var name = Tok(b.Arg0);
        switch (name)
        {
            case "@Int":
                type = IntBuiltinType(b);
                return true;

            // The AGGREGATE constructors. Every one takes comptime aggregate arguments — a
            // `[]const []const u8` of field names, a `*const [N]type`, an attributes struct — which is
            // the comptime-value engine dotcc has not built (S5 folds types, not aggregate VALUES).
            // Reifying from a partially-understood description would emit a wrong layout, so these are
            // named cuts. Their measured use count is the reason the order of work is what it is.
            case "@Struct" or "@Union" or "@Enum" or "@Pointer" or "@Fn" or "@Tuple":
                throw new IrUnsupportedException(
                    $"zig `{name}(…)` reifies a type from comptime AGGREGATE arguments (field-name and "
                    + "field-type arrays, an attributes struct), which dotcc has no comptime-aggregate engine "
                    + "for — `@Int` is the member of the family that is modeled (road-to-zig-std S7). Spell the "
                    + "type, or build it with `@Int` when it is an integer");

            // SIMD. Not a gap in the reflection arc — a whole execution model (vector registers, the
            // `@reduce`/`@shuffle`/`@splat` family, per-lane semantics) that dotcc's scalar C# backend
            // does not have. Named separately so its 475 uses do not read as "S7 is unfinished".
            case "@Vector":
                throw new IrUnsupportedException(
                    "zig `@Vector(len, T)` is a SIMD vector type — dotcc's backend is scalar and models no "
                    + "vector types (`@splat`, `@reduce`, `@shuffle` likewise). Use an array `[len]T` and a loop");

            default:
                return false;
        }
    }

    /// <summary>Lower <c>@Int(signedness, bits)</c> to the integer <see cref="CType"/> it names —
    /// <c>@Int(.unsigned, 18)</c> is <c>u18</c>, so it goes through the SAME
    /// <see cref="TryArbitraryWidthInt"/> widening a spelled <c>u18</c> does (there is one widening
    /// rule in the front end, and this is it). The declared width is not lost: a binding records it
    /// via <see cref="DeclaredBitsOfTypeArg"/>, so <c>@typeInfo(@Int(.unsigned, 21)).int.bits</c>
    /// answers 21.</summary>
    private CType IntBuiltinType(Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 2)
        {
            throw new IrUnsupportedException(
                $"zig `@Int` expects (signedness, bits); got {args.Count} argument(s)");
        }
        var signed = ComptimeSignedness(args[0]);
        var bits = ComptimeBitCount(args[1], "@Int");
        _reifiedIntBits[call.Arg2] = bits;   // the width the site was built with — see _reifiedIntBits
        var spelling = (signed ? "i" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        if (!TryArbitraryWidthInt(spelling, out var type))
        {
            throw new IrUnsupportedException(
                $"zig `@Int(.{(signed ? "signed" : "unsigned")}, {bits})`: dotcc models integer widths 1..128 "
                + $"(`{spelling}` is outside that range; a wider one needs BigInteger)");
        }
        return type;
    }

    /// <summary>Evaluate <c>@Int</c>'s first argument to a signedness. Two producers, matching how the
    /// pinned std writes it: the enum literal <c>.unsigned</c> / <c>.signed</c> (190 of the 207 uses)
    /// and a folded <c>@typeInfo(T).int.signedness</c> (the remaining 17, via
    /// <see cref="TryEvalComptimeTag"/> — which is exactly recoverable, since dotcc's width widening
    /// never changes a type's signedness).</summary>
    private bool ComptimeSignedness(Item item)
    {
        if (item.Content is Zig.Grouped g) { return ComptimeSignedness(g.Arg1); }
        var tag = item.Content is Zig.EnumLit lit
            ? Tok(lit.Arg1)
            : TryEvalComptimeTag(item, out var evaluated, out _) ? evaluated : null;
        return tag switch
        {
            "unsigned" => false,
            "signed" => true,
            null => throw new IrUnsupportedException(
                "zig `@Int`: the signedness must be comptime-known — `.unsigned` / `.signed`, or a "
                + "`@typeInfo(T).int.signedness` that folds"),
            _ => throw new IrUnsupportedException(
                $"zig `@Int`: `.{tag}` is not a signedness — expected `.unsigned` or `.signed`"),
        };
    }

    /// <summary>Evaluate a comptime BIT COUNT — <c>@Int</c>'s width argument. Any expression the
    /// const-folder can settle works, which is what the measured shapes need:
    /// <c>@bitSizeOf(T)</c> (the commonest by far), <c>@typeInfo(T).int.bits</c>, a comptime
    /// parameter, a literal, and arithmetic over those (<c>bits * 2</c>, <c>info.bits + 1</c>).
    /// Lowered through <see cref="LowerExpr"/> exactly as <see cref="ConstEvalArraySize"/> does, so
    /// every fold already installed is available here for free.</summary>
    private int ComptimeBitCount(Item item, string what)
    {
        if (_ir.ConstEval(LowerExpr(item)) is not { } n)
        {
            throw new IrUnsupportedException(
                $"zig `{what}`: the bit width must be a comptime-known integer (a literal, `@bitSizeOf(T)`, "
                + "`@typeInfo(T).int.bits`, a `comptime` parameter, or arithmetic over those)");
        }
        if (n < 0 || n > int.MaxValue)
        {
            throw new IrUnsupportedException($"zig `{what}`: the bit width {n} is out of range");
        }
        return (int)n;
    }

    // ---- @bitSizeOf -------------------------------------------------------

    /// <summary>Lower <c>@bitSizeOf(T)</c> — zig's "bits this type occupies as a field of a packed
    /// struct" — to a comptime literal. Answered from the type's DECLARED SPELLING first
    /// (<see cref="DeclaredBitsOfTypeArg"/>, the S5b machinery that also travels with a
    /// <c>comptime T: type</c> binding), because dotcc widens <c>u21</c> to a 32-bit <c>uint</c> and
    /// the lowered type would answer 32 where zig says 21. Only when no spelling is available does it
    /// fall back to the lowered type, and only for the kinds whose width is EXACTLY recoverable —
    /// which the measured operands are almost entirely made of (<c>usize</c> ×78, a
    /// <c>comptime T: type</c> ×69, integer aliases, and enums).
    ///
    /// <para>Typed <c>int</c> rather than the <c>comptime_int</c> zig gives it, for the same reason
    /// <c>@typeInfo(T).array.len</c> is: the literal then renders bare and C#'s implicit CONSTANT
    /// conversion lets it land in any integer sink, where a suffixed one would not (CS0266).</para></summary>
    private CExpr BitSizeOfBuiltin(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@bitSizeOf` expects (type); got {args.Count} argument(s)");
        }
        var bits = ZigBitWidth(args[0]);
        return new LitInt(bits.ToString(CultureInfo.InvariantCulture), bits) { Type = CType.Int };
    }

    /// <summary>The zig bit width of a type ARGUMENT: its declared spelling when there is one, else
    /// the exactly-recoverable width of the lowered type. Throws the fidelity cut when neither
    /// answers — the same judgement <c>@typeInfo(T).int.bits</c> makes, and for the same reason: a
    /// silently-32 answer where zig says 21 is the one outcome worth refusing.</summary>
    private int ZigBitWidth(Item typeAst)
    {
        if (DeclaredBitsOfTypeArg(typeAst) is { } declared) { return declared; }
        var type = LowerType(typeAst);
        if (ExactBitWidth(type) is { } exact) { return exact; }
        throw new IrUnsupportedException(
            $"zig `@bitSizeOf({type.Describe()})`: only a scalar (integer, float, bool, void, pointer) or an "
            + "enum has a bit width dotcc can state exactly. An aggregate's is its byte size in bits, and dotcc "
            + "byte-packs its own layout, so it may disagree with zig — use `@sizeOf(T) * 8` to opt into that "
            + "approximation deliberately (road-to-zig-std S7)");
    }

    /// <summary>The bit width of a lowered type when it is EXACTLY recoverable, else null. A pointer
    /// is 8 bytes on this LP64 target; an enum is its tag type; <c>bool</c> is one bit (zig's
    /// packed-field width, not the byte dotcc stores it in) and <c>void</c> is zero. An aggregate
    /// returns null deliberately — see the cut in <see cref="ZigBitWidth"/>.</summary>
    private static int? ExactBitWidth(CType type)
    {
        var t = type.Unqualified;
        if (t.Equals(CType.Bool)) { return 1; }
        return t switch
        {
            CType.VoidType => 0,
            CType.Pointer => 64,
            CType.Enum e => ExactBitWidth(e.Underlying),
            CType.Prim p => p.Bytes * 8,
            _ => null,
        };
    }

    // ---- @compileError / @compileLog / @setEvalBranchQuota ----------------

    /// <summary>Raise a <c>@compileError(msg)</c>. Reaching this point IS the semantic analysis zig
    /// specifies the diagnostic on: every way a real compiler avoids analysing the call — a folded
    /// <c>switch</c> prong, a comptime <c>if</c>, an unreferenced declaration — is a fold that already
    /// ran, so a call that arrives here is one zig would have raised too. The author's own message is
    /// carried through verbatim when it is comptime-readable, because that message is the entire point
    /// of the builtin ("this type does not support …", naming the type via <c>++ @typeName(T)</c>).</summary>
    private CExpr CompileErrorBuiltin(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@compileError` expects (message); got {args.Count} argument(s)");
        }
        throw new IrUnsupportedException("zig `@compileError`: " + (ComptimeMessageText(args[0]) ?? UnreadableMessage));
    }

    /// <summary>What a <c>@compileError</c> / <c>@compileLog</c> message reads as when it is not
    /// comptime-readable in the forms modeled — better than dropping the diagnostic, and it still
    /// tells the reader the program deliberately rejected this instantiation.</summary>
    private const string UnreadableMessage = "(the message is not a comptime-readable string here)";

    /// <summary>Read a comptime message string as PLAIN text. The literal and captured-name cases are
    /// <see cref="ComptimeStringArg"/>'s; this adds the two shapes a diagnostic message is actually
    /// built from — <c>"prefix " ++ x</c> concatenation and <c>@typeName(T)</c> — so the type that
    /// failed appears in the error the way the author wrote it. Null when some part is not readable,
    /// so the caller substitutes rather than throws a second, less useful error.</summary>
    private string? ComptimeMessageText(Item item)
    {
        switch (item.Content)
        {
            case Zig.Grouped g:
                return ComptimeMessageText(g.Arg1);
            case Zig.Concat c:
                return ComptimeMessageText(c.Arg0) is { } left && ComptimeMessageText(c.Arg2) is { } right
                    ? left + right
                    : null;
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@typeName":
            {
                var nameArgs = Flatten(b.Arg2);
                return nameArgs.Count == 1 ? DiagnosticTypeName(nameArgs[0]) : null;
            }
            default:
                return ComptimeStringArg(item);
        }
    }

    /// <summary>Name a type for a DIAGNOSTIC — <c>@compileError("unsupported type: " ++ @typeName(T))</c>,
    /// where naming the offending type is the whole value of the message. Deliberately more lenient
    /// than the <c>@typeName</c> VALUE path (<see cref="ZigTypeSpelling"/>), which refuses an
    /// unspellable type because emitting a wrong name into the program would be a real defect: here the
    /// name is prose, and an approximate one beats losing the author's message. The source spelling
    /// wins when there is one; a <c>comptime T: type</c> is named from its resolved type, honouring the
    /// declared width so a <c>u21</c> instantiation says <c>u21</c> rather than the <c>u32</c> it
    /// widened to (the <see cref="MangleTypeSeed"/> rule, for the same reason).</summary>
    private string DiagnosticTypeName(Item typeAst)
    {
        if (ZigTypeSpelling(typeAst) is { } spelled) { return spelled; }
        var type = LowerType(typeAst).Unqualified;
        if (DeclaredBitsOfTypeArg(typeAst) is { } bits
            && type is CType.Prim { Integer: true, Name: not "_Bool" } p)
        {
            return (p.Signed ? "i" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        }
        return MangleType(type);
    }

    /// <summary>Lower <c>@compileLog(…)</c>. Zig prints its arguments at compile time AND fails the
    /// build ("a compilation error is added to the build, pointing to the compile log statement", so
    /// a log left in a codebase cannot be missed) — so a loud error carrying the logged text is the
    /// faithful lowering, not a leniency.</summary>
    private CExpr CompileLogBuiltin(IReadOnlyList<Item> args)
    {
        var parts = new List<string>(args.Count);
        foreach (var arg in args) { parts.Add(ComptimeMessageText(arg) ?? "?"); }
        throw new IrUnsupportedException(
            "zig `@compileLog(" + string.Join(", ", parts) + ")`: a compile log fails the build in zig too — "
            + "remove it once the value has been read");
    }

    /// <summary>Lower <c>@setEvalBranchQuota(n)</c> — raise the comptime evaluation budget, never
    /// lower it, which is zig's own rule ("if the new_quota is smaller than the default quota or a
    /// previously explicitly set quota, it is ignored").
    ///
    /// <para>The units differ and that is fine in this direction: zig counts BACKWARD BRANCHES, dotcc
    /// counts eval STEPS — a strictly finer unit, so a quota that suffices in zig always suffices here
    /// when taken as a floor. dotcc's default budget is already far above the 2,000–100,000 the pinned
    /// std asks for, so in practice every call is honored and none of them changes anything; what
    /// changes is that 80 uses stop being a loud cut. Yields <c>void</c> in zig, so it is a STATEMENT
    /// (see the <see cref="LowerStmt"/> case) and emits nothing.</para></summary>
    private void SetEvalBranchQuota(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@setEvalBranchQuota` expects (quota); got {args.Count} argument(s)");
        }
        var quota = ComptimeBitCount(args[0], "@setEvalBranchQuota");
        if (quota > _evalStepBudget)
        {
            _evalStepBudget = quota;
            _ir.ComptimeStepBudget = quota;
        }
    }

    // ---- the poisoned declaration -----------------------------------------

    /// <summary>Raise the diagnostic a poisoned name carries, if it is one. Consulted where an
    /// ordinary resolution has already missed — a value reference and a type reference both — so a
    /// tombstone (<c>pub const MACH_PORT_RIGHT = @compileError("use MACH.PORT.RIGHT");</c>) is inert
    /// until something names it, exactly as zig's lazy declaration analysis makes it.</summary>
    private void RaiseIfPoisoned(string name)
    {
        if (_poisonedConsts.TryGetValue(name, out var message))
        {
            throw new IrUnsupportedException($"zig `{name}` is declared as `@compileError`: " + message);
        }
    }
}
