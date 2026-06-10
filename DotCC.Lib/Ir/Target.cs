#nullable enable

namespace DotCC.Ir;

/// <summary>
/// A backend's lexical projection of the neutral IR onto its own surface
/// language — the seam that turns dotcc from a 1×1 transpiler into an N×M frame.
/// The typed IR (<see cref="CExpr"/>/<see cref="CStmt"/>/<see cref="CType"/>)
/// carries only target-neutral C facts; everything that spells a particular
/// OUTPUT language lives behind this interface, so a second target (a C emitter,
/// a WebAssembly-text emitter, …) is "implement <see cref="ITarget"/> again"
/// rather than "untangle C# from the IR".
/// <para>The implementations live with their backends in <c>DotCC.Backends</c>
/// (<c>CSharpTarget</c> / <c>WatTarget</c>) — this file is contracts only, so
/// the IR namespace never depends on a particular output language.</para>
/// <para>For now the seam is the type-spelling map; the statement / expression
/// emitters (<c>CSharpBackend</c> / <c>WatBackend</c>) are per-target classes.
/// Literal and identifier projection follow.</para>
/// </summary>
internal interface ITarget
{
    /// <summary>Project a neutral <see cref="CType"/> onto this target's type
    /// spelling (e.g. C's <c>unsigned long</c> → C# <c>ulong</c>; a C array, which
    /// decays to a pointer, → <c>T*</c>).</summary>
    string RenderType(CType t);

    /// <summary>Render an integer literal — its neutral digit core plus whatever
    /// suffix this target spells for the literal's integer type.</summary>
    string RenderIntLit(LitInt lit);

    /// <summary>Render a floating-point literal in this target's syntax.</summary>
    string RenderFloatLit(LitFloat lit);
}

/// <summary>
/// A target's identifier policy, injected into <see cref="SymbolTable"/> so the
/// shared name-resolution machinery doesn't bake in one output language's rules.
/// The table owns the neutral MECHANISM (scope tracking + collision counting);
/// this owns the target POLICY — how a raw source name is escaped to a legal
/// target identifier, whether the target even forbids a local shadowing an
/// enclosing one, and how a collision is uniquified.
/// </summary>
internal interface INameLegalizer
{
    /// <summary>Escape a raw source identifier into a legal target identifier
    /// (the C# backend: a reserved word → <c>@word</c>).</summary>
    string Escape(string rawName);

    /// <summary>True when the target rejects a block-local shadowing an enclosing
    /// binding (C# — CS0136), so <see cref="SymbolTable"/> must uniquify; false when
    /// shadowing is legal (C), so names pass through unchanged.</summary>
    bool ForbidsShadowing { get; }

    /// <summary>Form the <paramref name="collision"/>-th uniquified variant of an
    /// already-escaped name (the C# backend: <c>name__k</c>).</summary>
    string Uniquify(string escaped, int collision);
}
