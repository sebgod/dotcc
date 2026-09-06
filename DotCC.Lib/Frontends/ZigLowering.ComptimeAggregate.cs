#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Comptime STRUCT CONSTANTS — a <c>const c: Cpu = .{ .arch = .aarch64 };</c> whose fields
/// can be read at lowering time (road-to-zig-std S3a). The piece S5 deliberately left out: it folds
/// TYPES exactly, and scalar values, but an aggregate VALUE had no comptime domain at all.
///
/// <para><b>Why this is the gate on the synthetic <c>builtin</c> module.</b> Measured on the pinned
/// std, the two things every platform-conditional in std asks are <c>builtin.cpu.arch</c> (172 uses)
/// and <c>builtin.os.tag</c> (92) — both a FIELD READ of a struct constant. Without a comptime
/// answer they lower to a runtime comparison, and then <em>both</em> arms of every
/// <c>if (builtin.cpu.arch == .x86_64)</c> get lowered — dragging in the inline asm, syscalls and
/// per-arch code that the branch exists precisely to avoid. A platform query has to fold or the
/// module graph cannot walk std at all.</para>
///
/// <para><b>What is recorded, and what is not.</b> A binding is recorded as a SIDE EFFECT and the
/// ordinary runtime decl still emits (the same shape <see cref="EvalComptimeValue"/>'s scalar
/// recording uses): a struct constant is a perfectly good runtime value too, and folding is an
/// answer to a comptime QUESTION, not a decision to erase the declaration.
///
/// <para><b>Only a MODULE-QUALIFIED read folds</b> — <c>builtin.cpu.arch</c>, not a same-file
/// <c>cpu.arch</c>. That is a deliberate line, not an oversight. Every other name-keyed comptime map
/// here is guarded on the name NOT resolving to a runtime symbol, which is what keeps a
/// function-flat map from answering for an unrelated local of the same name; a constant that also
/// emits its declaration always HAS such a symbol, so the same guard would (correctly) refuse it.
/// Reaching through an import has no such ambiguity: the module qualifies the name, and a
/// cross-module constant had no lowering before this at all, so nothing that worked changes shape.
/// The measured need is entirely module-qualified — 264 platform queries through
/// <c>builtin</c>.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>One field of a comptime aggregate — exactly one of the three is set: an enum-literal
    /// <see cref="Tag"/> (what a platform query actually asks for), a scalar <see cref="Value"/>, or a
    /// <see cref="Nested"/> aggregate (so <c>target.cpu.arch</c> chains).</summary>
    private sealed record ZigAggField(string? Tag, CExpr? Value, ZigComptimeAggregate? Nested);

    /// <summary>A comptime aggregate value: field name → what it holds. Field ORDER is irrelevant
    /// here (unlike the S5c member lists, which report declaration order), so a plain map is the whole
    /// model.</summary>
    private sealed record ZigComptimeAggregate(Dictionary<string, ZigAggField> Fields);

    /// <summary>Each name bound to a comptime aggregate. Name-keyed and function-flat like
    /// <see cref="_comptimeValues"/> / <see cref="_typeAliases"/> — the W1 leniency — and read only
    /// through guards that require the ordinary symbol resolution to have run first, so a runtime
    /// local never resolves here by accident.</summary>
    private readonly Dictionary<string, ZigComptimeAggregate> _comptimeAggregates =
        new(System.StringComparer.Ordinal);

    /// <summary>Read a struct-literal RHS as a comptime aggregate, or null when any field is not
    /// comptime-readable (then it is an ordinary runtime literal and nothing is recorded). Both
    /// spellings are accepted — the anonymous <c>.{ … }</c> that a typed <c>const</c> annotation
    /// result-locates, and the explicit <c>T{ … }</c>. Never throws: it is called speculatively while
    /// recording a binding, in every pass.</summary>
    private ZigComptimeAggregate? TryEvalComptimeAggregateLiteral(Item rhs)
    {
        if (StructInitFields(rhs) is not { } inits) { return null; }

        var fields = new Dictionary<string, ZigAggField>(System.StringComparer.Ordinal);
        foreach (var init in Flatten(inits))
        {
            // A POSITIONAL element means this is a tuple, not a named-field struct — the platform
            // queries are all named, so a tuple is simply not this domain.
            if (init.Content is not Zig.FieldInit fi) { return null; }
            var fieldName = Tok(fi.Arg1);
            if (ReadComptimeAggField(fi.Arg3) is not { } field) { return null; }
            fields[fieldName] = field;
        }
        return new ZigComptimeAggregate(fields);
    }

    /// <summary>The field-init list of a struct literal in either spelling — the anonymous
    /// <c>.{ … }</c> a typed annotation result-locates, or an explicit <c>T{ … }</c> — unwrapping
    /// parentheses. Null when the expression is not a struct literal at all.</summary>
    private static Item? StructInitFields(Item expr) => expr.Content switch
    {
        Zig.Grouped g => StructInitFields(g.Arg1),
        Zig.AnonStructInit a => a.Arg2,
        Zig.TypedStructInit t => t.Arg2,
        _ => null,
    };

    /// <summary>Read ONE field initializer's value into a <see cref="ZigAggField"/>, or null when it
    /// is not comptime-readable. Three shapes, in the order a platform descriptor uses them: an enum
    /// literal (<c>.aarch64</c>), a nested struct literal (<c>.{ .tag = .windows }</c>), and a scalar
    /// the existing comptime-value folder settles (an integer, a string, a bool).</summary>
    private ZigAggField? ReadComptimeAggField(Item value)
    {
        if (value.Content is Zig.Grouped g) { return ReadComptimeAggField(g.Arg1); }
        if (value.Content is Zig.EnumLit lit) { return new ZigAggField(Tok(lit.Arg1), null, null); }
        if (TryEvalComptimeAggregateLiteral(value) is { } nested)
        {
            return new ZigAggField(null, null, nested);
        }
        if (EvalComptimeValue(value) is { } scalar) { return new ZigAggField(null, scalar, null); }
        // `true` / `false` are the one shape the scalar folder does not carry (it handles ints and
        // strings), and `link_libc` / `single_threaded` are exactly that — 145 uses between them.
        return value.Content switch
        {
            Zig.TrueLit => new ZigAggField(null, new LitBool(true) { Type = CType.Bool }, null),
            Zig.FalseLit => new ZigAggField(null, new LitBool(false) { Type = CType.Bool }, null),
            _ => null,
        };
    }

    /// <summary>Resolve an expression to a comptime aggregate — a name bound in THIS unit, a nested
    /// field of one, or a constant exported by an imported module (<c>builtin.cpu</c>). Returns null
    /// for anything else, so every caller can probe before its own lowering.</summary>
    private ZigComptimeAggregate? TryEvalComptimeAggregate(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.Grouped g:
                return TryEvalComptimeAggregate(g.Arg1);

            // Guarded on the name NOT resolving to a real symbol, for the reason every name-keyed
            // comptime lookup here is: the map is function-flat.
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null
                                   && _comptimeAggregates.TryGetValue(Tok(id.Arg0), out var bound):
                return bound;

            case Zig.Field f:
            {
                // A module-qualified constant first — `builtin.cpu` is a Field whose base is an import,
                // and no local binding claims that shape.
                if (ResolveModulePath(f.Arg0) is { Lowering: { } owner }
                    && owner.ResolveExportedComptimeAggregate(Tok(f.Arg2)) is { } exported)
                {
                    return exported;
                }
                // …then a nested field of an aggregate already in hand (`target.cpu`).
                return TryEvalComptimeAggregate(f.Arg0) is { } baseAgg
                    && baseAgg.Fields.TryGetValue(Tok(f.Arg2), out var nested)
                        ? nested.Nested
                        : null;
            }
        }
        return null;
    }

    /// <summary>Read a comptime aggregate FIELD — the shared half of the tag and value folds. Null
    /// when the expression is not a field access on a comptime aggregate, or names a field it does
    /// not have (a genuinely absent field is the ordinary member path's error to raise, not this
    /// one's: a partial descriptor should fall through, not claim the read and fail).</summary>
    private ZigAggField? TryReadComptimeAggregateField(Item expr)
    {
        if (expr.Content is Zig.Grouped g) { return TryReadComptimeAggregateField(g.Arg1); }
        if (expr.Content is not Zig.Field f) { return null; }
        // A module-qualified SCALAR constant (`builtin.link_libc`) — the base is the module itself, so
        // there is no aggregate to look inside.
        if (ResolveModulePath(f.Arg0) is { Lowering: { } owner }
            && owner.ResolveExportedComptimeField(Tok(f.Arg2)) is { } exported)
        {
            return exported;
        }
        return TryEvalComptimeAggregate(f.Arg0) is { } agg
            && agg.Fields.TryGetValue(Tok(f.Arg2), out var field)
                ? field
                : null;
    }

    /// <summary>A comptime aggregate this module exports under <paramref name="name"/>, for an
    /// importer's <c>builtin.cpu</c> navigation — the aggregate analogue of
    /// <see cref="ResolveExportedType"/>. A prepared module has already recorded every top-level
    /// binding, so this needs no on-demand work.</summary>
    private ZigComptimeAggregate? ResolveExportedComptimeAggregate(string name)
        => _comptimeAggregates.GetValueOrDefault(name);

    /// <summary>A comptime FIELD (a tag, a scalar, or a nested aggregate) this module exports under
    /// <paramref name="name"/> — what <c>builtin.link_libc</c> / <c>builtin.mode</c> read. Checks the
    /// tag domain first: a bare <c>pub const mode = .ReleaseFast;</c> is an enum literal, which is a
    /// TAG rather than a value, and is the shape most of the platform descriptor uses.</summary>
    private ZigAggField? ResolveExportedComptimeField(string name)
    {
        if (_comptimeEnumLits.TryGetValue(name, out var tag)) { return new ZigAggField(tag, null, null); }
        if (_comptimeAggregates.TryGetValue(name, out var agg)) { return new ZigAggField(null, null, agg); }
        return _comptimeValues.TryGetValue(name, out var value) ? new ZigAggField(null, value, null) : null;
    }

    /// <summary>Each name bound to a bare enum LITERAL (<c>const mode = .ReleaseFast;</c>). A tag is
    /// not a value — it has no type until it reaches a sink — so it cannot live in
    /// <see cref="_comptimeValues"/>; this is its own small domain, feeding the same
    /// <see cref="TryEvalComptimeTag"/> path a <c>@typeInfo</c> tag does.</summary>
    private readonly Dictionary<string, string> _comptimeEnumLits = new(System.StringComparer.Ordinal);

    /// <summary>Record what a <c>const</c> RHS contributes to the comptime AGGREGATE / TAG domains,
    /// as a SIDE EFFECT of binding it — the runtime decl still emits, because a struct constant and
    /// an enum constant are ordinary runtime values as well. Never throws (it only reads shapes it
    /// recognizes), so it is safe in every pass including the top-level pass 0.</summary>
    private void RecordComptimeAggregateBinding(string name, Item rhs)
    {
        // One reader for both positions: a top-level `const` and a nested field initializer accept the
        // same shapes (an aggregate, an enum literal, a scalar, a bool), so a `pub const link_libc =
        // true;` in the synthetic `builtin` module records exactly as `.{ .link_libc = true }` would.
        if (ReadComptimeAggField(rhs) is not { } field) { return; }
        if (field.Nested is { } agg) { _comptimeAggregates[name] = agg; }
        else if (field.Tag is { } tag) { _comptimeEnumLits[name] = tag; }
        else if (field.Value is { } value) { _comptimeValues[name] = value; }
    }

    /// <summary>Fold a comptime aggregate field used as a VALUE — <c>builtin.link_libc</c> in an
    /// <c>if</c>, a nested constant in arithmetic. Restricted to a MODULE-QUALIFIED read on purpose:
    /// a local struct constant's field access already lowers to a perfectly good runtime field read,
    /// and rewriting those to literals would change emitted code for programs that never asked a
    /// comptime question. A cross-module constant had no lowering at all before this, so there is
    /// nothing to preserve.</summary>
    private bool TryFoldImportedComptimeValue(Item expr, out CExpr value)
    {
        value = null!;
        if (expr.Content is Zig.Grouped g) { return TryFoldImportedComptimeValue(g.Arg1, out value); }
        if (expr.Content is not Zig.Field f) { return false; }
        // The read must be rooted at a module, at whatever depth — `builtin.link_libc`,
        // `builtin.cpu.arch`, `builtin.target.os.tag`.
        if (!IsModuleRooted(f.Arg0)) { return false; }
        if (TryReadComptimeAggregateField(expr) is not { } field) { return false; }
        if (field.Value is { } scalar) { value = scalar; return true; }
        // A TAG has no value form on its own; it folds only against another tag (see
        // TryEvalComptimeTag), so leave it to that path rather than inventing an integer here.
        return false;
    }

    /// <summary>True when an expression's ultimate base is an imported MODULE, so a field read on it
    /// is a cross-module constant rather than an ordinary member access.</summary>
    private bool IsModuleRooted(Item expr) => expr.Content switch
    {
        Zig.Grouped g => IsModuleRooted(g.Arg1),
        Zig.Ident => ResolveModulePath(expr) is not null,
        Zig.Field f => ResolveModulePath(expr) is not null || IsModuleRooted(f.Arg0),
        _ => false,
    };
}
