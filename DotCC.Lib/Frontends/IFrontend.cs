#nullable enable

using System.Collections.Generic;
using DotCC.Ir;

namespace DotCC.Frontends;

/// <summary>
/// A source language's front half of the pipeline — the N-axis seam mirroring
/// <see cref="ITarget"/> on the M (backend) axis. A frontend lexes/parses its own
/// source language and binds it to the neutral typed IR, returning the
/// backend-agnostic <see cref="IrBuilder"/> that any <see cref="ITarget"/> backend
/// then projects onto its surface language. So a second source language (Zig, …) is
/// "implement <see cref="IFrontend"/> again", exactly as a second target is
/// "implement <see cref="ITarget"/> again" — neither has to untangle the other.
/// <para>Implementations live in <c>DotCC.Frontends</c> (today: <c>CFrontend</c>).</para>
/// </summary>
internal interface IFrontend
{
    /// <summary>Lex, parse and bind every input translation unit to the typed IR,
    /// flushing source-level diagnostics, and return the resulting
    /// <see cref="IrBuilder"/>.</summary>
    IrBuilder BuildIr(FrontendRequest request);
}

/// <summary>
/// A language-neutral compilation request handed to an <see cref="IFrontend"/>. The
/// knobs are general enough that any larger source language carries some form of
/// them: a set of input units, header/include search dirs, predefined symbols, a
/// dialect/standard version, strictness flags, the target's identifier policy, and
/// the enabled diagnostic warnings. A frontend that has no analogue for a field
/// simply ignores it (Zig has no preprocessor <c>Defines</c>, for instance).
/// </summary>
internal sealed record FrontendRequest(
    IReadOnlyList<string> InputPaths,
    IReadOnlyList<string>? IncludeDirs = null,
    IReadOnlyList<string>? Defines = null,
    CDialect? Dialect = null,
    INameLegalizer? Names = null,
    WarningFlags Warnings = WarningFlags.Default);
