#nullable enable

using System.Collections.Generic;

namespace DotCC;

/// <summary>
/// Collects dialect-gating diagnostics during the emit pass. dotcc parses the
/// UNION of all C dialects (one grammar), so every feature is accepted by the
/// parser regardless of <c>-std=</c>; this gate is the REJECTION layer that
/// flags input constructs that postdate the selected standard — the equivalent
/// of <c>gcc -std=c90 -pedantic</c> warning "X is a C99 extension". It does NOT
/// affect lowering: dotcc always emits modern idiomatic C# (real enums, etc.)
/// regardless of input dialect.
/// </summary>
/// <remarks>
/// Constructed (and threaded into the emit pass) only when
/// <c>-pedantic</c> / <c>-pedantic-errors</c> is set, so it is a no-op on the
/// default permissive path. It is wired into the EMIT pass only — never the
/// analysis pass — so each violation is reported exactly once. Severity (warn
/// vs error) is decided at flush time by <see cref="Compiler"/>, not here:
/// collect-all, then warn-and-continue or throw-with-all.
/// </remarks>
internal sealed class DialectGate
{
    private readonly CDialect _dialect;
    private readonly List<string> _diagnostics = new();

    public DialectGate(CDialect dialect) => _dialect = dialect;

    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool HasAny => _diagnostics.Count > 0;

    /// <summary>
    /// Flag <paramref name="feature"/> when the active dialect is older than
    /// the standard that introduced it. <paramref name="introducedEra"/> is the
    /// ISO year of that standard (matches <see cref="CDialect.Version"/>, which
    /// is keyed by year: 1999 / 2011 / 2023). <paramref name="line"/> is the
    /// source line (0 = unknown). No-op once a dialect is new enough.
    /// </summary>
    public void RequireMin(int introducedEra, string feature, int line)
    {
        if (_dialect.Version >= introducedEra) { return; }
        var where = line > 0 ? $" (line {Ir.SrcPos.DescribeLine(line)})" : "";
        _diagnostics.Add(
            $"`{feature}` is a {EraName(introducedEra)} feature, not available under -std={_dialect.Name}{where}");
    }

    private static string EraName(int era) => era switch
    {
        1990 => "C90",
        1999 => "C99",
        2011 => "C11",
        2017 => "C17",
        2023 => "C23",
        _ => "C" + era.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
