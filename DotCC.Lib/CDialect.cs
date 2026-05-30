#nullable enable

using System;
using System.Collections.Generic;

namespace DotCC;

/// <summary>
/// C language dialect selected via <c>-std=&lt;name&gt;</c> on the dotcc CLI.
/// Accepts <c>c90</c>/<c>c99</c>/<c>c11</c>/<c>c17</c>/<c>c18</c>/<c>c23</c>
/// — the year drives <c>__STDC_VERSION__</c>. <c>c89</c> is omitted on
/// purpose: ISO renamed the original C89 standard to C90 with no semantic
/// change, so the single canonical spelling is enough. <c>gnu*</c>
/// variants are also omitted: dotcc implements no GNU extensions
/// (no <c>__attribute__</c>, no <c>({…})</c> statement expressions, no
/// nested functions, no case ranges), so a parallel <c>gnu</c> surface
/// would be dead toggle space.
/// </summary>
/// <remarks>
/// v1 scope is intentionally narrow: <c>-std=</c> changes which
/// <c>__STDC_*</c> macros are predefined, and synthetic / user headers
/// branch on them with <c>#if __STDC_VERSION__ &gt;= ...</c>. The parser
/// is dialect-agnostic — <c>//</c> comments are accepted regardless of
/// the requested standard, and there is no <c>-pedantic</c> equivalent.
/// </remarks>
/// <param name="Version">The ISO publication year of the standard
/// (1990 / 1999 / 2011 / 2017 / 2023). Keyed by year on purpose so it is
/// <b>monotonic</b> across the C timeline — any "is this dialect at least as
/// new as standard X" comparison (dialect gating, rule-2 keyword promotion) is
/// a plain <c>Version &gt;= year</c>. (An earlier design keyed it by the short
/// <c>90/99/11/17/23</c> suffix, which sorts <c>c11</c> below <c>c99</c> and
/// quietly broke such comparisons; the user-facing <c>cNN</c> spelling now
/// lives only in <see cref="Name"/>, decoupled from the ordering value.)</param>
public readonly record struct CDialect(int Version)
{
    /// <summary>
    /// Default dialect when <c>-std=</c> is not specified. <c>c17</c>
    /// is "C11 with the bug-fix wording" — a modern but stable baseline
    /// that doesn't reject any feature the grammar already accepts.
    /// </summary>
    public static CDialect Default { get; } = new(Version: 2017);

    /// <summary>
    /// Display name in clang shape (e.g. <c>"c90"</c>, <c>"c17"</c>).
    /// </summary>
    public string Name => "c" + Version switch
    {
        1990 => "90",
        1999 => "99",
        2011 => "11",
        2017 => "17",
        2023 => "23",
        _ => Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// <c>__STDC_VERSION__</c> value for this dialect, or <c>null</c>
    /// if the macro should be left undefined (C90: per the standard,
    /// only <c>__STDC__</c> is defined in that mode).
    /// </summary>
    public string? StdcVersionLiteral => Version switch
    {
        1990 => null,
        1999 => "199901L",
        2011 => "201112L",
        2017 => "201710L",
        2023 => "202311L",
        _ => null,
    };

    private static readonly Dictionary<string, CDialect> _byName = new(StringComparer.Ordinal)
    {
        ["c90"] = new(1990),
        ["c99"] = new(1999),
        ["c11"] = new(2011),
        ["c17"] = new(2017),
        ["c18"] = new(2017), // clang alias — same content as c17
        ["c23"] = new(2023),
    };

    /// <summary>
    /// Recognised <c>-std=</c> values, for error messages.
    /// </summary>
    public static IReadOnlyCollection<string> KnownNames => _byName.Keys;

    /// <summary>
    /// Parse a <c>-std=</c> value (e.g. <c>"c99"</c>, <c>"c17"</c>) into
    /// the corresponding dialect. Throws <see cref="FormatException"/> on
    /// unknown values so the frontend can surface a clang-shaped
    /// diagnostic without the <c>ArgumentException</c> parameter-name
    /// decoration leaking into stderr.
    /// </summary>
    public static CDialect Parse(string name)
    {
        if (_byName.TryGetValue(name, out var d)) { return d; }
        throw new FormatException(
            $"unknown -std value `{name}` (expected one of: {string.Join(", ", _byName.Keys)})");
    }
}
